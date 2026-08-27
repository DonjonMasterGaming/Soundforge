using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soundforge.Models;

namespace Soundforge.Persistence;

public static class SoundforgeProjectStore
{
    private const string ManifestEntryName = "project.json";
    private const string SourceFolderName = "sources";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string path, SoundforgeProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        var exportedProject = JsonSerializer.Deserialize<SoundforgeProject>(
            JsonSerializer.Serialize(project, SerializerOptions), SerializerOptions)
            ?? throw new InvalidDataException("Unable to prepare the Soundforge project for export.");

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (var track in GetTracks(exportedProject))
                {
                    if (!File.Exists(track.FilePath))
                    {
                        throw new FileNotFoundException(
                            $"Soundforge cannot include the source for '{track.Name}' because the file is missing.",
                            track.FilePath);
                    }

                    var extension = Path.GetExtension(track.FilePath);
                    var entryName = $"{SourceFolderName}/{track.Id:N}{extension}";
                    archive.CreateEntryFromFile(track.FilePath, entryName, CompressionLevel.Optimal);
                    track.FilePath = entryName;
                }

                var manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(manifest.Open());
                writer.Write(JsonSerializer.Serialize(exportedProject, SerializerOptions));
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static SoundforgeProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return IsPortableProject(path) ? LoadPortableProject(path) : LoadLegacyProject(path);
    }

    private static SoundforgeProject LoadPortableProject(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifest = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The Soundforge project is missing its project information.");
        using var reader = new StreamReader(manifest.Open());
        var project = JsonSerializer.Deserialize<SoundforgeProject>(reader.ReadToEnd(), SerializerOptions)
            ?? throw new InvalidDataException("The file does not contain a Soundforge project.");

        var importFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soundforge",
            "ImportedSources",
            $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(importFolder);

        foreach (var track in GetTracks(project))
        {
            if (!track.FilePath.StartsWith($"{SourceFolderName}/", StringComparison.OrdinalIgnoreCase))
                continue;

            var source = archive.GetEntry(track.FilePath)
                ?? throw new InvalidDataException($"The Soundforge project is missing the source for '{track.Name}'.");
            var importedPath = Path.Combine(importFolder, Path.GetFileName(source.FullName));
            source.ExtractToFile(importedPath, overwrite: true);
            track.FilePath = importedPath;
        }

        return project ?? throw new InvalidDataException("The file does not contain a Soundforge project.");
    }

    private static SoundforgeProject LoadLegacyProject(string path)
    {
        var project = JsonSerializer.Deserialize<SoundforgeProject>(File.ReadAllText(path), SerializerOptions);
        return project ?? throw new InvalidDataException("The file does not contain a Soundforge project.");
    }

    private static bool IsPortableProject(string path)
    {
        using var file = File.OpenRead(path);
        return file.ReadByte() == 'P' && file.ReadByte() == 'K';
    }

    private static IEnumerable<Track> GetTracks(SoundforgeProject project) =>
        project.Scenes
            .SelectMany(scene => scene.Layers)
            .SelectMany(layer => layer.Playlists)
            .SelectMany(playlist => playlist.Tracks)
            .GroupBy(track => track.Id)
            .Select(group => group.First());
}
