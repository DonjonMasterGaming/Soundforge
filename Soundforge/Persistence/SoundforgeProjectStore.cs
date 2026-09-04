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
    private const string CacheIndexFileName = ".soundforge-cache.json";
    private static readonly uint[] CrcTable = CreateCrcTable();

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
        var exportedProject = CreateSnapshot(project);
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

    public static SoundforgeProject CreateSnapshot(SoundforgeProject project) =>
        JsonSerializer.Deserialize<SoundforgeProject>(SerializeRecovery(project), SerializerOptions)
        ?? throw new InvalidDataException("Unable to prepare the Soundforge project for export.");

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
        var cacheIndexPath = Path.Combine(importFolder, CacheIndexFileName);
        var cacheIndex = ReadCacheIndex(cacheIndexPath);
        var updatedIndex = new CacheIndex();

        foreach (var item in portableTracks)
        {
            var track = item.Track;
            var source = item.Entry;
            var importedPath = Path.Combine(importFolder, Path.GetFileName(source.FullName));
            var cacheKey = Path.GetFileName(source.FullName);
            try
            {
                if (IsCachedSourceValid(importedPath, source, cacheIndex.Files.GetValueOrDefault(cacheKey),
                        progress, track.Name, completedBytes, totalBytes, completedItems, portableTracks.Count))
                {
                    updatedIndex.Files[cacheKey] = CachedSource.From(importedPath, source);
                    completedBytes += source.Length;
                    completedItems++;
                    track.FilePath = importedPath;
                    continue;
                }

                progress?.Report(new ProjectStoreProgress(
                    "Extracting audio files…",
                    track.Name,
                    completedBytes,
                    totalBytes,
                    completedItems,
                    portableTracks.Count));
                var temporaryImportPath = $"{importedPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    using (var input = source.Open())
                    using (var output = new FileStream(temporaryImportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        CopyWithProgress(input, output, bytesCopied =>
                        {
                            progress?.Report(new ProjectStoreProgress(
                                "Extracting audio files…",
                                track.Name,
                                completedBytes + bytesCopied,
                                totalBytes,
                                completedItems,
                                portableTracks.Count));
                        }, source.Crc32, source.Length);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(temporaryImportPath, importedPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryImportPath))
                        File.Delete(temporaryImportPath);
                }
                updatedIndex.Files[cacheKey] = CachedSource.From(importedPath, source);
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

        WriteCacheIndex(cacheIndexPath, updatedIndex);

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

    private static void CopyWithProgress(Stream input, Stream output, Action<long> report,
        uint? expectedCrc = null, long? expectedLength = null)
    {
        var buffer = new byte[4 * 1024 * 1024];
        long copied = 0;
        uint crc = uint.MaxValue;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (expectedCrc.HasValue)
                crc = UpdateCrc(crc, buffer, read);
            copied += read;
            report(copied);
        }
        if ((expectedCrc.HasValue && ~crc != expectedCrc.Value) ||
            (expectedLength.HasValue && copied != expectedLength.Value))
            throw new InvalidDataException("The bundled audio failed its ZIP integrity check.");
    }

    private static bool IsCachedSourceValid(
        string path, ZipArchiveEntry source, CachedSource? record,
        IProgress<ProjectStoreProgress>? progress, string trackName,
        long completedBytes, long totalBytes, int completedItems, int totalItems)
    {
        if (!File.Exists(path))
            return false;
        var info = new FileInfo(path);
        if (info.Length != source.Length)
            return false;
        if (record is not null && record.SourceLength == source.Length && record.SourceCrc32 == source.Crc32 &&
            record.CachedLength == info.Length && record.CachedLastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
        {
            progress?.Report(new ProjectStoreProgress("Using verified cached audio…", trackName,
                completedBytes + source.Length, totalBytes, completedItems + 1, totalItems));
            return true;
        }

        progress?.Report(new ProjectStoreProgress("Verifying cached audio…", trackName,
            completedBytes, totalBytes, completedItems, totalItems));
        using var stream = File.OpenRead(path);
        var crc = ComputeCrc32(stream, bytesRead => progress?.Report(new ProjectStoreProgress(
            "Verifying cached audio…", trackName, completedBytes + bytesRead,
            totalBytes, completedItems, totalItems)));
        return crc == source.Crc32;
    }

    private static uint ComputeCrc32(Stream stream, Action<long> report)
    {
        uint crc = uint.MaxValue;
        var buffer = new byte[4 * 1024 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            crc = UpdateCrc(crc, buffer, read);
            total += read;
            report(total);
        }
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte[] buffer, int count)
    {
        for (var index = 0; index < count; index++)
            crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[index]) & 0xFF];
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ (0xEDB88320u & (uint)-(int)(value & 1));
            table[index] = value;
        }
        return table;
    }

    private static CacheIndex ReadCacheIndex(string path)
    {
        try
        {
            var index = File.Exists(path)
                ? JsonSerializer.Deserialize<CacheIndex>(File.ReadAllText(path), SerializerOptions)
                : null;
            return index is { Version: 1, Files: not null } ? index : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    private static void WriteCacheIndex(string path, CacheIndex index)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(index, SerializerOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed class CacheIndex
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, CachedSource> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CachedSource(long SourceLength, uint SourceCrc32, long CachedLength, long CachedLastWriteUtcTicks)
    {
        public static CachedSource From(string path, ZipArchiveEntry source)
        {
            var info = new FileInfo(path);
            return new(source.Length, source.Crc32, info.Length, info.LastWriteTimeUtc.Ticks);
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
