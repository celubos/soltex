using System.Windows;
using Soltex.Audio.Sonar;

namespace Soltex.Audio.UI;

public partial class MixerWorkspace
{
    private LinkedOutputPolicySettings _outputPolicy = new();
    private CancellationTokenSource? _policyPending;
    private DateTimeOffset _lastPolicyWrite;
    private string? _expectedPolicyOutput;
    private string _policyStatus = "Automatic output selection is off.";

    private void DevicesClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || !_initialized) return;
        var available = (_snapshot?.Devices ?? Array.Empty<SonarDevice>()).Where(d => !d.IsVad && d.DataFlow == "render" && d.State == "active")
            .Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dialog = new DevicePolicyWindow(_outputPolicy, available, SaveOutputPolicyAsync) { Owner = Window.GetWindow(this) };
        ApplyDialogTheme(dialog); ShowOwnedDialog(dialog);
    }

    private async Task<string?> SaveOutputPolicyAsync(LinkedOutputPolicySettings next)
    {
        if (_disposed) return "Soltex is closing.";
        bool entered = false;
        try
        {
            await _operations.WaitAsync(_lifetime.Token); entered = true;
            if (_disposed) return "Soltex is closing.";
            if (next.Enabled)
            {
                if (_sonar is null) return "Connect to Sonar before enabling automatic selection.";
                if (LinkOutputs.IsChecked != true) return "Turn on Link playback outputs first. Separate channel choices stay saved.";
                if (next.PriorityEndpointIds.All(id => next.ExcludedEndpointIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
                    return "Choose at least one device with Use enabled and Exclude disabled.";
                if (await _sonar.GetFallbackEnabledAsync(_lifetime.Token))
                    return "Sonar's own device fallback is on. Turn it off in SteelSeries GG first so the two don't compete.";
                var current = await _sonar.GetSnapshotAsync(_lifetime.Token);
                if (_disposed) return "Soltex is closing.";
                if (LinkedOutput(current) is null) return "The playback channels use different outputs. Choose one playback device for all of them first.";
                next.LastObservedEndpointId = LinkedOutput(current);
            }
            // Keep discoveries and independent choices made while the dialog was open.
            foreach (var known in _outputPolicy.KnownDevices)
                if (next.KnownDevices.All(d => !Same(d.Id, known.Id))) next.KnownDevices.Add(known);
            foreach (var item in _outputPolicy.IndependentBusEndpointIds) next.IndependentBusEndpointIds[item.Key] = item.Value;
            next.Save();
            _outputPolicy = next.Copy();
            UpdateStateTimer();
            _policyStatus = next.Enabled ? "Configured linked output priorities are enabled." : "Automatic output selection is off.";
            StatusText.Text = _policyStatus; LinkedDevice.ToolTip = _policyStatus;
            if (_snapshot is not null) ScheduleOutputPolicy(_snapshot);
            return null;
        }
        catch (Exception ex) when (IsExpected(ex)) { return "Device policy was not saved: " + ex.Message; }
        finally { if (entered) _operations.Release(); }
    }

    private void ScheduleOutputPolicy(SonarSnapshot snapshot)
    {
        // A preview has no Sonar connection and must not record devices in the saved policy.
        if (_disposed || _preview) return;
        ObserveOutputPolicy(snapshot);
        _policyPending?.Cancel();
        if (!_outputPolicy.Enabled) return;
        _ = ReconcileOutputPolicyAsync();
    }

    private void ObserveOutputPolicy(SonarSnapshot snapshot)
    {
        bool changed = false;
        foreach (var device in snapshot.Devices.Where(d => !d.IsVad && d.DataFlow == "render"))
        {
            int index = _outputPolicy.KnownDevices.FindIndex(d => Same(d.Id, device.Id));
            var known = new SavedOutputDevice(device.Id, device.FriendlyName);
            if (index < 0) { _outputPolicy.KnownDevices.Add(known); changed = true; }
            else if (_outputPolicy.KnownDevices[index] != known) { _outputPolicy.KnownDevices[index] = known; changed = true; }
        }
        string? current = LinkedOutput(snapshot);
        if (current is not null && !Same(current, _outputPolicy.LastObservedEndpointId))
        {
            if (_outputPolicy.LastObservedEndpointId is not null && !Same(current, _expectedPolicyOutput))
            {
                // An explicit selection in GG/another controller wins over our priority list.
                _outputPolicy.ManualEndpointId = current;
                _outputPolicy.ExternalSelectionHold = _outputPolicy.ExcludedEndpointIds.Contains(current, StringComparer.OrdinalIgnoreCase);
            }
            _outputPolicy.LastObservedEndpointId = current;
            changed = true;
        }
        if (Same(current, _expectedPolicyOutput)) _expectedPolicyOutput = null;
        if (current is null && _outputPolicy.Enabled && !_outputPolicy.ExternalSelectionHold)
        {
            _outputPolicy.ExternalSelectionHold = true; changed = true;
            _policyStatus = "Automatic output selection paused: the playback channels use different outputs.";
        }
        if (changed) PersistOutputPolicy();
        LinkedDevice.ToolTip = _policyStatus;
    }

    private async Task ReconcileOutputPolicyAsync()
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _policyPending = pending;
        bool entered = false;
        try
        {
            await Task.Delay(350, pending.Token);
            await _operations.WaitAsync(pending.Token); entered = true;
            if (_disposed || pending.IsCancellationRequested || !_outputPolicy.Enabled || _sonar is null) return;
            if (LinkOutputs.IsChecked != true || _outputPolicy.ExternalSelectionHold)
            { _policyStatus = "Automatic output selection paused for an independent or excluded selection."; LinkedDevice.ToolTip = _policyStatus; return; }
            var snapshot = await _sonar.GetSnapshotAsync(_lifetime.Token);
            if (_disposed) return;
            ObserveOutputPolicy(snapshot);
            string? current = LinkedOutput(snapshot);
            if (current is null || _outputPolicy.ExternalSelectionHold) return;
            var playback = snapshot.Channels.Where(c => c.Id is "game" or "chat" or "media" or "aux").ToArray();
            if (playback.Length != 4 || playback.Any(c => !snapshot.Devices.Any(d => d.Id == c.VirtualDeviceId && d.IsVad && d.State == "active")))
            { _policyStatus = "Automatic selection waits for Sonar playback channels."; LinkedDevice.ToolTip = _policyStatus; return; }
            var endpoints = snapshot.Devices.Where(d => !d.IsVad && d.DataFlow == "render").Select(d =>
                new AudioEndpoint(d.FriendlyName, AudioEndpointDirection.Render, d.State == "active" ? AudioEndpointState.Active : AudioEndpointState.Unplugged, false, null, null)
                { EndpointId = d.Id, FullName = d.FriendlyName });
            var selection = OutputDevicePolicy.Select(new(_outputPolicy.PriorityEndpointIds, _outputPolicy.ExcludedEndpointIds,
                _outputPolicy.ManualEndpointId, _outputPolicy.ReturnToPreferred), endpoints, current);
            if (selection.EndpointId is null)
            { _policyStatus = "No configured playback device is available; saved priorities are retained."; StatusText.Text = _policyStatus; LinkedDevice.ToolTip = _policyStatus; return; }
            if (Same(selection.EndpointId, current)) return;
            if (DateTimeOffset.UtcNow - _lastPolicyWrite < TimeSpan.FromSeconds(2)) return;
            if (await _sonar.GetFallbackEnabledAsync(_lifetime.Token))
            {
                _outputPolicy.Enabled = false; PersistOutputPolicy();
                _policyStatus = "Automatic selection stopped: Sonar's native fallback is enabled."; StatusText.Text = _policyStatus; LinkedDevice.ToolTip = _policyStatus; return;
            }
            if (_disposed || pending.IsCancellationRequested) return;
            var latest = await _sonar.GetSnapshotAsync(_lifetime.Token);
            if (_disposed || pending.IsCancellationRequested) return;
            if (!Same(LinkedOutput(latest), current)) { Apply(latest); return; }
            _expectedPolicyOutput = selection.EndpointId;
            _lastPolicyWrite = DateTimeOffset.UtcNow;
            var confirmed = await _sonar.SetOutputAsync("game", selection.EndpointId, linked: true, _lifetime.Token);
            if (_disposed) return;
            Apply(confirmed);
            _policyStatus = "Linked output confirmed · " + selection.Reason;
            StatusText.Text = _policyStatus; LinkedDevice.ToolTip = _policyStatus;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!_disposed && _expectedPolicyOutput is not null)
            { _outputPolicy.Enabled = false; _expectedPolicyOutput = null; PersistOutputPolicy(); }
            if (!_disposed && ReferenceEquals(_policyPending, pending))
            { _policyStatus = "Automatic output change was not confirmed: " + ex.Message; StatusText.Text = _policyStatus; LinkedDevice.ToolTip = _policyStatus; }
        }
        finally
        {
            if (entered) _operations.Release();
            if (ReferenceEquals(_policyPending, pending)) _policyPending = null;
        }
    }

    private Task ManualOutputAsync(string bus, string id, bool linked, ChannelModel? channel = null)
    {
        _policyPending?.Cancel();
        return CommandAsync(client => client.SetOutputAsync(bus, id, linked, _lifetime.Token), channel, () =>
        {
            if (bus == "mic") return;
            _outputPolicy.ManualEndpointId = id;
            _outputPolicy.ExternalSelectionHold = !linked || _outputPolicy.ExcludedEndpointIds.Contains(id, StringComparer.OrdinalIgnoreCase);
            if (!linked) _outputPolicy.IndependentBusEndpointIds[bus] = id;
            else _outputPolicy.LastObservedEndpointId = id;
            PersistOutputPolicy();
        });
    }

    private void PersistOutputPolicy()
    {
        try { _outputPolicy.Save(); }
        catch (Exception ex) when (IsExpected(ex))
        {
            _outputPolicy.Enabled = false;
            _policyStatus = "Automatic selection disabled because device preferences could not be saved: " + ex.Message;
            StatusText.Text = _policyStatus;
        }
    }

    private static string? LinkedOutput(SonarSnapshot snapshot)
    {
        var channels = snapshot.Channels.Where(c => c.Id is "game" or "chat" or "media" or "aux").ToArray();
        if (channels.Length != 4 || channels.Any(c => string.IsNullOrWhiteSpace(c.DeviceId))) return null;
        var ids = channels.Select(c => c.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
