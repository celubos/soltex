using System.Windows;

namespace Soltex.Audio.UI;

/// <summary>Resources are scoped to this audio surface, never merged into a suite's application.</summary>
public static class AudioTheme
{
    /// <summary>
    /// Set by a host application that supplies its own theme (the Soltex app). Audio dialogs then use the host's
    /// colours and control styles instead of the standalone audio palette, and follow the host's light and dark modes.
    /// </summary>
    public static bool UseHostResources { get; set; }

    public static void Apply(FrameworkElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (UseHostResources) { HostThemeBridge.Attach(target); return; }
        target.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/Soltex.Audio.UI;component/Themes/Audio.xaml", UriKind.Relative) });
    }

    /// <summary>
    /// Ticked check boxes under <paramref name="target"/> use the host's accent (its Studio.AccentBrush) instead of the
    /// Windows accent colour the Fluent theme uses, following the host's light and dark modes. For host views that are
    /// not themed as a whole by <see cref="Apply"/>.
    /// </summary>
    public static void UseHostCheckBoxAccent(FrameworkElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        HostThemeBridge.AttachCheckBoxAccent(target);
    }
}

/// <summary>
/// Maps the audio palette keys to the host's theme keys on one element. Each mapped key follows the host resource,
/// so a light, dark or high-contrast switch in the host updates the audio surface without reopening it.
/// </summary>
internal static class HostThemeBridge
{
    private static readonly (string Audio, string Host)[] Map =
    [
        ("CanvasBrush", "SolidBackgroundFillColorBaseBrush"),
        ("SidebarBrush", "Studio.RailBrush"),
        ("FieldBrush", "Studio.ControlBrush"),
        ("InsetBrush", "Studio.ControlBrush"),
        ("QuietBrush", "Studio.RaisedBrush"),
        ("SurfaceBrush", "CardBackgroundFillColorDefaultBrush"),
        ("RaisedBrush", "Studio.RaisedBrush"),
        ("ElevatedBrush", "Studio.RaisedBrush"),
        ("TrackBrush", "Studio.TrackBrush"),
        ("ScrollTrackBrush", "Studio.TrackBrush"),
        ("ScrollThumbBrush", "Studio.StrokeStrongBrush"),
        ("BorderBrush", "Studio.StrokeBrush"),
        ("StrongBorderBrush", "Studio.StrokeStrongBrush"),
        ("TextBrightBrush", "Studio.TextBrush"),
        ("TextBrush", "Studio.TextBrush"),
        ("TextSecondaryBrush", "Studio.TextSecondaryBrush"),
        ("MutedBrush", "Studio.TextSecondaryBrush"),
        ("QuietTextBrush", "Studio.TextTertiaryBrush"),
        ("AccentBrush", "Studio.AccentBrush"),
        ("AccentFocusBrush", "Studio.AccentTextBrush"),
        ("AccentDimBrush", "Studio.SelectedBrush"),
        ("AccentQuietBrush", "Studio.SelectedBrush"),
        ("OnAccentBrush", "Studio.OnAccentBrush"),
        ("SignalBrush", "Studio.SuccessBrush"),
        ("SignalTextBrush", "Studio.SuccessBrush"),
        ("WarningBrush", "Studio.CautionBrush"),
        ("DangerBrush", "Studio.CriticalBrush"),
        ("DangerTextBrush", "Studio.CriticalBrush"),
        ("SelectionBrush", "Studio.SelectedBrush"),
        // Shared with the suite's Studio look: meters, cards, state surfaces, channel colours and the fader.
        ("MeterBrush", "Studio.MeterBrush"),
        ("MeterHorizontalBrush", "Studio.MeterHorizontalBrush"),
        ("CardBrush", "Studio.CardBrush"),
        ("GoodSurfaceBrush", "Studio.SuccessSurfaceBrush"),
        ("WarningSurfaceBrush", "Studio.CautionSurfaceBrush"),
        ("DangerSurfaceBrush", "Studio.CriticalSurfaceBrush"),
        ("ChannelMasterBrush", "Studio.Channel.Master"),
        ("ChannelGameBrush", "Studio.Channel.Game"),
        ("ChannelChatBrush", "Studio.Channel.Chat"),
        ("ChannelMediaBrush", "Studio.Channel.Media"),
        ("ChannelAuxBrush", "Studio.Channel.Aux"),
        ("ChannelMicBrush", "Studio.Channel.Microphone"),
        ("FaderStyle", "Studio.Fader"),
        // The host's check boxes come from its Fluent theme, which paints a ticked box in the Windows accent colour (a
        // grey accent reads as disabled). Ticked boxes in the audio surface use the host's own accent instead.
        ("CheckBoxCheckBackgroundFillChecked", "Studio.AccentBrush"),
        ("CheckBoxCheckBackgroundFillCheckedPointerOver", "Studio.AccentTextBrush"),
        ("CheckBoxCheckBackgroundFillCheckedPressed", "Studio.AccentBrush"),
        ("CheckBoxCheckBackgroundStrokeChecked", "Studio.AccentBrush"),
        ("CheckBoxCheckBackgroundStrokeCheckedPointerOver", "Studio.AccentTextBrush"),
        ("CheckBoxCheckBackgroundStrokeCheckedPressed", "Studio.AccentBrush"),
        ("CheckBoxCheckGlyphForeground", "Studio.OnAccentBrush"),
        ("CheckBoxCheckGlyphForegroundPressed", "Studio.OnAccentBrush")
    ];

    private static readonly DependencyProperty[] Properties = Map
        .Select((pair, index) => DependencyProperty.RegisterAttached("HostResource" + index, typeof(object), typeof(HostThemeBridge),
            new PropertyMetadata(null, HostResourceChanged)))
        .ToArray();

    private static readonly Dictionary<DependencyProperty, string> AudioKeys =
        Properties.Select((property, index) => (property, index)).ToDictionary(item => item.property, item => Map[item.index].Audio);

    /// <summary>Points every audio palette key on <paramref name="target"/> at the matching host resource.</summary>
    public static void Attach(FrameworkElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        for (int i = 0; i < Map.Length; i++) target.SetResourceReference(Properties[i], Map[i].Host);
    }

    /// <summary>Only the check box keys: ticked boxes use the host's accent.</summary>
    public static void AttachCheckBoxAccent(FrameworkElement target)
    {
        ArgumentNullException.ThrowIfNull(target);
        for (int i = 0; i < Map.Length; i++)
            if (Map[i].Audio.StartsWith("CheckBox", StringComparison.Ordinal)) target.SetResourceReference(Properties[i], Map[i].Host);
    }

    private static void HostResourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not FrameworkElement element || !AudioKeys.TryGetValue(e.Property, out string? key)) return;
        // A host without this key leaves the audio default in place rather than painting nothing.
        if (e.NewValue is null || e.NewValue == DependencyProperty.UnsetValue) element.Resources.Remove(key);
        else element.Resources[key] = e.NewValue;
    }
}
