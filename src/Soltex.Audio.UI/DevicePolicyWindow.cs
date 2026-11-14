using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Soltex.Audio.UI;

internal sealed record SavedOutputDevice(string Id, string Name);

internal sealed class LinkedOutputPolicySettings
{
    private string FileName = "";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool ReturnToPreferred { get; set; } = true;
    public string? ManualEndpointId { get; set; }
    public string? LastObservedEndpointId { get; set; }
    public bool ExternalSelectionHold { get; set; }
    public List<SavedOutputDevice> KnownDevices { get; set; } = [];
    public List<string> PriorityEndpointIds { get; set; } = [];
    public List<string> ExcludedEndpointIds { get; set; } = [];
    public Dictionary<string, string> IndependentBusEndpointIds { get; set; } = new(StringComparer.Ordinal);
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PersistenceError { get; private set; }

    public LinkedOutputPolicySettings Copy() => new()
    {
        Version = Version, Enabled = Enabled, ReturnToPreferred = ReturnToPreferred, ManualEndpointId = ManualEndpointId,
        LastObservedEndpointId = LastObservedEndpointId, ExternalSelectionHold = ExternalSelectionHold,
        KnownDevices = [.. KnownDevices], PriorityEndpointIds = [.. PriorityEndpointIds], ExcludedEndpointIds = [.. ExcludedEndpointIds],
        IndependentBusEndpointIds = new(IndependentBusEndpointIds, StringComparer.Ordinal), PersistenceError = PersistenceError, FileName = FileName
    };

    public static LinkedOutputPolicySettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new() { FileName = path };
            var settings = JsonSerializer.Deserialize<LinkedOutputPolicySettings>(File.ReadAllText(path)) ?? throw new JsonException("Empty device preferences.");
            if (settings.Version != 1 || settings.KnownDevices is null || settings.PriorityEndpointIds is null || settings.ExcludedEndpointIds is null || settings.IndependentBusEndpointIds is null ||
                settings.KnownDevices.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name)) ||
                settings.PriorityEndpointIds.Any(string.IsNullOrWhiteSpace) || settings.ExcludedEndpointIds.Any(string.IsNullOrWhiteSpace))
                throw new JsonException("Unsupported or incomplete device preferences.");
            settings.FileName = path; return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        { return new() { FileName = path, PersistenceError = "Saved device preferences could not be read and have been preserved: " + exception.Message }; }
    }

    public void Save()
    {
        if (PersistenceError is not null) throw new IOException(PersistenceError);
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        string temporary = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, this, Json); stream.Flush(flushToDisk: true); }
            Soltex.Shared.AtomicFileReplace.Move(temporary, FileName);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}

internal sealed class PolicyDeviceRow(SavedOutputDevice device, bool available, bool included, bool excluded) : ObservableModel
{
    private bool _included = included, _excluded = excluded;
    public string Id => device.Id;
    public string Label => device.Name + (available ? "" : " · unavailable") + " · " + device.Id[Math.Max(0, device.Id.Length - 9)..];
    public bool Included { get => _included; set => Set(ref _included, value); }
    public bool Excluded { get => _excluded; set => Set(ref _excluded, value); }
}

internal sealed class DevicePolicyWindow : Window
{
    private readonly ObservableCollection<PolicyDeviceRow> _rows = [];

