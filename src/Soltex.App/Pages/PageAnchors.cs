using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Pages;

/// <summary>Maps search anchors to elements and reveals them: scroll into view, then focus the first focusable control.</summary>
internal sealed class PageAnchors
{
    private readonly Dictionary<string, FrameworkElement> anchors = new(StringComparer.Ordinal);

    public void Add(string anchor, FrameworkElement element) => anchors[anchor] = element;

    public bool Reveal(string anchor)
    {
        if (!anchors.TryGetValue(anchor, out FrameworkElement? element)) return false;
        element.BringIntoView();
        IInputElement? target = FirstFocusable(element);
        // UIElement.Focus sets logical focus even while the window is inactive, then keyboard focus when it activates.
        if (target is UIElement ui) ui.Focus();
        else if (target is not null) Keyboard.Focus(target);
        return true;
    }

    private static IInputElement? FirstFocusable(DependencyObject root)
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
