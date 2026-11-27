using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Pages;

/// <summary>Maps search anchors to elements and reveals them: scroll into view, then focus the first focusable control.</summary>
internal sealed class PageAnchors
{
    private readonly Dictionary<string, (FrameworkElement Element, FrameworkElement? Fallback)> anchors = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="element"/> under <paramref name="anchor"/>. <paramref name="fallback"/> is revealed instead
    /// while the element is hidden (an empty list, say), so search always lands somewhere the keyboard can continue from.
    /// </summary>
    public void Add(string anchor, FrameworkElement element, FrameworkElement? fallback = null) => anchors[anchor] = (element, fallback);

    public bool Reveal(string anchor)
    {
        if (!anchors.TryGetValue(anchor, out (FrameworkElement Element, FrameworkElement? Fallback) entry)) return false;
        FrameworkElement element = entry.Fallback is { } fallback && !entry.Element.IsVisible ? fallback : entry.Element;
        element.BringIntoView();
        IInputElement? target = FirstFocusable(element);
        // UIElement.Focus sets logical focus even while the window is inactive, then keyboard focus when it activates.
        if (target is UIElement ui) ui.Focus();
        else if (target is not null) Keyboard.Focus(target);
        return true;
    }

    /// <summary>The first control under <paramref name="root"/> that can take focus now, in visual order.</summary>
    internal static IInputElement? FirstFocusable(DependencyObject root)
    {
        if (root is UIElement { Focusable: true, IsEnabled: true, IsVisible: true } focusable && root is not System.Windows.Controls.ScrollViewer)
            return focusable;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FirstFocusable(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}

/// <summary>
/// Keeps the keyboard where it was across work that hides, disables or rebuilds the focused control (an Undo button that
/// goes away once used, a list filled again). Taken before the work; <see cref="Restore"/> afterwards.
/// </summary>
internal readonly struct FocusSnapshot
{
    private readonly UIElement? origin;

    private FocusSnapshot(UIElement? origin) => this.origin = origin;

    /// <summary>Remembers <paramref name="origin"/> when it has focus now (keyboard focus, or logical focus in an inactive window).</summary>
    public static FocusSnapshot Take(UIElement origin) => new(HasFocus(origin) ? origin : null);

    /// <summary>True when <paramref name="element"/> or something inside it has keyboard or logical focus.</summary>
    public static bool HasFocus(UIElement element)
    {
        if (element.IsKeyboardFocusWithin) return true;
        DependencyObject? scope = FocusManager.GetFocusScope(element);
        return scope is not null && FocusManager.GetFocusedElement(scope) is DependencyObject focused
            && (ReferenceEquals(focused, element) || element.IsAncestorOf(focused));
    }

    /// <summary>
    /// When the remembered control had focus and the work took it away, focuses the first of the control itself and
    /// <paramref name="fallbacks"/> that can take focus now. A text among the fallbacks (a result line, when the buttons
    /// around it have nothing left to do) holds focus until the keyboard moves on, without becoming a tab stop. Focus the
    /// user moved to another control inside <paramref name="scope"/> meanwhile is left alone.
    /// </summary>
    public void Restore(FrameworkElement scope, params UIElement?[] fallbacks)
    {
        if (origin is null) return;
        if (origin is { IsEnabled: true, IsVisible: true } && HasFocus(origin)) return;
        DependencyObject? focusScope = FocusManager.GetFocusScope(scope);
        object? now = Keyboard.FocusedElement ?? (focusScope is null ? null : FocusManager.GetFocusedElement(focusScope));
        if (now is UIElement { IsVisible: true, IsEnabled: true } other && !ReferenceEquals(other, origin) && scope.IsAncestorOf(other)) return;
        foreach (UIElement? candidate in fallbacks.Prepend(origin))
        {
            if (candidate is not { IsEnabled: true, IsVisible: true }) continue;
            if (!candidate.Focusable)
            {
                if (candidate is not System.Windows.Controls.TextBlock text) continue;
                HoldFocus(text);
            }
            candidate.Focus();
            return;
        }
    }

    private static void HoldFocus(System.Windows.Controls.TextBlock text)
    {
        text.Focusable = true;
        KeyboardNavigation.SetIsTabStop(text, false);
        text.LostFocus += Release;
        static void Release(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.TextBlock left = (System.Windows.Controls.TextBlock)sender;
            left.LostFocus -= Release;
            left.Focusable = false;
        }
    }
}

/// <summary>Saving app settings from a page, without crashing when the settings file can't be written.</summary>
internal static class PageSettings
{
    /// <summary>
    /// Saves a change. When the file can't be written (disk full, folder locked), says so in a notice and returns false;
    /// the caller then shows the saved state again.
    /// </summary>
    public static bool TrySave(ShellContext context, Func<AppSettings, AppSettings> change)
    {
        try
        {
            context.Services.Settings.Update(change);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            context.ShowNotice(NoticeSeverity.Caution, "Your change could not be saved: " + error.Message);
            return false;
        }
    }
}

/// <summary>Search entries for destinations whose page has not been opened yet. Pages return the same lists.</summary>
internal static class StaticSearch
{
    public static IEnumerable<SearchEntry> For(Destination destination) => destination switch
    {
        Destination.MyPc => MyPcPage.Entries,
        _ when CustomizePage.Handles(destination) => CustomizePage.EntriesFor(destination),
        Destination.History => HistoryPage.Entries,
        Destination.Audio => AudioPage.Entries,
        Destination.Tools => ToolsPage.Entries,
        Destination.Desktop => DesktopPage.Entries,
        _ => SettingsPage.Entries
    };
}