    public DevicePolicyWindow(LinkedOutputPolicySettings settings, IReadOnlySet<string> available, Func<LinkedOutputPolicySettings, Task<string?>> save)
    {
        Title = "Playback device priorities";
        Width = 760; Height = 620; MinWidth = 660; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var draft = settings.Copy();
        var panel = new DockPanel { Margin = new Thickness(22) };
        Content = panel;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Linked playback outputs", FontSize = 22, FontWeight = FontWeights.SemiBold });
        top.Children.Add(new TextBlock { Text = "Applies to Game, Chat, Media and Aux together. Separate channel choices are kept. Windows defaults don't change.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) });
        var enabled = new CheckBox { Content = "Automatically select a configured playback device", IsChecked = draft.Enabled, Margin = new Thickness(0, 0, 0, 8) }; top.Children.Add(enabled);
        var returns = new CheckBox { Content = "Return to a preferred device when it reconnects", IsChecked = draft.ReturnToPreferred, Margin = new Thickness(0, 0, 0, 10) }; top.Children.Add(returns);
        var pinLabel = new TextBlock { Text = PinText(draft), TextWrapping = TextWrapping.Wrap };
        top.Children.Add(pinLabel);
        var clear = new Button { Content = "Clear manual selection and resume priority order", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 12) };
        clear.Click += (_, _) => { draft.ManualEndpointId = null; draft.ExternalSelectionHold = false; pinLabel.Text = PinText(draft); };
        top.Children.Add(clear);
        top.Children.Add(new TextBlock { Text = "Top to bottom priority · Use admits a device · Exclude always prevents automatic selection", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var status = new TextBlock { Text = settings.PersistenceError ?? "Turn off Sonar's own device fallback in SteelSeries GG before you turn this on.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) }; bottom.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; bottom.Children.Add(buttons);
        var up = new Button { Content = "Move up", Margin = new Thickness(4) }; var down = new Button { Content = "Move down", Margin = new Thickness(4) }; var apply = new Button { Content = "Save policy", Margin = new Thickness(4) };
        buttons.Children.Add(up); buttons.Children.Add(down); buttons.Children.Add(apply);
        var list = new ListBox { ItemsSource = _rows, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var factory = new FrameworkElementFactory(typeof(DockPanel));
        var use = new FrameworkElementFactory(typeof(CheckBox)); use.SetValue(ContentControl.ContentProperty, "Use"); use.SetValue(FrameworkElement.WidthProperty, 55d); use.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(PolicyDeviceRow.Included)) { Mode = System.Windows.Data.BindingMode.TwoWay }); factory.AppendChild(use);
        var exclude = new FrameworkElementFactory(typeof(CheckBox)); exclude.SetValue(ContentControl.ContentProperty, "Exclude"); exclude.SetValue(FrameworkElement.WidthProperty, 80d); exclude.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(PolicyDeviceRow.Excluded)) { Mode = System.Windows.Data.BindingMode.TwoWay }); factory.AppendChild(exclude);
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PolicyDeviceRow.Label))); label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); factory.AppendChild(label);
        list.ItemTemplate = new DataTemplate { VisualTree = factory }; panel.Children.Add(list);
        foreach (string id in draft.PriorityEndpointIds.Concat(draft.KnownDevices.Select(d => d.Id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var device = draft.KnownDevices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) ?? new(id, "Saved device");
            _rows.Add(new(device, available.Contains(id), draft.PriorityEndpointIds.Contains(id, StringComparer.OrdinalIgnoreCase), draft.ExcludedEndpointIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
        }
        up.Click += (_, _) => { int i = list.SelectedIndex; if (i > 0) { _rows.Move(i, i - 1); list.SelectedIndex = i - 1; } };
        down.Click += (_, _) => { int i = list.SelectedIndex; if (i >= 0 && i + 1 < _rows.Count) { _rows.Move(i, i + 1); list.SelectedIndex = i + 1; } };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try
            {
                draft.Enabled = enabled.IsChecked == true; draft.ReturnToPreferred = returns.IsChecked == true;
                draft.PriorityEndpointIds = _rows.Where(row => row.Included).Select(row => row.Id).ToList();
                draft.ExcludedEndpointIds = _rows.Where(row => row.Excluded).Select(row => row.Id).ToList();
                string? error = await save(draft);
                if (error is null) Close(); else status.Text = error;
            }
            finally { apply.IsEnabled = true; }
        };
    }

    private static string PinText(LinkedOutputPolicySettings settings) => settings.ManualEndpointId is null ? "No manual output pinned." :
        "Manual selection: " + (settings.KnownDevices.FirstOrDefault(d => d.Id == settings.ManualEndpointId)?.Name ?? "Saved device") +
        (settings.ExternalSelectionHold ? " · automatic switching paused for this selection" : " · retained while disconnected");
}
