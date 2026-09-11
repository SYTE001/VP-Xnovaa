using System;
using System.Collections.Generic;
using System.Linq;
using Xnovaa.App.Models;

namespace Xnovaa.App.Services;

public interface IPlaylistService
{
    IReadOnlyList<Playlist> Playlists { get; }
    Playlist DefaultPlaylist { get; }
    Playlist? ActivePlaylist { get; set; }
    void Load();
    void Save();
    Playlist Create(string name);
    void AddVideo(Playlist playlist, Guid videoId);
    void RemoveVideo(Playlist playlist, Guid videoId);
    bool Remove(Guid playlistId);
}

public class PlaylistService : IPlaylistService
{
    private readonly List<Playlist> _playlists = new();

    public IReadOnlyList<Playlist> Playlists => _playlists;

    public Playlist DefaultPlaylist { get; private set; } = new()
    {
        Name = "Now Playing"
    };

    public Playlist? ActivePlaylist { get; set; }

    public void Load()
    {
        _playlists.Clear();
        _playlists.AddRange(AppPaths.Load<List<Playlist>>(AppPaths.PlaylistsFile));

        DefaultPlaylist = _playlists.FirstOrDefault(p => p.Name == "Now Playing")
                          ?? new Playlist { Name = "Now Playing" };
        if (!_playlists.Contains(DefaultPlaylist))
            _playlists.Insert(0, DefaultPlaylist);

        ActivePlaylist ??= DefaultPlaylist;
    }

    public void Save()
    {
        AppPaths.Save(AppPaths.PlaylistsFile, _playlists);
    }

    public Playlist Create(string name)
    {
        var pl = new Playlist { Name = string.IsNullOrWhiteSpace(name) ? $"Playlist {_playlists.Count + 1}" : name };
        _playlists.Add(pl);
        return pl;
    }

    public void AddVideo(Playlist playlist, Guid videoId)
    {
        if (!playlist.VideoIds.Contains(videoId))
            playlist.VideoIds.Add(videoId);
    }

    public void RemoveVideo(Playlist playlist, Guid videoId)
    {
        playlist.VideoIds.Remove(videoId);
    }

    public bool Remove(Guid playlistId)
    {
        var pl = _playlists.FirstOrDefault(p => p.Id == playlistId);
        if (pl is null || ReferenceEquals(pl, DefaultPlaylist)) return false;
        _playlists.Remove(pl);
        if (ReferenceEquals(ActivePlaylist, pl)) ActivePlaylist = DefaultPlaylist;
        return true;
    }
}
