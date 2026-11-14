using System.Text.Json;
using Soltex.Audio.WindowsRouting;
using NAudio.Wave;
using Soltex.Audio.Transport.Tests;

if (args.Length > 0 && args[0] == "--self-test")
{
    SignalMeasurement.SelfTest();
    ClearProbe.Check(WindowsAudioPolicy.ToRawDeviceId("{example}", RoutingDirection.Render) == @"\\?\SWD#MMDEVAPI#{example}#{e6327cad-dcec-4949-ae8a-991e976a79d2}", "Render interface identity encoding failed.");
    Console.WriteLine("PASS: routing identifier encoding; no policy activated.");
    await AppRoutingChecks.RunAsync();
    return;
}
if (args.Length > 0 && args[0] == "--tone-child")
{
    WasapiOut? output = null;
    try
    {
        void Open(bool silent = false)
        {
            output?.Dispose();
            output = new WasapiOut(); // Intentionally the default-device constructor, no explicit endpoint.
            output.Init(new EndpointToneSource(output.OutputWaveFormat, 1877, silent ? 0 : 0.002));
            output.Play();
            Thread.Sleep(300);
            Console.WriteLine("READY");
            Console.Out.Flush();
        }
        Open();
        while (Console.ReadLine() is { } command && command != "exit")
        {
            if (command == "reopen") Open();
            else if (command == "silence") Open(true);
        }
    }
    finally { output?.Dispose(); }
    return;
}
if (args.Length > 0 && args[0] == "--run-owned-routing")
{
    OwnedRouteRun.Run(args);
    return;
}
if (args.Length > 0 && args[0] == "--restore-from-journal")
{
    RouteRecovery.Run(args);
    return;
}

if (args.Length > 0 && args[0] == "--silent-child")
{
    using var output = new WasapiOut();
    output.Init(new SilenceProvider(output.OutputWaveFormat));
    output.Play();
    Console.WriteLine("READY");
    Console.Out.Flush();
    while (Console.ReadLine() is { } command && command != "exit") { }
    return;
}
if (args.Length > 0 && args[0] == "--verify-clear")
{
    ClearProbe.Run(args);
    return;
}

if (args.Length == 0 || args[0] == "--read-only")
{
    using var silentOutput = new WasapiOut();
    silentOutput.Init(new SilenceProvider(silentOutput.OutputWaveFormat));
    silentOutput.Play();
    Thread.Sleep(300);
    using var policy = new WindowsAudioPolicy();
    Console.WriteLine(JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, Executable = Environment.ProcessPath,
        OS = Environment.OSVersion.Version.ToString(), InterfaceId = WindowsAudioPolicy.InterfaceId, Policies = policy.ReadCurrentProcess(), Writes = 0 }));
    return;
}
throw new ArgumentException("Use --read-only or explicit --verify-clear --endpoint-id ID --journal PATH.");
