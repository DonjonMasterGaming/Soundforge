using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundforge.Audio;

/// <summary>Seekable local decoding shared by playback and the trim editor.</summary>
public sealed class LocalAudioReader : ISampleProvider, IDisposable
{
    private readonly WaveStream _stream;
    private readonly VolumeSampleProvider _samples;

    public LocalAudioReader(string path)
    {
        WaveStream? stream = null;
        try
        {
            ISampleProvider samples;
            if (System.IO.Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var wave = new WaveFileReader(path);
                stream = wave;
                var format = GetStandardFormat(wave.WaveFormat);
                if (format.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
                {
                    // Only reinterpret a recognised uncompressed subtype. Audio bytes and
                    // source files are unchanged; no legacy ACM codec is needed for PCM.
                    samples = new StandardWaveProvider(wave, format).ToSampleProvider();
                }
                else
                {
                    // Retain ownership even when an installed codec cannot open this WAV.
                    stream = WaveFormatConversionStream.CreatePcmStream(wave);
                    samples = stream.ToSampleProvider();
                }
            }
            else
            {
                var fallback = new AudioFileReader(path);
                stream = fallback;
                samples = fallback;
            }

            _stream = stream;
            _samples = new VolumeSampleProvider(samples);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat => _samples.WaveFormat;
    public TimeSpan TotalTime => _stream.TotalTime;
    public TimeSpan CurrentTime { get => _stream.CurrentTime; set => _stream.CurrentTime = value; }
    public float Volume { get => _samples.Volume; set => _samples.Volume = value; }
    public int Read(float[] buffer, int offset, int count) => _samples.Read(buffer, offset, count);
    public void Dispose() => _stream.Dispose();

    private static WaveFormat GetStandardFormat(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible)
            return format;

        // NAudio 2.2.1 reads WAV fmt chunks as WaveFormatExtraData, not
        // WaveFormatExtensible. Inspect the serialized extension without native casts.
        using var memory = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(memory);
        format.Serialize(writer);
        var bytes = memory.ToArray();
        // Serialization prefixes the 40-byte WAVEFORMATEXTENSIBLE with a 4-byte length.
        if (format.ExtraSize < 22 || bytes.Length < 44)
            throw new System.IO.InvalidDataException("The WAV Extensible format header is incomplete.");
        var subtype = new Guid(bytes.AsSpan(28, 16));
        var pcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        var ieeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        var encoding = subtype == pcm ? WaveFormatEncoding.Pcm
            : subtype == ieeeFloat ? WaveFormatEncoding.IeeeFloat
            : throw new NotSupportedException($"Unsupported WAV Extensible subtype: {subtype}.");
        var validBits = BitConverter.ToUInt16(bytes, 22);
        if (format.SampleRate <= 0 || format.Channels <= 0 ||
            validBits > format.BitsPerSample ||
            (encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is not (8 or 16 or 24 or 32)) ||
            (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample is not (32 or 64)) ||
            format.BlockAlign != format.Channels * (format.BitsPerSample / 8))
            throw new NotSupportedException("Unsupported WAV Extensible sample layout.");
        return WaveFormat.CreateCustomFormat(encoding, format.SampleRate, format.Channels,
            format.AverageBytesPerSecond, format.BlockAlign, format.BitsPerSample);
    }

    private sealed class StandardWaveProvider(WaveFileReader source, WaveFormat format) : IWaveProvider
    {
        public WaveFormat WaveFormat => format;
        public int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    }
}
