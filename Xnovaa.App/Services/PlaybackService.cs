using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Xnovaa.App.Models;

namespace Xnovaa.App.Services;

public interface IPlaybackService : IDisposable
{
    bool IsInitialized { get; }
    /// <summary>True while a media is loaded and not paused/ended.</summary>
    bool IsPlaying { get; }
    VideoItem? CurrentItem { get; }

    event EventHandler<float>? PositionChanged;   // 0..1
    event EventHandler<long>? TimeChanged;        // ms
    event EventHandler<long>? DurationChanged;    // ms
    event EventHandler? MediaEnded;
    event EventHandler? MediaStarted;
    event EventHandler? PauseStateChanged;

    void Initialize(bool hardwareAcceleration);
    void Play(VideoItem item);
    void Pause();
    void Resume();
    void TogglePlayPause();
    void Stop();
    void SetPosition(float fraction);
    void SeekBy(TimeSpan delta);
    void SetVolumePercent(int volume);            // 0-100
    int GetVolume();
    void SetMute(bool mute);
    bool IsMuted();

    /// <summary>Returns the underlying LibVLCSharp MediaPlayer (for VideoView binding).</summary>
    LibVLCSharp.Shared.MediaPlayer? GetNativePlayer();

    /// <summary>Raised (once) when the MediaPlayer instance is created.</summary>
    event Action<LibVLCSharp.Shared.MediaPlayer>? PlayerCreated;

    /// <summary>Creates an independent MediaPlayer (same LibVLC instance) for thumbnail frame grabs.</summary>
    LibVLCSharp.Shared.MediaPlayer? CreateThumbnailPlayer();

    /// <summary>The shared LibVLC instance, or null before initialization.</summary>
    LibVLCSharp.Shared.LibVLC? GetLibVlc();

    /// <summary>Reads duration for a file via LibVLC parse (no playback).</summary>
    Task<TimeSpan> GetDurationAsync(string filePath);
}

public class PlaybackService : IPlaybackService
{
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private readonly object _gate = new();
    private Media? _currentMedia;
    private bool _hwAccel = true;
    private bool _disposed;
    private int _generation;   // bumped on every Play/Stop/Dispose; stale async work checks it

    // Local mirrors: libvlc's Volume getter can lag right after a set, and the
    // player may not exist yet before first play. State lives here.
    private int _volumePercent = 100;
    private bool _muted;
    private int _lastNonMutedVolume = 100;

    public bool IsInitialized { get { lock (_gate) return _player is not null; } }
    public bool IsPlaying { get { lock (_gate) return _player?.IsPlaying == true; } }

    public VideoItem? CurrentItem { get; private set; }

    /// <summary>Raised (once) when the MediaPlayer instance is created — attach VideoView here.</summary>
    public event Action<MediaPlayer>? PlayerCreated;

    public event EventHandler<float>? PositionChanged;
    public event EventHandler<long>? TimeChanged;
    public event EventHandler<long>? DurationChanged;
    public event EventHandler? MediaEnded;
    public event EventHandler? MediaStarted;
    public event EventHandler? PauseStateChanged;

    /// <summary>
    /// Startup step (Plan §8): resolve native paths + store the HW-accel preference.
    /// The LibVLC instance itself is created lazily on first playback (idle RAM).
    /// </summary>
    public void Initialize(bool hardwareAcceleration)
    {
        lock (_gate)
        {
            Core.Initialize();
            _hwAccel = hardwareAcceleration;
        }
    }

    // ------------------------------------------------ instance creation

