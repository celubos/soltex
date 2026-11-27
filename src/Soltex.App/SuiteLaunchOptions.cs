using System.Globalization;
using System.IO;
using Soltex.App.Settings;

namespace Soltex.App;

/// <summary>
/// Command line. User switches: --tray, --audio (alias --mixer), --quit, --profile-dir &lt;absolute&gt;.
/// Test switches (only with --render-smoke): --panel &lt;page&gt;, --theme light|dark, --scale &lt;100-200&gt;,
/// --size &lt;width&gt;x&lt;height&gt; (window size in pixels; the canonical capture is 1280x820).
/// </summary>
internal sealed record SuiteLaunchOptions(
    string? ProfileDirectory, bool Audio, bool Tray, bool Quit, string? CapturePath, string? Panel,
    ThemePreference? Theme = null, int? Scale = null, int? CaptureWidth = null, int? CaptureHeight = null)
{
    public bool Mixer => Audio;

    public static readonly string[] PanelNames = ["my-pc", "customize", "history", "audio", "audio-mic", "audio-apps", "audio-sonar", "tools", "settings", "search", "taskbar", "start", "explorer", "appearance", "multitasking", "desktop"];

    /// <summary>Smallest and largest window a capture may use, in pixels.</summary>
    internal const int MinimumCaptureWidth = 640, MaximumCaptureWidth = 3840, MinimumCaptureHeight = 480, MaximumCaptureHeight = 2160;

    public static SuiteLaunchOptions Parse(string[] args)
    {
        string? profile = null, capture = null, panel = null;
        bool audio = false, tray = false, quit = false;
        ThemePreference? theme = null;
        int? scale = null, width = null, height = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mixer":
                case "--audio": audio = true; break;
                case "--tray": tray = true; break;
                case "--quit": quit = true; break;
                case "--profile-dir": profile = PathValue(args, ref i); break;
                case "--render-smoke": capture = PathValue(args, ref i); break;
                case "--panel":
                    panel = Value(args, ref i, "--panel requires a page name.");
                    if (!PanelNames.Contains(panel, StringComparer.Ordinal))
                        throw new ArgumentException("--panel must be one of: " + string.Join(", ", PanelNames) + ".");
                    break;
                case "--theme":
                    theme = Value(args, ref i, "--theme requires light or dark.") switch
                    {
                        "light" => ThemePreference.Light,
                        "dark" => ThemePreference.Dark,
                        _ => throw new ArgumentException("--theme must be light or dark.")
                    };
                    break;
                case "--scale":
                    if (!int.TryParse(Value(args, ref i, "--scale requires a percentage."), NumberStyles.None, CultureInfo.InvariantCulture, out int percent) ||
                        percent < AppSettings.MinimumScalePercent || percent > AppSettings.MaximumScalePercent)
                        throw new ArgumentException("--scale must be a whole percentage from 100 to 200.");
                    scale = percent;
                    break;
                case "--size":
                    (width, height) = SizeValue(Value(args, ref i, "--size requires a size such as 1280x820."));
                    break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if ((panel is not null || theme is not null || scale is not null || width is not null) && capture is null)
            throw new ArgumentException("--panel, --theme, --scale and --size are only for --render-smoke.");
        return new(profile, audio, tray, quit, capture, panel, theme, scale, width, height);
    }

    private static (int Width, int Height) SizeValue(string text)
    {
        string[] parts = text.Split('x');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) &&
            width is >= MinimumCaptureWidth and <= MaximumCaptureWidth && height is >= MinimumCaptureHeight and <= MaximumCaptureHeight)
            return (width, height);
        throw new ArgumentException($"--size must be <width>x<height> in pixels, from {MinimumCaptureWidth}x{MinimumCaptureHeight} to {MaximumCaptureWidth}x{MaximumCaptureHeight}.");
    }

    private static string Value(string[] args, ref int index, string message)
    {
        if (++index == args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException(message);
        return args[index];
    }

    private static string PathValue(string[] args, ref int index)
    {
        if (++index == args.Length || !Path.IsPathFullyQualified(args[index]))
            throw new ArgumentException("Provide an absolute path after " + args[index - 1]);
        return Path.GetFullPath(args[index]);
    }
}
