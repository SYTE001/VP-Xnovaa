using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Xnovaa.App.Models;

/// <summary>A video file registered in the library.</summary>
public class VideoItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;   // full path
    public string FileName { get; set; } = string.Empty;   // display name
    public TimeSpan Duration { get; set; }
    public string? ThumbnailPath { get; set; }             // local cache path, nullable
    public bool IsFavorite { get; set; }
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public long FileSizeBytes { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DurationLabel => Duration == TimeSpan.Zero
        ? "--:--"
        : Duration.TotalHours >= 1
            ? $"{(int)Duration.TotalHours:00}:{Duration.Minutes:00}:{Duration.Seconds:00}"
            : $"{Duration.Minutes:00}:{Duration.Seconds:00}";

    private bool _isPlayingInList;

    /// <summary>UI-only: this item is the one loaded in the player.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCurrentItem { get; set; }

    /// <summary>UI-only state for the play/pause icon on the playing item (Plan §4.4).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPlayingInList
    {
        get => _isPlayingInList;
        set
        {
            if (_isPlayingInList == value) return;
            _isPlayingInList = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(ShowStatusGlyph));
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusGlyph => _isPlayingInList ? "⏸" : "▶";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowStatusGlyph => IsCurrentItem || _isPlayingInList;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
