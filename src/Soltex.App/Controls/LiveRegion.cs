using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Soltex.App.Controls;

/// <summary>
/// Makes Narrator (and other screen readers) read a piece of text when it changes, without moving focus: a UI Automation
/// live region. Use it for notices and status lines that change on their own, such as "Saved", "No matches" or
/// "Audio could not start".
/// <para>In XAML (<c>xmlns:controls="clr-namespace:Soltex.App.Controls"</c>):</para>
/// <code>&lt;TextBlock controls:LiveRegion.IsLive="True" Text="{Binding Status}" /&gt;</code>
/// <para>In code: <c>LiveRegion.SetIsLive(statusText, true);</c> and, to read it again when the text is the same as
/// before (the same notice shown twice), <c>LiveRegion.Notify(statusText);</c>.</para>
/// <para>
/// Works on a <see cref="TextBlock"/> or <see cref="TextBox"/> (its Text) and a <see cref="ContentControl"/> (its Content).
/// Polite by default: the reader finishes what it is saying first. For urgent text set
/// <see cref="AutomationProperties.LiveSettingProperty"/> to <see cref="AutomationLiveSetting.Assertive"/> as well.
/// The reader speaks the element's accessible name, which for a TextBlock is its text unless
/// AutomationProperties.Name is set. Changes are announced once the dispatcher is idle, so several quick changes are read
/// once, and only while the element is visible (nothing is read from a hidden window).
/// </para>
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

    /// <summary>True once the binding has delivered the starting text, which is not announced.</summary>
    private static readonly DependencyProperty ArmedProperty = DependencyProperty.RegisterAttached(
        "Armed", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false));

    /// <summary>True while an announcement waits for the dispatcher to be idle.</summary>
    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(bool), typeof(LiveRegion), new PropertyMetadata(false));

    /// <summary>Raised for every announcement, whether or not a screen reader is listening (tests and diagnostics).</summary>
    internal static event Action<UIElement>? Announced;

    /// <summary>
    /// Asks screen readers to read <paramref name="element"/> now, even if its text did not change. Makes the element a
    /// polite live region if it was not one.
    /// </summary>
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
            string? path = element switch
            {
                TextBlock or TextBox => "Text",
                ContentControl => "Content",
                _ => null
            };
            element.ClearValue(ArmedProperty);
            if (path is not null)
                BindingOperations.SetBinding(element, WatchedProperty, new Binding(path) { Source = element, Mode = BindingMode.OneWay });
            // Text set while the element is still being built (XAML attributes, the first data binding) is not news.
            if (element is FrameworkElement { IsLoaded: false } building)
            {
                building.Loaded -= ArmWhenLoaded;
                building.Loaded += ArmWhenLoaded;
            }
            else element.SetValue(ArmedProperty, true);
        }
        else
        {
            // Disarm first: clearing the binding changes the watched value, which is not news either.
            element.ClearValue(ArmedProperty);
            BindingOperations.ClearBinding(element, WatchedProperty);
            element.ClearValue(AutomationProperties.LiveSettingProperty);
        }
    }

    private static void ArmWhenLoaded(object sender, RoutedEventArgs e)
    {
        FrameworkElement element = (FrameworkElement)sender;
        element.Loaded -= ArmWhenLoaded;
        if (GetIsLive(element)) element.SetValue(ArmedProperty, true);
    }

    private static void OnWatchedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && (bool)element.GetValue(ArmedProperty)) Notify(element);
    }
}
