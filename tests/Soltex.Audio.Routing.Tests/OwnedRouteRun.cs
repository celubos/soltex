using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Soltex.Audio.WindowsRouting;

internal static class OwnedRouteRun
{
    private static readonly RoutingRole[] Roles = [RoutingRole.Console, RoutingRole.Multimedia];
    private sealed record SessionObservation(string EndpointId, string SessionId, bool Active);
    internal static void Run(string[] args)
    {
        if (!args.Contains("--allow-owned-route-changes", StringComparer.Ordinal)) throw new ArgumentException("Owned routing test requires --allow-owned-route-changes.", nameof(args));
        var game = ClearProbe.Required(args, "--game-id");
        var chat = ClearProbe.Required(args, "--chat-id");
        var physicalIndex = Array.IndexOf(args, "--physical-id");
        var physical = physicalIndex < 0 ? null : ClearProbe.Required(args, "--physical-id");
        using var journal = new RouteJournal(ClearProbe.Required(args, "--journal"));
        using var policy = new WindowsAudioPolicy();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var id in new[] { game, chat })
        {
            using var device = enumerator.GetDevice(id);
            ClearProbe.Check(device.State == DeviceState.Active && device.DataFlow == DataFlow.Render, "Both chosen endpoints must be active render devices.");
        }
        var defaults = ReadDefaults(enumerator);
        using var signal = new RouteSignalProbe(enumerator, game, chat, physical, journal);
        WindowsAudioPolicy.OwnedRoutingProcess? child = null;
        IReadOnlyList<ProcessPolicy>? before = null;
        var knownWrites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restored = true;
        var passed = false;
        var liveMoved = true;
        var canceled = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; canceled = true; };
        void CheckCancellation() { if (canceled) throw new OperationCanceledException("Routing test canceled."); }
        WindowsAudioPolicy.OwnedRoutingProcess Start()
        {
            var owned = policy.LaunchOwned(Environment.ProcessPath!, "--tone-child");
            try { WaitReady(owned); return owned; }
            catch { owned.Dispose(); throw; }
        }
        void Assign(string endpoint)
        {
            CheckCancellation();
            var raw = WindowsAudioPolicy.ToRawDeviceId(endpoint, RoutingDirection.Render);
            knownWrites.Add(raw);
            foreach (var role in Roles)
            {
                journal.Write("set-intent", new { child!.Identity, role, RawDeviceId = raw });
                var after = child.Set(RoutingDirection.Render, role, raw);
                journal.Write("set-readback", after);
                ClearProbe.Check(string.Equals(after.RawDeviceId, raw, StringComparison.OrdinalIgnoreCase), "Policy readback did not confirm destination.");
            }
        }
        try
        {
            child = Start();
            before = child.Read();
            journal.Write("before", new { child.Identity, Policies = before, Defaults = defaults, Sessions = Observe(child.Identity.ProcessId), ToneHz = 1877, PeakAmplitude = 0.002 });
            foreach (var destination in new[] { game, chat })
            {
                Assign(destination);
                var moved = WaitForEndpoint(child.Identity.ProcessId, destination, 2500, CheckCancellation);
                journal.Write("active-session-after-policy", new { Destination = destination, MovedWithoutReopen = moved, Sessions = Observe(child.Identity.ProcessId) });
                liveMoved &= moved;
                if (!moved)
                {
                    journal.Write("reopen-owned-stream", child.Identity);
                    child.Input.WriteLine("reopen"); child.Input.Flush(); WaitReady(child);
                    ClearProbe.Check(WaitForEndpoint(child.Identity.ProcessId, destination, 2500, CheckCancellation), "Default-device stream did not honor policy even after reopening.");
                    journal.Write("active-session-after-reopen", new { Destination = destination, Sessions = Observe(child.Identity.ProcessId) });
                }
                signal.Verify(destination, "after-policy-and-session");
            }
            var firstIdentity = child.Identity;
            journal.Write("restart-intent", firstIdentity);
            child.Dispose(); child = null;
            child = Start();
            var restartedPolicy = child.Read();
            var expectedChat = WindowsAudioPolicy.ToRawDeviceId(chat, RoutingDirection.Render);
            ClearProbe.Check(child.Identity.ProcessId != firstIdentity.ProcessId && child.Identity.ExecutablePath == firstIdentity.ExecutablePath,
                "Restart identity did not establish a new process of the same executable.");
            foreach (var role in Roles)
                ClearProbe.Check(string.Equals(restartedPolicy.Single(p => p.Direction == RoutingDirection.Render && p.Role == role).RawDeviceId,
                    expectedChat, StringComparison.OrdinalIgnoreCase), "Policy did not persist to restarted executable.");
            ClearProbe.Check(WaitForEndpoint(child.Identity.ProcessId, chat, 2500, CheckCancellation), "Restarted default-device stream did not use persisted Chat route.");
            journal.Write("restart-confirmed", new { child.Identity, Policies = restartedPolicy, Sessions = Observe(child.Identity.ProcessId) });
            signal.Verify(chat, "after-process-restart");
            passed = true;
            journal.Write("policy-and-active-session-pass", new { LiveMovedWithoutReopen = liveMoved, RestartPersistence = true });
        }
        catch (Exception error) { journal.Write("failure", new { Type = error.GetType().FullName, error.Message }); throw; }
        finally
        {
            if (before is not null)
            {
                try
                {
                    if (child is null)
                    {
                        child = policy.LaunchOwned(Environment.ProcessPath!, "--silent-child");
                        WaitReady(child);
                        journal.Write("recovery-child", child.Identity);
                    }
                    else
                    {
                        child.Input.WriteLine("silence"); child.Input.Flush(); WaitReady(child);
                        journal.Write("own-tone-stopped-before-restore", child.Identity);
                    }
                    foreach (var role in Roles)
                    {
                        var original = before.Single(p => p.Direction == RoutingDirection.Render && p.Role == role);
                        var current = child.Read().Single(p => p.Direction == RoutingDirection.Render && p.Role == role);
                        if (string.Equals(current.RawDeviceId, original.RawDeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                        if (current.RawDeviceId is not null && !knownWrites.Contains(current.RawDeviceId))
                        { restored = false; journal.Write("restore-conflict", current); continue; }
                        journal.Write("restore-intent", original);
                        var result = child.Set(original.Direction, original.Role, original.RawDeviceId);
                        journal.Write("restore-readback", result);
                    }
                    var after = child.Read();
                    var afterDefaults = ReadDefaults(enumerator);
                    restored &= before.SequenceEqual(after) && defaults.SequenceEqual(afterDefaults);
                    journal.Write("after", new { Policies = after, Defaults = afterDefaults, RestorationComplete = restored, ProofPassed = passed });
                }
                catch (Exception error) { restored = false; journal.Write("restore-error", error.Message); }
            }
            else journal.Write("after", new { OwnPolicyWrites = 0, RestorationRequired = false });
            child?.Dispose();
            if (!restored) Console.Error.WriteLine("OWNED ROUTING RESTORATION INCOMPLETE: inspect journal; user application policies were never selected.");
        }
        ClearProbe.Check(restored, "Owned policy restoration incomplete.");
        Console.WriteLine($"PASS: own executable policy, measured signal destination, active session, restart persistence and restoration; moved without reopen={liveMoved}.");
    }
    private static void WaitReady(WindowsAudioPolicy.OwnedRoutingProcess child)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (child.Output.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() != "READY") throw new IOException("Owned test child did not report readiness.");
    }
    private static bool WaitForEndpoint(int pid, string endpoint, int milliseconds, Action cancellationCheck)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            cancellationCheck();
            var active = Observe(pid).Where(s => s.Active).ToArray();
            if (active.Length > 0 && active.All(s => s.EndpointId == endpoint)) return true;
            Thread.Sleep(100);
        }
        return false;
    }
    private static SessionObservation[] Observe(int pid)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<SessionObservation>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.GetProcessID == pid) result.Add(new(device.ID, session.GetSessionIdentifier,
                            session.State == AudioSessionState.AudioSessionStateActive));
                    }
                }
                catch (COMException) { } // One disappearing unrelated endpoint cannot establish success; no matches fail the bounded wait.
            }
        }
        return result.ToArray();
    }
    private static string[] ReadDefaults(MMDeviceEnumerator enumerator)
    {
        var result = new List<string>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            result.Add($"{flow}/{role}:{device.ID}");
        }
        return result.ToArray();
    }
}
