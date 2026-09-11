using System;

namespace Xnovaa.App.Models;

/// <summary>Persisted window position/size.</summary>
public class WindowStateInfo
{
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 800;
    public bool IsMaximized { get; set; }
}
