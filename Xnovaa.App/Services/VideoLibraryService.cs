using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xnovaa.App.Models;

namespace Xnovaa.App.Services;

public interface IVideoLibraryService
{
    IReadOnlyList<VideoItem> Videos { get; }
    VideoItem? FindByPath(string filePath);
    VideoItem? FindById(Guid id);
    void Load();
    void Save();
    /// <summary>Adds a video if not already present (dedupe by FilePath). Returns null when duplicate.</summary>
    VideoItem? Add(string filePath);
    bool Remove(Guid id);
    void Update(VideoItem video);
    /// <summary>Scans a folder recursively for video files; adds all new ones. Returns count added.</summary>
    Task<int> AddFolderAsync(string folder, IProgress<string>? progress = null, CancellationToken ct = default);
}

public class VideoLibraryService : IVideoLibraryService
{
    public static readonly string[] VideoExtensions =
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv" };

    private readonly List<VideoItem> _videos = new();
    private readonly object _gate = new();

    public IReadOnlyList<VideoItem> Videos
    {
        get { lock (_gate) return _videos.ToList(); }
    }

    public void Load()
    {
        lock (_gate)
        {
            _videos.Clear();
            _videos.AddRange(AppPaths.Load<List<VideoItem>>(AppPaths.LibraryFile));
        }
    }

    public void Save()
    {
        List<VideoItem> snapshot;
        lock (_gate) snapshot = _videos.ToList();
        AppPaths.Save(AppPaths.LibraryFile, snapshot);
    }

    public VideoItem? FindByPath(string filePath)
    {
        var norm = Normalize(filePath);
        lock (_gate) return _videos.FirstOrDefault(v =>
            string.Equals(v.FilePath, norm, StringComparison.OrdinalIgnoreCase));
    }

    public VideoItem? FindById(Guid id)
    {
        lock (_gate) return _videos.FirstOrDefault(v => v.Id == id);
    }

    public VideoItem? Add(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        if (FindByPath(filePath) is not null) return null;

        var item = new VideoItem
        {
            FilePath = Normalize(filePath),
            FileName = Path.GetFileNameWithoutExtension(filePath),
            DateAdded = DateTime.UtcNow,
            FileSizeBytes = new FileInfo(filePath).Length
        };
        lock (_gate) _videos.Add(item);
        return item;
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var v = _videos.FirstOrDefault(x => x.Id == id);
            if (v is null) return false;
            _videos.Remove(v);
            return true;
        }
    }

    public void Update(VideoItem video)
    {
        // VideoItem instances are shared references; nothing else to copy.
    }

    public async Task<int> AddFolderAsync(string folder, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int added = 0;
        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (Add(file) is not null) added++;
            progress?.Report($"{added} new of {files.Count}");
            // yield periodically so UI stays responsive
            if (added % 25 == 0) await Task.Delay(1, ct);
        }
        return added;
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
