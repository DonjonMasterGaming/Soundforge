namespace Soundforge.Models;

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LayerId { get; set; }
    public bool AutoPlayOnSceneActivation { get; set; } = true;
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public double Volume { get; set; } = 1.0;
    public bool Loop { get; set; } = true;
}
