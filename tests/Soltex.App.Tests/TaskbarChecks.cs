using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using Soltex.App.Settings;
using Soltex.App.Shell;
using static Soltex.App.Tests.Program;

namespace Soltex.App.Tests;

/// <summary>
/// The taskbar helper end to end against the hidden XAML test host (never Explorer). The test loads the helper through
/// Windows' XAML diagnostics, restyles the taskbar-shaped rectangles, reads the real brushes and rendered pixels back,
/// then restores the originals.
/// </summary>
internal static class TaskbarChecks
{
    public static void HelperRestylesAndRestoresTaskbarBackground()
    {
        (string hostPath, string helperPath) = NativeArtifacts();
        string id = Guid.NewGuid().ToString("N");
        string key = @"Software\Soltex\TaskbarTest\" + id;
        string work = Path.Combine(Path.GetTempPath(), "soltex-taskbar-test-" + id);
        using Process host = Process.Start(new ProcessStartInfo(hostPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        try
        {
            string ready = ReadLine(host);
            True(ready.StartsWith("ready ", StringComparison.Ordinal), "The XAML test host did not start: " + ready + HostExit(host));
            int thread = int.Parse(ready.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
            True(Ask(host, "fill") == "fill=#FF202020 stroke=#FF404040", "The test host's starting brushes are wrong.");
            TaskbarHelper helper = new(key, @"Local\SoltexTaskbarTest" + id, work, Path.Combine(work, "helper.log"), helperPath);

            Show(helper, host, thread, new(TaskbarLookKind.Clear, 0x00000000, Line: true), "fill=#00000000 stroke=#FF404040", "#101010");
            True(helper.ReadStatus() is { Targets: 2, Applied: 2, Result: 0 }, "The helper did not report both background rectangles.");
            Show(helper, host, thread, new(TaskbarLookKind.Opaque, 0x40CC3300, Line: false), "fill=#FFCC3300 stroke=#00000000", "#CC3300");
            Show(helper, host, thread, new(TaskbarLookKind.Clear, 0x80000000, Line: true), "fill=#80000000 stroke=#FF404040", null);
            // Acrylic falls back to its tint colour when acrylic cannot render (Transparency effects off, Energy Saver).
            Show(helper, host, thread, new(TaskbarLookKind.Acrylic, 0x80336699, Line: true),
                "fill=acrylic:source=0,tint=#FF336699,opacity=0.502,fallback=#FF336699 stroke=#FF404040", null);
            // Blur hides the colour row, so a colour saved for another look must not show as its fallback: Windows' own
            // taskbar colour for the current theme does.
            Show(helper, host, thread, new(TaskbarLookKind.Blur, 0x00C42B5C, Line: true),
                $"fill=acrylic:source=0,tint=#FFC42B5C,opacity=0.000,fallback={ThemeTaskbarColor()} stroke=#FF404040", null);
            // Windows default puts the original brushes back.
            Show(helper, host, thread, TaskbarLookState.Windows, "fill=#FF202020 stroke=#FF404040", "#202020");
            True(!File.ReadAllText(Path.Combine(work, "helper.log")).Contains("hr=0x8", StringComparison.Ordinal), "The helper logged a failed call.");
        }
        catch (InvalidOperationException error)
        {
            string log = Path.Combine(work, "helper.log");
            throw new InvalidOperationException(error.Message + (File.Exists(log) ? Environment.NewLine + File.ReadAllText(log) : ""), error);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.StandardInput.WriteLine("quit");
                if (!host.WaitForExit(5000)) host.Kill();
            }
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            using (RegistryKey? parent = Registry.CurrentUser.OpenSubKey(@"Software\Soltex\TaskbarTest"))
                if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\Soltex\TaskbarTest", throwOnMissingSubKey: false);
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    public static void HelperAppliesAndRestoresStyleRules()
    {
        (string hostPath, string helperPath) = NativeArtifacts();
        string id = Guid.NewGuid().ToString("N");
        string key = @"Software\Soltex\TaskbarTest\" + id;
        string work = Path.Combine(Path.GetTempPath(), "soltex-taskbar-test-" + id);
        using Process host = Process.Start(new ProcessStartInfo(hostPath) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            string ready = ReadLine(host);
            True(ready.StartsWith("ready ", StringComparison.Ordinal), "The XAML test host did not start: " + ready + HostExit(host));
            int thread = int.Parse(ready.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
            TaskbarHelper helper = new(key, @"Local\SoltexTaskbarTest" + id, work, Path.Combine(work, "helper.log"), helperPath);
            const string Untouched = "inside=o:1.00,v:0,w:40 outside=o:1.00,v:0,w:40 templated=r:3";
            string start = "";
            for (int wait = 0; wait < 40 && (start = Ask(host, "probe")).Contains("r:-1", StringComparison.Ordinal); wait++) Thread.Sleep(50); // template not applied yet
            True(start == Untouched, "The probes do not start untouched: " + start);

            const string StackPanel = "Windows.UI.Xaml.Controls.StackPanel", Border = "Windows.UI.Xaml.Controls.Border";
            TaskbarStyleRule[] rules =
            [
                new(StackPanel, Border, "", "UIElement.Opacity", "0.5"),   // only the Border inside the StackPanel
                new("", "", "Outside", "FrameworkElement.Width", "12"),
                new("", Border, "Inside", "UIElement.Visibility", "Collapsed")
            ];
            Style(helper, host, thread, rules, "inside=o:0.50,v:1,w:40 outside=o:1.00,v:0,w:12 templated=r:3");
            True(helper.ReadStatus() is { Styled: 3, StyleApplied: 3, StyleResult: 0 }, $"Expected three styled elements: {helper.ReadStatus()}");

            // A template-bound value (like the taskbar buttons' corner radius) comes back exactly.
            Style(helper, host, thread, [new("Windows.UI.Xaml.Controls.ContentControl", Border, "TemplatedBorder", "Border.CornerRadius", "0")],
                "inside=o:1.00,v:0,w:40 outside=o:1.00,v:0,w:40 templated=r:0");

            // Changing the rules puts every old value back first.
            TaskbarStyleRule[] two = [new("", "", "Outside", "FrameworkElement.Width", "20"), new(StackPanel, Border, "", "UIElement.Opacity", "0.5")];
            const string TwoApplied = "inside=o:0.50,v:0,w:40 outside=o:1.00,v:0,w:20 templated=r:3";
            Style(helper, host, thread, two, TwoApplied);
            True(!File.ReadAllText(Path.Combine(work, "helper.log")).Contains("restore style hr", StringComparison.Ordinal), "A style could not be restored.");

            // Signalling the same rules again (as every colour change or maximized window does) leaves them in place: no
            // restore, no new walk of the tree. All of a multi-line rule list counts, not only its first line.
            int replaced = RulesReplaced(work);
            Style(helper, host, thread, two, TwoApplied);
            Style(helper, host, thread, two, TwoApplied);
            True(RulesReplaced(work) == replaced, $"Unchanged rules were replaced again ({RulesReplaced(work) - replaced} time(s)).");
            if (File.ReadAllLines(Path.Combine(work, "helper.log")).FirstOrDefault(l => l.Contains("restored the shown value", StringComparison.Ordinal)) is { } shown)
                Console.WriteLine("      note: " + shown[(shown.IndexOf(']', StringComparison.Ordinal) + 2)..]);
            Style(helper, host, thread, [], Untouched);
            True(helper.ReadStatus() is { Styled: 0 }, "Rules removed but elements are still styled.");

            // A property Windows does not know is reported, and nothing changes.
            helper.Write(TaskbarLookState.Windows, [new("", "", "Inside", "Nope.Missing", "1")]);
            helper.Signal(host.Id);
            string? problem = Await(helper.ConfirmAsync(TaskbarLookState.Windows));
            True(problem is not null && problem.Contains("could not be applied", StringComparison.Ordinal), "An invalid rule was not reported: " + problem);
            True(Ask(host, "probe") == Untouched, "An invalid rule changed something.");
        }
        catch (InvalidOperationException error)
        {
            string log = Path.Combine(work, "helper.log");
            throw new InvalidOperationException(error.Message + (File.Exists(log) ? Environment.NewLine + File.ReadAllText(log) : ""), error);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.StandardInput.WriteLine("quit");
                if (!host.WaitForExit(5000)) host.Kill();
            }
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            using (RegistryKey? parent = Registry.CurrentUser.OpenSubKey(@"Software\Soltex\TaskbarTest"))
                if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\Soltex\TaskbarTest", throwOnMissingSubKey: false);
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { }
        }
    }

    public static void TweakCatalogIsAnchoredAndNormalized()
    {
        foreach (TaskbarTweak tweak in TaskbarTweaks.All)
        {
            True(tweak.Options.Count >= 2 && tweak.Options[0].Key == "default" && tweak.Options[0].Rules.Count == 0, tweak.Id + ": the first choice must be Windows' look.");
            True(tweak.Options.Select(o => o.Key).Distinct().Count() == tweak.Options.Count, tweak.Id + ": choice keys repeat.");
            foreach (TaskbarStyleRule rule in tweak.Options.SelectMany(o => o.Rules))
            {
                _ = rule.Encode();
                // Anchored to a taskbar part, or targeting a taskbar-only type, so nothing else in Explorer can match.
                True(rule.Under.StartsWith("Taskbar.", StringComparison.Ordinal) || rule.Under.StartsWith("SystemTray.", StringComparison.Ordinal)
                    || rule.Type.StartsWith("SystemTray.", StringComparison.Ordinal) || rule.Type.StartsWith("Taskbar.", StringComparison.Ordinal),
                    $"{tweak.Id}: {rule} is not anchored to the taskbar.");
            }
        }
        bool threw = false;
        try { _ = new TaskbarStyleRule("", "A|B", "", "UIElement.Opacity", "1").Encode(); }
        catch (ArgumentException) { threw = true; }
        True(threw, "A rule with '|' must be refused.");

        AppSettings saved = new AppSettings { TaskbarTweaks = new Dictionary<string, string> { ["clock"] = "time", ["nope"] = "x", ["corners"] = "default", ["iconsize"] = "huge" } }.Normalize();
        True(saved.TaskbarTweaks.Count == 1 && saved.TaskbarTweaks["clock"] == "time", "Unknown tweaks, unknown choices and Windows' look must not be saved.");
        True(TaskbarTweaks.RulesFor(saved.TaskbarTweaks) is [{ Name: "DateInnerTextBlock", Value: "Collapsed" }], "Time only hides the date text.");
        True(TaskbarTweaks.ChoiceIndex(TaskbarTweaks.All.Single(t => t.Id == "clock"), saved.TaskbarTweaks) == 1, "The saved choice index is wrong.");
    }

    public static void ScrollVolumeHookStartsAndStops()
    {
        int rest = 0;
        True(TaskbarScrollVolume.Steps(ref rest, 120) == (0xAF, 1) && rest == 0 && TaskbarScrollVolume.Steps(ref rest, -240) == (0xAE, 2) && rest == 0
            && TaskbarScrollVolume.Steps(ref rest, -12000) == (0xAE, 10) && rest == 0, "Wheel notches map to the wrong volume key presses.");
        // A high-resolution wheel or touchpad sends one notch as several small deltas: they add up to one press.
        int presses = 0;
        for (int i = 0; i < 8; i++) presses += TaskbarScrollVolume.Steps(ref rest, 15).Presses;
        True(presses == 1 && rest == 0, $"Eight 15-unit wheel events pressed the volume key {presses} time(s); one notch is one press.");
        True(TaskbarScrollVolume.Steps(ref rest, 30) == (0xAF, 0) && rest == 30, "Less than a notch must not press a key yet.");
        True(TaskbarScrollVolume.Steps(ref rest, -100) == (0xAE, 0) && rest == -100, "Turning the wheel back must start over, not cancel out.");
        True(TaskbarScrollVolume.Steps(ref rest, -50) == (0xAE, 1) && rest == -30, "The part of a notch left over must carry to the next event.");
        using TaskbarScrollVolume volume = new();
        True(volume.SetEnabled(true) && volume.Enabled, "The low-level mouse hook did not start.");
        True(volume.SetEnabled(false) && !volume.Enabled, "The hook did not stop.");
        True(volume.SetEnabled(true) && volume.Enabled, "The hook did not start again.");
    }

    /// <summary>
    /// The look for maximized windows only applies on top of a chosen background, and helper problems show next to the
    /// details when they are the only thing in use.
    /// </summary>
    public static void MaximizedLookNeedsAChosenLook()
    {
        AppSettings clear = new() { TaskbarLook = TaskbarLookKind.Clear, TaskbarMaximizedLook = TaskbarLookKind.Opaque };
        True(TaskbarLookService.MaximizedLook(clear) == TaskbarLookKind.Opaque, "Clear with Solid when maximized must turn solid on maximize.");
        AppSettings windows = clear with { TaskbarLook = TaskbarLookKind.Default };
        True(TaskbarLookService.MaximizedLook(windows) is null, "With Windows default the hidden maximized choice must have no effect.");
        True(windows.Normalize().TaskbarMaximizedLook == TaskbarLookKind.Opaque, "The maximized choice must stay saved for when a look is picked again.");

        Dictionary<string, string> clock = new() { ["clock"] = "time" };
        True(TaskbarLookService.OnlyDetails(new AppSettings { TaskbarTweaks = clock }), "Only a detail in use: its problems belong to the details.");
        True(!TaskbarLookService.OnlyDetails(new AppSettings { TaskbarTweaks = clock, TaskbarLook = TaskbarLookKind.Clear }), "A look in use keeps problems under Background.");
        True(!TaskbarLookService.OnlyDetails(new AppSettings { TaskbarTweaks = clock, TaskbarLine = false }), "A hidden line is part of the look.");
        True(!TaskbarLookService.OnlyDetails(new AppSettings()), "Nothing in use: problems stay under Background.");
    }

    /// <summary>
    /// The built test host and x64 helper. Fails (never skips) with what is missing and how to build it: on a fresh
    /// checkout or CI runner the native build has to run before these tests.
    /// </summary>
    private static (string Host, string Helper) NativeArtifacts()
    {
        string native = Path.Combine(RepoRoot(), "artifacts", "native", "taskbar");
        string hostPath = Path.Combine(native, "test", "TaskbarTestHost.exe");
        string helperPath = Path.Combine(native, "x64", "SoltexTaskbar.dll");
        string[] missing = [.. new[] { hostPath, helperPath }.Where(p => !File.Exists(p))];
        True(missing.Length == 0, "The native taskbar helper is not built (missing " + string.Join(", ", missing) + "). Build it first with "
            + "native/Soltex.TaskbarStyle/build.ps1: pass -Toolchain or set SOLTEX_LLVM_MINGW to an LLVM-MinGW folder, or add -Acquire to download the pinned one.");
        return (hostPath, helperPath);
    }

    private static string HostExit(Process host) => host.WaitForExit(1000) ? $" (exit code {host.ExitCode})" : "";

    private static int RulesReplaced(string work) =>
        File.ReadAllLines(Path.Combine(work, "helper.log")).Count(l => l.Contains("rules replaced", StringComparison.Ordinal));

    /// <summary>What the helper falls back to for Blur: Windows' plain taskbar colour for the current theme.</summary>
    private static string ThemeTaskbarColor()
    {
        using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return personalize?.GetValue("SystemUsesLightTheme") is int light && light != 0 ? "#FFF3F3F3" : "#FF202020";
    }

    private static void Style(TaskbarHelper helper, Process host, int thread, TaskbarStyleRule[] rules, string expected)
    {
        helper.Write(TaskbarLookState.Windows, rules);
        if (helper.IsLoaded(host.Id)) helper.Signal(host.Id);
        else True(Await(helper.LoadAsync(host.Id, thread)) is null, "The helper did not load into the test host.");
        string? problem = Await(helper.ConfirmAsync(TaskbarLookState.Windows));
        True(problem is null, $"{rules.Length} rule(s): {problem}");
        string seen = Ask(host, "probe");
        True(seen == expected, $"{rules.Length} rule(s): expected {expected}, saw {seen}");
    }

    /// <summary>
    /// Opt-in (SOLTEX_LIVE_TASKBAR=1): the real taskbar. Loads the helper into Explorer the same way the app does,
    /// shows Clear briefly, checks the helper's report and a few taskbar pixels, then restores Windows' look.
    /// </summary>
    public static void LiveExplorerClearAndRestore()
    {
        (int explorer, int thread) = TaskbarHelper.ExplorerTaskbar();
        True(explorer != 0, "The taskbar is not running.");
        string bundled = Path.Combine(RepoRoot(), "artifacts", "native", "taskbar", "x64", "SoltexTaskbar.dll");
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soltex", "Taskbar");
        TaskbarHelper helper = new(TaskbarHelper.ExplorerKey, TaskbarHelper.ExplorerEvent, directory, Path.Combine(directory, "helper.log"), bundled);
        if (!helper.IsLoaded(explorer)) Await(helper.RetireOthersAsync(explorer).ContinueWith(_ => true, TaskScheduler.Default));
        int[] before = TaskbarPixels();
        // An auto-hidden taskbar sits off screen, where no pixel can be read; the helper's own report still applies.
        bool visible = !before.Contains(-1);
        string? failure = null;
        try
        {
            TaskbarLookState clear = new(TaskbarLookKind.Clear, 0x00000000, Line: true);
            // Every taskbar detail at once: hidden indicator, time only, large icons, square corners.
            IReadOnlyList<TaskbarStyleRule> rules = TaskbarTweaks.RulesFor(new Dictionary<string, string>
                { ["indicator"] = "hidden", ["clock"] = "time", ["iconsize"] = "large", ["corners"] = "square" });
            helper.Write(clear, rules);
            string? loaded = helper.IsLoaded(explorer) ? null : Await(helper.LoadAsync(explorer, thread));
            if (loaded is null) helper.Signal(explorer);
            string? problem = loaded ?? Await(helper.ConfirmAsync(clear));
            TaskbarHelperStatus? status = helper.ReadStatus();
            Thread.Sleep(400);
            int[] during = TaskbarPixels();
            Console.WriteLine($"      live: explorer={explorer} thread={thread} status={status} visible={visible} before={Hex(before)} clear={Hex(during)}");
            if (problem is not null) failure = "Clear: " + problem;
            else if (status is not { Targets: > 0, Result: 0 } || status.Applied != status.Targets) failure = "The helper did not restyle every taskbar background rectangle.";
            else if (status is not { Styled: > 0, StyleResult: 0 } || status.StyleApplied != status.Styled) failure = "The helper did not apply every taskbar detail.";
            else if (visible && before.SequenceEqual(during)) failure = "The taskbar pixels did not change.";
        }
        finally
        {
            helper.Write(TaskbarLookState.Windows);
            helper.Signal(explorer);
            string? restored = helper.IsLoaded(explorer) ? Await(helper.ConfirmAsync(TaskbarLookState.Windows)) : null;
            Thread.Sleep(400);
            int[] after = TaskbarPixels();
            Console.WriteLine($"      live: restored={restored ?? "ok"} status={helper.ReadStatus()} after={Hex(after)}");
            failure ??= restored is not null ? "Restore: " + restored
                : helper.ReadStatus() is not { Styled: 0 } ? "Taskbar details are still applied after the restore."
                : visible && !before.SequenceEqual(after) ? "The taskbar did not return to its original pixels." : null;
        }
        True(failure is null, failure ?? "");
    }

    /// <summary>
    /// Read-only map of the live taskbar: loads the helper into Explorer (Windows' look, no rules) with a one-time list of
    /// the visual tree's element types and names written to <paramref name="dumpFile"/>.
    /// </summary>
    public static void LiveExplorerTreeDump(string dumpFile)
    {
        (int explorer, int thread) = TaskbarHelper.ExplorerTaskbar();
        True(explorer != 0, "The taskbar is not running.");
        string bundled = Path.Combine(RepoRoot(), "artifacts", "native", "taskbar", "x64", "SoltexTaskbar.dll");
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soltex", "Taskbar");
        TaskbarHelper helper = new(TaskbarHelper.ExplorerKey, TaskbarHelper.ExplorerEvent, directory, Path.Combine(directory, "helper.log"), bundled) { DumpPath = dumpFile };
        True(!helper.IsLoaded(explorer), "This helper build is already loaded in Explorer; the tree is listed only when a build first attaches.");
        Await(helper.RetireOthersAsync(explorer).ContinueWith(_ => true, TaskScheduler.Default));
        helper.Write(TaskbarLookState.Windows);
        string? loaded = Await(helper.LoadAsync(explorer, thread));
        Console.WriteLine($"      dump: explorer={explorer} thread={thread} load={loaded ?? "ok"} file={dumpFile}");
        True(loaded is null, "Load: " + loaded);
        for (int wait = 0; wait < 100 && !File.Exists(dumpFile); wait++) Thread.Sleep(100);
        True(File.Exists(dumpFile), "The helper did not write the tree list.");
    }

    private static string Hex(int[] pixels) => string.Join(",", pixels.Select(p => p.ToString("X6", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Three pixels from the empty middle of the primary taskbar (never window content).</summary>
    private static int[] TaskbarPixels()
    {
        nint tray = FindWindowW("Shell_TrayWnd", null);
        _ = GetWindowRect(tray, out Rect bounds);
        nint screen = GetDC(0);
        try
        {
            int y = (bounds.Top + bounds.Bottom) / 2;
            int width = bounds.Right - bounds.Left;
            return [.. SamplePoints.Select(f => (int)GetPixel(screen, bounds.Left + (int)(width * f), y))];
        }
        finally { _ = ReleaseDC(0, screen); }
    }

    private static readonly double[] SamplePoints = [0.55, 0.6, 0.65];

    private struct Rect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowW(string className, string? windowName);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDC(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern uint GetPixel(nint dc, int x, int y);

    private static void Show(TaskbarHelper helper, Process host, int thread, TaskbarLookState state, string brushes, string? pixel)
    {
        helper.Write(state);
        if (helper.IsLoaded(host.Id)) helper.Signal(host.Id);
        else True(Await(helper.LoadAsync(host.Id, thread)) is null, "The helper did not load into the test host.");
        string? problem = Await(helper.ConfirmAsync(state));
        True(problem is null, $"{state.Kind}: {problem}");
        string seen = Ask(host, "fill");
        True(seen == brushes, $"{state.Kind}: expected {brushes}, saw {seen}");
        if (pixel is null) return;
        string rendered = "";
        for (int attempt = 0; attempt < 20 && rendered != "pixel=" + pixel; attempt++)
        {
            rendered = Ask(host, "pixel");
            Thread.Sleep(50);
        }
        True(rendered == "pixel=" + pixel, $"{state.Kind}: expected pixel {pixel}, saw {rendered}");
    }

    private static string Ask(Process host, string command)
    {
        host.StandardInput.WriteLine(command);
        host.StandardInput.Flush();
        return ReadLine(host);
    }

    private static string ReadLine(Process host)
    {
        Task<string?> line = host.StandardOutput.ReadLineAsync();
        return line.Wait(TimeSpan.FromSeconds(15)) ? line.Result ?? "(closed)" : "(timeout)";
    }

    private static T Await<T>(Task<T> task)
    {
        DispatcherFrame frame = new();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static string RepoRoot()
    {
        // The verification matrix builds tests outside the checkout and names the checkout here.
        if (Environment.GetEnvironmentVariable("SOLTEX_TEST_REPOSITORY_ROOT") is { Length: > 0 } configured && File.Exists(Path.Combine(configured, "Soltex.sln")))
            return configured;
        // Built inside the checkout, or run from it (dotnet run --project ... from the repository folder).
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Soltex.sln"))) return directory.FullName;
        throw new InvalidOperationException("The repository root was not found. Run from the checkout or set SOLTEX_TEST_REPOSITORY_ROOT.");
    }
}
