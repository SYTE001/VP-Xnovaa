using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Xnovaa.App.Models;
using Xnovaa.App.Utils;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Xnovaa.App.Services;

public interface IThumbnailService
{
    /// <summary>Ensures a thumbnail exists for the item; returns the cache path or null. Never throws.</summary>
    Task<string?> EnsureThumbnailAsync(VideoItem item, CancellationToken ct = default);

    /// <summary>Loads a decoded + frozen BitmapImage for binding (DecodePixelWidth honored).</summary>
    BitmapSource? LoadImage(string path, int decodeWidth = 160);

    /// <summary>Stops all in-flight generations and releases resources. Called before PlaybackService disposal.</summary>
    void Shutdown();
}

public class ThumbnailService : IThumbnailService
{
    private readonly SemaphoreSlim _parallelGate = new(2, 2);
    private readonly CancellationTokenSource _shutdown = new();

    // Every live VLC frame-grab session is tracked so shutdown can stop them all
    // BEFORE LibVLC is disposed (fixes close-while-generating crash, P2/P3).
    private static readonly ConcurrentDictionary<Guid, FrameSession> _sessions = new();

    private sealed class FrameSession
    {
        public required Guid Key;
        public required VlcMediaPlayer Player;
        public required Media Media;
        public required IntPtr Scratch;          // HGlobal pixel buffer
        public required byte[] Pixels;           // managed copy target
        public required ManualResetEventSlim FrameArrived;
        public required object CleanupLock;       // serializes cleanup vs shutdown
        public bool CleanedUp;                    // guarded by CleanupLock
    }

