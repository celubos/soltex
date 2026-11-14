using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;

namespace Soltex.Audio.Engine;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--list")
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            {
                using (device) Console.WriteLine(JsonSerializer.Serialize(new { device.ID, device.FriendlyName, Flow = device.DataFlow.ToString() }, Json));
            }
            return 0;
        }
        if (args.Length != 2 || args[0] != "--config")
        {
            Console.WriteLine("Soltex independent PCM engine. --list enumerates active endpoints; --config <file.json> starts explicitly configured bridges. Ctrl+C stops. No virtual endpoint installation or default-device changes.");
            return args.Length == 0 ? 0 : 2;
        }
        var bridges = new List<WasapiBridge>();
        using var stop = new ManualResetEventSlim();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Set(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var configurations = JsonSerializer.Deserialize<BridgeConfiguration[]>(File.ReadAllText(args[1]), Json)
                ?? throw new InvalidDataException("Expected a JSON array of bridge configurations.");
            if (configurations.Length is < 1 or > 5) throw new InvalidDataException("Configure one to five explicit bridges.");
            if (configurations.Select(c => c.CaptureEndpointId).Distinct(StringComparer.Ordinal).Count() != configurations.Length)
                throw new InvalidDataException("Each capture endpoint must have one owner in this process.");
            foreach (var config in configurations) bridges.Add(new WasapiBridge(config));
            foreach (var bridge in bridges) bridge.Start();
            Console.WriteLine("Bridges running. Ctrl+C stops owned streams. External endpoints and defaults remain independently owned.");
            while (!stop.Wait(1000))
            {
                foreach (var bridge in bridges)
                {
                    Console.WriteLine(JsonSerializer.Serialize(bridge.Status, Json));
                    if (bridge.Status.Failure is not null) return 1;
                }
            }
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Console.Error.WriteLine(error.Message); return 1; }
        finally
        {
            foreach (var bridge in bridges.AsEnumerable().Reverse())
            {
                try { bridge.Dispose(); }
                catch (Exception error) { Console.Error.WriteLine($"Stream shutdown: {error.Message}"); }
            }
            Console.CancelKeyPress -= cancel;
        }
    }
}
