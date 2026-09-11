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

    // Local mirrors: libvlc's Volume getter can lag right after a set, and the
    // player may not exist yet before first play. State lives here.
    private int _volumePercent = 100;
    private bool _muted;

    public bool IsInitialized => _player is not null;
    public bool IsPlaying => _player?.IsPlaying == true;
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
    /// Lightweight startup step (Plan §8): resolves native paths + stores the HW-accel
    /// preference. The actual LibVLC instance is created lazily on first playback to
    /// keep idle RAM low.
    /// </summary>
    public void Initialize(bool hardwareAcceleration)
    {
        Core.Initialize();
        _hwAccel = hardwareAcceleration;
    }

    /// <summary>Creates the shared LibVLC + MediaPlayer on first use (thread-safe).</summary>
    private MediaPlayer EnsureInstance()
    {
        MediaPlayer player;
        lock (_gate)
        {
            if (_player is not null) return _player;

            var options = new List<string>
            {
                "--file-caching=300",
                "--no-video-title-show",
                "--quiet",
                "--no-osd",
                // Low-RAM: cap decoder threads (dual-core target per Plan §0)
                "--avcodec-threads=2",
                _hwAccel ? "--avcodec-hw=dxva2" : "--avcodec-hw=none"
            };
            var libVlc = new LibVLC(options.ToArray());
            player = new MediaPlayer(libVlc)
            {
                EnableHardwareDecoding = _hwAccel
            };
            _libVlc = libVlc;
            _player = player;

            player.PositionChanged += (_, e) => PositionChanged?.Invoke(this, e.Position);
            player.TimeChanged += (_, e) => TimeChanged?.Invoke(this, e.Time);
            player.LengthChanged += (_, e) => DurationChanged?.Invoke(this, e.Length);
            player.EncounteredError += (_, _) => { /* keep quiet */ };
            player.Playing += (_, _) => MediaStarted?.Invoke(this, EventArgs.Empty);
            player.Paused += (_, _) => PauseStateChanged?.Invoke(this, EventArgs.Empty);
            player.Stopped += (_, _) => PauseStateChanged?.Invoke(this, EventArgs.Empty);
            player.EndReached += (_, _) =>
            {
                // Never call libvlc API directly from the EndReached callback.
                Task.Run(() => MediaEnded?.Invoke(this, EventArgs.Empty));
            };

            ApplyVolumeToPlayer();
        }

        PlayerCreated?.Invoke(player);
        return player;
    }

    public void SetHardwareAcceleration(bool enabled)
    {
        // Only affects media loaded afterwards.
        if (_player is not null)
            _player.EnableHardwareDecoding = enabled;
    }

    public void Play(VideoItem item)
    {
        var player = EnsureInstance();

        lock (_gate)
        {
            StopInternal();

            var media = new Media(_libVlc!, new Uri(item.FilePath));
            _currentMedia = media;
            CurrentItem = item;

            // Auto-load .srt subtitle with the same file name if present.
            var srt = Path.ChangeExtension(item.FilePath, ".srt");
            if (File.Exists(srt))
            {
                media.AddSlave(MediaSlaveType.Subtitle, 1, srt);
            }

            player.Play(media);
        }
    }

    public void Pause() => _player?.Pause();
    public void Resume() => _player?.Play();

    public void TogglePlayPause()
    {
        var p = _player;
        if (p is null) return;
        if (p.IsPlaying) p.Pause();
        else p.Play();
    }

    public void Stop()
    {
        lock (_gate) StopInternal();
    }

    private void StopInternal()
    {
        if (_player is { IsPlaying: true })
            _player.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
    }

    public void SetPosition(float fraction)
    {
        var p = _player;
        if (p is null || p.Length <= 0) return;
        p.Time = (long)(fraction * p.Length);
    }

    public void SeekBy(TimeSpan delta)
    {
        var p = _player;
        if (p is null || p.Length <= 0) return;
        var target = Math.Clamp(p.Time + (long)delta.TotalMilliseconds, 0, p.Length);
        p.Time = target;
    }

    public void SetVolumePercent(int volume)
    {
        _volumePercent = Math.Clamp(volume, 0, 100);
        if (_volumePercent > 0) _muted = false;
        ApplyVolumeToPlayer();
    }
    public int GetVolume() => _muted ? 0 : _volumePercent;

    public void SetMute(bool mute)
    {
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
    private int _lastNonMutedVolume = 100;

    private void ApplyVolumeToPlayer()
    {
        var p = _player;
        if (p is null) return;
        try { p.Volume = _muted ? 0 : _volumePercent; } catch { }
    }

    public bool IsMuted() => _muted;

    public LibVLCSharp.Shared.MediaPlayer? GetNativePlayer() => _player;

    public LibVLCSharp.Shared.MediaPlayer? CreateThumbnailPlayer()
    {
        try { EnsureInstance(); } catch { return null; }
        return new MediaPlayer(_libVlc!);
    }

    public LibVLCSharp.Shared.LibVLC? GetLibVlc() => _libVlc;

    public async Task<TimeSpan> GetDurationAsync(string filePath)
    {
        EnsureInstance();
        var libVlc = _libVlc!;
        var media = new Media(libVlc, new Uri(filePath));
        try
        {
            var status = await media.Parse(MediaParseOptions.ParseLocal);
            if (status != MediaParsedStatus.Done) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(media.Duration);
        }
        finally
        {
            media.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopInternal();
            _player?.Dispose();
            _player = null;
            _libVlc?.Dispose();
            _libVlc = null;
        }
    }
}