    public async Task<string?> EnsureThumbnailAsync(VideoItem item, CancellationToken ct = default)
    {
        try
        {
            var target = Path.Combine(AppPaths.ThumbnailsFolder, $"{item.Id}.jpg");
            if (File.Exists(target)) return target;
            if (item.ThumbnailPath is not null && File.Exists(item.ThumbnailPath)) return item.ThumbnailPath;

            // Linked: cancelled as soon as the app begins shutdown.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            await _parallelGate.WaitAsync(linked.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (File.Exists(target)) return target;

                Diag($"thumb start: {item.FileName} ({Path.GetFileName(item.FilePath)})");

                // 1) Windows Shell thumbnail — no native playback involved at all.
                BitmapSource? bitmap = null;
                try
                {
                    bitmap = await Task.Run(() => NativeThumbnail.Extract(item.FilePath, 160), linked.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* fall through to VLC */ }

                // 2) Fallback: decode one frame via a dedicated off-screen player.
                if (bitmap is null)
                {
                    bitmap = await ExtractVlcFrameAsync(item.FilePath, linked.Token);
                }

                if (bitmap is null)
                {
                    Diag($"thumb failed: {item.FileName} (no frame extracted)");
                    return null;
                }

                var encoded = bitmap;
                if (encoded.PixelWidth > 160)
                {
                    var scaled = new TransformedBitmap(encoded,
                        new ScaleTransform(160.0 / encoded.PixelWidth, 160.0 / encoded.PixelHeight));
                    scaled.Freeze();
                    encoded = scaled;
                }

                var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                encoder.Frames.Add(BitmapFrame.Create(encoded));
                await using (var fs = File.Create(target))
                {
                    encoder.Save(fs);
                }

                item.ThumbnailPath = target;
                Diag($"thumb end: {item.FileName}");
                return target;
            }
            finally
            {
                _parallelGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return null; // shutdown or caller cancel — not an error
        }
        catch (Exception ex)
        {
            // Thumbnail generation must NEVER crash the process (P2).
            Diag($"thumb error: {item.FileName}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Frame extraction via libvlc video-memory callbacks. Ownership rules:
    ///  - The HGlobal buffer and delegate instances are owned by a session object
    ///    that stays referenced (and thus keeps delegates alive) until cleanup
    ///    has PROVEN that native playback stopped.
    ///  - Freeing happens only after a successful Stop(); if Stop() fails, the
    ///    memory is deliberately leaked (~57 KB) instead of risking a native
    ///    access violation into freed memory (leak-not-crash policy).
    /// </summary>
    private static async Task<BitmapSource?> ExtractVlcFrameAsync(string filePath, CancellationToken ct)
    {
        const int w = 160, h = 90;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return null;

        VlcMediaPlayer? player = null;
        LibVLC? libVlc = null;
        try
        {
            player = App.PlaybackService.CreateThumbnailPlayer();
            libVlc = App.PlaybackService.GetLibVlc();
        }
        catch (Exception ex)
        {
            Diag($"thumb player create failed: {ex.Message}");
            return null;
        }
        if (player is null || libVlc is null) return null;

        var session = new FrameSession
        {
            Key = Guid.NewGuid(),
            Player = player,
            Media = new Media(libVlc, new Uri(filePath)),
            Scratch = Marshal.AllocHGlobal(w * h * 4),
            Pixels = new byte[w * h * 4],
            FrameArrived = new ManualResetEventSlim(false),
            CleanupLock = new object()
        };
        _sessions[session.Key] = session;

        // Local delegate instances captured by the session — never GC'd mid-flight
        // because the session is rooted in the static registry until cleaned up.
        VlcMediaPlayer.LibVLCVideoLockCb lockCb = (opaque, planes) =>
        {
            Marshal.WriteIntPtr(planes, session.Scratch);
            return session.Scratch;
        };
        VlcMediaPlayer.LibVLCVideoUnlockCb unlockCb = (opaque, picture, planes) =>
        {
            try { Marshal.Copy(session.Scratch, session.Pixels, 0, session.Pixels.Length); } catch { }
            session.FrameArrived.Set();
        };
        VlcMediaPlayer.LibVLCVideoDisplayCb displayCb = (opaque, picture) => { };
        GC.KeepAlive(lockCb);
        GC.KeepAlive(unlockCb);
        GC.KeepAlive(displayCb);

        try
        {
            session.Media.AddOption(":no-audio");
            session.Media.AddOption(":start-time=2");

            // Setup + play on the UI thread (LibVLCSharp.WPF requirement).
            await dispatcher.InvokeAsync(() =>
            {
                try
                {
                    player!.SetVideoCallbacks(lockCb, unlockCb, displayCb);
                    player.SetVideoFormat("BGRA", (uint)w, (uint)h, (uint)(w * 4));
                    player.Play(session.Media);
                }
                catch (Exception ex)
                {
                    Diag($"thumb play setup failed: {ex.Message}");
                }
            }, System.Windows.Threading.DispatcherPriority.Send);

            // Wait for the first decoded frame — off the UI thread, timeout 4s.
            var got = await Task.Run(
                () => session.FrameArrived.Wait(4000, CancellationToken.None),
                CancellationToken.None); // never cancel the wait itself: cleanup needs determinism

            // Copy pixels while the session is guaranteed alive (before any teardown).
            byte[] pixels = session.Pixels;

            // Deterministic teardown: this is the ONLY place session resources die.
            bool stopped = StopAndCleanupSession(session, alsoDisposePlayer: true);
            if (!stopped)
            {
                // Stop failed — session stays registered & delegates stay alive;
                // memory intentionally NOT freed to avoid a native AV (leak-not-crash).
                Diag($"thumb stop FAILED for {Path.GetFileName(filePath)} — session leaked deliberately");
                return null;
            }

            if (!got) return null;

            var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
            wb.Freeze();
            return wb;
        }
        catch (Exception ex)
        {
            Diag($"thumb extract failed for {filePath}: {ex.GetType().Name}: {ex.Message}");
            StopAndCleanupSession(session, alsoDisposePlayer: true);
            return null;
        }
    }

    /// <summary>
    /// Stops the session's player and — only when the stop is CONFIRMED — frees the
    /// native buffer and unregisters the session. Thread-safe vs Shutdown().
    /// Returns true when cleanup completed (safe to free), false when leaked on purpose.
    /// </summary>
    private static bool StopAndCleanupSession(FrameSession session, bool alsoDisposePlayer)
    {
        lock (session.CleanupLock)
        {
            if (session.CleanedUp) return true;
            if (session.Player is null) { session.CleanedUp = true; return true; }

            var stopped = false;
            try
            {
                session.Player.Stop();   // synchronous: joins the vout thread
                stopped = true;
            }
            catch (Exception ex)
            {
                Diag($"thumb session stop exception: {ex.Message}");
            }

            if (!stopped)
            {
                // Could not prove playback stopped — do NOT free anything.
                return false;
            }

            // vout joined: no more lock/unlock/display callbacks can run.
            session.CleanedUp = true;
            if (alsoDisposePlayer)
            {
                try { session.Player.Dispose(); } catch { }
                try { session.Media.Dispose(); } catch { }
            }
            Marshal.FreeHGlobal(session.Scratch);
            session.FrameArrived.Dispose();
            _sessions.TryRemove(session.Key, out _);
            return true;
        }
    }

    /// <summary>Stops every in-flight session and cancels queued work. Must run before LibVLC disposal.</summary>
    public void Shutdown()
    {
        try { _shutdown.Cancel(); } catch { }

        foreach (var kv in _sessions)
        {
            var session = kv.Value;
            lock (session.CleanupLock)
            {
                if (session.CleanedUp) continue;
                try
                {
                    session.Player.Stop();
                    session.CleanedUp = true;
                    try { session.Player.Dispose(); } catch { }
                    try { session.Media.Dispose(); } catch { }
                    Marshal.FreeHGlobal(session.Scratch);
                    session.FrameArrived.Dispose();
                }
                catch (Exception ex)
                {
                    Diag($"shutdown session cleanup failed: {ex.Message}");
                }
            }
            _sessions.TryRemove(kv.Key, out _);
        }
        Diag("ThumbnailService shut down");
    }

    private static void Diag(string msg)
    {
        try { App.LogDiagSafe($"[Thumb tid={Environment.CurrentManagedThreadId:X2}] {msg}"); }
        catch { }
    }

    public BitmapSource? LoadImage(string path, int decodeWidth = 160)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            using var fs = File.OpenRead(path);
            bmp.BeginInit();
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Attached behavior: binds a VideoItem to an Image and lazily generates the
/// thumbnail only when the (virtualized) container is realized.
/// </summary>
public static class LazyThumbnail
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(VideoItem), typeof(LazyThumbnail),
        new PropertyMetadata(null, OnItemChanged));

    public static VideoItem? GetItem(DependencyObject obj) => (VideoItem?)obj.GetValue(ItemProperty);
    public static void SetItem(DependencyObject obj, VideoItem? value) => obj.SetValue(ItemProperty, value);

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Image image) return;

        image.Loaded -= OnImageLoaded;
        image.Source = null;
        image.Loaded += OnImageLoaded;

        if (image.IsLoaded) OnImageLoaded(image, EventArgs.Empty);
    }

    // async void: every path must be exception-proof — an escape here is a process killer.
    private static async void OnImageLoaded(object sender, EventArgs e)
    {
        try
        {
            if (sender is not System.Windows.Controls.Image image) return;
            var item = GetItem(image);
            if (item is null) return;

            var svc = App.ThumbnailService;
            if (svc is null) return; // app shutting down

            var existing = item.ThumbnailPath;
            if (existing is not null && File.Exists(existing))
            {
                image.Source = svc.LoadImage(existing);
                return;
            }

            var path = await svc.EnsureThumbnailAsync(item);
            // Only apply if the container still shows this same item (recycling).
            if (path is not null && ReferenceEquals(GetItem(image), item))
            {
                image.Source = svc.LoadImage(path);
            }
        }
        catch
        {
            // Never let thumbnail binding issues surface as an unhandled exception.
        }
    }
}