    private MediaPlayer CreateInstanceLocked()
    {
        // caller holds _gate
        var options = new List<string>
        {
            "--file-caching=300",
            "--no-video-title-show",
            "--quiet",
            "--no-osd",
            // Low-RAM: cap decoder threads (dual-core target per Plan §0)
            "--avcodec-threads=2",
            // HW accel is a PREFERENCE: libvlc falls back to software decoding
            // internally when dxva2 is unavailable — never a hard failure (P4).
            _hwAccel ? "--avcodec-hw=dxva2" : "--avcodec-hw=none"
        };
        var libVlc = new LibVLC(options.ToArray());
        MediaPlayer player;
        try
        {
            player = new MediaPlayer(libVlc)
            {
                EnableHardwareDecoding = _hwAccel
            };
        }
        catch
        {
            libVlc.Dispose();
            throw;
        }

        _libVlc = libVlc;
        _player = player;
        _generation++;
        Diag($"libvlc+player created (hwAccel={_hwAccel}, tid={Environment.CurrentManagedThreadId:X2})");
        // Static handlers only capture state / marshal outward — they never call
        // back into libvlc from the native callback thread (P1).
        player.PositionChanged += OnPlayerPositionChanged;
        player.TimeChanged += OnPlayerTimeChanged;
        player.LengthChanged += OnPlayerLengthChanged;
        player.EncounteredError += OnPlayerEncounteredError;
        player.Playing += OnPlayerPlaying;
        player.Paused += OnPlayerPaused;
        player.Stopped += OnPlayerStopped;
        player.EndReached += OnPlayerEndReached;
        libVlc.Log += OnLibVlcLog;

        ApplyVolumeToPlayer();

        // Notify UI (VideoView lazy wiring) — invoked outside any callback context.
        var handler = PlayerCreated;
        if (handler is not null)
        {
            Post(() => handler.Invoke(player));
        }
        return player;
    }

