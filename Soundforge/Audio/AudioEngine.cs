using System.Runtime;
using Soundforge.Models;

namespace Soundforge.Audio;

/// <summary>
/// Owns active playback sessions independently of the user interface.
/// The UI, scene runner, and a future remote control all use this single
/// boundary to start, stop, and control audio.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ActiveSession> _sessions = new();
    private readonly GCLatencyMode _normalGcLatencyMode = GCSettings.LatencyMode;
    private float _masterVolume = 1f;
    private string? _outputDeviceId;
    private bool _disposed;

    public float MasterVolume
    {
        get
        {
            lock (_sync)
                return _masterVolume;
        }
        set
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _masterVolume = Math.Clamp(value, 0f, 1f);
                foreach (var session in _sessions.Values)
                    ApplyVolume(session);
            }
        }
    }

    public string? OutputDeviceId
    {
        get
        {
            lock (_sync)
                return _outputDeviceId;
        }
    }

    public void SetOutputDevice(string? outputDeviceId)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _outputDeviceId = outputDeviceId;
            foreach (var session in _sessions.Values)
                session.Player.SetOutputDevice(outputDeviceId);
        }
    }

    public Guid Start(Track track, TimeSpan? fadeIn = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (string.IsNullOrWhiteSpace(track.FilePath))
            throw new ArgumentException("A track needs a local audio-file path.", nameof(track));

        string? outputDeviceId;
        lock (_sync)
        {
            ThrowIfDisposed();
            outputDeviceId = _outputDeviceId;
        }

        var player = new AudioTrackPlayer();
        var sessionId = Guid.NewGuid();

        try
        {
            var trimStart = TimeSpan.FromSeconds(Math.Max(0, track.TrimStartSeconds));
            var trimEnd = track.TrimEndSeconds is > 0
                ? TimeSpan.FromSeconds(track.TrimEndSeconds.Value)
                : (TimeSpan?)null;
            player.Load(track.FilePath, track.Loop, outputDeviceId, trimStart, trimEnd);

            lock (_sync)
            {
                ThrowIfDisposed();
                var session = new ActiveSession(sessionId, track, player);
                session.FadeGain = fadeIn is { } duration && duration > TimeSpan.Zero ? 0f : 1f;
                if (_sessions.Count == 0)
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                _sessions.Add(sessionId, session);
                ApplyVolume(session);
            }

            player.Play();
            if (fadeIn is { } fadeDuration && fadeDuration > TimeSpan.Zero)
                StartFade(sessionId, 1f, fadeDuration);
            return sessionId;
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    public bool Pause(Guid sessionId) => WithSession(sessionId, session => session.Player.Pause());

    public bool Resume(Guid sessionId) => WithSession(sessionId, session => session.Player.Play());

    public bool SetTrackVolume(Guid sessionId, float volume) => WithSession(sessionId, session =>
    {
        session.Track.Volume = Math.Clamp(volume, 0f, 1f);
        ApplyVolume(session);
    });

    public bool SetSessionLayerGain(Guid sessionId, float gain) => WithSession(sessionId, session =>
    {
        session.LayerGain = Math.Clamp(gain, 0f, 1f);
        ApplyVolume(session);
    });

    public bool SetLoop(Guid sessionId, bool enabled) => WithSession(sessionId, session =>
    {
        session.Track.Loop = enabled;
        session.Player.SetLoop(enabled);
    });

    public bool Stop(Guid sessionId, TimeSpan? fadeOut = null)
    {
        if (fadeOut is { } duration && duration > TimeSpan.Zero)
        {
            lock (_sync)
            {
                if (!_sessions.ContainsKey(sessionId))
                    return false;
            }

            StartFade(sessionId, 0f, duration, stopAfterFade: true);
            return true;
        }

        return StopImmediately(sessionId);
    }

    private bool StopImmediately(Guid sessionId)
    {
        ActiveSession? session;
        lock (_sync)
        {
            if (!_sessions.Remove(sessionId, out session))
                return false;
            if (_sessions.Count == 0)
                GCSettings.LatencyMode = _normalGcLatencyMode;
        }

        session.Player.Dispose();
        return true;
    }

    public void StopAll()
    {
        ActiveSession[] sessions;
        lock (_sync)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            GCSettings.LatencyMode = _normalGcLatencyMode;
        }

        foreach (var session in sessions)
            session.Player.Dispose();
    }

    public IReadOnlyList<PlaybackSessionInfo> GetSessions()
    {
        lock (_sync)
        {
            return _sessions.Values
                .Select(session => new PlaybackSessionInfo(
                    session.Id,
                    session.Track.Id,
                    session.Track.Name,
                    session.Track.FilePath,
                    session.Player.IsPlaying,
                    session.Player.HasReachedEnd,
                    session.Player.Position,
                    session.Player.Length,
                    session.Player.Volume))
                .ToList();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        StopAll();
        _disposed = true;
    }

    private bool WithSession(Guid sessionId, Action<ActiveSession> action)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return false;

            action(session);
            return true;
        }
    }

    private void ApplyVolume(ActiveSession session) =>
        session.Player.Volume = (float)Math.Clamp(
            session.Track.Volume * session.LayerGain * _masterVolume * session.FadeGain,
            0d,
            1d);

    private void StartFade(Guid sessionId, float targetFadeGain, TimeSpan duration, bool stopAfterFade = false)
    {
        ActiveSession? session;
        int fadeVersion;
        float startingFadeGain;

        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out session))
                return;

            startingFadeGain = session.FadeGain;
            fadeVersion = ++session.FadeVersion;
        }

        _ = FadeAsync(sessionId, startingFadeGain, targetFadeGain, duration, fadeVersion, stopAfterFade);
    }

    private async Task FadeAsync(Guid sessionId, float startingFadeGain, float targetFadeGain, TimeSpan duration, int fadeVersion, bool stopAfterFade)
    {
        var startedAt = DateTime.UtcNow;
        while (true)
        {
            var progress = Math.Clamp((DateTime.UtcNow - startedAt).TotalMilliseconds / duration.TotalMilliseconds, 0d, 1d);
            var isComplete = progress >= 1d;

            lock (_sync)
            {
                if (!_sessions.TryGetValue(sessionId, out var session) || session.FadeVersion != fadeVersion)
                    return;

                session.FadeGain = startingFadeGain + ((targetFadeGain - startingFadeGain) * (float)progress);
                ApplyVolume(session);
            }

            if (isComplete)
                break;

            await Task.Delay(20).ConfigureAwait(false);
        }

        if (stopAfterFade)
            StopImmediately(sessionId);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ActiveSession(Guid id, Track track, AudioTrackPlayer player)
    {
        public Guid Id { get; } = id;
        public Track Track { get; } = track;
        public AudioTrackPlayer Player { get; } = player;
        public int FadeVersion { get; set; }
        public float LayerGain { get; set; } = 1f;
        public float FadeGain { get; set; } = 1f;
    }
}

public sealed record PlaybackSessionInfo(
    Guid SessionId,
    Guid TrackId,
    string TrackName,
    string FilePath,
    bool IsPlaying,
    bool HasReachedEnd,
    TimeSpan Position,
    TimeSpan Length,
    float EffectiveVolume);
