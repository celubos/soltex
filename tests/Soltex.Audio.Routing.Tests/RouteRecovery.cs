using System.Security.Cryptography;
using System.Text.Json;
using Soltex.Audio.WindowsRouting;

internal static class RouteRecovery
{
    internal static void Run(string[] args)
    {
        var source = Path.GetFullPath(ClearProbe.Required(args, "--restore-from-journal"));
        using var journal = new RouteJournal(ClearProbe.Required(args, "--journal"));
        var records = File.ReadLines(source).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var original = records.Single(r => r.GetProperty("Kind").GetString() == "before").GetProperty("Value");
        var identity = original.GetProperty("Identity").Deserialize<OwnedProcessIdentity>()!;
        var before = original.GetProperty("Policies").Deserialize<ProcessPolicy[]>()!;
        ClearProbe.Check(Path.GetFullPath(Environment.ProcessPath!).Equals(identity.ExecutablePath, StringComparison.OrdinalIgnoreCase), "Recovery executable path must exactly match original owned test identity.");
        var currentDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath!)));
        var expectedDigest = ClearProbe.Required(args, "--expected-executable-sha256");
        ClearProbe.Check(currentDigest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase), "Recovery apphost digest must match the explicitly supplied verified build digest.");
        journal.Write("recovery-build-identity", new { identity.ExecutablePath, OriginalSha256 = identity.Sha256, CurrentSha256 = currentDigest,
            Rebuilt = !currentDigest.Equals(identity.Sha256, StringComparison.OrdinalIgnoreCase), Purpose = "Corrected recovery code for the same dedicated test executable path." });
        var allowed = records.Where(r => r.GetProperty("Kind").GetString() == "set-intent")
            .Select(r => r.GetProperty("Value").GetProperty("RawDeviceId").GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var policy = new WindowsAudioPolicy();
        using var child = policy.LaunchOwned(Environment.ProcessPath!, "--silent-child");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ClearProbe.Check(child.Output.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() == "READY", "Silent recovery child did not become ready.");
        journal.Write("recovery-before", new { Source = source, child.Identity, Policies = child.Read() });
        foreach (var role in new[] { RoutingRole.Console, RoutingRole.Multimedia })
        {
            var wanted = before.Single(p => p.Direction == RoutingDirection.Render && p.Role == role);
            var current = child.Read().Single(p => p.Direction == RoutingDirection.Render && p.Role == role);
            if (string.Equals(current.RawDeviceId, wanted.RawDeviceId, StringComparison.OrdinalIgnoreCase)) continue;
            ClearProbe.Check(allowed.Contains(current.RawDeviceId), "Current policy differs from original test intents; preserved for manual review.");
            journal.Write("restore-intent", wanted);
            journal.Write("restore-readback", child.Set(wanted.Direction, wanted.Role, wanted.RawDeviceId));
        }
        var after = child.Read();
        var restored = before.SequenceEqual(after);
        journal.Write("recovery-after", new { Policies = after, RestorationComplete = restored });
        ClearProbe.Check(restored, "Original own-executable policy was not fully restored.");
        Console.WriteLine("PASS: exact original six-policy state restored for dedicated own test executable.");
    }
}
