using System.Buffers.Binary;
using NAudio.Wave;

namespace Soltex.Audio.Transport.Tests;

internal static class SignalMeasurement
{
    internal static double Amplitude(ReadOnlySpan<float> samples, int sampleRate, double frequency)
    {
        if (samples.Length < 3) throw new ArgumentException("At least three samples are required.", nameof(samples));
        double real = 0, imaginary = 0, weight = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (samples.Length - 1));
            var angle = 2 * Math.PI * frequency * i / sampleRate;
            real += samples[i] * window * Math.Cos(angle);
            imaginary += samples[i] * window * Math.Sin(angle);
            weight += window;
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / weight;
    }

    internal static double Decibels(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-12));

    internal static float DecodeSample(ReadOnlySpan<byte> sample, WaveFormat format)
    {
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extensible)
        {
            encoding = extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")
                ? WaveFormatEncoding.IeeeFloat
                : extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71")
                    ? WaveFormatEncoding.Pcm : throw new NotSupportedException("Unsupported extensible loopback encoding.");
        }
        if (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample));
        if (encoding != WaveFormatEncoding.Pcm) throw new NotSupportedException("Unsupported loopback encoding.");
        return format.BitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
            24 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608f,
            32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0),
            _ => throw new NotSupportedException("Unsupported loopback sample width.")
        };
    }

    internal static void SelfTest()
    {
        const int rate = 48000;
        var samples = Enumerable.Range(0, 4800).Select(i => (float)(0.003 * Math.Sin(2 * Math.PI * 997 * i / rate)
            + 0.2 * Math.Sin(2 * Math.PI * 2557 * i / rate))).ToArray();
        Check(Math.Abs(Amplitude(samples, rate, 997) - 0.003) < 0.00001, "Detector must reject a much louder unrelated frequency.");
        Check(Amplitude(samples, rate, 1451) < 0.00001, "Absent tone must not be invented.");
        Check(Math.Abs(Decibels(0.5) + 6.020599913) < 1e-6, "Amplitude dB conversion.");
        Check(Math.Abs(DecodeSample([0, 64], new WaveFormat(rate, 16, 1)) - 0.5) < 1e-6, "PCM16 scaling.");
        Check(Math.Abs(DecodeSample([0, 0, 192], new WaveFormat(rate, 24, 1)) + 0.5) < 1e-6, "PCM24 sign extension.");
        Check(Math.Abs(DecodeSample([0, 0, 0, 64], new WaveFormat(rate, 32, 1)) - 0.5) < 1e-6, "PCM32 scaling.");
        Check(Math.Abs(DecodeSample([0, 0, 0, 63], WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)) - 0.5) < 1e-6, "Float32 decoding.");
        var source = new ToneSource(rate, 2, 997, 0.003);
        var rendered = new float[9600];
        Check(source.Read(rendered, 0, rendered.Length) == rendered.Length, "Tone source fills complete buffer.");
        Check(rendered.Max(Math.Abs) <= 0.00301, "Tone peak bound.");
        Check(Enumerable.Range(0, rendered.Length / 2).All(i => rendered[2 * i] == rendered[2 * i + 1]), "Channels have identical test phase.");
        var extensible = new WaveFormatExtensible(96000, 32, 8, 0x63f);
        var endpointSource = new EndpointToneSource(extensible, 997, 0.003);
        Check(ReferenceEquals(endpointSource.WaveFormat, extensible), "Exact extensible format and channel mask must be preserved.");
        var endpointBytes = new byte[9600 * extensible.BlockAlign];
        Check(endpointSource.Read(endpointBytes, 0, endpointBytes.Length) == endpointBytes.Length, "Extensible source fills complete frames.");
        var mono = Enumerable.Range(0, 9600).Select(i => DecodeSample(endpointBytes.AsSpan(i * extensible.BlockAlign, 4), extensible)).ToArray();
        Check(Math.Abs(Amplitude(mono, 96000, 997) - 0.003) < 1e-6, "96kHz8ch float tone retains target amplitude.");
        Check(Enumerable.Range(0, 9600).All(i => endpointBytes.AsSpan(i * extensible.BlockAlign, 4).SequenceEqual(endpointBytes.AsSpan(i * extensible.BlockAlign + 28, 4))),
            "All eight extensible channels have identical phase.");
        var pcm = new WaveFormat(48000, 16, 2);
        var pcmSource = new EndpointToneSource(pcm, 997, 0.003);
        var pcmBytes = new byte[4800 * pcm.BlockAlign];
        pcmSource.Read(pcmBytes, 0, pcmBytes.Length);
        var pcmMono = Enumerable.Range(0, 4800).Select(i => DecodeSample(pcmBytes.AsSpan(i * pcm.BlockAlign, 2), pcm)).ToArray();
        Check(Math.Abs(Amplitude(pcmMono, 48000, 997) - 0.003) < 0.00003, "PCM endpoint source quantization remains within one16bit step.");
        Console.WriteLine("PASS: 15 signal/PCM/source assertions; no device opened and no audio state changed.");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class ToneSource(int sampleRate, int channels, double frequency, double amplitude) : ISampleProvider
{
    private long frame;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

    public int Read(float[] buffer, int offset, int count)
    {
        if (count % channels != 0) throw new ArgumentException("Tone request contains a partial frame.", nameof(count));
        for (var i = 0; i < count; i += channels)
        {
            var value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame++ / sampleRate));
            for (var channel = 0; channel < channels; channel++) buffer[offset + i + channel] = value;
        }
        return count;
    }
}

