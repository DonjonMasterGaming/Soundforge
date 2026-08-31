using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundforge.Audio;

public sealed class AudioTrackPlayer : IDisposable
{
    private const int WasapiLatencyMilliseconds = 200;
    private readonly object _sync = new();
    private IWavePlayer? _output;
    private MMDevice? _outputDevice;
    private LocalAudioReader? _reader;
    private TrimmedSampleProvider? _playbackSource;
    private string? _path;
    private string? _outputDeviceId;
    private bool _loop;
    private TimeSpan _trimStart;
    private TimeSpan _trimEnd;

    public string? FilePath => _path;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public bool IsLoaded => _reader is not null;
    public bool HasReachedEnd => _playbackSource?.HasReachedEnd == true;

    public float Volume
    {
        get => _reader?.Volume ?? 1f;
        set { if (_reader is not null) _reader.Volume = Math.Clamp(value, 0f, 1f); }
    }

    public TimeSpan Position => _reader is null
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Clamp((_reader.CurrentTime - _trimStart).Ticks, 0, Length.Ticks));
    public TimeSpan Length => _trimEnd > _trimStart ? _trimEnd - _trimStart : TimeSpan.Zero;

    public void Load(
        string path,
        bool loop = true,
        string? outputDeviceId = null,
        TimeSpan? trimStart = null,
        TimeSpan? trimEnd = null)
    {
        lock (_sync)
        {
            Stop();
            _path = path;
            _loop = loop;

            _reader = new LocalAudioReader(path);
            _trimStart = ClampTime(trimStart ?? TimeSpan.Zero, TimeSpan.Zero, _reader.TotalTime);
            _trimEnd = ClampTime(trimEnd ?? _reader.TotalTime, _trimStart, _reader.TotalTime);
            if (_trimEnd <= _trimStart)
                _trimEnd = _reader.TotalTime;
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
            _playbackSource = null;
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

        _playbackSource = new TrimmedSampleProvider(_reader, () => _loop, _trimStart, _trimEnd);
        ISampleProvider playbackSource = _playbackSource;
        if (string.IsNullOrWhiteSpace(_outputDeviceId))
        {
            _output = new WaveOutEvent();
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            _outputDevice = enumerator.GetDevice(_outputDeviceId);
            playbackSource = MatchOutputFormat(playbackSource, _outputDevice.AudioClient.MixFormat);
            _output = new WasapiOut(
                _outputDevice,
                AudioClientShareMode.Shared,
                true,
                WasapiLatencyMilliseconds);
        }

        _output.Init(playbackSource);
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    private static ISampleProvider MatchOutputFormat(ISampleProvider source, WaveFormat outputFormat)
    {
        ISampleProvider converted = source;

        if (converted.WaveFormat.Channels == 1 && outputFormat.Channels == 2)
            converted = new MonoToStereoSampleProvider(converted);
        else if (converted.WaveFormat.Channels == 2 && outputFormat.Channels == 1)
            converted = new StereoToMonoSampleProvider(converted);

        if (converted.WaveFormat.SampleRate != outputFormat.SampleRate)
            converted = new WdlResamplingSampleProvider(converted, outputFormat.SampleRate);

        return converted;
    }

    private static TimeSpan ClampTime(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

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

    private sealed class TrimmedSampleProvider : ISampleProvider
    {
        private readonly LocalAudioReader _source;
        private readonly Func<bool> _loop;
        private readonly TimeSpan _start;
        private readonly TimeSpan _end;

        public TrimmedSampleProvider(LocalAudioReader source, Func<bool> loop, TimeSpan start, TimeSpan end)
        {
            _source = source;
            _loop = loop;
            _start = start;
            _end = end;
            WaveFormat = source.WaveFormat;
            _source.CurrentTime = _start;
        }

        public WaveFormat WaveFormat { get; }
        public bool HasReachedEnd { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            var total = 0;
            var retriedLoop = false;
            while (total < count)
            {
                var secondsRemaining = (_end - _source.CurrentTime).TotalSeconds;
                var framesRemaining = Math.Floor(Math.Max(0, secondsRemaining) * WaveFormat.SampleRate + 0.0001);
                var requested = (int)Math.Min(count - total, framesRemaining * WaveFormat.Channels);
                requested -= requested % WaveFormat.Channels;
                var read = requested > 0 ? _source.Read(buffer, offset + total, requested) : 0;
                if (read > 0)
                {
                    total += read;
                    retriedLoop = false;
                    continue;
                }

                if (!_loop() || retriedLoop)
                {
                    HasReachedEnd = true;
                    break;
                }

                HasReachedEnd = false;
                _source.CurrentTime = _start;
                retriedLoop = true;
            }
            return total;
        }
    }
}
