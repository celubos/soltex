using System.IO;

namespace Soltex.Audio.UI;

/// <summary>Host capabilities, not a second application lifetime or tray owner.</summary>
public sealed record MixerHostOptions
{
    public string PreferencesDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoltexAudio");
    public bool EnableGlobalShortcuts { get; init; }
    public Func<bool>? GetStartupEnabled { get; init; }
    public Action<bool>? SetStartupEnabled { get; init; }
    public Action? ProcessingRequested { get; init; }

    /// <summary>
    /// True when the mixer is a section inside a host page. The host supplies the page title and theme, so the mixer
    /// drops its own header and footer note, uses the host's colours and lets the host page scroll.
    /// Only honoured when passed to the constructor.
    /// </summary>
    public bool Embedded { get; init; }
}

public sealed record MixerOutput(string Id, string Label)
{
    /// <summary>What a screen reader announces for a list item.</summary>
    public override string ToString() => Label;
}