internal sealed class EndpointToneSource : IWaveProvider
{
    private readonly double frequency, amplitude;
    private readonly bool isFloat;
    private long frame;
    public WaveFormat WaveFormat { get; }
    internal EndpointToneSource(WaveFormat format, double frequency, double amplitude)
    {
        WaveFormat = format;
        this.frequency = frequency;
        this.amplitude = amplitude;
        var encoding = format is WaveFormatExtensible extensible
            ? extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? WaveFormatEncoding.IeeeFloat
                : extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71") ? WaveFormatEncoding.Pcm
                    : throw new NotSupportedException("Unsupported endpoint encoding.")
            : format.Encoding;
        isFloat = encoding == WaveFormatEncoding.IeeeFloat;
        if (format.BlockAlign != format.Channels * format.BitsPerSample / 8 ||
            !(isFloat && format.BitsPerSample == 32 || encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32))
            throw new NotSupportedException("Endpoint requires unsupported PCM/float layout.");
    }
    public int Read(byte[] buffer, int offset, int count)
    {
        if (count % WaveFormat.BlockAlign != 0) throw new ArgumentException("Endpoint tone request contains a partial frame.", nameof(count));
        var width = WaveFormat.BitsPerSample / 8;
        for (var i = 0; i < count; i += WaveFormat.BlockAlign)
        {
            var sample = amplitude * Math.Sin(2 * Math.PI * frequency * frame++ / WaveFormat.SampleRate);
            for (var channel = 0; channel < WaveFormat.Channels; channel++)
            {
                var destination = buffer.AsSpan(offset + i + channel * width, width);
                if (isFloat) BinaryPrimitives.WriteSingleLittleEndian(destination, (float)sample);
                else if (width == 2) BinaryPrimitives.WriteInt16LittleEndian(destination, (short)Math.Round(sample * short.MaxValue));
                else if (width == 4) BinaryPrimitives.WriteInt32LittleEndian(destination, (int)Math.Round(sample * int.MaxValue));
                else
                {
                    var encoded = (int)Math.Round(sample * 8388607);
                    destination[0] = (byte)encoded;
                    destination[1] = (byte)(encoded >> 8);
                    destination[2] = (byte)(encoded >> 16);
                }
            }
        }
        return count;
    }
}
