using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundforge.Audio;

public sealed class AudioTrackPlayer : IDisposable
{
    private readonly object _sync = new();
    private IWavePlayer? _output;
    private MMDevice? _outputDevice;
    private AudioFileReader? _reader;
    private string? _path;
    private string? _outputDeviceId;
    private bool _loop;

    public string? FilePath => _path;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsLoaded => _reader is not null;

    public float Volume
    {
        get => _reader?.Volume ?? 1f;
        set { if (_reader is not null) _reader.Volume = Math.Clamp(value, 0f, 1f); }
    }

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Length => _reader?.TotalTime ?? TimeSpan.Zero;

    public void Load(string path, bool loop = true, string? outputDeviceId = null)
    {
        lock (_sync)
        {
            Stop();
            _path = path;
            _loop = loop;

            _reader = new AudioFileReader(path);
            _outputDeviceId = outputDeviceId;
            CreateOutput();
        }
    }

    public void Play() => _output?.Play();
    public void Pause() => _output?.Pause();

    public void Stop()
    {
        lock (_sync)
        {
            DisposeOutput();
            _reader?.Dispose();
            _reader = null;
            _path = null;
        }
    }

    public void SetOutputDevice(string? outputDeviceId)
    {
        lock (_sync)
        {
            _outputDeviceId = outputDeviceId;
            if (_reader is null)
                return;

            var wasPlaying = _output?.PlaybackState == PlaybackState.Playing;
            DisposeOutput();
            CreateOutput();
            if (wasPlaying)
                _output?.Play();
        }
    }

    public void SetLoop(bool enabled)
    {
        lock (_sync)
            _loop = enabled;
    }

    private void CreateOutput()
    {
        if (_reader is null)
            throw new InvalidOperationException("An audio reader must be created before its output.");

        if (string.IsNullOrWhiteSpace(_outputDeviceId))
        {
            _output = new WaveOutEvent();
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            _outputDevice = enumerator.GetDevice(_outputDeviceId);
            _output = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, true, 100);
        }

        _output.Init(new LoopingSampleProvider(_reader, () => _loop));
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    private void DisposeOutput()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Stop();
            _output.Dispose();
            _output = null;
        }

        _outputDevice?.Dispose();
        _outputDevice = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // LoopingSampleProvider restarts the source when loop mode is enabled.
    }

    public void Dispose() => Stop();

    private sealed class LoopingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Func<bool> _loop;
        private readonly WaveStream? _waveStream;

        public LoopingSampleProvider(ISampleProvider source, Func<bool> loop)
        {
            _source = source;
            _loop = loop;
            _waveStream = source as WaveStream;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = _source.Read(buffer, offset + total, count - total);
                if (read > 0)
                {
                    total += read;
                    continue;
                }

                if (!_loop() || _waveStream is null)
                    break;

                _waveStream.Position = 0;
            }
            return total;
        }
    }
}
