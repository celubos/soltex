namespace Soltex.App.Shell;

/// <summary>One choice of a taskbar tweak and the style rules it needs (none for Windows' own look).</summary>
public sealed record TaskbarTweakOption(string Key, string Label, IReadOnlyList<TaskbarStyleRule> Rules);

/// <summary>A taskbar detail (Windhawk Taskbar Styler-class) with its choices; the first choice is Windows' own look.</summary>
public sealed record TaskbarTweak(string Id, string Title, IReadOnlyList<TaskbarTweakOption> Options);

/// <summary>
/// The taskbar details offered on the Taskbar page. Element types and names come from the Windows 11 taskbar's own XAML
/// (mapped on Windows build 26300). Every rule is anchored under the taskbar part it belongs to,
/// so it cannot touch anything else in Explorer. A choice the running Windows does not have simply matches nothing.
/// </summary>
public static class TaskbarTweaks
{
    private const string Rectangle = "Windows.UI.Xaml.Shapes.Rectangle", TextBlock = "Windows.UI.Xaml.Controls.TextBlock",
        Image = "Windows.UI.Xaml.Controls.Image", Border = "Windows.UI.Xaml.Controls.Border",
        AppButton = "Taskbar.TaskListButton", StartButton = "Taskbar.ExperienceToggleButton", Clock = "SystemTray.DateTimeIconContent";
    private const string Visibility = "UIElement.Visibility", Collapsed = "Collapsed";

    private static TaskbarTweakOption Windows(string label) => new("default", label, []);

    public static IReadOnlyList<TaskbarTweak> All { get; } =
    [
        new("indicator", "Running app indicator",
        [
            Windows("Show"),
            new("hidden", "Hide", [new(AppButton, Rectangle, "RunningIndicator", Visibility, Collapsed)])
        ]),
        new("clock", "Clock",
        [
            Windows("Time and date"),
            new("time", "Time only", [new(Clock, TextBlock, "DateInnerTextBlock", Visibility, Collapsed)]),
            new("date", "Date only", [new(Clock, TextBlock, "TimeInnerTextBlock", Visibility, Collapsed)]),
            new("hidden", "Hidden", [new("", Clock, "", Visibility, Collapsed)])
        ]),
        new("iconsize", "App icon size",
        [
            Windows("Default"),
            new("small", "Small", [new(AppButton, Image, "Icon", "FrameworkElement.Width", "20"), new(AppButton, Image, "Icon", "FrameworkElement.Height", "20")]),
            new("large", "Large", [new(AppButton, Image, "Icon", "FrameworkElement.Width", "28"), new(AppButton, Image, "Icon", "FrameworkElement.Height", "28")])
        ]),
        new("corners", "Button corners",
        [
            Windows("Rounded"),
            new("square", "Square",
            [
                new(AppButton, Border, "BackgroundElement", "Border.CornerRadius", "0"),
                new(StartButton, Border, "BackgroundElement", "Border.CornerRadius", "0")
            ])
        ])
    ];

    /// <summary>The style rules for the saved choices (tweak id → option key); unknown ids and keys are ignored.</summary>
    public static IReadOnlyList<TaskbarStyleRule> RulesFor(IReadOnlyDictionary<string, string>? choices)
    {
        if (choices is null || choices.Count == 0) return [];
        List<TaskbarStyleRule> rules = [];
        foreach (TaskbarTweak tweak in All)
            if (choices.TryGetValue(tweak.Id, out string? key) && tweak.Options.FirstOrDefault(o => o.Key == key) is { } option)
                rules.AddRange(option.Rules);
        return rules;
    }

    /// <summary>Index of the saved choice for a tweak (0, Windows' look, when unset or unknown).</summary>
    public static int ChoiceIndex(TaskbarTweak tweak, IReadOnlyDictionary<string, string>? choices) =>
        choices is not null && choices.TryGetValue(tweak.Id, out string? key) ? Math.Max(0, tweak.Options.ToList().FindIndex(o => o.Key == key)) : 0;
}
