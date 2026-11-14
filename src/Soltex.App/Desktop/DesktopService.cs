using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Soltex.App.Settings;
using Soltex.Monitoring;

namespace Soltex.App.Desktop;

/// <summary>
/// Desktop groups and widgets: one window per saved group or widget, kept in step with app settings. Nothing runs when
/// there are none; the system and network widgets share one light sample every two seconds while they are shown.
/// When File Explorer restarts, every surface is opened again so it belongs to the new desktop; when screens change,
/// surfaces left off screen are brought back.
/// </summary>
public sealed class DesktopService : IDisposable
{
    private const int RebuildTries = 10;
    private readonly AppSettingsStore settings;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, DesktopGroupWindow> groups = [];
    private readonly Dictionary<DesktopWidgetKind, DesktopWidgetWindow> widgets = [];
    private readonly DispatcherTimer rebuildLater;
    private CancellationTokenSource? sampling;
    private DesktopEvents? events;
    private int rebuildTries;
    private bool attached, onDesktop, disposed;

    public DesktopService(AppSettingsStore settings)
    {
        this.settings = settings;
        dispatcher = Dispatcher.CurrentDispatcher;
        rebuildLater = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Rebuild(), dispatcher);
        rebuildLater.Stop();
        settings.Changed += next => { if (attached) Application.Current?.Dispatcher.InvokeAsync(() => Reconcile(next)); };
    }

    internal IReadOnlyDictionary<string, DesktopGroupWindow> GroupWindows => groups;
    internal IReadOnlyDictionary<DesktopWidgetKind, DesktopWidgetWindow> WidgetWindows => widgets;

    /// <summary>Shows the saved groups and widgets. <paramref name="desktopLayer"/> is false in tests (not attached to the desktop).</summary>
    public void Attach(bool desktopLayer = true)
    {
        attached = true;
        onDesktop = desktopLayer;
        Reconcile(settings.Current);
    }

    private void Reconcile(AppSettings current)
    {
        if (disposed) return;
        foreach (string id in groups.Keys.Except(current.DesktopGroups.Select(g => g.Id)).ToArray())
        {
            Retire(groups[id]);
            groups.Remove(id);
        }
        foreach (DesktopGroup group in current.DesktopGroups)
        {
            if (groups.TryGetValue(group.Id, out DesktopGroupWindow? window))
            {
                if (!Same(window.Group, group))
                {
                    bool moved = window.Group.Left != group.Left || window.Group.Top != group.Top;
                    window.Update(group);
                    if (moved) KeepOnScreenSoon(window);
                }
            }
            else
            {
                window = new DesktopGroupWindow(group, onDesktop);
                window.BoundsCommitted += bounds => SaveGroup(group.Id, g => g with { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = g.RolledUp ? g.Height : bounds.Height });
                window.RolledUpChanged += (id, rolled) => SaveGroup(id, g => g with { RolledUp = rolled });
                window.ItemsDropped += (id, paths) => SaveGroup(id, g => g with { Items = [.. g.Items.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).Take(DesktopGroup.MaximumItems)] });
                window.ItemRemoved += (id, path) => SaveGroup(id, g => g with { Items = [.. g.Items.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))] });
                window.Closed += Surface_Closed;
                groups[group.Id] = window;
                window.Show();
            }
            window.Arranging = current.DesktopArrange;
        }

        foreach (DesktopWidgetKind kind in widgets.Keys.Except(current.DesktopWidgets.Select(w => w.Kind)).ToArray())
        {
            Retire(widgets[kind]);
            widgets.Remove(kind);
        }
        foreach (DesktopWidget widget in current.DesktopWidgets)
        {
            if (widgets.TryGetValue(widget.Kind, out DesktopWidgetWindow? window))
            {
                if (window.Left != widget.Left || window.Top != widget.Top)
                {
                    window.Move(widget);
                    KeepOnScreenSoon(window);
                }
            }
            else
            {
                window = new DesktopWidgetWindow(widget, onDesktop);
                DesktopWidgetKind kind = widget.Kind;
                window.BoundsCommitted += bounds => settings.Update(s => s with
                {
                    DesktopWidgets = [.. s.DesktopWidgets.Select(w => w.Kind == kind ? w with { Left = bounds.Left, Top = bounds.Top } : w)]
                });
                window.Closed += Surface_Closed;
                widgets[widget.Kind] = window;
                window.Show();
            }
            window.Arranging = current.DesktopArrange;
        }
        UpdateSampling();
        UpdateEvents();
    }

    private static bool Same(DesktopGroup a, DesktopGroup b) =>
        a with { Items = [] } == b with { Items = [] } && a.Items.SequenceEqual(b.Items, StringComparer.OrdinalIgnoreCase);

    private void SaveGroup(string id, Func<DesktopGroup, DesktopGroup> change) =>
        settings.Update(s => s with { DesktopGroups = [.. s.DesktopGroups.Select(g => g.Id == id ? change(g) : g)] });

    private static void Retire(DesktopSurfaceWindow window)
    {
        window.Retired = true;
        try { window.Close(); }
        catch (InvalidOperationException) { } // already closed or closing
    }

    private void KeepOnScreenSoon(DesktopSurfaceWindow window) =>
        dispatcher.InvokeAsync(window.KeepOnScreen, DispatcherPriority.Background);

    /// <summary>A surface closed without the service asking: it went down with the old desktop. Open everything again shortly.</summary>
    private void Surface_Closed(object? sender, EventArgs e)
    {
        if (disposed || !attached || sender is DesktopSurfaceWindow { Retired: true } || dispatcher.HasShutdownStarted) return;
        rebuildLater.Stop();
        rebuildLater.Start();
    }

    /// <summary>
    /// Closes every group and widget and opens it again, so each is owned by the current desktop window. Called after
    /// File Explorer restarts (the old desktop window is gone) or when a surface was closed by Windows.
    /// </summary>
    internal void Rebuild()
    {
        rebuildLater.Stop();
        if (disposed || !attached) return;
        if (onDesktop && FindWindowW("Progman", null) == 0 && rebuildTries++ < RebuildTries)
        {
            rebuildLater.Start(); // the desktop is not back yet
            return;
        }
        rebuildTries = 0;
        foreach (DesktopGroupWindow window in groups.Values) Retire(window);
        foreach (DesktopWidgetWindow window in widgets.Values) Retire(window);
        groups.Clear();
        widgets.Clear();
        Reconcile(settings.Current);
    }

    private DesktopSurfaceWindow[] Surfaces() => [.. groups.Values.Cast<DesktopSurfaceWindow>().Concat(widgets.Values)];

    private void KeepAllOnScreen()
    {
        if (disposed) return;
        foreach (DesktopSurfaceWindow window in Surfaces()) window.KeepOnScreen();
    }

    /// <summary>
    /// Explorer announced a new taskbar. It also does that without restarting (after some display changes, say), so
    /// surfaces still owned by the current desktop are left as they are.
    /// </summary>
    private void ShellRestarted()
    {
        if (disposed || !attached) return;
        nint desktop = onDesktop ? FindWindowW("Progman", null) : 0;
        if (desktop != 0 && Surfaces().All(window => window.OwnedBy(desktop))) return;
        Rebuild();
    }

    /// <summary>Listens for Explorer restarts and screen changes only while something is on the desktop.</summary>
    private void UpdateEvents()
    {
        bool wanted = groups.Count + widgets.Count > 0;
        if (wanted && events is null)
        {
            events = new DesktopEvents();
            events.ShellRestarted += ShellRestarted;
            events.DisplayChanged += KeepAllOnScreen;
        }
        else if (!wanted && events is not null)
        {
            events.Dispose();
            events = null;
        }
    }

    private void UpdateSampling()
    {
        bool wanted = widgets.Keys.Any(k => k != DesktopWidgetKind.Clock);
        if (wanted && sampling is null)
        {
            sampling = new CancellationTokenSource();
            _ = SampleAsync(sampling.Token);
        }
        else if (!wanted && sampling is not null)
        {
            sampling.Cancel();
            sampling.Dispose();
            sampling = null;
        }
    }

    private async Task SampleAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                SystemTelemetrySnapshot sample = await SystemTelemetryProvider.CaptureAsync(TimeSpan.FromMilliseconds(250), includeProcesses: false, token).ConfigureAwait(true);
                foreach (DesktopWidgetWindow window in widgets.Values)
                    window.Show(sample.CpuPercent, sample.Memory?.UsedPercent, sample.Network?.ReceiveBytesPerSecond, sample.Network?.SendBytesPerSecond);
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(true); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>True when Windows hides the desktop icons ("Show desktop icons" off).</summary>
    public static bool DesktopIconsHidden()
    {
        using RegistryKey? advanced = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return advanced?.GetValue("HideIcons") is int hidden && hidden != 0;
    }

    /// <summary>Shows or hides the desktop icons the way the desktop's View menu does. Returns false if the desktop was not found.</summary>
    public static bool SetDesktopIconsHidden(bool hidden)
    {
        if (DesktopIconsHidden() == hidden) return true;
        nint view = DesktopView();
        if (view == 0) return false;
        _ = PostMessageW(view, 0x0111, 0x7402, 0); // WM_COMMAND: the View › Show desktop icons toggle
        return true;
    }

    private static nint DesktopView()
    {
        nint view = FindWindowExW(FindWindowW("Progman", null), 0, "SHELLDLL_DefView", null);
        // With a wallpaper slideshow, the icons live under a WorkerW window instead.
        for (nint worker = 0; view == 0 && (worker = FindWindowExW(0, worker, "WorkerW", null)) != 0;)
            view = FindWindowExW(worker, 0, "SHELLDLL_DefView", null);
        return view;
    }

    public void Dispose()
    {
        if (disposed) return;
        // Windows belong to the UI thread; close them there even if shutdown runs elsewhere.
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownFinished) dispatcher.Invoke(Dispose);
            return;
        }
        disposed = true;
        rebuildLater.Stop();
        sampling?.Cancel();
        sampling?.Dispose();
        sampling = null;
        events?.Dispose();
        events = null;
        foreach (DesktopGroupWindow window in groups.Values) Retire(window);
        foreach (DesktopWidgetWindow window in widgets.Values) Retire(window);
        groups.Clear();
        widgets.Clear();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
