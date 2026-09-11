using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xnovaa.App.Models;
using Xnovaa.App.Services;

namespace Xnovaa.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IVideoLibraryService _library;
    private readonly IPlaylistService _playlists;
    private readonly IPlaybackService _playback;
    private readonly IAppStateService _appState;
    private readonly IStorageInfoService _storage;
    private readonly IThumbnailService _thumbnails;

    private ILogger _logger = NullLogger.Instance;

    public ObservableCollection<VideoItem> PlaylistVideos { get; } = new();
    public ObservableCollection<VideoItem> MainList { get; } = new();

    public ICollectionView MainListView { get; private set; } = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private NavigationSection _currentSection = NavigationSection.Home;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _currentTitle = "No video selected";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _position;   // 0..1

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private string _currentTimeText = "00:00";

    [ObservableProperty]
    private string _totalTimeText = "00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent))]
    private double _volume = 1.0;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private string _playlistHeader = "0 videos • 00:00";

    [ObservableProperty]
    private string _storageText = "—";

    [ObservableProperty]
    private double _storageUsedPercent;

    [ObservableProperty]
    private VideoItem? _selectedItem;

    [ObservableProperty]
    private bool _isFullscreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightPanelItems))]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private bool _isNowPlayingTab;

    /// <summary>Items shown in the right panel: queue (Now Playing) or raw playlist order.</summary>
    public System.Collections.IEnumerable RightPanelItems
    {
        get
        {
            if (!IsNowPlayingTab) return PlaylistVideos;
            var cur = _playback.CurrentItem;
            if (cur is null || PlaylistVideos.Count == 0) return PlaylistVideos;
            var idx = PlaylistVideos.IndexOf(cur);
            if (idx <= 0) return PlaylistVideos;
            // references only — no model duplication (Plan §9.4)
            return PlaylistVideos.Skip(idx).Concat(PlaylistVideos.Take(idx)).ToList();
        }
    }

    partial void OnSelectedItemChanged(VideoItem? value) => OnPropertyChanged(nameof(RightPanelItems));

    public string EmptyStateText => IsNowPlayingTab
        ? "Playlist is empty — import videos or click + to add."
        : "No videos in this view yet.";

    public string HeaderText => CurrentSection switch
    {
        NavigationSection.Library => "Library",
        NavigationSection.Playlist => "Playlist",
        NavigationSection.RecentlyAdded => "Recently Added",
        NavigationSection.Favorites => "Favorites",
        NavigationSection.Home => "Home",
        _ => "Xnovaa"
    };

    public string PlayIcon => IsPlaying ? "⏸" : "▶";

    public string VolumePercent => $"{(int)Math.Round(Volume * 100)}%";

    /// <summary>Raised when the view must perform the actual fullscreen toggle.</summary>
    public event EventHandler? FullscreenRequested;

    [RelayCommand]
    private void ToggleFullscreen() => FullscreenRequested?.Invoke(this, EventArgs.Empty);

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayIcon));
    partial void OnVolumeChanged(double value) => OnPropertyChanged(nameof(VolumePercent));

    public MainViewModel(
        IVideoLibraryService library,
        IPlaylistService playlists,
        IPlaybackService playback,
        IAppStateService appState,
        IStorageInfoService storage,
        IThumbnailService thumbnails)
    {
        _library = library;
        _playlists = playlists;
        _playback = playback;
        _appState = appState;
        _storage = storage;
        _thumbnails = thumbnails;

        WirePlaybackEvents();
        AttachView(MainList);
    }

    public void SetLogger(ILogger logger) => _logger = logger;

    private void AttachView(ObservableCollection<VideoItem> collection)
    {
        MainListView = CollectionViewSource.GetDefaultView(collection);
        MainListView.Filter = FilterItem;
    }

    private bool FilterItem(object obj)
    {
        if (obj is not VideoItem v) return false;

        // Section filter
        switch (CurrentSection)
        {
            case NavigationSection.Favorites:
                if (!v.IsFavorite) return false;
                break;
            case NavigationSection.RecentlyAdded:
                if (_recentIds is not null && !_recentIds.Contains(v.Id)) return false;
                break;
            case NavigationSection.Playlist:
                if (_playlistIds is not null && !_playlistIds.Contains(v.Id)) return false;
                break;
        }

        // Search filter (by file name)
        if (!string.IsNullOrWhiteSpace(SearchText) &&
            (v.FileName?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0))
            return false;

        return true;
    }

    private HashSet<Guid>? _recentIds;
    private HashSet<Guid>? _playlistIds;

    /// <summary>True when the center area should show the browse grid instead of the player.</summary>
    public bool ShowLibraryBrowser => CurrentSection is NavigationSection.Library
        or NavigationSection.Playlist or NavigationSection.Favorites or NavigationSection.RecentlyAdded;

    partial void OnSearchTextChanged(string value) => MainListView?.Refresh();
    partial void OnCurrentSectionChanged(NavigationSection value)
    {
        // Recently Added = DateAdded desc, capped at 50 (Plan §4.5) — computed as a
        // filter set over the single shared list, no duplicate collection.
        if (value == NavigationSection.RecentlyAdded)
        {
            _recentIds = new HashSet<Guid>(
                MainList.OrderByDescending(v => v.DateAdded).Take(50).Select(v => v.Id));
        }
        else
        {
            _recentIds = null;
        }
        if (value == NavigationSection.Playlist)
        {
            var pl = _playlists.ActivePlaylist ?? _playlists.DefaultPlaylist;
            _playlistIds = new HashSet<Guid>(pl.VideoIds);
        }
        else
        {
            _playlistIds = null;
        }
        MainListView?.Refresh();
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ShowLibraryBrowser));
    }

    // ------------------------------------------------------------------ startup

    public void InitializeFromState()
    {
        var state = _appState.State;

        foreach (var v in _library.Videos)
            MainList.Add(v);

        RebuildPlaylistFromActive();

        Volume = state.LastVolume;
        IsMuted = state.IsMuted;
        _playback.SetVolumePercent((int)Math.Round(state.LastVolume * 100));
        _playback.SetMute(state.IsMuted);

        RefreshStorageInfo();

        if (state.LastVideoId is { } id)
        {
            var last = ById(id);
            if (last is not null && File.Exists(last.FilePath))
            {
                SelectedItem = last;
                CurrentTitle = last.FileName;
            }
        }
    }

    public void RebuildPlaylistFromActive()
    {
        PlaylistVideos.Clear();
        var pl = _playlists.ActivePlaylist ?? _playlists.DefaultPlaylist;
        foreach (var id in pl.VideoIds)
        {
            var v = ById(id);
            if (v is not null) PlaylistVideos.Add(v);
        }
        UpdatePlaylistHeader();
        OnPropertyChanged(nameof(RightPanelItems));
    }

    /// <summary>Returns the single shared instance for a video Id (MainList first, then library).</summary>
    private VideoItem? ById(Guid id)
        => MainList.FirstOrDefault(v => v.Id == id) ?? _library.FindById(id);

    private void UpdatePlaylistHeader()
    {
        var total = TimeSpan.Zero;
        foreach (var v in PlaylistVideos) total += v.Duration;
        PlaylistHeader = $"{PlaylistVideos.Count} videos • {(int)total.TotalHours:00}:{total.Minutes:00}:{total.Seconds:00}";
        OnPropertyChanged(nameof(RightPanelItems));
    }

    public void RefreshStorageInfo()
    {
        var (free, total, pct) = _storage.GetDriveInfo();
        StorageText = $"{free:F1} GB free of {total:F0} GB";
        StorageUsedPercent = pct;
    }

    // ------------------------------------------------------------------ playback events

    private void WirePlaybackEvents()
    {
        _playback.PositionChanged += OnPlaybackPosition;
        _playback.TimeChanged += OnPlaybackTime;
        _playback.DurationChanged += OnPlaybackDuration;
        _playback.MediaEnded += OnMediaEnded;
        _playback.MediaStarted += OnMediaStarted;
        _playback.PauseStateChanged += OnPauseStateChanged;
    }

    private void OnPlaybackPosition(object? s, float pos) => ExecuteOnUi(() =>
    {
        if (!_isSeeking) Position = pos;
    });

    private void OnPlaybackTime(object? s, long ms) => ExecuteOnUi(() =>
    {
        CurrentTimeText = FormatTime(ms);
    });

    private void OnPlaybackDuration(object? s, long ms) => ExecuteOnUi(() =>
    {
        DurationSeconds = ms / 1000.0;
        TotalTimeText = FormatTime(ms);
    });

    private void OnMediaStarted(object? s, EventArgs e) => ExecuteOnUi(() =>
    {
        IsPlaying = true;
        RefreshPlayingGlyphs();
        if (_playback.CurrentItem is { } cur)
            CurrentTitle = cur.FileName;
    });

    private void OnPauseStateChanged(object? s, EventArgs e) => ExecuteOnUi(() =>
    {
        IsPlaying = _playback.IsPlaying;
        RefreshPlayingGlyphs();
    });

    private void OnMediaEnded(object? s, EventArgs e) => ExecuteOnUi(async () =>
    {
        IsPlaying = false;
        Position = 1.0;
        RefreshPlayingGlyphs();
        await PlayNextAsync();
    });

    /// <summary>Sets the play/pause glyph flag on exactly the playing item (Plan §4.4).</summary>
    private void RefreshPlayingGlyphs()
    {
        var cur = _playback.CurrentItem;
        foreach (var v in PlaylistVideos)
        {
            v.IsCurrentItem = ReferenceEquals(v, cur);
            v.IsPlayingInList = ReferenceEquals(v, cur) && IsPlaying;
        }
    }

    private static void ExecuteOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void Navigate(string section)
    {
        if (!Enum.TryParse<NavigationSection>(section, out var sec)) return;

        switch (sec)
        {
            case NavigationSection.ImportFiles:
                ImportFilesCommand.Execute(null);
                return;
            case NavigationSection.Folders:
                ImportFolderCommand.Execute(null);
                return;
        }

        CurrentSection = sec;
    }

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import video files",
            Multiselect = true,
            Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv|All files|*.*"
        };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;

        await ImportPathsAsync(dlg.FileNames);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var folder = PickFolder();
        if (folder is null) return;

        await ImportPathsAsync(Array.Empty<string>(), folder);
    }

    private static string? PickFolder()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            ShowNewFolderButton = false,
            Description = "Choose a folder to scan for videos"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            return dlg.SelectedPath;
        return null;
    }

    private async Task ImportPathsAsync(string[] files, string? folder = null)
    {
        int added = 0;
        var pl = _playlists.ActivePlaylist ?? _playlists.DefaultPlaylist;

        if (folder is not null)
        {
            var progress = new Progress<string>(_ => { });
            added = await _library.AddFolderAsync(folder, progress);
        }
        else
        {
            foreach (var f in files)
            {
                var item = _library.Add(f);
                if (item is null) continue;
                added++;
            }
        }

        if (added == 0)
        {
            RefreshMainList();
            return;
        }

        // Only the items added by this import go to "Now Playing" (Plan §8 step 2).
        var knownBefore = new HashSet<Guid>(MainList.Select(x => x.Id));
        var justAdded = _library.Videos.Where(v => !knownBefore.Contains(v.Id)).ToList();

        // Read durations in background (bounded), then finish on UI thread.
        await Task.Run(async () =>
        {
            foreach (var v in justAdded)
            {
                try
                {
                    v.Duration = await _playback.GetDurationAsync(v.FilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read duration for {File}", v.FilePath);
                }
            }
        });

        ExecuteOnUi(() =>
        {
            foreach (var v in justAdded)
            {
                MainList.Add(v);
                _playlists.AddVideo(pl, v.Id);
            }

            RebuildPlaylistFromActive();
            MainListView.Refresh();
            UpdatePlaylistHeader();
            _library.Save();
            _playlists.Save();
        });
    }

    private void RefreshMainList()
    {
        // sync MainList with library without duplicating memory
        MainList.Clear();
        foreach (var v in _library.Videos)
            MainList.Add(v);
        MainListView.Refresh();
        UpdatePlaylistHeader();
    }

    [RelayCommand]
    private async Task PlayItemAsync(VideoItem? item)
    {
        if (item is null || !File.Exists(item.FilePath)) return;

        SelectedItem = item;
        _playback.Play(item);
        CurrentTitle = item.FileName;

        var appState = _appState.State;
        appState.LastVideoId = item.Id;
        _appState.Save();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (_playback.CurrentItem is null)
        {
            // nothing loaded yet: play selected or first in playlist
            var first = SelectedItem ?? PlaylistVideos.FirstOrDefault();
            if (first is not null) PlayItemCommand.Execute(first);
            return;
        }
        _playback.TogglePlayPause();
        // state is confirmed by MediaStarted/StateChanged via UI polling fallback
        IsPlaying = _playback.IsPlaying;
    }

    [RelayCommand]
    private async Task PlayNextAsync()
    {
        var list = PlaylistVideos;
        if (list.Count == 0) return;
        var cur = _playback.CurrentItem;
        var idx = cur is null ? -1 : list.IndexOf(cur);

        if (idx == -1)
        {
            await PlayItemAsync(list[0]);
            return;
        }
        if (idx < list.Count - 1)
        {
            await PlayItemAsync(list[idx + 1]);
            return;
        }

        // End of playlist: stop.
        _playback.Stop();
        IsPlaying = false;
    }

    [RelayCommand]
    private async Task PlayPreviousAsync()
    {
        var list = PlaylistVideos;
        if (list.Count == 0) return;
        var cur = _playback.CurrentItem;
        var idx = cur is null ? -1 : list.IndexOf(cur);
        if (idx > 0)
            await PlayItemAsync(list[idx - 1]);
        else
        {
            // restart current
            if (cur is not null) await PlayItemAsync(cur);
        }
    }

    [RelayCommand]
    private void SeekBySeconds(string param)
    {
        var seconds = double.Parse(param);
        _playback.SeekBy(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>± buttons / Ctrl+arrows: use the configured seek step (Plan §4.6).</summary>
    [RelayCommand]
    private void SeekStep(string sign)
    {
        var step = _appState.State.SeekStepSeconds > 0 ? _appState.State.SeekStepSeconds : 10;
        _playback.SeekBy(TimeSpan.FromSeconds(sign == "-" ? -step : step));
    }

    [RelayCommand]
    private void SetVolumeFromSlider()
    {
        _playback.SetVolumePercent((int)Math.Round(Volume * 100));
        IsMuted = Volume <= 0.001;
        var st = _appState.State;
        st.LastVolume = Volume;
        st.IsMuted = IsMuted;
        _appState.Save();
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        _playback.SetMute(IsMuted);
        _appState.State.IsMuted = IsMuted;
        _appState.Save();
    }

    [RelayCommand]
    private void ToggleFavorite(VideoItem? item)
    {
        if (item is null) return;
        item.IsFavorite = !item.IsFavorite;
        _library.Save();
        MainListView.Refresh();
    }

    [RelayCommand]
    private void RevealInExplorer(VideoItem? item)
    {
        if (item is null || !File.Exists(item.FilePath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"")
        {
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void RemoveFromPlaylist(VideoItem? item)
    {
        if (item is null) return;
        var pl = _playlists.ActivePlaylist ?? _playlists.DefaultPlaylist;
        _playlists.RemoveVideo(pl, item.Id);
        PlaylistVideos.Remove(item);
        UpdatePlaylistHeader();
        _playlists.Save();
    }

    [RelayCommand]
    private void RemoveSelectedFromPlaylist()
    {
        RemoveFromPlaylist(SelectedItem);
    }

    [RelayCommand]
    private void DeleteFromLibrary(VideoItem? item)
    {
        if (item is null) return;
        RemoveFromPlaylist(item);
        _library.Remove(item.Id);
        MainList.Remove(item);
        MainListView.Refresh();
        _library.Save();
    }

    [RelayCommand]
    private void SetTab(string tab) => IsNowPlayingTab = tab == "NowPlaying";

    [RelayCommand]
    private void AddToPlaylist(VideoItem? item)
    {
        if (item is null) return;
        var pl = _playlists.ActivePlaylist ?? _playlists.DefaultPlaylist;
        _playlists.AddVideo(pl, item.LibraryIdForAdd());
        RebuildPlaylistFromActive();
        _playlists.Save();
    }

    [RelayCommand]
    private void RefreshStorage() => RefreshStorageInfo();

    [RelayCommand]
    private void SetVolumeDefault()
    {
        Volume = 1.0;
        SetVolumeFromSlider();
    }

    // seek drag tracking
    private bool _isSeeking;

    public void BeginSeekDrag() => _isSeeking = true;
    public void EndSeekDrag()
    {
        _isSeeking = false;
        _playback.SetPosition((float)Position);
    }

    public void OnWindowClosing()
    {
        var st = _appState.State;
        st.LastVolume = Volume;
        st.IsMuted = IsMuted;
        st.LastPlaylistId = (_playlists.ActivePlaylist ?? _playlists.DefaultPlaylist).Id;
        var sel = SelectedItem;
        st.LastVideoId = sel?.Id;
        _appState.Save();
        _library.Save();
        _playlists.Save();
    }

    public void Dispose()
    {
        _playback.PositionChanged -= OnPlaybackPosition;
        _playback.TimeChanged -= OnPlaybackTime;
        _playback.DurationChanged -= OnPlaybackDuration;
        _playback.MediaEnded -= OnMediaEnded;
        _playback.MediaStarted -= OnMediaStarted;
        _playback.PauseStateChanged -= OnPauseStateChanged;
        _playback.Dispose();
    }
}

internal static class VideoItemExtensions
{
    public static Guid LibraryIdForAdd(this VideoItem item) => item.Id;
}
