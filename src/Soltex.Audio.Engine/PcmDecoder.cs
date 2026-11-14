using System.Buffers.Binary;
using NAudio.Wave;

namespace Soltex.Audio.Engine;

public static class PcmDecoder
{
    /// <summary>Converts little-endian WASAPI PCM/IEEE samples. Input and destination must describe whole frames.</summary>
    public static int Decode(ReadOnlySpan<byte> bytes, Span<float> samples, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        int width = format.BitsPerSample / 8;
        if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat)
            || width is not (2 or 3 or 4) || (format.Encoding == WaveFormatEncoding.IeeeFloat && width != 4))
            throw new NotSupportedException($"Capture format {format} is not supported; choose PCM16/24/32 or float32.");
        if (bytes.Length % format.BlockAlign != 0) throw new ArgumentException("Capture data contains a partial frame.", nameof(bytes));
        int count = bytes.Length / width;
        if (samples.Length < count) throw new ArgumentException("Destination too short.", nameof(samples));
        for (int i = 0; i < count; i++)
        {
            var value = bytes.Slice(i * width, width);
            samples[i] = format.Encoding == WaveFormatEncoding.IeeeFloat
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value))
                : width switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(value) / 32768f,
                    3 => ((value[0] | (value[1] << 8) | (value[2] << 16)) << 8 >> 8) / 8388608f,
                    _ => (float)(BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648.0)
                };
        }
        return count;
    }
}
