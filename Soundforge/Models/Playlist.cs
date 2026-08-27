namespace Soundforge.Models;

public sealed class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsExpanded { get; set; } = true;
    public List<Guid> AutoActivateWithMusicPlaylistIds { get; set; } = new();
    public List<Track> Tracks { get; set; } = new();
}
