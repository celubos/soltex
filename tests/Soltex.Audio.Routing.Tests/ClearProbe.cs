using System.Text.Json;
using Soltex.Audio.Transport.Tests;
using Soltex.Audio.WindowsRouting;

internal sealed class RouteJournal : IDisposable
{
    private readonly StreamWriter writer;
    internal RouteJournal(string path)
    {
        path = LocalOutputFile.Require(path, nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) { AutoFlush = true };
    }
    internal void Write(string kind, object? value) => writer.WriteLine(JsonSerializer.Serialize(new { Time = DateTimeOffset.UtcNow, Kind = kind, Value = value }));
    public void Dispose() => writer.Dispose();
}

internal static class ClearProbe
{
    internal static string Required(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {key}.", nameof(args));
    }
    internal static void Run(string[] args)
    {
        var endpoint = Required(args, "--endpoint-id");
        using var journal = new RouteJournal(Required(args, "--journal"));
        using var policy = new WindowsAudioPolicy();
        using var child = policy.LaunchOwned(Environment.ProcessPath!, "--silent-child");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (child.Output.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() != "READY") throw new IOException("Owned silent child did not become ready.");
        var before = child.Read();
        journal.Write("before", new { child.Identity, Policies = before, Silent = true, InterfaceId = WindowsAudioPolicy.InterfaceId });
        var desired = WindowsAudioPolicy.ToRawDeviceId(endpoint, RoutingDirection.Render);
        var touched = new Dictionary<RoutingRole, string?>();
        var restored = true;
        try
        {
            foreach (var role in new[] { RoutingRole.Console, RoutingRole.Multimedia })
            {
                var initial = before.Single(p => p.Direction == RoutingDirection.Render && p.Role == role);
                if (initial.RawDeviceId is null)
                {
                    touched[role] = null;
                    journal.Write("null-noop-intent", role);
                    var empty = child.Set(RoutingDirection.Render, role, null);
                    journal.Write("null-noop-readback", empty);
                    Check(empty.RawDeviceId is null, "Null policy no-op failed.");
                }
                touched[role] = desired;
                journal.Write("set-intent", new { role, desired });
                var written = child.Set(RoutingDirection.Render, role, desired);
                journal.Write("set-readback", written);
                Check(string.Equals(written.RawDeviceId, desired, StringComparison.OrdinalIgnoreCase), "Own process policy did not match requested endpoint.");
                touched[role] = null;
                journal.Write("clear-intent", role);
                var cleared = child.Set(RoutingDirection.Render, role, null);
                journal.Write("clear-readback", cleared);
                Check(cleared.RawDeviceId is null, "Null HSTRING did not clear this own-process policy.");
            }
            journal.Write("clear-semantics-pass", "Both render roles accepted a real assignment and returned null after per-process clear.");
        }
        catch (Exception error) { journal.Write("failure", new { Type = error.GetType().FullName, error.Message }); throw; }
        finally
        {
            foreach (var (role, expected) in touched)
            {
                try
                {
                    var initial = before.Single(p => p.Direction == RoutingDirection.Render && p.Role == role);
                    var current = child.Read().Single(p => p.Direction == RoutingDirection.Render && p.Role == role);
                    if (string.Equals(current.RawDeviceId, initial.RawDeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(current.RawDeviceId, expected, StringComparison.OrdinalIgnoreCase))
                    { restored = false; journal.Write("restore-conflict", new { role, current.RawDeviceId }); continue; }
                    journal.Write("restore-intent", initial);
                    var result = child.Set(initial.Direction, initial.Role, initial.RawDeviceId);
                    journal.Write("restore-readback", result);
                }
                catch (Exception error) { restored = false; journal.Write("restore-error", new { role, error.Message }); }
            }
            try
            {
                var after = child.Read();
                restored &= before.SequenceEqual(after);
                journal.Write("after", new { Policies = after, RestorationComplete = restored });
            }
            catch (Exception error) { restored = false; journal.Write("restore-verification-error", error.Message); }
            if (!restored) Console.Error.WriteLine("OWNED TEST POLICY RESTORATION INCOMPLETE; inspect journal. No user application route was changed.");
        }
        Check(restored, "Owned process policy restoration failed.");
        Console.WriteLine("PASS: silent own-process set/clear for Console+Multimedia; exact six-policy restoration.");
    }
    internal static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
