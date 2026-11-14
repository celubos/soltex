using System.Globalization;
using System.Text.Json;

namespace Soltex.Audio.Sonar;

public sealed record SonarChatMix(double Balance, string State)
{
    public bool CanAdjust => State == "enabled";
}

public sealed partial class SonarClient
{
    public async Task<bool> GetFallbackEnabledAsync(CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await ReadAsync("FallbackSettings/isEnabled", cancellationToken).ConfigureAwait(false));
        return document.RootElement.GetBoolean();
    }

    public async Task<SonarChatMix> GetChatMixAsync(CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(await ReadAsync("v1/ChatMix", cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        double balance = root.GetProperty("balance").GetDouble();
        if (!double.IsFinite(balance) || balance is < -1 or > 1) throw new SonarCommandException("Sonar returned an invalid ChatMix balance.");
        return new(balance, root.GetProperty("state").GetString() ?? "unavailable");
    }

    public async Task<SonarChatMix> SetChatMixAsync(double balance, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(balance) || balance is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(balance));
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await GetChatMixAsync(cancellationToken).ConfigureAwait(false);
            if (!before.CanAdjust) throw new SonarCommandException("ChatMix requires Game and Chat on the same output and software control enabled.");
            await PutAsync("v1/ChatMix?balance=" + balance.ToString("R", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            var after = await GetChatMixAsync(cancellationToken).ConfigureAwait(false);
            if (Math.Abs(after.Balance - balance) > 0.002) throw new SonarCommandException("Sonar did not confirm the ChatMix balance.");
            return after;
        }
        finally { _commands.Release(); }
    }
}
