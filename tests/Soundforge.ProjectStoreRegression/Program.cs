using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundforge.Audio;
using Soundforge.Models;
using Soundforge.Persistence;

var testRoot = Path.Combine(AppContext.BaseDirectory, "regression-work");
Directory.CreateDirectory(testRoot);

try
{
    if (args.Length == 2 && args[0] == "--benchmark-compression")
    {
        CompressionBenchmark.Run(args[1], testRoot);
        return;
    }
    if (args.Length == 2 && args[0] == "--benchmark-cache")
    {
        CompressionBenchmark.Run(args[1], testRoot, cacheOnly: true);
        return;
    }
    CacheRegression.Run(testRoot);
    var sourcePath = Path.Combine(testRoot, "source.wav");
    File.WriteAllBytes(sourcePath, [0x52, 0x49, 0x46, 0x46]);

    var track = new Track
    {
        Name = "Cache regression",
        FilePath = sourcePath,
        TrimStartSeconds = 1.25,
        TrimEndSeconds = 8.5
    };
    var playlist = new Playlist { Name = "Main", Tracks = [track] };
    var layer = new Layer { Name = "Music", Playlists = [playlist], ActivePlaylistId = playlist.Id };
    var project = new SoundforgeProject
    {
        Name = "Cache regression",
        Scenes = [new Scene { Name = "Test", Layers = [layer] }]
    };
    var projectPath = Path.Combine(testRoot, "cache-regression.soundforge");

    var sceneA = new Scene { Name = "Alpha" };
    var sceneB = new Scene { Name = "beta" };
    var sceneC = new Scene { Name = "Combat" };
    var orderedScenes = new List<Scene> { sceneC, sceneA, sceneB };
    if (!SceneOrdering.Move(orderedScenes, sceneC, 1) || orderedScenes[1] != sceneC)
        throw new InvalidOperationException("Manual scene reordering failed.");
    if (SceneOrdering.Move(orderedScenes, sceneC, 10))
        throw new InvalidOperationException("Scene reordering moved beyond the available range.");
    SceneOrdering.SortByName(orderedScenes);
    if (!orderedScenes.SequenceEqual(new[] { sceneA, sceneB, sceneC }))
        throw new InvalidOperationException("Alphabetical scene sorting failed.");

    var recoveryPath = Path.Combine(testRoot, "recovery", "Soundforge-recovery.autosave.json");
    SoundforgeProjectStore.WriteRecovery(recoveryPath, SoundforgeProjectStore.SerializeRecovery(project));
    var recovered = SoundforgeProjectStore.Load(recoveryPath);
    if (recovered.Scenes[0].Layers[0].Playlists[0].Tracks[0].TrimStartSeconds != 1.25)
        throw new InvalidOperationException("Autosave recovery did not preserve the project state.");

    var saveProgress = new List<ProjectStoreProgress>();
    SoundforgeProjectStore.Save(projectPath, project, new InlineProgress<ProjectStoreProgress>(saveProgress.Add));
    var loadProgress = new List<ProjectStoreProgress>();
    var loaded = SoundforgeProjectStore.Load(projectPath, new InlineProgress<ProjectStoreProgress>(loadProgress.Add));
    if (saveProgress.Count == 0 || saveProgress[^1].Percentage != 100)
        throw new InvalidOperationException("Project save did not report completion.");
    if (loadProgress.Count == 0 || loadProgress[^1].Percentage != 100)
        throw new InvalidOperationException("Project load did not report completion.");
    var loadedPath = loaded.Scenes[0].Layers[0].Playlists[0].Tracks[0].FilePath;
    var loadedTrack = loaded.Scenes[0].Layers[0].Playlists[0].Tracks[0];
    if (loadedTrack.TrimStartSeconds != 1.25 || loadedTrack.TrimEndSeconds != 8.5)
        throw new InvalidOperationException("Source trim markers were not preserved by project save/load.");
    var expectedRoot = Path.Combine(AppContext.BaseDirectory, "cache", "ImportedSources");

    if (!Path.GetFullPath(loadedPath).StartsWith(Path.GetFullPath(expectedRoot), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Project audio was extracted outside the application cache: {loadedPath}");
    if (!File.Exists(loadedPath))
        throw new InvalidOperationException("Extracted project audio is missing.");

    var matchOutputFormat = typeof(AudioTrackPlayer).GetMethod(
        "MatchOutputFormat",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Audio format conversion method is missing.");
    var mono22050 = new SignalGenerator(22050, 1) { Type = SignalGeneratorType.Sin };
    var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    var converted = (ISampleProvider)(matchOutputFormat.Invoke(null, [mono22050, targetFormat])
        ?? throw new InvalidOperationException("Audio format conversion returned no provider."));
    if (converted.WaveFormat.SampleRate != 48000 || converted.WaveFormat.Channels != 2)
        throw new InvalidOperationException($"Audio was not matched to the output format: {converted.WaveFormat}");
    var sampleBuffer = new float[2048];
    if (converted.Read(sampleBuffer, 0, sampleBuffer.Length) == 0)
        throw new InvalidOperationException("Converted audio produced no samples.");

    var fadeSourcePath = Path.Combine(testRoot, "fade-silence.wav");
    using (var writer = new WaveFileWriter(fadeSourcePath, new WaveFormat(44100, 16, 2)))
        writer.Write(new byte[44100 * 4 * 2], 0, 44100 * 4 * 2);

    var extensiblePath = Path.Combine(testRoot, "extensible-24.wav");
    using (var writer = new WaveFileWriter(extensiblePath, new WaveFormatExtensible(48000, 24, 2)))
    {
        // One stereo frame: +0.5 left, -0.5 right, signed little-endian PCM24.
        for (var frame = 0; frame < 4800; frame++)
            writer.Write([0, 0, 0x40, 0, 0, 0xC0], 0, 6);
    }
    using (var reader = new LocalAudioReader(extensiblePath))
    {
        var samples = new float[96];
        if (reader.Read(samples, 0, samples.Length) != samples.Length ||
            Math.Abs(samples[0] - 0.5f) > 0.00001 || Math.Abs(samples[1] + 0.5f) > 0.00001)
            throw new InvalidOperationException("Extensible PCM24 samples were not decoded accurately.");
        reader.CurrentTime = TimeSpan.FromSeconds(0.05);
        reader.Volume = 0.5f;
        if (Math.Abs(reader.TotalTime.TotalSeconds - 0.1) > 0.0001 ||
            reader.Read(samples, 0, samples.Length) != samples.Length || Math.Abs(samples[0] - 0.25f) > 0.00001)
            throw new InvalidOperationException("Extensible WAV seeking, duration or gain failed.");
    }
    var extensibleFloatPath = Path.Combine(testRoot, "extensible-float.wav");
    using (var writer = new WaveFileWriter(extensibleFloatPath, new WaveFormatExtensible(48000, 32, 2)))
        writer.Write([0, 0, 0x80, 0x3E, 0, 0, 0x80, 0xBE], 0, 8);
    using (var reader = new LocalAudioReader(extensibleFloatPath))
    {
        var samples = new float[2];
        if (reader.Read(samples, 0, 2) != 2 || samples[0] != 0.25f || samples[1] != -0.25f)
            throw new InvalidOperationException("Extensible float samples were not decoded accurately.");
    }
    var unsupportedPath = Path.Combine(testRoot, "unsupported-extensible.wav");
    var unsupportedBytes = File.ReadAllBytes(extensiblePath);
    // RIFF/WAVE (12), fmt tag/length (8), then subtype at format offset 24.
    unsupportedBytes[44] = 0x7F;
    File.WriteAllBytes(unsupportedPath, unsupportedBytes);
    try
    {
        using var reader = new LocalAudioReader(unsupportedPath);
        throw new Exception("An unknown WAV subtype was incorrectly treated as PCM.");
    }
    catch (NotSupportedException) { }
    using (File.Open(unsupportedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    using (var engine = new AudioEngine())
    {
        engine.MasterVolume = 0;
        var previous = engine.Start(new Track { Name = "Previous pool", FilePath = fadeSourcePath });
        try
        {
            engine.Transition([
                new Track { Name = "Valid replacement", FilePath = extensiblePath },
                new Track { Name = "Missing replacement", FilePath = Path.Combine(testRoot, "missing.wav") }
            ], [previous], TimeSpan.Zero, TimeSpan.Zero);
            throw new Exception("A missing replacement unexpectedly succeeded.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Missing replacement")) { }
        if (engine.GetSessions().Count != 1 || engine.GetSessions().Single().SessionId != previous ||
            !engine.GetSessions().Single().IsPlaying)
            throw new InvalidOperationException("Failed transition did not preserve old playback or leaked a replacement session.");
        var replacement = engine.Transition([
            new Track { Name = "Valid replacement", FilePath = extensiblePath, Loop = false,
                TrimStartSeconds = 0.02, TrimEndSeconds = 0.07 }
        ], [previous], TimeSpan.Zero, TimeSpan.Zero).Single();
        if (engine.GetSessions().Single().SessionId != replacement ||
            Math.Abs(engine.GetSessions().Single().Length.TotalSeconds - 0.05) > 0.001)
            throw new InvalidOperationException("Successful extensible WAV transition/trim failed.");
    }

    // Optional read-only check of a recovery manifest's local sources. Never extracts,
    // saves, plays or uploads user audio; fixtures above remain self-contained.
    if (args.Length == 2 && args[0] == "--verify-sources")
    {
        var userProject = SoundforgeProjectStore.Load(args[1]);
        var checkedTracks = 0;
        var samples = new float[16384];
        foreach (var source in userProject.Scenes.SelectMany(scene => scene.Layers)
                     .SelectMany(item => item.Playlists).SelectMany(item => item.Tracks).DistinctBy(item => item.Id))
        {
            using var reader = new LocalAudioReader(source.FilePath);
            long sampleCount = 0;
            int read;
            while ((read = reader.Read(samples, 0, samples.Length)) > 0)
            {
                if (samples.Take(read).Any(sample => !float.IsFinite(sample)))
                    throw new InvalidOperationException($"Non-finite audio samples: {source.Name}");
                sampleCount += read;
            }
            if (sampleCount == 0)
                throw new InvalidOperationException($"No audio samples: {source.Name}");
            checkedTracks++;
            if (checkedTracks % 20 == 0)
                Console.WriteLine($"Fully decoded {checkedTracks} local sources...");
        }
        Console.WriteLine($"Full-file decoding passed for {checkedTracks} local project sources (no playback or file changes).");
    }
    using (var engine = new AudioEngine())
    {
        var fadeTrack = new Track { Name = "Fade regression", FilePath = fadeSourcePath, Volume = 1.0, Loop = false };
        var sessionId = engine.Start(fadeTrack, TimeSpan.FromSeconds(1));
        engine.SetSessionLayerGain(sessionId, 0.5f);
        await Task.Delay(30);
        var earlyVolume = engine.GetSessions().Single().EffectiveVolume;
        await Task.Delay(180);
        var laterVolume = engine.GetSessions().Single().EffectiveVolume;
        if (earlyVolume > 0.08f || laterVolume <= earlyVolume || laterVolume > 0.5f)
            throw new InvalidOperationException($"Crossfade gain bounced: early={earlyVolume:0.000}, later={laterVolume:0.000}");
    }

    using (var engine = new AudioEngine())
    {
        var trimmedTrack = new Track
        {
            Name = "Trim regression",
            FilePath = fadeSourcePath,
            Volume = 1.0,
            Loop = false,
            TrimStartSeconds = 0.25,
            TrimEndSeconds = 0.75
        };
        engine.Start(trimmedTrack);
        var trimmedSession = engine.GetSessions().Single();
        if (Math.Abs(trimmedSession.Length.TotalSeconds - 0.5) > 0.02)
            throw new InvalidOperationException($"Trimmed playback duration was incorrect: {trimmedSession.Length}.");
        if (trimmedSession.Position < TimeSpan.Zero || trimmedSession.Position > trimmedSession.Length)
            throw new InvalidOperationException($"Trimmed playback position was outside its range: {trimmedSession.Position}.");

        var completionDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!engine.GetSessions().Single().HasReachedEnd && DateTime.UtcNow < completionDeadline)
            await Task.Delay(20);
        if (!engine.GetSessions().Single().HasReachedEnd)
            throw new InvalidOperationException("Trimmed music did not report explicit playback completion.");
    }

    Console.WriteLine($"Project cache, progress, recovery, audio-format, WAV Extensible, transition rollback, crossfade, source-trim, music-completion, and scene-order regressions passed: {loadedPath}");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    throw;
}
finally
{
    try
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
        var cacheRoot = Path.Combine(AppContext.BaseDirectory, "cache");
        if (Directory.Exists(cacheRoot))
            Directory.Delete(cacheRoot, recursive: true);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"Could not remove test fixtures: {ex.Message}");
    }
}

file sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
