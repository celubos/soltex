using System.Globalization;
using Microsoft.Win32;

namespace Soltex.App.Shell;

/// <summary>The look for one taskbar window (Shell_TrayWnd or Shell_SecondaryTrayWnd) in Explorer.</summary>
internal readonly record struct TaskbarWindowLook(nint Window, TaskbarLookState Look);

/// <summary>
/// A look per taskbar. The helper reads the optional REG_MULTI_SZ value "Taskbars" with the other settings, one entry per
/// taskbar window: <c>hwnd=&lt;decimal HWND&gt;;look=&lt;0-4&gt;;color=&lt;decimal ARGB&gt;;line=&lt;0|1&gt;</c>. A taskbar
/// the helper can match to an entry's window shows that entry's look; every other taskbar shows the global look. The
/// helper matches a taskbar to its window by size, so two taskbars of the same size (two displays with the same
/// resolution and scale) both keep the global look. Its report counts the background rectangles that used an entry
/// (<see cref="ReadMappedCount"/>).
/// </summary>
internal sealed partial class TaskbarHelper
{
    /// <summary>
    /// More window classes the helper treats as taskbars, comma-separated (the XAML test host's windows). Passed to the
    /// helper when it loads; Explorer's own Shell_TrayWnd and Shell_SecondaryTrayWnd always count.
    /// </summary>
    public string? TaskbarClasses { get; init; }

    /// <summary>
    /// Saves a look per taskbar window (or deletes the Taskbars value when <paramref name="perTaskbar"/> is empty), then
    /// the global look and style rules as <see cref="Write(TaskbarLookState, IReadOnlyList{TaskbarStyleRule}?)"/> does,
    /// which also starts a new request. The first entry for a window counts.
    /// </summary>
    public void Write(TaskbarLookState state, IReadOnlyList<TaskbarStyleRule>? rules, IReadOnlyList<TaskbarWindowLook> perTaskbar)
    {
        string[] entries = [.. perTaskbar.Where(p => p.Window != 0).Select(Encode)];
        using (RegistryKey settings = Registry.CurrentUser.CreateSubKey(key))
        {
            if (entries.Length > 0) settings.SetValue("Taskbars", entries, RegistryValueKind.MultiString);
            else settings.DeleteValue("Taskbars", throwOnMissingValue: false);
        }
        Write(state, rules);
    }

    /// <summary>
    /// How many background rectangles showed a Taskbars entry's look in the helper's last report (two per matched
    /// taskbar: its fill and its line), or null when there is no report or it comes from a helper without per-taskbar
    /// looks.
    /// </summary>
    public int? ReadMappedCount()
    {
        using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(key);
        if (settings?.GetValue("Status") is not string text) return null;
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith("mapped=", StringComparison.Ordinal) && int.TryParse(part.AsSpan("mapped=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int mapped))
                return mapped;
        return null;
    }

    /// <summary>One Taskbars entry. The window handle is written as a signed decimal number (the helper compares its low 32 bits).</summary>
    internal static string Encode(TaskbarWindowLook entry) => string.Create(CultureInfo.InvariantCulture,
        $"hwnd={(long)entry.Window};look={(int)entry.Look.Kind};color={entry.Look.Color};line={(entry.Look.Line ? 1 : 0)}");
}
