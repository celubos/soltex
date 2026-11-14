using System.Globalization;
using System.IO;
using Soltex.App.Settings;

namespace Soltex.App;

/// <summary>
/// Command line. User switches: --tray, --audio (alias --mixer), --quit, --profile-dir &lt;absolute&gt;.
/// Test switches (only with --render-smoke): --panel &lt;page&gt;, --theme light|dark, --scale &lt;100-200&gt;.
/// </summary>
internal sealed record SuiteLaunchOptions(
    string? ProfileDirectory, bool Audio, bool Tray, bool Quit, string? CapturePath, string? Panel,
    ThemePreference? Theme = null, int? Scale = null)
{
    public bool Mixer => Audio;

    public static readonly string[] PanelNames = ["my-pc", "customize", "history", "audio", "audio-apps", "tools", "settings", "search", "taskbar", "start", "explorer", "appearance", "multitasking", "desktop"];

    public static SuiteLaunchOptions Parse(string[] args)
    {
        string? profile = null, capture = null, panel = null;
        bool audio = false, tray = false, quit = false;
        ThemePreference? theme = null;
        int? scale = null;
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
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if ((panel is not null || theme is not null || scale is not null) && capture is null)
            throw new ArgumentException("--panel, --theme and --scale are only for --render-smoke.");
        return new(profile, audio, tray, quit, capture, panel, theme, scale);
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
