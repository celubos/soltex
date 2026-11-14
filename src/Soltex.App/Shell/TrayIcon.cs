using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Soltex.App.Shell;

internal enum QuitChoice { Cancel, Quit, QuitAndStopAsking }

/// <summary>
/// Notification-area icon. The menu is rebuilt each time it opens, so every item reflects the
/// current audio state instead of a stale snapshot.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly ShellWindow window;
    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon? ownedIcon;
    private bool disposed;

    public TrayIcon(ShellWindow window, ShellServices services)
    {
        _ = services;
        this.window = window;
        ownedIcon = Environment.ProcessPath is { } path ? System.Drawing.Icon.ExtractAssociatedIcon(path) : null;
        icon = new Forms.NotifyIcon
        {
            Icon = ownedIcon ?? System.Drawing.SystemIcons.Application,
            Text = ProductIdentity.DisplayName,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
            Visible = true
        };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) window.Dispatcher.Invoke(window.ShowSuite); };
        icon.ContextMenuStrip.Opening += (_, _) => Rebuild();
        Rebuild();
    }

    public void Rebuild()
    {
        if (disposed) return;
        Forms.ContextMenuStrip menu = icon.ContextMenuStrip!;
        foreach (Forms.ToolStripItem old in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) old.Dispose();
        menu.Items.Clear();
        menu.Items.Add("Open " + ProductIdentity.DisplayName, null, (_, _) => window.Dispatcher.Invoke(window.ShowSuite));
        menu.Items.Add("Open audio", null, (_, _) => window.Dispatcher.Invoke(window.OpenAudio));

        Pages.AudioPage? audio = window.Audio;
        if (audio is { IsEngineReady: true })
        {
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(new Forms.ToolStripMenuItem(audio.IsAudioRunning ? "Audio is running" : "Audio is stopped") { Enabled = false });
            menu.Items.Add(audio.IsAudioRunning ? "Stop audio" : "Start audio", null, async (_, _) =>
                await window.Dispatcher.Invoke(() => audio.IsAudioRunning ? audio.StopAudioAsync() : audio.StartAudioInBackgroundAsync()));
            menu.Items.Add("Mute or unmute playback", null, async (_, _) => await window.Dispatcher.Invoke(audio.ToggleMuteAsync));
            if (audio.Outputs.Count > 0)
            {
                Forms.ToolStripMenuItem outputs = new("Play through");
                foreach ((string id, string label) in audio.Outputs)
                    outputs.DropDownItems.Add(label, null, async (_, _) => await window.Dispatcher.Invoke(() => audio.SelectOutputAsync(id)));
                menu.Items.Add(outputs);
            }
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit " + ProductIdentity.DisplayName, null, (_, _) => window.Dispatcher.Invoke(window.RequestQuit));
    }

    public void ShowMessage(string title, string text)
    {
        if (!disposed) icon.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Info);
    }

    public static QuitChoice AskQuitWhileAudioRuns(Window owner)
    {
        Forms.TaskDialogButton quit = new("Quit");
        Forms.TaskDialogPage page = new()
        {
            Caption = ProductIdentity.DisplayName,
            Heading = "Audio is running",
            Text = "Quitting stops your audio routes, including your microphone. Close the window instead to keep audio running in the background.",
            Icon = Forms.TaskDialogIcon.Warning,
            Buttons = { quit, Forms.TaskDialogButton.Cancel },
            DefaultButton = Forms.TaskDialogButton.Cancel,
            Verification = new Forms.TaskDialogVerificationCheckBox("Don't ask me again")
        };
        IntPtr handle = new WindowInteropHelper(owner).Handle;
        Forms.TaskDialogButton result = handle == IntPtr.Zero
            ? Forms.TaskDialog.ShowDialog(page)
            : Forms.TaskDialog.ShowDialog(handle, page);
        if (result != quit) return QuitChoice.Cancel;
        return page.Verification.Checked ? QuitChoice.QuitAndStopAsking : QuitChoice.Quit;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        ownedIcon?.Dispose();
    }
}
