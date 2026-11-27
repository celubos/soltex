using System.Buffers.Binary;
using Soltex.Audio.Transport;

int checks = 0;
void Check(bool condition) { if (!condition) throw new InvalidOperationException("Transport assertion failed."); checks++; }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { checks++; return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
var fake = new FakeDevice();
var transport = new OwnedAudioTransport(fake);
Check(transport.Query(OwnedAudioBus.Game) is { Active: true, Generation: 7, Frames: 0 });
Check(fake.LastRequest.Length == 32 && BinaryPrimitives.ReadUInt32LittleEndian(fake.LastRequest.AsSpan(4)) == 32);
var packet = transport.ReadRender(OwnedAudioBus.Chat, 2);
Check(packet.Samples.SequenceEqual(new float[] { -1, 0, 0.5f, 32767 / 32768f }));
Check(packet.Status.Bus == OwnedAudioBus.Chat && packet.Status.Frames == 2);
var written = transport.WriteMicrophone([-2, -0.5f, 0.5f, 2]);
Check(written.Frames == 2 && written.Sequence == 5);
Check(BinaryPrimitives.ReadUInt64LittleEndian(fake.LastRequest.AsSpan(16)) == 3);
Check(BinaryPrimitives.ReadInt16LittleEndian(fake.LastRequest.AsSpan(32)) == short.MinValue);
Check(BinaryPrimitives.ReadInt16LittleEndian(fake.LastRequest.AsSpan(34)) == -16384);
Check(BinaryPrimitives.ReadInt16LittleEndian(fake.LastRequest.AsSpan(38)) == short.MaxValue);
Throws<ArgumentException>(() => transport.WriteMicrophone([0, float.NaN]));
Throws<ArgumentException>(() => transport.WriteMicrophone([0]));
Throws<ArgumentOutOfRangeException>(() => transport.ReadRender(OwnedAudioBus.Game, 4801));
Throws<ArgumentException>(() => transport.ReadRender(OwnedAudioBus.Microphone));
Throws<ArgumentOutOfRangeException>(() => transport.Query((OwnedAudioBus)6));
foreach (int corruption in Enumerable.Range(1, 6))
{
    fake.Corruption = corruption;
    Throws<InvalidDataException>(() => transport.ReadRender(OwnedAudioBus.Game, 2));
}
fake.Corruption = 0; fake.ChangeGeneration = true;
Throws<IOException>(() => transport.WriteMicrophone([0, 0]));
transport.Dispose(); transport.Dispose(); Check(fake.DisposeCount == 1);
Throws<ObjectDisposedException>(() => transport.Query(OwnedAudioBus.Game));
// PCM16 conversion: exact values and digital silence stay bit exact; other values get unbiased TPDF dither within ±1 LSB.
var ditherDevice = new FakeDevice();
using (var dithered = new OwnedAudioTransport(ditherDevice))
{
    dithered.WriteMicrophone(new float[960]);
    Check(Enumerable.Range(0, 960).All(i => BinaryPrimitives.ReadInt16LittleEndian(ditherDevice.LastRequest.AsSpan(32 + i * 2)) == 0));
    double sum = 0; int outside = 0, count = 0; var levels = new HashSet<short>();
    float quarterLsb = 0.25f / 32768f;
    for (int block = 0; block < 50; block++)
    {
        dithered.WriteMicrophone(Enumerable.Repeat(quarterLsb, 960).ToArray());
        for (int i = 0; i < 960; i++)
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(ditherDevice.LastRequest.AsSpan(32 + i * 2));
            sum += value; count++; levels.Add(value); if (Math.Abs(value - 0.25) > 1.25) outside++;
        }
    }
    Check(outside == 0 && levels.SetEquals(new short[] { -1, 0, 1 }));
    Check(Math.Abs(sum / count - 0.25) < 0.02); // the mean keeps the sub-LSB level instead of rounding it away
}
Console.WriteLine($"PASS {checks} managed transport checks; no driver opened and no audio endpoints touched.");

sealed class FakeDevice : IOwnedAudioDevice
{
    public byte[] LastRequest { get; private set; } = [];
    public int Corruption { get; set; }
    public bool ChangeGeneration { get; set; }
    public int DisposeCount { get; private set; }
    public byte[] Exchange(uint controlCode, byte[] request, int responseCapacity)
    {
        LastRequest = request.ToArray();
        uint bus = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8));
        uint frames = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(12));
        bool read = controlCode == OwnedAudioTransport.ReadControl;
        var result = new byte[56 + (read ? frames * 4 : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), bus);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), frames);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), controlCode == OwnedAudioTransport.WriteControl ? 3 + frames : 3);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), ChangeGeneration && controlCode == OwnedAudioTransport.WriteControl ? 8UL : 7UL);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48), 1);
        if (read) { short[] samples = [-32768, 0, 16384, 32767]; for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(56 + i * 2), samples[i]); }
        switch (Corruption)
        {
            case 1: result[0] = 2; break;
            case 2: result[8] = 4; break;
            case 3: result[12] = 255; break;
            case 4: result[48] = 2; break;
            case 5: BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(52), 9601); break;
            case 6: return result[..^1];
        }
        if (result.Length > responseCapacity) throw new InvalidOperationException("Fake response capacity exceeded.");
        return result;
    }
    public void Dispose() => DisposeCount++;
}
