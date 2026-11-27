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
// PCM16 conversion: exact values stay bit exact; blocks with nothing above half an LSB (digital silence, a closed gate's
// residue) stay exact digital zero; everything else gets unbiased TPDF dither within ±1 LSB.
var ditherDevice = new FakeDevice();
short Sample(int i) => BinaryPrimitives.ReadInt16LittleEndian(ditherDevice.LastRequest.AsSpan(32 + i * 2));
using (var dithered = new OwnedAudioTransport(ditherDevice))
{
    const float lsb = 1f / 32768f;
    dithered.WriteMicrophone(new float[960]);
    Check(Enumerable.Range(0, 960).All(i => Sample(i) == 0));
    // A gate closed to -80 dB on a -50 dBFS noise floor leaves about -130 dBFS: digital silence, not a -96 dBFS hiss.
    var random = new Random(7);
    for (int block = 0; block < 20; block++)
    {
        float[] residue = Enumerable.Range(0, 960).Select(_ => (float)((random.NextDouble() * 2 - 1) * 0.49 * lsb)).ToArray();
        if (block == 0) residue[17] = -0.4999f * lsb;
        dithered.WriteMicrophone(residue);
        Check(Enumerable.Range(0, 960).All(i => Sample(i) == 0));
    }
    // One sample at half an LSB makes the block signal again: it is dithered, not muted.
    float[] edge = new float[960]; Array.Fill(edge, 0.25f * lsb); edge[100] = 0.5f * lsb;
    dithered.WriteMicrophone(edge);
    Check(Enumerable.Range(0, 960).Any(i => Sample(i) != 0));
    // Sub-LSB detail inside a block that carries signal keeps its level on average instead of rounding away.
    double sum = 0; int outside = 0, count = 0; var levels = new HashSet<short>();
    for (int block = 0; block < 50; block++)
    {
        float[] quiet = Enumerable.Repeat(0.25f * lsb, 960).ToArray(); quiet[0] = lsb; // an exactly representable 1 LSB
        dithered.WriteMicrophone(quiet);
        Check(Sample(0) == 1);
        for (int i = 1; i < 960; i++)
        {
            short value = Sample(i);
            sum += value; count++; levels.Add(value); if (Math.Abs(value - 0.25) > 1.25) outside++;
        }
    }
    Check(outside == 0 && levels.SetEquals(new short[] { -1, 0, 1 }));
    Check(Math.Abs(sum / count - 0.25) < 0.02);
    // Noise floor of dither plus rounding: 0.5 LSB RMS, about -96 dBFS (not -101).
    double error = 0; int n = 0;
    for (int block = 0; block < 50; block++)
    {
        float[] music = Enumerable.Range(0, 960).Select(i => (float)(0.01 * Math.Sin(2 * Math.PI * 997 * (block * 960 + i) / 96000.0))).ToArray();
        dithered.WriteMicrophone(music);
        for (int i = 0; i < 960; i++) { double e = Sample(i) - music[i] * 32768.0; error += e * e; n++; }
    }
    double floorDb = 20 * Math.Log10(Math.Sqrt(error / n) / 32768);
    Check(Math.Abs(floorDb + 96.3) < 0.5);
    Console.WriteLine($"  TPDF noise floor {floorDb:F1} dBFS RMS; closed-gate blocks stay digital silence.");
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
