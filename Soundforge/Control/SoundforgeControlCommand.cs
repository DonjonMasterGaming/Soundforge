namespace Soundforge.Control;

/// <summary>
/// Small, local-only command contract used by companion controls such as Stream Deck.
/// It deliberately contains no network address or authentication because the pipe is
/// available only to applications on this Windows PC.
/// </summary>
public sealed class SoundforgeControlCommand
{
    public string Action { get; set; } = string.Empty;
    public string? SceneName { get; set; }
    public string? TrackName { get; set; }
    public string? PoolName { get; set; }
    public int Ticks { get; set; }
}

public sealed record SoundforgeControlResult(
    bool Success,
    string Message,
    IReadOnlyList<string>? Values = null,
    double? Level = null);
