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

    Console.WriteLine($"Project cache, progress, recovery, audio-format, crossfade, source-trim, music-completion, and scene-order regressions passed: {loadedPath}");
}

finally
{
    if (Directory.Exists(testRoot))
        Directory.Delete(testRoot, recursive: true);
    var cacheRoot = Path.Combine(AppContext.BaseDirectory, "cache");
    if (Directory.Exists(cacheRoot))
        Directory.Delete(cacheRoot, recursive: true);
}

file sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
