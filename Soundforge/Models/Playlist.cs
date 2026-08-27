namespace Soundforge.Models;

public sealed class Playlist
{
    public string Name { get; set; } = "";
    public List<Track> Tracks { get; set; } = new();
}
