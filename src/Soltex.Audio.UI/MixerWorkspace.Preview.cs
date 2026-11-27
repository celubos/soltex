using Soltex.Audio.Sonar;

namespace Soltex.Audio.UI;

public partial class MixerWorkspace
{
    private bool _preview;

    /// <summary>True when the workspace shows supplied state (<see cref="ShowPreview"/>) instead of a Sonar connection.</summary>
    public bool IsPreview => _preview;

    /// <summary>
    /// Shows supplied Sonar state through the normal presentation path without connecting to Sonar, reading devices or
    /// saving preferences. For render captures and layout checks; controls stay inert because no Sonar connection exists.
    /// </summary>
    public void ShowPreview(SonarSnapshot snapshot, SonarChatMix chatMix, IReadOnlyDictionary<string, double>? peaks = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(chatMix);
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialization is not null) throw new InvalidOperationException("A workspace connected to Sonar cannot show a preview.");
        _preview = true;
        Apply(snapshot);
        ApplyChatMix(chatMix);
        if (peaks is null) return;
        foreach (var channel in _model.Channels)
            if (peaks.TryGetValue(channel.Id, out double peak)) channel.Peak = peak;
    }

    /// <summary>Shows what the workspace looks like when Sonar can't be reached, without trying to reach it.</summary>
    public void ShowUnavailablePreview(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialization is not null) throw new InvalidOperationException("A workspace connected to Sonar cannot show a preview.");
        _preview = true;
        Disconnected(reason);
    }
}
