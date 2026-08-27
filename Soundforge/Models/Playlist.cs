namespace Soundforge.Models;

public sealed class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Track> Tracks { get; set; } = new();
}
