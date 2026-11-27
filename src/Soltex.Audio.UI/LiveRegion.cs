using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Soltex.Audio.UI;

/// <summary>
/// Makes screen readers read a piece of text when it changes, without moving focus: a UI Automation live region.
/// Setting <see cref="AutomationProperties.LiveSettingProperty"/> alone only describes the region; it is spoken when the
/// element raises <see cref="AutomationEvents.LiveRegionChanged"/>, which this does. The same API as the suite's
/// Soltex.App.Controls.LiveRegion (this assembly can't reference the suite), so audio surfaces work the same standalone.
/// <para>XAML: <c>&lt;TextBlock local:LiveRegion.IsLive="True" /&gt;</c>. Polite by default; for an error that needs
/// attention at once also set <see cref="AutomationProperties.LiveSettingProperty"/> to Assertive. Never make text that
/// changes on a timer (a countdown) a live region.</para>
/// <para>Text set while the element is being built is not announced; later changes are announced once the dispatcher is
/// idle (several quick changes are read once) and only while the element is visible.</para>
/// </summary>
public static class LiveRegion
{
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.RegisterAttached(
        "IsLive", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false, OnIsLiveChanged));

    public static bool GetIsLive(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsLiveProperty);
    }

    public static void SetIsLive(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsLiveProperty, value);
    }

    /// <summary>A copy of the watched text, kept up to date by a binding (no event handler keeps the element alive).</summary>
    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached(
        "Watched", typeof(object), typeof(LiveRegion), new PropertyMetadata(null, OnWatchedChanged));

    /// <summary>True once the element is built; the text it starts with is not news.</summary>
    private static readonly DependencyProperty ArmedProperty = DependencyProperty.RegisterAttached(
        "Armed", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false));

    /// <summary>True while an announcement waits for the dispatcher to be idle.</summary>
    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false));

    /// <summary>Raised for every announcement, whether or not a screen reader is listening (for tests).</summary>
    internal static event Action<UIElement>? Announced;

    /// <summary>Asks screen readers to read <paramref name="element"/> now, even if its text did not change.</summary>
    public static void Notify(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (AutomationProperties.GetLiveSetting(element) == AutomationLiveSetting.Off)
            AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
        if ((bool)element.GetValue(PendingProperty)) return;
        element.SetValue(PendingProperty, true);
        element.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Raise(element));
    }

    private static void Raise(UIElement element)
    {
        element.ClearValue(PendingProperty);
        if (!element.IsVisible) return;
        Announced?.Invoke(element);
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        AutomationPeer? peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static void OnIsLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if (e.NewValue is true)
        {
            if (AutomationProperties.GetLiveSetting(element) == AutomationLiveSetting.Off)
                AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
            string? path = element switch { TextBlock or TextBox => "Text", ContentControl => "Content", _ => null };
            element.ClearValue(ArmedProperty);
            if (path is not null)
                BindingOperations.SetBinding(element, WatchedProperty, new Binding(path) { Source = element, Mode = BindingMode.OneWay });
            if (element is FrameworkElement { IsLoaded: false } building)
            {
                building.Loaded -= ArmWhenLoaded;
                building.Loaded += ArmWhenLoaded;
            }
            else element.SetValue(ArmedProperty, true);
        }
        else
        {
            BindingOperations.ClearBinding(element, WatchedProperty);
            element.ClearValue(ArmedProperty);
            element.ClearValue(AutomationProperties.LiveSettingProperty);
        }
    }

    private static void ArmWhenLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= ArmWhenLoaded;
        if (GetIsLive(element)) element.SetValue(ArmedProperty, true);
    }

    private static void OnWatchedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && (bool)element.GetValue(ArmedProperty)) Notify(element);
    }
}
