using System;

namespace Xnovaa.App.Models;

/// <summary>Persisted application state (state.json).</summary>
public class AppState
{
    public Guid? LastPlaylistId { get; set; }
    public Guid? LastVideoId { get; set; }
    public double LastVolume { get; set; } = 1.0;           // 0.0-1.0
    public bool IsMuted { get; set; }
    public bool UseHardwareAcceleration { get; set; } = true;
    public double SeekStepSeconds { get; set; } = 10;
    public WindowStateInfo WindowState { get; set; } = new();
}
