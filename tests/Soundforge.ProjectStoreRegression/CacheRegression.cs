using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Soundforge.Models;
using Soundforge.Persistence;

internal static class CacheRegression
{
    public static void Run(string root)
    {
        var audio = Path.Combine(root, "cache-source.wav");
        var projectPath = Path.Combine(root, "verified-cache.soundforge");
        var bytes = Enumerable.Range(0, 65536).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(audio, bytes);
        var track = new Track { FilePath = audio, Name = "Cache source", SourceKind = AudioSourceKind.HttpUrl,
            SourceUri = "https://example.com/cache-source.wav", SourceETag = "fixture", CachedAtUtc = DateTimeOffset.UtcNow };
        var project = new SoundforgeProject
        {
            Scenes = [new Scene { Layers = [new Layer { Playlists = [new Playlist { Tracks = [track] }] }] }]
        };
        var snapshot = SoundforgeProjectStore.CreateSnapshot(project);
        track.Name = "Changed after snapshot";
        if (snapshot.Scenes[0].Layers[0].Playlists[0].Tracks[0].Name != "Cache source")
            throw new Exception("The save snapshot shares mutable source state.");
        SoundforgeProjectStore.Save(projectPath, project);
        var first = SoundforgeProjectStore.Load(projectPath);
        var firstTrack = first.Scenes[0].Layers[0].Playlists[0].Tracks[0];
        if (firstTrack.SourceKind != AudioSourceKind.HttpUrl || firstTrack.SourceUri != track.SourceUri || firstTrack.SourceETag != "fixture")
            throw new Exception("Portable save/load lost cloud source identity.");
        var cachedPath = first.Scenes[0].Layers[0].Playlists[0].Tracks[0].FilePath;
        var cacheDirectory = Path.GetDirectoryName(cachedPath)!;
        var indexPath = Path.Combine(cacheDirectory, ".soundforge-cache.json");
        var timestamp = File.GetLastWriteTimeUtc(cachedPath);
        var stages = new List<string>();
        SoundforgeProjectStore.Load(projectPath, new CaptureProgress(stages));
        if (!stages.Contains("Using verified cached audio…") || stages.Contains("Extracting audio files…") ||
            File.GetLastWriteTimeUtc(cachedPath) != timestamp)
            throw new Exception("Warm load unexpectedly rewrote verified audio.");

        // An old cache without an index is checked against the actual ZIP CRC, not its name.
        File.Delete(indexPath);
        stages.Clear();
        SoundforgeProjectStore.Load(projectPath, new CaptureProgress(stages));
        if (!stages.Contains("Verifying cached audio…") || stages.Contains("Extracting audio files…"))
            throw new Exception("Legacy cache migration failed.");

        File.WriteAllBytes(cachedPath, [1, 2, 3]);
        SoundforgeProjectStore.Load(projectPath);
        if (!File.ReadAllBytes(cachedPath).SequenceEqual(bytes))
            throw new Exception("Truncated cache was not repaired.");
        var damaged = (byte[])bytes.Clone();
        damaged[0] ^= 0xFF;
        File.WriteAllBytes(cachedPath, damaged);
        File.SetLastWriteTimeUtc(cachedPath, DateTime.UtcNow.AddMinutes(-5));
        SoundforgeProjectStore.Load(projectPath);
        if (!File.ReadAllBytes(cachedPath).SequenceEqual(bytes))
            throw new Exception("Same-length changed cache was not repaired.");

        // Same IDs, archive path and source size, but changed audio must not reuse stale data.
        File.WriteAllBytes(audio, damaged);
        SoundforgeProjectStore.Save(projectPath, project);
        SoundforgeProjectStore.Load(projectPath);
        if (!File.ReadAllBytes(cachedPath).SequenceEqual(damaged))
            throw new Exception("Changed archive source incorrectly reused an older cached revision.");

        File.WriteAllText(indexPath, "{broken");
        SoundforgeProjectStore.Load(projectPath);
        File.WriteAllText(indexPath, "{\"Version\":1,\"Files\":null}");
        SoundforgeProjectStore.Load(projectPath);
        File.Delete(cachedPath);
        SoundforgeProjectStore.Load(projectPath);
        if (!File.ReadAllBytes(cachedPath).SequenceEqual(damaged))
            throw new Exception("Missing cache was not restored.");

        // A failed extraction must leave the old cache intact and no temporary file behind.
        File.WriteAllBytes(audio, bytes);
        SoundforgeProjectStore.Save(projectPath, project);
        try
        {
            SoundforgeProjectStore.Load(projectPath, new InterruptExtraction());
            throw new Exception("The extraction interruption did not execute.");
        }
        catch (OperationCanceledException) { }
        if (!File.ReadAllBytes(cachedPath).SequenceEqual(damaged) || Directory.GetFiles(cacheDirectory, "*.tmp").Length != 0)
            throw new Exception("Interrupted extraction damaged the old cache or left temporary files.");

        var previousSave = File.ReadAllBytes(projectPath);
        var corruptArchive = (byte[])previousSave.Clone();
        var centralDirectoryOffset = -1;
        for (var index = 0; index < corruptArchive.Length - 20; index++)
        {
            if (corruptArchive[index] == 0x50 && corruptArchive[index + 1] == 0x4B &&
                corruptArchive[index + 2] == 1 && corruptArchive[index + 3] == 2)
            {
                centralDirectoryOffset = index;
                break;
            }
        }
        if (centralDirectoryOffset < 0)
            throw new Exception("Test ZIP central directory not found.");
        corruptArchive[centralDirectoryOffset + 16] ^= 1; // Corrupt the expected audio CRC.
        File.WriteAllBytes(projectPath, corruptArchive);
        try
        {
            SoundforgeProjectStore.Load(projectPath);
            throw new Exception("A corrupt ZIP checksum unexpectedly passed.");
        }
        catch (InvalidDataException) { }
        catch (IOException ex) when (ex.InnerException is InvalidDataException) { }
        if (!File.ReadAllBytes(cachedPath).SequenceEqual(damaged) || Directory.GetFiles(cacheDirectory, "*.tmp").Length != 0)
            throw new Exception("Bad ZIP checksum replaced the previous cache or left temporary data.");
        File.WriteAllBytes(projectPath, previousSave);
        track.FilePath = Path.Combine(root, "missing-source.wav");
        try
        {
            SoundforgeProjectStore.Save(projectPath, project);
            throw new Exception("Saving a missing source unexpectedly succeeded.");
        }
        catch (FileNotFoundException) { }
        if (!File.ReadAllBytes(projectPath).SequenceEqual(previousSave))
            throw new Exception("Failed save changed the existing project.");
        Console.WriteLine("Verified-cache reuse, migration, repair, revision change, interrupted extraction and save snapshot tests passed.");
    }

    private sealed class CaptureProgress(List<string> stages) : IProgress<ProjectStoreProgress>
    {
        public void Report(ProjectStoreProgress value) => stages.Add(value.Stage);
    }

    private sealed class InterruptExtraction : IProgress<ProjectStoreProgress>
    {
        public void Report(ProjectStoreProgress value)
        {
            if (value.Stage == "Extracting audio files…" && value.CompletedBytes > 0)
                throw new OperationCanceledException("Test interruption during extraction.");
        }
    }
}
