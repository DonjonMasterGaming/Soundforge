using System.IO;
using System.Text.Json;

namespace Soundforge.Settings;

public sealed class AppSettings
{
    public string? OutputDeviceId { get; set; }
    public bool AutosaveEnabled { get; set; } = true;
    public int AutosaveIntervalMinutes { get; set; } = 2;
    public string? AutosaveFolder { get; set; }
    public bool LastShutdownClean { get; set; } = true;
}

/// <summary>
/// Stores machine-specific preferences outside portable Soundforge projects.
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Soundforge",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // Playback remains available if preferences cannot be written.
        }
        catch (UnauthorizedAccessException)
        {
            // Playback remains available if preferences cannot be written.
        }
    }
}
