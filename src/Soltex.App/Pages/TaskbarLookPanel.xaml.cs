using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Pages;

/// <summary>
/// Taskbar look (TranslucentTB-style): background (clear, acrylic, blur or solid), colour, tint, a different look
/// while a window is maximized, and the line above the taskbar. Changes save to app settings; the taskbar service
/// applies them to the real taskbar.
/// </summary>
public partial class TaskbarLookPanel : UserControl
{
    private static readonly (string Label, TaskbarLookKind Kind)[] Looks =
    [
        ("Windows default", TaskbarLookKind.Default),
        ("Clear", TaskbarLookKind.Clear),
        ("Acrylic", TaskbarLookKind.Acrylic),
        ("Blur", TaskbarLookKind.Blur),
        ("Solid", TaskbarLookKind.Opaque)
    ];

    private static readonly (string Label, TaskbarLookKind? Kind)[] MaximizedLooks =
    [
        ("Keep this look", null),
        ("Windows default", TaskbarLookKind.Default),
        ("Solid", TaskbarLookKind.Opaque),
        ("Acrylic", TaskbarLookKind.Acrylic),
        ("Blur", TaskbarLookKind.Blur),
        ("Clear", TaskbarLookKind.Clear)
    ];

    private static readonly (string Name, uint Rgb)[] Palette =
    [
        ("Black", 0x000000), ("Graphite", 0x202020), ("White", 0xFFFFFF), ("Blue", 0x0067C0),
        ("Violet", 0x6B4FBB), ("Rose", 0xC42B5C), ("Green", 0x107C41)
    ];

    /// <summary>Windows 11 draws its taskbar with XAML; earlier versions are not supported.</summary>
    private static readonly bool Supported = Environment.OSVersion.Version.Build >= 22000;

    private readonly DispatcherTimer opacityDelay;
    private ShellContext? context;
    private bool loading;

    public TaskbarLookPanel()
    {
        InitializeComponent();
        LookList.ItemsSource = Looks.Select(l => l.Label).ToArray();
        MaximizedList.ItemsSource = MaximizedLooks.Select(l => l.Label).ToArray();
        foreach ((string name, uint rgb) in Palette)
        {
            Button swatch = new() { Style = (Style)FindResource("Swatch"), Tag = rgb, ToolTip = name, Background = new SolidColorBrush(ToColor(rgb)) };
            AutomationProperties.SetName(swatch, "Taskbar color: " + name);
            swatch.Click += Swatch_Click;
            Swatches.Children.Add(swatch);
        }
        opacityDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => SaveOpacity(), Dispatcher);
        opacityDelay.Stop();
    }

    internal void Attach(ShellContext shell)
    {
        context = shell;
        shell.Services.TaskbarLook.Changed += () => Dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    private AppSettings Current => context!.Services.Settings.Current;

    internal void Refresh()
    {
        if (context is null) return;
        loading = true;
        try
        {
            AppSettings s = Current;
            TaskbarLookService service = context.Services.TaskbarLook;
            LookList.SelectedIndex = Array.FindIndex(Looks, l => l.Kind == s.TaskbarLook);
            MaximizedList.SelectedIndex = Math.Max(0, Array.FindIndex(MaximizedLooks, l => l.Kind == s.TaskbarMaximizedLook));
            LineSwitch.IsChecked = s.TaskbarLine;
            bool styled = s.TaskbarLook != TaskbarLookKind.Default;
            ColorRow.Visibility = styled && s.TaskbarLook != TaskbarLookKind.Blur ? Visibility.Visible : Visibility.Collapsed;
            OpacityRow.Visibility = s.TaskbarLook is TaskbarLookKind.Clear or TaskbarLookKind.Acrylic ? Visibility.Visible : Visibility.Collapsed;
            OpacityLabel.Text = s.TaskbarLook == TaskbarLookKind.Clear ? "Tint" : "Tint strength";
            MaximizedRow.Visibility = styled ? Visibility.Visible : Visibility.Collapsed;
            int percent = (int)Math.Round((s.TaskbarColor >> 24) * 100 / 255.0);
            OpacitySlider.Value = percent;
            OpacityText.Text = percent + "%";
            uint rgb = s.TaskbarColor & 0xFFFFFF;
            Brush selected = (Brush)FindResource("Studio.AccentBrush");
            foreach (Button swatch in Swatches.Children.OfType<Button>())
                swatch.BorderBrush = (uint)swatch.Tag == rgb ? selected : Brushes.Transparent;

            // When only the details are in use, the details section shows the helper's problems instead.
            string? problem = !Supported ? "Needs Windows 11." : service.Unavailable ?? (TaskbarLookService.OnlyDetails(s) ? null : service.Problem);
            Note.Text = problem ?? "";
            Note.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
            bool usable = Supported && service.Available;
            LookList.IsEnabled = MaximizedList.IsEnabled = LineSwitch.IsEnabled = Swatches.IsEnabled = CustomButton.IsEnabled = OpacitySlider.IsEnabled = usable;
        }
        finally { loading = false; }
    }

    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (context is null || loading) return;
        context.Services.Settings.Update(change);
        Refresh();
    }

    private void Look_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LookList.SelectedIndex < 0) return;
        TaskbarLookKind look = Looks[LookList.SelectedIndex].Kind;
        // Acrylic without any tint looks like Blur; start it at a visible tint.
        Update(s => s with
        {
            TaskbarLook = look,
            TaskbarColor = look == TaskbarLookKind.Acrylic && s.TaskbarColor >> 24 == 0 ? 0x66000000 | (s.TaskbarColor & 0xFFFFFF) : s.TaskbarColor
        });
    }

    private void Maximized_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MaximizedList.SelectedIndex < 0) return;
        TaskbarLookKind? look = MaximizedLooks[MaximizedList.SelectedIndex].Kind;
        Update(s => s with { TaskbarMaximizedLook = look });
    }

    private void Line_Changed(object sender, RoutedEventArgs e) => Update(s => s with { TaskbarLine = LineSwitch.IsChecked == true });

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: uint rgb }) Update(s => s with { TaskbarColor = (s.TaskbarColor & 0xFF000000) | rgb });
    }

    private void Custom_Click(object sender, RoutedEventArgs e)
    {
        if (context is null) return;
        uint current = Current.TaskbarColor;
        using System.Windows.Forms.ColorDialog dialog = new()
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(unchecked((int)(current | 0xFF000000)))
        };
        System.Windows.Forms.NativeWindow owner = new();
        owner.AssignHandle(new WindowInteropHelper(Window.GetWindow(this)).Handle);
        try
        {
            if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
        }
        finally { owner.ReleaseHandle(); }
        uint rgb = unchecked((uint)dialog.Color.ToArgb()) & 0xFFFFFF;
        Update(s => s with { TaskbarColor = (s.TaskbarColor & 0xFF000000) | rgb });
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        OpacityText.Text = (int)OpacitySlider.Value + "%";
        // Save once the slider settles, not on every step of a drag.
        opacityDelay.Stop();
        opacityDelay.Start();
    }

    private void SaveOpacity()
    {
        opacityDelay.Stop();
        uint alpha = (uint)Math.Round(OpacitySlider.Value * 255 / 100);
        Update(s => s with { TaskbarColor = (alpha << 24) | (s.TaskbarColor & 0xFFFFFF) });
    }

    private static Color ToColor(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
