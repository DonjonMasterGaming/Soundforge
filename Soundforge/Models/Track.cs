namespace Soundforge.Models;

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LayerId { get; set; }
    public bool AutoPlayOnSceneActivation { get; set; } = true;
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public AudioSourceKind SourceKind { get; set; } = AudioSourceKind.LocalFile;
    public string? SourceUri { get; set; }
    public string? SourceProviderId { get; set; }
    public string? SourceETag { get; set; }
    public DateTimeOffset? SourceModifiedUtc { get; set; }
    public DateTimeOffset? CachedAtUtc { get; set; }
    public double Volume { get; set; } = 1.0;
    public bool Loop { get; set; } = true;
    public double TrimStartSeconds { get; set; }
    public double? TrimEndSeconds { get; set; }
}

public enum AudioSourceKind
{
    LocalFile,
    HttpUrl,
    GoogleDrive
}
