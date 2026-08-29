using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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

    public static void Save(
        string path,
        SoundforgeProject project,
        IProgress<ProjectStoreProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);

        progress?.Report(ProjectStoreProgress.Indeterminate("Preparing project…"));
        var exportedProject = JsonSerializer.Deserialize<SoundforgeProject>(
            JsonSerializer.Serialize(project, SerializerOptions), SerializerOptions)
            ?? throw new InvalidDataException("Unable to prepare the Soundforge project for export.");
        var tracks = GetTracks(exportedProject).ToList();
        var totalBytes = tracks
            .Where(track => File.Exists(track.FilePath))
            .Sum(track => new FileInfo(track.FilePath).Length);
        long completedBytes = 0;
        var completedItems = 0;

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (var track in tracks)
                {
                    if (!File.Exists(track.FilePath))
                    {
                        throw new FileNotFoundException(
                            $"Soundforge cannot include the source for '{track.Name}' because the file is missing.",
                            track.FilePath);
                    }

                    var extension = Path.GetExtension(track.FilePath);
                    var entryName = $"{SourceFolderName}/{track.Id:N}{extension}";
                    progress?.Report(new ProjectStoreProgress(
                        "Adding audio files…",
                        track.Name,
                        completedBytes,
                        totalBytes,
                        completedItems,
                        tracks.Count));

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (var input = File.OpenRead(track.FilePath))
                    using (var output = entry.Open())
                    {
                        CopyWithProgress(input, output, bytesCopied =>
                        {
                            progress?.Report(new ProjectStoreProgress(
                                "Adding audio files…",
                                track.Name,
                                completedBytes + bytesCopied,
                                totalBytes,
                                completedItems,
                                tracks.Count));
                        });
                        completedBytes += input.Length;
                    }

                    completedItems++;
                    track.FilePath = entryName;
                }

                progress?.Report(new ProjectStoreProgress(
                    "Finalizing project…",
                    null,
                    totalBytes,
                    totalBytes,
                    tracks.Count,
                    tracks.Count));
                var manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(manifest.Open());
                writer.Write(JsonSerializer.Serialize(exportedProject, SerializerOptions));
            }

            File.Move(temporaryPath, path, overwrite: true);
            progress?.Report(ProjectStoreProgress.Complete("Project saved."));
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static SoundforgeProject Load(
        string path,
        IProgress<ProjectStoreProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        progress?.Report(ProjectStoreProgress.Indeterminate("Reading project…"));
        var project = IsPortableProject(path)
            ? LoadPortableProject(path, progress)
            : LoadLegacyProject(path);
        progress?.Report(ProjectStoreProgress.Complete("Project loaded."));
        return project;
    }

    public static string SerializeRecovery(SoundforgeProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, SerializerOptions);
    }

    public static void WriteRecovery(string path, string projectJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(projectJson);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, projectJson);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static SoundforgeProject LoadPortableProject(
        string path,
        IProgress<ProjectStoreProgress>? progress)
    {
        using var archive = ZipFile.OpenRead(path);
        var manifest = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The Soundforge project is missing its project information.");
        using var reader = new StreamReader(manifest.Open());
        var project = JsonSerializer.Deserialize<SoundforgeProject>(reader.ReadToEnd(), SerializerOptions)
            ?? throw new InvalidDataException("The file does not contain a Soundforge project.");

        var importFolder = GetImportFolder(path);
        try
        {
            Directory.CreateDirectory(importFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Soundforge could not create its audio cache at '{importFolder}'.", ex);
        }

        var portableTracks = GetTracks(project)
            .Where(track => track.FilePath.StartsWith($"{SourceFolderName}/", StringComparison.OrdinalIgnoreCase))
            .Select(track => (Track: track, Entry: archive.GetEntry(track.FilePath)
                ?? throw new InvalidDataException($"The Soundforge project is missing the source for '{track.Name}'.")))
            .ToList();
        var totalBytes = portableTracks.Sum(item => item.Entry.Length);
        long completedBytes = 0;
        var completedItems = 0;

        foreach (var item in portableTracks)
        {
            var track = item.Track;
            var source = item.Entry;
            var importedPath = Path.Combine(importFolder, Path.GetFileName(source.FullName));
            try
            {
                progress?.Report(new ProjectStoreProgress(
                    "Extracting audio files…",
                    track.Name,
                    completedBytes,
                    totalBytes,
                    completedItems,
                    portableTracks.Count));
                using var input = source.Open();
                using var output = new FileStream(importedPath, FileMode.Create, FileAccess.Write, FileShare.None);
                CopyWithProgress(input, output, bytesCopied =>
                {
                    progress?.Report(new ProjectStoreProgress(
                        "Extracting audio files…",
                        track.Name,
                        completedBytes + bytesCopied,
                        totalBytes,
                        completedItems,
                        portableTracks.Count));
                });
                completedBytes += source.Length;
                completedItems++;
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Soundforge could not unpack '{track.Name}' into its audio cache at '{importFolder}'.",
                    ex);
            }
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

    private static string GetImportFolder(string projectPath)
    {
        var fullProjectPath = Path.GetFullPath(projectPath);
        var pathHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(fullProjectPath.ToUpperInvariant())))[..12];
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeProjectName = new string(Path.GetFileNameWithoutExtension(projectPath)
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());

        // Keep potentially large bundled audio beside the portable Soundforge build.
        // When Soundforge runs from F:, loading a project no longer consumes C: space.
        return Path.Combine(
            AppContext.BaseDirectory,
            "cache",
            "ImportedSources",
            $"{safeProjectName}-{pathHash}");
    }

    private static IEnumerable<Track> GetTracks(SoundforgeProject project) =>
        project.Scenes
            .SelectMany(scene => scene.Layers)
            .SelectMany(layer => layer.Playlists)
            .SelectMany(playlist => playlist.Tracks)
            .GroupBy(track => track.Id)
            .Select(group => group.First());

    private static void CopyWithProgress(Stream input, Stream output, Action<long> report)
    {
        var buffer = new byte[4 * 1024 * 1024];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            copied += read;
            report(copied);
        }
    }
}

public sealed record ProjectStoreProgress(
    string Stage,
    string? CurrentItem,
    long CompletedBytes,
    long TotalBytes,
    int CompletedItems,
    int TotalItems)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(CompletedBytes * 100d / TotalBytes, 0d, 100d);

    public static ProjectStoreProgress Indeterminate(string stage) =>
        new(stage, null, 0, 0, 0, 0);

    public static ProjectStoreProgress Complete(string stage) =>
        new(stage, null, 1, 1, 1, 1);
}
