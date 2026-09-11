using System;
using System.IO;

namespace Xnovaa.App.Services;

public interface IStorageInfoService
{
    /// <summary>Free space in bytes for the drive of the given path (default C:).</summary>
    (double FreeGB, double TotalGB, double UsedPercent) GetDriveInfo(string? path = null);
}

public class StorageInfoService : IStorageInfoService
{
    public (double FreeGB, double TotalGB, double UsedPercent) GetDriveInfo(string? path = null)
    {
        try
        {
            var root = Path.GetPathRoot(string.IsNullOrWhiteSpace(path)
                ? AppContext.BaseDirectory
                : path) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return (0, 0, 0);
            return (drive.AvailableFreeSpace / 1073741824.0,
                    drive.TotalSize / 1073741824.0,
                    100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
