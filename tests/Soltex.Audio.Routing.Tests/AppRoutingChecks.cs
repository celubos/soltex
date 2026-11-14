using System.Text.Json;
using Soltex.Audio;
using Soltex.Audio.Sessions;
using Soltex.Audio.WindowsRouting;

internal static class AppRoutingChecks
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "soltex-routing-pure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            count++; Console.WriteLine("PASS " + name);
        }
        async Task With(string name, Func<AppRoutingService, FakeBackend, Task> run)
        {
            string directory = Path.Combine(root, name);
            FakeBackend fake = new(directory);
            await using AppRoutingService service = new(directory, () => fake);
            await run(service, fake);
        }
        try
        {
            await With("preview", async (service, fake) =>
            {
                var result = await service.PreviewAsync(fake.Selected, "new");
                Check(result.Allowed && result.BeforePolicies.Count == 6 && fake.Writes == 0, "preview reads six policies without writes");
                var denied = await service.RouteAsync(fake.Selected with { IsSystemSounds = true }, "new");
                Check(!denied.PolicyConfirmed && fake.Writes == 0, "system session rejected before any write");
                denied = await service.RouteAsync(fake.Selected with { CanControl = false }, "new");
                Check(!denied.PolicyConfirmed && fake.Writes == 0, "ambiguous multi-process session rejected");
                denied = await service.RouteAsync(fake.Selected with { Key = fake.Selected.Key with { ProcessStartTimeUtc = DateTimeOffset.UtcNow } }, "new");
                Check(!denied.PolicyConfirmed && fake.Writes == 0, "stale process birth rejected");
                denied = await service.RouteAsync(fake.Selected, "missing");
                Check(!denied.PolicyConfirmed && fake.Writes == 0, "missing destination never substitutes another device");
                denied = await service.RouteAsync(fake.Selected, "capture");
                Check(!denied.PolicyConfirmed && fake.Writes == 0, "wrong-direction destination rejected");
            });
            await With("route-restore", async (service, fake) =>
            {
                var initial = fake.Policies.ToArray();
                var savedBefore = new AppRoutingRule(fake.Selected.ExecutablePath!, RoutingDirection.Render, "old");
                await service.Profiles.UpsertAsync(savedBefore);
                var result = await service.RouteAsync(fake.Selected, "new");
                Check(result.PolicyConfirmed && !result.SessionObserved && result.RequiresStreamRestart && fake.Writes == 3,
                    "policy confirmation does not invent active stream migration");
                Check(fake.IntentDurable && result.JournalPath is not null && File.Exists(result.JournalPath), "each setter has durable prior intent and exact recovery journal");
                Check(fake.Policies.Where(p => p.Direction == RoutingDirection.Capture).SequenceEqual(initial.Where(p => p.Direction == RoutingDirection.Capture)), "render routing preserves all capture roles");
                Check((await service.Profiles.LoadAsync()).Rules.Single().EndpointId == "new", "successful route persists executable intent");
                var restored = await service.RestoreAsync(result.JournalPath!, fake.Selected);
                Check(restored.PolicyConfirmed && fake.Policies.SequenceEqual(initial), "restore retains null versus empty and all six original policies");
                Check((await service.Profiles.LoadAsync()).Rules.Single() == savedBefore, "undo restores the previous saved executable rule");
                Check((await service.GetHistoryAsync()).Single().State == "Restored", "durable journal history exposes recovery outcome after completion");
            });
            await With("observed", async (service, fake) =>
            {
                fake.ObserveNew = true;
                var result = await service.RouteAsync(fake.Selected, "new");
                Check(result.PolicyConfirmed && result.SessionObserved && !result.RequiresStreamRestart, "actual active-session observation reported separately");
            });
            await With("partial-write", async (service, fake) =>
            {
                var before = fake.Policies.ToArray(); fake.FailAfterWrite = 2;
                var result = await service.RouteAsync(fake.Selected, "new");
                Check(!result.PolicyConfirmed && fake.Policies.SequenceEqual(before), "failure after native write rolls back confirmed and uncertain writes");
                Check((await service.Profiles.LoadAsync()).Rules.Count == 0, "failed transaction does not save an application rule");
            });
            await With("concurrent-change", async (service, fake) =>
            {
                fake.FailAfterWrite = 2; fake.ChangeFirstBeforeFailure = true;
                var result = await service.RouteAsync(fake.Selected, "new");
                Check(!result.PolicyConfirmed && fake.Policies[0].RawDeviceId == "later-user-choice", "rollback preserves a concurrent newer policy");
                Check(result.Message.Contains("newer policy preserved", StringComparison.Ordinal), "rollback conflict is surfaced rather than reported restored");
            });
            await With("cancel", async (service, fake) =>
            {
                var initial = fake.Policies.ToArray(); using CancellationTokenSource canceled = new();
                fake.AfterFirst = canceled.Cancel;
                var result = await service.RouteAsync(fake.Selected, "new", cancellationToken: canceled.Token);
                Check(!result.PolicyConfirmed && fake.Policies.SequenceEqual(initial), "cancellation after a setter completes selective recovery");
            });
            await With("identity-loss", async (service, fake) =>
            {
                fake.AfterFirst = () => fake.IdentityValid = false;
                var result = await service.RouteAsync(fake.Selected, "new");
                Check(!result.PolicyConfirmed && fake.Writes == 1 && result.JournalPath is not null,
                    "PID identity loss prevents further mutation and retains recoverable journal");
            });
            await With("restore-conflict", async (service, fake) =>
            {
                var result = await service.RouteAsync(fake.Selected, "new");
                fake.Policies[0] = fake.Policies[0] with { RawDeviceId = "newer" };
                await service.Profiles.UpsertAsync(new(fake.Selected.ExecutablePath!, RoutingDirection.Render, "later"));
                var restored = await service.RestoreAsync(result.JournalPath!, fake.Selected);
                Check(!restored.PolicyConfirmed && fake.Policies[0].RawDeviceId == "newer", "explicit restore preserves newer per-role choices");
                Check((await service.Profiles.LoadAsync()).Rules.Single().EndpointId == "later", "restore preserves a later saved rule");
            });
            await With("profiles", async (service, fake) =>
            {
                var rule = new AppRoutingRule(fake.Selected.ExecutablePath!, RoutingDirection.Render, "new");
                await service.Profiles.SaveAsync(new(1, [rule]));
                string export = Path.Combine(root, "export.json"); await service.Profiles.ExportAsync(export);
                await service.Profiles.RemoveAsync(rule.ExecutablePath, rule.Direction);
                Check((await service.Profiles.LoadAsync()).Rules.Count == 0, "remove rule changes only stored intent");
                var imported = await service.Profiles.ImportAsync(export);
                Check(imported.Rules.Single() == rule && fake.Writes == 0, "profile roundtrip never applies Windows policy");
                bool invalid = false;
                try { await service.Profiles.SaveAsync(new(2, [rule])); } catch (InvalidDataException) { invalid = true; }
                Check(invalid, "unknown profile version rejected");
                invalid = false;
                try { await service.Profiles.SaveAsync(new(1, [rule, rule with { ExecutablePath = rule.ExecutablePath.ToUpperInvariant() }])); } catch (InvalidDataException) { invalid = true; }
                Check(invalid, "case-insensitive duplicate executable rule rejected");
                invalid = false;
                try { await service.Profiles.SaveAsync(new(1, [rule with { ExecutablePath = "relative.exe" }])); } catch (InvalidDataException) { invalid = true; }
                Check(invalid, "relative executable rule rejected");
                await File.WriteAllBytesAsync(export, new byte[RoutingProfileStore.MaximumBytes + 1]);
                invalid = false;
                try { await service.Profiles.ImportAsync(export); } catch (InvalidDataException) { invalid = true; }
                Check(invalid && (await service.Profiles.LoadAsync()).Rules.Single() == rule, "oversized import preserves prior profile");
            });
            Console.WriteLine($"{count}/{count} application routing transaction/profile checks passed; fake policies only, no native audio activated.");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeBackend(string directory) : IAppRoutingBackend
    {
        private readonly string outputDirectory = directory;
        internal AudioSessionInfo Selected { get; } = new(new("old", "session", 1234, DateTimeOffset.Parse("2026-11-11T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            "Test player", @"D:\Applications\player.exe", AudioSessionState.Active, 1, false, .1, false, true);
        internal ProcessPolicy[] Policies { get; } = [new(RoutingDirection.Render, RoutingRole.Console, null),
            new(RoutingDirection.Render, RoutingRole.Multimedia, ""), new(RoutingDirection.Render, RoutingRole.Communications, "old-render"),
            new(RoutingDirection.Capture, RoutingRole.Console, "capture-console"), new(RoutingDirection.Capture, RoutingRole.Multimedia, null),
            new(RoutingDirection.Capture, RoutingRole.Communications, "")];
        internal int Writes { get; private set; }
        internal int FailAfterWrite { get; set; }
        internal bool ChangeFirstBeforeFailure { get; set; }
        internal bool ObserveNew { get; set; }
        internal bool IdentityValid { get; set; } = true;
        internal bool IntentDurable { get; private set; } = true;
        internal Action? AfterFirst { get; set; }
        public AudioDeviceSnapshot Devices() => new(DateTimeOffset.UtcNow,
            [new("Old", AudioEndpointDirection.Render, AudioEndpointState.Active, true, 1, false) { EndpointId = "old" },
             new("New", AudioEndpointDirection.Render, AudioEndpointState.Active, false, 1, false) { EndpointId = "new" },
             new("Mic", AudioEndpointDirection.Capture, AudioEndpointState.Active, false, 1, false) { EndpointId = "capture" }],
            [new(AudioEndpointDirection.Render, AudioDeviceRole.Multimedia, "old", 0)], AudioObservationState.Current, [], true);
        public AudioSessionSnapshot Sessions()
        {
            var session = Writes > 0 && ObserveNew ? Selected with { Key = Selected.Key with { EndpointId = "new" } } : Selected;
            return new(DateTimeOffset.UtcNow, [new(session.Key.EndpointId, "device", AudioEndpointClassification.Unknown, 1, false, .1, [session])], []);
        }
        public IAppRoutingTarget Open(AudioSessionInfo selected)
        {
            AppRoutingService.ValidateSelection(selected, Sessions()); return new Target(this);
        }
        public void Dispose() { }
        private sealed class Target(FakeBackend owner) : IAppRoutingTarget
        {
            public IReadOnlyList<ProcessPolicy> Read()
            {
                if (!owner.IdentityValid) throw new IOException("Process identity changed.");
                return owner.Policies.ToArray();
            }
            public ProcessPolicy CompareSet(RoutingDirection direction, RoutingRole role, string? expected, string? desired)
            {
                if (!owner.IdentityValid) throw new IOException("Process identity changed.");
                int index = Array.FindIndex(owner.Policies, p => p.Direction == direction && p.Role == role);
                if (owner.Policies[index].RawDeviceId != expected) throw new IOException("Policy conflict.");
                string journal = Directory.GetFiles(Path.Combine(owner.outputDirectory, "routing-journals"), "*.json").Single();
                using var document = JsonDocument.Parse(File.ReadAllText(journal));
                owner.IntentDurable &= document.RootElement.GetProperty("AttemptedRoles").EnumerateArray().Any(r => r.GetString() == role.ToString());
                owner.Policies[index] = new(direction, role, desired); owner.Writes++;
                if (owner.Writes == 1) owner.AfterFirst?.Invoke();
                if (owner.Writes == owner.FailAfterWrite)
                {
                    if (owner.ChangeFirstBeforeFailure) owner.Policies[0] = owner.Policies[0] with { RawDeviceId = "later-user-choice" };
                    throw new IOException("Injected native failure after write.");
                }
                return owner.Policies[index];
            }
            public void Dispose() { }
        }
    }
}