    private MediaPlayer EnsureInstance()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _player ?? CreateInstanceLocked();
        }
    }

    // -------------------------------- native event handlers (libvlc threads)
    // RULE: capture state, marshal off-thread, generation-check, NO libvlc calls.

    private void OnPlayerPositionChanged(object? s, MediaPlayerPositionChangedEventArgs e)
    {
        var h = PositionChanged;
        if (h is null) return;
        var gen = _generation;
        Post(() => { if (_generation == gen) h.Invoke(this, e.Position); });
    }

    private void OnPlayerTimeChanged(object? s, MediaPlayerTimeChangedEventArgs e)
    {
        var h = TimeChanged;
        if (h is null) return;
        var gen = _generation;
        Post(() => { if (_generation == gen) h.Invoke(this, e.Time); });
    }

    private void OnPlayerLengthChanged(object? s, MediaPlayerLengthChangedEventArgs e)
    {
        var h = DurationChanged;
        if (h is null) return;
        var gen = _generation;
        Post(() => { if (_generation == gen) h.Invoke(this, e.Length); });
    }

    private void OnPlayerEncounteredError(object? s, EventArgs e)
    {
        var cur = CurrentItem?.FilePath;
        Post(() => Diag($"libvlc error event (file={cur ?? "n/a"}, hwAccel={_hwAccel})"));
    }

    private void OnPlayerPlaying(object? s, EventArgs e)
    {
        var h = MediaStarted;
        var gen = _generation;
        Post(() => { if (_generation == gen) h?.Invoke(this, EventArgs.Empty); });
    }

    private void OnPlayerPaused(object? s, EventArgs e)
    {
        var h = PauseStateChanged;
        var gen = _generation;
        Post(() => { if (_generation == gen) h?.Invoke(this, EventArgs.Empty); });
    }

    private void OnPlayerStopped(object? s, EventArgs e)
    {
        var h = PauseStateChanged;
        var gen = _generation;
        Post(() => { if (_generation == gen) h?.Invoke(this, EventArgs.Empty); });
    }

    private void OnPlayerEndReached(object? s, EventArgs e)
    {
        var h = MediaEnded;
        var gen = _generation;
        // Never call libvlc from EndReached; marshal and generation-check (P1).
        Post(() => { if (_generation == gen) h?.Invoke(this, EventArgs.Empty); });
    }

    private void OnLibVlcLog(object? s, LogEventArgs e)
    {
        try
        {
            // LogLevel has Debug/Notice/Warning/Error — Fatal doesn't exist in 3.10.
            if (e.Level is LogLevel.Error)
                Diag($"libvlc [{e.Level}] {e.FormattedLog}");
        }
        catch { }
    }

    /// <summary>Marshal work off the native callback thread (dispatcher if alive).</summary>
    private static void Post(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is not null && !d.HasShutdownStarted)
        {
            try { d.BeginInvoke(action); return; } catch { }
        }
        Task.Run(action);
    }

    private static void Diag(string msg)
    {
        try { App.LogDiagSafe($"[Playback tid={Environment.CurrentManagedThreadId:X2}] {msg}"); }
        catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PlaybackService));
    }

    // ------------------------------------------------ public API

    public void SetHardwareAcceleration(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _hwAccel = enabled;
            if (_player is not null)
                _player.EnableHardwareDecoding = enabled;
            Diag($"hwAccel set to {enabled}");
        }
    }

    public void Play(VideoItem item)
    {
        Media? oldMedia;
        lock (_gate)
        {
            ThrowIfDisposed();
            var player = _player ?? CreateInstanceLocked();

            var oldFile = CurrentItem?.FilePath;

            try
            {
                // Stop() joins the vout thread; once it returns, no callback of the
                // previous media can fire into freed native objects (P1).
                if (player.IsPlaying || _currentMedia is not null)
                    player.Stop();
            }
            catch (Exception ex)
            {
                Diag($"stop-before-play failed for {oldFile}: {ex.Message}");
            }
            finally
            {
                oldMedia = _currentMedia;
                _currentMedia = null;
            }

            Media media;
            try
            {
                media = new Media(_libVlc!, new Uri(item.FilePath));
            }
            catch (Exception ex)
            {
                Diag($"media create failed for {item.FilePath}: {ex.Message}");
                CurrentItem = null;
                oldMedia?.Dispose();
                return;
            }

            _currentMedia = media;
            CurrentItem = item;
            _generation++;

            try
            {
                var srt = Path.ChangeExtension(item.FilePath, ".srt");
                if (File.Exists(srt))
                    media.AddSlave(MediaSlaveType.Subtitle, 1, srt);
            }
            catch (Exception ex)
            {
                Diag($"subtitle slave add failed: {ex.Message}");
            }

            Diag($"play start: {item.FilePath} (hwAccel={_hwAccel})");
            player.Play(media);
        }

        // Dispose the replaced media outside the lock — safe & non-blocking.
        try { oldMedia?.Dispose(); } catch { }
        Diag("old media disposed");
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_disposed || _player is null) return;
            try { _player.Pause(); } catch (Exception ex) { Diag($"pause failed: {ex.Message}"); }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_disposed || _player is null) return;
            try { _player.Play(); } catch (Exception ex) { Diag($"resume failed: {ex.Message}"); }
        }
    }

    public void TogglePlayPause()
    {
        lock (_gate)
        {
            if (_disposed || _player is null) return;
            try
            {
                if (_player.IsPlaying) _player.Pause();
                else _player.Play();
            }
            catch (Exception ex) { Diag($"toggle play/pause failed: {ex.Message}"); }
        }
    }

    public void Stop()
    {
        Media? mediaToDispose;
        lock (_gate)
        {
            if (_disposed || _player is null) return;
            try { _player.Stop(); }
            catch (Exception ex) { Diag($"stop failed: {ex.Message}"); }
            mediaToDispose = _currentMedia;
            _currentMedia = null;
            CurrentItem = null;
            _generation++;
        }

        try { mediaToDispose?.Dispose(); } catch { }
        Diag("playback stopped, media disposed");
    }

    public void SetPosition(float fraction)
    {
        lock (_gate)
        {
            if (_disposed || _player is null) return;
            try
            {
                if (_player.Length > 0)
                    _player.Time = (long)(fraction * _player.Length);
            }
            catch (Exception ex) { Diag($"set position failed: {ex.Message}"); }
        }
    }

    public void SeekBy(TimeSpan delta)
    {
        lock (_gate)
        {
            if (_disposed || _player is null) return;
            try
            {
                if (_player.Length > 0)
                {
                    var target = Math.Clamp(_player.Time + (long)delta.TotalMilliseconds, 0, _player.Length);
                    _player.Time = target;
                }
            }
            catch (Exception ex) { Diag($"seek failed: {ex.Message}"); }
        }
    }

    public void SetVolumePercent(int volume)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _volumePercent = Math.Clamp(volume, 0, 100);
            if (_volumePercent > 0) _muted = false;
            ApplyVolumeToPlayer();
        }
    }

    public void SetMute(bool mute)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _muted = mute;
            if (mute)
            {
                if (_volumePercent > 0) _lastNonMutedVolume = _volumePercent;
            }
            else if (_volumePercent <= 0)
            {
                _volumePercent = _lastNonMutedVolume <= 0 ? 100 : _lastNonMutedVolume;
            }
            ApplyVolumeToPlayer();
        }
    }

    public int GetVolume() { lock (_gate) return _muted ? 0 : _volumePercent; }
    public bool IsMuted() { lock (_gate) return _muted; }

    private void ApplyVolumeToPlayer()
    {
        // caller holds _gate
        var p = _player;
        if (p is null) return;
        try { p.Volume = _muted ? 0 : _volumePercent; } catch { }
    }

    public LibVLCSharp.Shared.MediaPlayer? GetNativePlayer()
    {
        lock (_gate) return _player;
    }

    public LibVLCSharp.Shared.MediaPlayer? CreateThumbnailPlayer()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            try
            {
                _ = _player ?? CreateInstanceLocked();
                return new MediaPlayer(_libVlc!);
            }
            catch (Exception ex)
            {
                Diag($"thumbnail player create failed: {ex.Message}");
                return null;
            }
        }
    }

    public LibVLCSharp.Shared.LibVLC? GetLibVlc()
    {
        lock (_gate) return _libVlc;
    }

    public async Task<TimeSpan> GetDurationAsync(string filePath)
    {
        LibVLC? libVlc;
        lock (_gate)
        {
            if (_disposed) return TimeSpan.Zero;
            libVlc = _libVlc;
            if (libVlc is null)
            {
                _ = CreateInstanceLocked();
                libVlc = _libVlc;
            }
            if (libVlc is null) return TimeSpan.Zero;
        }

        var media = new Media(libVlc!, new Uri(filePath));
        try
        {
            var status = await media.Parse(MediaParseOptions.ParseLocal);
            if (status != MediaParsedStatus.Done) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(media.Duration);
        }
        catch (Exception ex)
        {
            Diag($"duration parse failed for {filePath}: {ex.Message}");
            return TimeSpan.Zero;
        }
        finally
        {
            try { media.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------ disposal (idempotent, P1)

    public void Dispose()
    {
        MediaPlayer? player;
        Media? media;
        LibVLC? libVlc;
        lock (_gate)
        {
            if (_disposed) return;          // idempotent
            _disposed = true;
            _generation++;                  // invalidate all in-flight posted work

            player = _player;
            media = _currentMedia;
            libVlc = _libVlc;
            _player = null;
            _currentMedia = null;
            _libVlc = null;
            CurrentItem = null;
        }

        // Teardown OUTSIDE the lock, in dependency order. Stop() joins libvlc's
        // playback thread, so after it returns no callback can touch the media.
        try { player?.Stop(); }
        catch (Exception ex) { Diag($"dispose stop failed: {ex.Message}"); }
        try { media?.Dispose(); }
        catch (Exception ex) { Diag($"dispose media failed: {ex.Message}"); }
        try { player?.Dispose(); }
        catch (Exception ex) { Diag($"dispose player failed: {ex.Message}"); }
        try { libVlc?.Dispose(); }
        catch (Exception ex) { Diag($"dispose libvlc failed: {ex.Message}"); }

        Diag("PlaybackService disposed");
    }
}
