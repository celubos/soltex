using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Soltex.App.Controls;

namespace Soltex.App.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // "--dump-taskbar <result file> <tree file>": read-only list of the live taskbar's XAML tree (types and names).
        if (args is ["--dump-taskbar", string dumpResult, string treeFile])
        {
            using StreamWriter writer = new(dumpResult) { AutoFlush = true };
            Console.SetOut(writer);
            global::Soltex.App.App.HostedByTests = true;
            global::Soltex.App.App dumpApplication = new();
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            try { TaskbarChecks.LiveExplorerTreeDump(treeFile); Console.WriteLine("PASS  tree listed"); return 0; }
            catch (InvalidOperationException error) { Console.WriteLine("FAIL  " + error.Message); return 1; }
        }
        // "--live-taskbar <result file>": only the opt-in live taskbar check, written to a file (for runs started without a console).
        if (args is ["--live-taskbar", string resultFile])
        {
            using StreamWriter writer = new(resultFile) { AutoFlush = true };
            Console.SetOut(writer);
            Environment.SetEnvironmentVariable("SOLTEX_LIVE_TASKBAR", "1");
            Environment.SetEnvironmentVariable("SOLTEX_TEST_FILTER", "Live:");
            return Run();
        }
        return Run();
    }

    private static int Run()
    {
        // Without this the application's real startup runs on the first pump: it would take the real single-instance lock
        // (and quit when another copy is running), install the crash reporter into the real profile and start real services.
        global::Soltex.App.App.HostedByTests = true;
        global::Soltex.App.App application = new();
        application.InitializeComponent();
        // A running WPF app always has this context on its UI thread; awaits in event handlers rely on it.
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        global::Soltex.App.Shell.ThemeService.Apply(application, new global::Soltex.App.Settings.AppSettings());

        List<(string Name, Action Test)> tests =
        [
            ("Fluent light and dark palettes and shell styles are available", ShellChecks.ThemeResourcesAndPalettes),
            ("Suite launch options preserve explicit paths and reject malformed arguments", IntegrationChecks.LaunchOptions),
            ("Windows mixer construction and disposal do not activate audio services", IntegrationChecks.MixerConstructionAndResources),
            ("Windows mixer preserves composite session identity and editing boundaries", IntegrationChecks.SessionUpdates),
            ("Application routing UI requires explicit preflight and preserves device identity", IntegrationChecks.RoutingWindowIsInert),
            ("Windows mixer chooses initial visible controls and retains explicit missing devices", IntegrationChecks.InitialDeviceSelection),
            ("App settings survive corrupt and future files, normalize and round-trip", ShellChecks.SettingsStoreResilience),
            ("Normal startup wiring constructs, stays inert with default settings and shuts down cleanly", ShellChecks.NormalStartupWiringIsInert),
            ("Drift is reported once, in neutral words, and Put back reports its results", ShellChecks.DriftIsReportedOnceAndNeutrally),
            ("Settings keep large desktop groups and back up before import", ShellChecks.SettingsStoreKeepsLargeDesktopGroups),
            ("Import finds network and device locations and can strip them", ShellChecks.ImportFindsNetworkLocations),
            ("Search ranks by title and synonyms, requires every word and skips hidden pages", ShellChecks.SearchRanking),
            ("Navigation items are named, selectable and follow visibility settings", ShellChecks.NavigationIsNamedAndSelectable),
            ("Every interactive control on every page has an accessible name", ShellChecks.EveryControlIsNamed),
            ("My PC samples telemetry only while shown", ShellChecks.MyPcSamplesOnlyWhileShown),
            ("Search results reveal and focus the exact setting", ShellChecks.RevealFocusesTarget),
            ("Customize switches apply through UI Automation Toggle", ShellChecks.CustomizeToggleAppliesThroughAutomation),
            ("Customize finishes a change on its own thread without a UI context", ShellChecks.CustomizeToggleSurvivesMissingSyncContext),
            ("Customize pages head each group, link to Windows Settings and offer taskbar auto-hide", ShellChecks.CustomizeSectionsLinksAndAutoHide),
            ("A change Windows refuses says so on its row and links to Windows Settings", ShellChecks.CustomizeRowSaysWhenWindowsRefuses),            ("Elevated De-Windows helper accepts only its own request and known changes", ShellChecks.ElevatedHelperIsBounded),
            ("Taskbar helper restyles and restores the taskbar background in a XAML test host", TaskbarChecks.HelperRestylesAndRestoresTaskbarBackground),
            ("Taskbar helper applies, replaces and restores style rules in a XAML test host", TaskbarChecks.HelperAppliesAndRestoresStyleRules),
            ("Taskbar helper applies a look per taskbar window in a XAML test host", TaskbarChecks.HelperAppliesALookPerTaskbarWindow),
            ("Taskbar states: only enabled states count, in TranslucentTB-style precedence", TaskbarStateChecks.PrecedenceCountsOnlyEnabledStates),
            ("Taskbar states follow each display", TaskbarStateChecks.StatesFollowEachDisplay),
            ("Taskbar state hooks run only while a state needs them", TaskbarStateChecks.HooksOnlyWhileAStateNeedsThem),
            ("A 1.0 taskbar look is kept as taskbar states", TaskbarStateChecks.VersionOneLookIsKept),
            ("Taskbar states save through UI Automation", ShellChecks.TaskbarStatesSaveThroughAutomation),
            ("Taskbar look saves choices and shows only the rows that apply", ShellChecks.TaskbarLookSavesChoicesAndShowsRelevantRows),
            ("Taskbar details save choices through UI Automation and are searchable", ShellChecks.TaskbarDetailsSaveChoices),
            ("Taskbar detail rules are anchored to the taskbar and saved choices are normalized", TaskbarChecks.TweakCatalogIsAnchoredAndNormalized),
            ("Maximized look needs a chosen look, and helper problems show where they belong", TaskbarChecks.MaximizedLookNeedsAChosenLook),
            ("Scroll-for-volume hook starts and stops on its own thread", TaskbarChecks.ScrollVolumeHookStartsAndStops),
            ("Desktop groups and widgets are normalized when saved", DesktopChecks.SettingsAreNormalized),
            ("Desktop groups show folders and lists, widgets show readings, and both follow settings", DesktopChecks.ServiceShowsGroupsAndWidgets),
            ("Desktop page saves groups, widgets and arranging", ShellChecks.DesktopPageSavesGroupsAndWidgets),
            ("Folder groups read their folder only when it changes, once per burst of files", DesktopChecks.FolderGroupsReadOnlyWhenNeeded),
            ("Groups and widgets reopen after Explorer restarts or Windows closes them", DesktopChecks.SurfacesComeBackAfterExplorerRestarts),
            ("Explorer restart and display change messages settle into one event", DesktopChecks.DesktopEventsSettleBursts),
            ("Off-screen groups and widgets come back; the clock wakes once a minute", DesktopChecks.SurfacesStayOnScreen),
            ("Desktop page explains groups, saves names on Enter and keeps rows on rename", DesktopChecks.PageExplainsGroupsAndKeepsRows),
            ("Dark mode schedule switches only when attached and when the period changes", PersonalizeChecks.ScheduleSwitchesOnlyWhenThePeriodChanges),
            ("Windows default pointer scheme resolves to Windows' own pointers", PersonalizeChecks.PointerDefaultIsWindowsOwnScheme),
            ("Live wallpaper reports a missing video and does nothing until attached", PersonalizeChecks.WallpaperReportsWhatHappened),
            ("Window zones split the work area and fit a window's visible frame", ToolsChecks.ZonesSplitTheWorkAreaAndFitWindows),
            ("Batch rename plans, refuses clashes, applies and undoes", ToolsChecks.BatchRenamePlansAppliesAndUndoes),
            ("Text extractor reads text with Windows OCR", ToolsChecks.TextExtractorReadsText),
            ("Key remapping hook starts, maps and stops", ToolsChecks.KeyRemapperStartsMapsAndStops),
            ("Remapped keys carry real scan codes and extended flags", ToolsChecks.KeyRemapperSendsScanCodes),
            ("Startup apps include 32-bit entries for every user with their Run32 state", ToolsChecks.StartupAppsInclude32BitMachineEntries),
            ("PowerToys-class tools save their choices", ShellChecks.PowerToolsSaveChoices),
            ("Start folders keep Windows' order in an isolated registry key", PersonalizeChecks.StartFoldersKeepWindowsOrder),
            ("Dark mode schedule, pointer schemes and wallpaper settings", PersonalizeChecks.ScheduleAndSchemes),
            ("Start and Appearance extras show and save", ShellChecks.PersonalizeExtrasShowAndSave),
            ("Startup apps list every source and switch the user's own like Task Manager", ToolsChecks.StartupAppsListAndSwitch),
            ("Setup export and import keep app preferences and Windows options", ShellChecks.ProfileExportsAndImports),
            ("Setups exported by the first format keep their meaning after the switches were renamed", ShellChecks.OldProfilesKeepTheirMeaning),
            ("Pages render in light and dark at 100 and 200 percent", ShellChecks.PagesRenderInThemesAndScales),
            ("Microphone card names the app input and guides a capture-only test", MicrophoneChecks.CardDescribesRouteAndGuidesTest),
            ("Microphone setup saves the route, never starts audio and fixes the input in one click", MicrophoneChecks.SetupSavesRouteAndFixesInput),
            ("Processing editor keeps untouched values and presets", MicrophoneChecks.ProcessingEditorKeepsPresetsAndEdits),
            ("Playback stages routing until Apply and saves one channel's sound", PlaybackChecks.PlaybackStagesAppliesAndEditsSound),
            ("Microphone card explains the connection and shows refusals", MicrophoneChecks.CardExplainsConnectionAndRefusals),
            ("Playback shows refusals and the output all channels share", PlaybackChecks.PlaybackShowsRefusalsAndSharedOutput),
            ("Playback says why audio isn't connected and reconnects on Try again", PlaybackChecks.PlaybackExplainsNoConnectionAndRetries),
            ("Audio tabs fit every window width, text scale and theme", AudioLayoutChecks.TabsFitEveryWidthScaleAndTheme),
            ("Every page fits every window width and text scale, with the navigation rail collapsing when needed", PageLayoutChecks.MainPagesFitEveryWidthAndScale),
            ("Processing editors keep the safety limiter and smooth background through every edit", MicrophoneChecks.ProcessingEditorKeepsLimiterAndSmoothBackground),
            ("Sparkline renders bounded percentage values", SparklineRenders),
            ("Sparkline auto-scales unbounded throughput values", SparklineAutoScales),
            ("Render-smoke uses an unconstrained popup viewport", RenderSmokeUsesCanonicalViewport)
        ];

        // Opt-in: changes the real taskbar for a moment and restores it.
        if (Environment.GetEnvironmentVariable("SOLTEX_LIVE_TASKBAR") == "1")
            tests.Add(("Live: Explorer's taskbar turns clear and returns to Windows' look", TaskbarChecks.LiveExplorerClearAndRestore));

        int failed = 0, skipped = 0;
        Stopwatch suite = Stopwatch.StartNew();
        foreach ((string name, Action test) in tests)
        {
            if (Environment.GetEnvironmentVariable("SOLTEX_TEST_FILTER") is { Length: > 0 } filter && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Stopwatch testTimer = Stopwatch.StartNew();
            try
            {
                test();
                testTimer.Stop();
                Console.WriteLine($"PASS  {name} ({testTimer.Elapsed.TotalMilliseconds:F1} ms)");
            }
            catch (SkipException skip)
            {
                testTimer.Stop();
                skipped++;
                Console.WriteLine($"SKIP  {name}: {skip.Message}");
            }
            catch (Exception exception)
            {
                testTimer.Stop();
                failed++;
                Console.WriteLine($"FAIL  {name} ({testTimer.Elapsed.TotalMilliseconds:F1} ms)");
                Console.WriteLine("      " + exception.Message);
                if (Environment.GetEnvironmentVariable("SOLTEX_TEST_VERBOSE") == "1") Console.WriteLine(exception);
            }
        }

        suite.Stop();
        Console.WriteLine();
        Console.WriteLine($"{tests.Count - failed - skipped}/{tests.Count} tests passed" + (skipped > 0 ? $", {skipped} skipped." : "."));
        Console.WriteLine($"MEASURE app_control_suite tests={tests.Count} failed={failed} skipped={skipped} total_ms={suite.Elapsed.TotalMilliseconds:F1}");
        return failed == 0 ? 0 : 1;
    }

    private static void SparklineRenders()
    {
        Sparkline sparkline = new()
        {
            Values = [0, 18, 43, 37, 82, 64, 100],
            Stroke = Brushes.Coral,
            Fill = new SolidColorBrush(Color.FromArgb(40, 247, 111, 83)),
            StrokeThickness = 2
        };
        byte[] pixels = Render(sparkline, 320, 90);
        True(CountVisiblePixels(pixels) > 50, "The sparkline render did not produce visible pixels.");
    }

    private static void SparklineAutoScales()
    {
        Sparkline sparkline = new()
        {
            Values = [0, 4_096, 2_048, 16_384, 8_192],
            AutoScale = true,
            Stroke = Brushes.LightGreen,
            Fill = new SolidColorBrush(Color.FromArgb(32, 126, 208, 167)),
            StrokeThickness = 2
        };
        byte[] pixels = Render(sparkline, 320, 90);
        True(CountVisiblePixels(pixels) > 50, "The auto-scaled sparkline did not produce visible pixels.");
    }

    private static void RenderSmokeUsesCanonicalViewport()
    {
        Grid surface = new()
        {
            Background = Brushes.Black
        };
        TextBlock inheritedText = new()
        {
            Text = "Inherited foreground",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        surface.Children.Add(inheritedText);
        surface.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Magenta
        });
        Window constrainedHost = new()
        {
            Width = 1044,
            Height = 788,
            Foreground = Brushes.White,
            Content = surface
        };
        RenderSmokeCapture.ConfigureWindow(constrainedHost);
        constrainedHost.ShowInTaskbar = false;
        constrainedHost.WindowStartupLocation = WindowStartupLocation.Manual;
        constrainedHost.Left = -32_000;
        constrainedHost.Top = -32_000;
        constrainedHost.Show();

        RenderTargetBitmap bitmap;
        try
        {
            bitmap = RenderSmokeCapture.Capture(constrainedHost);
        }
        finally
        {
            constrainedHost.Close();
        }
        True(ReferenceEquals(constrainedHost.Content, surface),
            "Render-smoke replaced the product content surface.");
        True(constrainedHost.WindowStyle == WindowStyle.None && constrainedHost.ResizeMode == ResizeMode.NoResize,
            "Render-smoke did not configure a borderless, nonresizable popup window.");
        True(ReferenceEquals(TextElement.GetForeground(inheritedText), Brushes.White),
            "Render-smoke lost a descendant foreground inherited from the constrained host.");
        True(bitmap.PixelWidth == 1280 && bitmap.PixelHeight == 820,
            "Render-smoke did not use the canonical 1280x820 viewport.");

        byte[] pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, checked(bitmap.PixelWidth * 4), 0);
        int bottomRight = checked((((bitmap.PixelHeight - 8) * bitmap.PixelWidth) + bitmap.PixelWidth - 8) * 4);
        True(pixels[bottomRight] > 200 && pixels[bottomRight + 2] > 200 && pixels[bottomRight + 3] > 200,
            "Render-smoke clipped content that expanded beyond the constrained host viewport.");
    }

    internal static byte[] Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        byte[] pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(pixels, checked(width * 4), 0);
        return pixels;
    }

    internal static int CountVisiblePixels(byte[] pixels)
    {
        int visible = 0;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 0)
            {
                visible++;
            }
        }

        return visible;
    }

    internal static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

/// <summary>Thrown by a check that cannot run on this machine (for example, no OCR language installed). Reported as SKIP, never as PASS.</summary>
internal sealed class SkipException(string reason) : Exception(reason);
