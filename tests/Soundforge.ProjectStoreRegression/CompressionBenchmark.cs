using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Soundforge.Models;
using Soundforge.Persistence;

internal static class CompressionBenchmark
{
    public static void Run(string sourcesDirectory, string outputDirectory, bool cacheOnly = false)
    {
        var wavs = Directory.GetFiles(sourcesDirectory, "*.wav").OrderBy(path => new FileInfo(path).Length).ToList();
        var sources = wavs.Take(2).Concat(wavs.TakeLast(2))
            .Concat(Directory.GetFiles(sourcesDirectory, "*.mp3").Take(2)).Distinct().ToList();
        if (sources.Count == 0)
            throw new InvalidOperationException("No benchmark sources found.");
        var originalHashes = sources.ToDictionary(path => Path.GetFileName(path)!, path =>
        {
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream);
        });
        var inputBytes = sources.Sum(path => new FileInfo(path).Length);
        var project = new SoundforgeProject
        {
            Scenes = [new Scene { Layers = [new Layer { Playlists = [new Playlist
            {
                Tracks = sources.Select(path => new Track { Name = Path.GetFileName(path), FilePath = "sources/" + Path.GetFileName(path) }).ToList()
            }] }] }]
        };
        Console.WriteLine($"Sample: {sources.Count} sources, {inputBytes} bytes. Each ZIP is read back and SHA-256 checked.");
        foreach (var trial in cacheOnly ? new[] { 2 } : new[] { 1, 2 })
        foreach (var level in cacheOnly ? new[] { CompressionLevel.Optimal } : trial == 1
                     ? new[] { CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.NoCompression }
                     : new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal })
        {
            var outputPath = Path.Combine(outputDirectory, $"compression-{trial}-{level}.zip");
            var stopwatch = Stopwatch.StartNew();
            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                foreach (var source in sources)
                {
                    var entry = archive.CreateEntry("sources/" + Path.GetFileName(source), level);
                    using var input = File.OpenRead(source);
                    using var output = entry.Open();
                    input.CopyTo(output, 4 * 1024 * 1024);
                }
                using var writer = new StreamWriter(archive.CreateEntry("project.json").Open());
                writer.Write(SoundforgeProjectStore.SerializeRecovery(project));
            }
            stopwatch.Stop();
            var seconds = stopwatch.Elapsed.TotalSeconds;
            var outputBytes = new FileInfo(outputPath).Length;
            using (var archive = ZipFile.OpenRead(outputPath))
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName == "project.json")
                    continue;
                using var input = entry.Open();
                if (!SHA256.HashData(input).SequenceEqual(originalHashes[entry.Name]))
                    throw new InvalidDataException("Compression round-trip changed audio bytes.");
            }
            Console.WriteLine($"Trial {trial}: {level}: {seconds:F2}s, {outputBytes} bytes ({100d * outputBytes / inputBytes:F1}% of source), integrity PASS");
            if (trial == 2 && level == CompressionLevel.Optimal)
            {
                stopwatch.Restart();
                SoundforgeProjectStore.Load(outputPath);
                stopwatch.Stop();
                var coldSeconds = stopwatch.Elapsed.TotalSeconds;
                stopwatch.Restart();
                SoundforgeProjectStore.Load(outputPath);
                stopwatch.Stop();
                Console.WriteLine($"Sample load: first extraction {coldSeconds:F3}s; verified repeat load {stopwatch.Elapsed.TotalSeconds:F3}s.");
            }
            File.Delete(outputPath);
        }
    }
}
