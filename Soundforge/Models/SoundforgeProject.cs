namespace Soundforge.Models;

public sealed class SoundforgeProject
{
    public int FormatVersion { get; set; } = 4;
    public string Name { get; set; } = "New Project";
    public double MasterVolume { get; set; } = 0.8;
    public string? PreferredOutputDeviceId { get; set; }
    public List<Scene> Scenes { get; set; } = new();
}
