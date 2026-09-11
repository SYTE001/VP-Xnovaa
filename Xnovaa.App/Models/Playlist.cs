using System;
using System.Collections.Generic;

namespace Xnovaa.App.Models;

/// <summary>An ordered playlist referencing videos by Id.</summary>
public class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Now Playing";
    public List<Guid> VideoIds { get; set; } = new();
}
