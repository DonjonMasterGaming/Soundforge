namespace Soundforge.Models;

public sealed class Scene
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool UseCrossfade { get; set; } = true;
    public double FadeInSeconds { get; set; } = 1.5;
    public double FadeOutSeconds { get; set; } = 1.5;
    public List<Layer> Layers { get; set; } = new();
}
