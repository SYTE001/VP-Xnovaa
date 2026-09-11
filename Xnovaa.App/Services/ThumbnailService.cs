using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Xnovaa.App.Models;
using Xnovaa.App.Utils;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Xnovaa.App.Services;

public interface IThumbnailService
{
    /// <summary>Ensures a thumbnail exists for the item; returns the cache path or null. Concurrency is limited.</summary>
    Task<string?> EnsureThumbnailAsync(VideoItem item, CancellationToken ct = default);

    /// <summary>Loads a decoded + frozen BitmapImage for binding (DecodePixelWidth honored).</summary>
    BitmapSource? LoadImage(string path, int decodeWidth = 160);
}

public class ThumbnailService : IThumbnailService
{
    private readonly SemaphoreSlim _parallelGate = new(2, 2);

    public async Task<string?> EnsureThumbnailAsync(VideoItem item, CancellationToken ct = default)
    {
        var target = Path.Combine(AppPaths.ThumbnailsFolder, $"{item.Id}.jpg");
        if (File.Exists(target)) return target;
        if (item.ThumbnailPath is not null && File.Exists(item.ThumbnailPath)) return item.ThumbnailPath;

        await _parallelGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(target)) return target;

            // 1) Windows Shell thumbnail (no extra window, MF-backed).
            BitmapSource? bitmap = await Task.Run(() =>
            {
                try { return NativeThumbnail.Extract(item.FilePath, 160); }
                catch { return null; }
            }, ct);

            // 2) Fallback: decode one frame with LibVLC into an in-memory bitmap.
            bitmap ??= await ExtractVlcFrameAsync(item.FilePath, ct);

            if (bitmap is null) return null;

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
            await using var fs = File.Create(target);
            encoder.Save(fs);

            item.ThumbnailPath = target;
            return target;
        }
        catch
        {
            return null;
        }
        finally
        {
            _parallelGate.Release();
        }
    }

    private static async Task<BitmapSource?> ExtractVlcFrameAsync(string filePath, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return null;

        const int w = 160, h = 90;
        var pixels = new byte[w * h * 4];
        var scratch = Marshal.AllocHGlobal(w * h * 4);
        using var frameArrived = new ManualResetEventSlim(false);
        VlcMediaPlayer? player = null;
        Media? media = null;

        // Keep delegate instances alive for the lifetime of the playback session.
        VlcMediaPlayer.LibVLCVideoLockCb lockCb = (opaque, planes) =>
        {
            Marshal.WriteIntPtr(planes, scratch); // planes[0] = our pixel buffer
            return scratch;
        };
        VlcMediaPlayer.LibVLCVideoUnlockCb unlockCb = (opaque, picture, planes) =>
        {
            try { Marshal.Copy(scratch, pixels, 0, pixels.Length); } catch { }
            frameArrived.Set();
        };
        VlcMediaPlayer.LibVLCVideoDisplayCb displayCb = (opaque, picture) => { };

        await dispatcher.InvokeAsync(new Action(() =>
        {
            try
            {
                player = App.PlaybackService.CreateThumbnailPlayer();
                var libVlc = App.PlaybackService.GetLibVlc();
                if (player is null || libVlc is null) return;

                player.SetVideoCallbacks(lockCb, unlockCb, displayCb);
                player.SetVideoFormat("BGRA", (uint)w, (uint)h, (uint)(w * 4));

                media = new Media(libVlc, new Uri(filePath));
                media.AddOption(":no-audio");
                media.AddOption(":start-time=3");
                player.Play(media);
            }
            catch { /* leave frameArrived unset; cleanup path handles it */ }
        }));

        // Wait for one decoded frame (or 4s timeout), off the UI thread.
        bool got = await Task.Run(() => frameArrived.Wait(4000, CancellationToken.None), ct);

        // Stop + dispose on the UI thread. Stop() joins the vout thread, so after it
        // returns no callback can touch scratch/delegates any more — safe to free.
        var bmp = await dispatcher.InvokeAsync(new Func<BitmapSource?>(() =>
        {
            try { player?.Stop(); } catch { }
            try { player?.Dispose(); } catch { }
            try { media?.Dispose(); } catch { }

            if (!got) return null;
            try
            {
                var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
                wb.Freeze();
                return wb;
            }
            catch { return null; }
        }));

        GC.KeepAlive(lockCb);
        GC.KeepAlive(unlockCb);
        GC.KeepAlive(displayCb);
        Marshal.FreeHGlobal(scratch);
        return bmp;
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

    private static async void OnImageLoaded(object sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.Image image) return;
        var item = GetItem(image);
        if (item is null) return;

        var svc = App.ThumbnailService;
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
}
