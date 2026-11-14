using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Soltex.Audio.UI;

namespace Soltex.Audio.App.Tests;

internal static class SharedWorkspaceChecks
{
    public static async Task ProcessingOverrideAsync(string workingDirectory)
    {
        int requests = 0;
        var workspace = new MixerWorkspace(new MixerHostOptions
        {
            PreferencesDirectory = Path.Combine(workingDirectory, "processing-override"),
            ProcessingRequested = () => requests++
        });
        var host = new Window { Content = workspace, Width = 1240, Height = 810 };
        Checks.PrepareOffscreen(host);
        try
        {
            workspace.ShowProcessing();
            host.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var button = Checks.Descendants(workspace).OfType<Button>().Single(control => Equals(control.Content, "Processing"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Checks.That(requests == 2, "Both the public Processing command and button must invoke the host override.");
            Checks.That(Checks.FieldIsNull(workspace, "_processingWindow"), "A host-owned Processing action must not create another child or audio worker.");
            Checks.That(!workspace.IsBackendInitialized && Checks.FieldIsNull(workspace, "_devices"), "Processing override must not initialize the channel backend.");
            await workspace.ShutdownAsync();
            workspace.ShowProcessing();
            Checks.That(requests == 2, "A stopped workspace must not dispatch new host Processing requests.");
        }
        finally { await workspace.ShutdownAsync(); host.Close(); }
    }

    public static async Task OwnershipAndLifetimeAsync(string workingDirectory)
    {
        string preferences = Path.Combine(workingDirectory, "embedded-host");
        var hostStyle = new Style(typeof(Button));
        hostStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Magenta));
        var outside = new Button { Content = "Unrelated suite action" };
        var workspace = new MixerWorkspace(new MixerHostOptions { PreferencesDirectory = preferences });
        var panel = new Grid(); panel.Children.Add(outside); panel.Children.Add(workspace);
        var host = new Window { Content = panel, Width = 1240, Height = 810 };
        host.Resources.Add(typeof(Button), hostStyle);
        Checks.PrepareOffscreen(host);
        int applicationResourceCount = Application.Current.Resources.MergedDictionaries.Count;
        EngineControlWindow? child = null;
        try
        {
            Checks.That(!Directory.Exists(preferences) && !workspace.IsBackendInitialized, "Constructing the embeddable workspace must not initialize backend or write preferences.");
            Checks.That(Checks.FieldIsNull(workspace, "_devices") && Checks.FieldIsNull(workspace, "_sonar"), "Constructor must not open native devices or discover GG.");
            host.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Checks.That(ReferenceEquals(outside.Style, hostStyle), "Audio styles leaked into unrelated host controls.");
            var settings = Checks.Descendants(workspace).OfType<Button>().Single(button => Equals(button.Content, "Settings"));
            Checks.That(!ReferenceEquals(settings.Style, hostStyle), "Mixer must use its own scoped theme.");
            Checks.That(Application.Current.Resources.MergedDictionaries.Count == applicationResourceCount, "Embedding must not modify application-level themes.");
            workspace.Activate(); workspace.Activate(); workspace.Deactivate(); workspace.Deactivate();
            Checks.That(!workspace.IsActive && !workspace.IsBackendInitialized, "Presentation activation must not silently initialize audio services.");
            Checks.That(!Checks.Field<DispatcherTimer>(workspace, "_meterTimer").IsEnabled && !Checks.Field<DispatcherTimer>(workspace, "_chatMixTimer").IsEnabled,
                "Inactive workspace must stop presentation polling.");
            // Invoke the production creation seam before Show so this fixture can force an offscreen position.
            child = workspace.CreateProcessingWindow();
            Checks.PrepareOffscreen(child);
            Checks.That(ReferenceEquals(child.Owner, host), "Processing must be owned by the real suite Window, never a detached hidden window.");
            Checks.That(ReferenceEquals(workspace.CreateProcessingWindow(), child), "Repeated Processing requests must reuse one owned worker/window.");
            var worker = Checks.Field<Thread>(child, "worker");
            child.Show();
            await Checks.UntilAsync(() => Checks.Named<TextBlock>(child, "Feedback").Text.StartsWith("DSP preview ready", StringComparison.Ordinal), "Owned Processing did not load its isolated preset path.");
            workspace.Deactivate();
            Checks.That(worker.IsAlive, "Navigating away must not dispose independent processing.");
            var shutdown = workspace.ShutdownAsync();
            Checks.That(ReferenceEquals(shutdown, workspace.ShutdownAsync()), "Host shutdown must be idempotent.");
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Checks.That(!worker.IsAlive && Checks.FieldIsNull(workspace, "_processingWindow"), "Host shutdown must await child closure and owned worker shutdown.");
            Checks.That(File.Exists(Path.Combine(preferences, "engine-dsp-v1.json")), "Processing must use the host's isolated preference directory.");
        }
        finally
        {
            await workspace.ShutdownAsync();
            if (child is not null) await child.ShutdownAsync();
            host.Close();
        }
    }
}
