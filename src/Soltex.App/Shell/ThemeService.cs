using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Soltex.App.Settings;

namespace Soltex.App.Shell;

/// <summary>
/// Applies appearance preferences. WPF's Fluent theme supplies the control behaviour; the Studio palette is merged after
/// it so every standard control and the suite's own controls share one look. High contrast keeps Windows' own colours
/// untouched. Safe to call often: nothing is replaced when the result would be the same.
/// </summary>
internal static class ThemeService
{
    /// <summary>Length of the Studio controls' own animations (the switch knob); zero when animations are off.</summary>
    internal const string MotionDurationKey = "Studio.MotionDuration";
    private static readonly Duration Motion = new(TimeSpan.FromMilliseconds(120));

    private static readonly Uri StudioControls = new("/Soltex;component/Themes/Studio.Controls.xaml", UriKind.Relative);
    private static readonly Uri StudioDark = new("/Soltex;component/Themes/Studio.Dark.xaml", UriKind.Relative);
    private static readonly Uri StudioLight = new("/Soltex;component/Themes/Studio.Light.xaml", UriKind.Relative);
    private static readonly Uri StudioHighContrast = new("/Soltex;component/Themes/Studio.HighContrast.xaml", UriKind.Relative);

    public static bool AnimationsEnabled { get; private set; } = true;

    /// <summary>True when the Studio dark palette is in use (the last Apply resolved to dark).</summary>
    public static bool IsDark { get; private set; } = true;

    public static void Apply(Application application, AppSettings settings)
    {
#pragma warning disable WPF0001 // Fluent ThemeMode is the supported .NET 9+ theme API; marked experimental.
        ThemeMode mode = settings.Theme switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        if (application.ThemeMode != mode) application.ThemeMode = mode;
#pragma warning restore WPF0001

        bool compact = settings.Density == DensityPreference.Compact;
        ResourceDictionary resources = application.Resources;
        Set(resources, "Shell.PagePadding", compact ? new Thickness(24, 16, 24, 20) : new Thickness(40, 28, 40, 36));
        Set(resources, "Shell.CardPadding", compact ? new Thickness(16, 12, 16, 14) : new Thickness(22, 18, 22, 20));
        Set(resources, "Shell.CardGap", compact ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 0, 16));
        Set(resources, "Shell.RowGap", compact ? new Thickness(0, 4, 0, 4) : new Thickness(0, 8, 0, 8));
        Set(resources, "Shell.NavItemHeight", compact ? 36d : 44d);

        IsDark = settings.Theme switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => !SystemUsesLightApps()
        };
        AnimationsEnabled = settings.Motion == MotionPreference.System && SystemParameters.ClientAreaAnimation;
        // Read by Studio.Controls when its styles are first used, so it must be set before that dictionary is merged.
        bool motionChanged = Set(resources, MotionDurationKey, AnimationsEnabled ? Motion : new Duration(TimeSpan.Zero));
        ApplyStudio(resources, SystemParameters.HighContrast ? StudioHighContrast : IsDark ? StudioDark : StudioLight, motionChanged);
    }

    /// <summary>Sets a resource only when its value differs, so unchanged values do not refresh every element. True when set.</summary>
    private static bool Set(ResourceDictionary resources, string key, object value)
    {
        if (resources.Contains(key) && Equals(resources[key], value)) return false;
        resources[key] = value;
        return true;
    }

    private static void ApplyStudio(ResourceDictionary resources, Uri palette, bool motionChanged)
    {
        // Already in place (and still last, after any Fluent dictionary): re-merging would re-template every control.
        int count = resources.MergedDictionaries.Count;
        if (!motionChanged && count >= 2
            && Same(resources.MergedDictionaries[count - 2].Source, palette)
            && Same(resources.MergedDictionaries[count - 1].Source, StudioControls))
            return;

        for (int i = resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (resources.MergedDictionaries[i].Source?.OriginalString.Contains("/Themes/Studio.", StringComparison.Ordinal) == true)
                resources.MergedDictionaries.RemoveAt(i);
        }
        // Studio controls are always present (pages reference them); in high contrast the palette maps them to system colours.
        // Appended last so the palette's Fluent keys and AccentButtonStyle win over the Fluent theme dictionary.
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = palette });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = StudioControls });
    }

    private static bool Same(Uri? merged, Uri wanted) =>
        merged is not null && merged.OriginalString.EndsWith(wanted.OriginalString, StringComparison.OrdinalIgnoreCase);

    private static bool SystemUsesLightApps()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Scales the whole window content; text and controls grow together so nothing clips.</summary>
    public static void ApplyScale(FrameworkElement root, AppSettings settings)
    {
        double factor = settings.ScalePercent / 100d;
        root.LayoutTransform = factor == 1 ? Transform.Identity : new ScaleTransform(factor, factor);
    }
}
