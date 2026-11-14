namespace Soltex.App.Settings;

/// <summary>
/// A desktop group (Fences-class): either a live view of <see cref="Folder"/> or the user's own list of
/// <see cref="Items"/> (files, folders or shortcuts dropped on it). Bounds are in device-independent pixels.
/// </summary>
public sealed record DesktopGroup(string Id, string Title, string? Folder, IReadOnlyList<string> Items,
    double Left, double Top, double Width, double Height, bool RolledUp = false)
{
    public const double MinimumWidth = 180, MinimumHeight = 120;
    public const int MaximumItems = 200;
}

public enum DesktopWidgetKind { Clock, System, Network }

/// <summary>A desktop widget (Rainmeter-class) and where it sits.</summary>
public sealed record DesktopWidget(DesktopWidgetKind Kind, double Left, double Top);
