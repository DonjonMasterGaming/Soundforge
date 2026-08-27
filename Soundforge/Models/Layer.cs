namespace Soundforge.Models;

/// <summary>
/// A named mixer layer within a scene, such as Music, Ambience, or Effects.
/// Layers provide the control boundary used by future scene transitions.
/// </summary>
public sealed class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public LayerPlaybackBehavior PlaybackBehavior { get; set; } = LayerPlaybackBehavior.Manual;
    public double Volume { get; set; } = 1.0;
    public bool IsMuted { get; set; }
    public Guid? ActivePlaylistId { get; set; }
    public List<Playlist> Playlists { get; set; } = new();
}

public enum LayerPlaybackBehavior
{
    Manual,
    Music,
    Ambience,
    Effects
}
