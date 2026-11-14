using System.Windows;
using System.Windows.Threading;
using Soltex.Audio.Sonar;

namespace Soltex.Audio.UI;

public partial class MixerWorkspace
{
    private readonly DispatcherTimer _chatMixTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _chatMixApplying, _chatMixReading;
    private CancellationTokenSource? _chatMixPending;
    private EngineControlWindow? _processingWindow;

    private void InitializeChatMix()
    {
        PreviewKeyDown += MixerShortcut;
        _chatMixTimer.Tick += ChatMixTick;
    }

    private void CloseChatMix()
    {
        _chatMixTimer.Stop();
        _chatMixTimer.Tick -= ChatMixTick;
        _chatMixPending?.Cancel();
    }

    private async void ChatMixTick(object? sender, EventArgs e)
    {
        if (!_active || _disposed || !IsVisible || _chatMixReading || _chatMixPending is not null || _sonar is null) return;
        _chatMixReading = true;
        bool held = false;
        try
        {
            await _operations.WaitAsync(_lifetime.Token); held = true;
            if (_disposed || _sonar is null) return;
            var state = await _sonar.GetChatMixAsync(_lifetime.Token);
            if (!_disposed && _chatMixPending is null) ApplyChatMix(state);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!_disposed) { ChatMixSlider.IsEnabled = false; ChatMixStatus.Text = "ChatMix isn't available. Choose Reconnect."; }
        }
        finally { if (held) _operations.Release(); _chatMixReading = false; }
    }

    private void ApplyChatMix(SonarChatMix state)
    {
        _chatMixApplying = true;
        try
        {
            ChatMixSlider.Value = state.Balance;
            ChatMixSlider.IsEnabled = state.CanAdjust && _model.Channels.Where(c => c.Id is "game" or "chat").All(c => c.IsReady);
            ChatMixStatus.Text = state.State switch
            {
                "enabled" => Math.Abs(state.Balance) < 0.005 ? "Centered · Media and Aux unaffected" : $"{Math.Abs(state.Balance) * 100:0}% toward {(state.Balance < 0 ? "Game" : "Chat")}",
                "differentDeviceSelected" => "Choose the same output for Game and Chat",
                "finiteWheel" => "Controlled by the device's ChatMix wheel",
                _ => "Select an available Game and Chat output"
            };
        }
        finally { _chatMixApplying = false; }
    }

    private void ChatMixChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded && !_chatMixApplying && ChatMixSlider.IsEnabled) QueueChatMix(e.NewValue);
    }

    private void ChatMixCenter(object sender, RoutedEventArgs e)
    {
        if (ChatMixSlider.IsEnabled) QueueChatMix(0);
    }

    private async void QueueChatMix(double value)
    {
        if (_disposed || !double.IsFinite(value) || value is < -1 or > 1) return;
        _chatMixPending?.Cancel();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _chatMixPending = pending;
        ChatMixStatus.Text = "Adjusting…";
        bool held = false;
        try
        {
            await Task.Delay(65, pending.Token);
            await _operations.WaitAsync(pending.Token); held = true;
            if (_disposed || _sonar is null || pending.IsCancellationRequested) return;
            // Debouncing ends before submission. A sent write must retain its readback.
            var state = await _sonar.SetChatMixAsync(value, _lifetime.Token);
            if (!_disposed && ReferenceEquals(_chatMixPending, pending)) ApplyChatMix(state);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex)) { if (!_disposed && ReferenceEquals(_chatMixPending, pending)) ChatMixStatus.Text = ex.Message; }
        finally
        {
            if (held) _operations.Release();
            if (ReferenceEquals(_chatMixPending, pending)) _chatMixPending = null;
        }
    }

    private void ProcessingClick(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        if (_hostOptions.ProcessingRequested is { } request) { request(); return; }
        if (_processingWindow is null)
        {
            CreateProcessingWindow().Show();
        }
        else { _processingWindow.Show(); _processingWindow.Activate(); }
    }

    internal EngineControlWindow CreateProcessingWindow()
    {
        if (_processingWindow is not null) return _processingWindow;
        var owner = Window.GetWindow(this) ?? throw new InvalidOperationException("Attach the audio workspace to a real host Window before opening Processing.");
        _processingWindow = new EngineControlWindow(System.IO.Path.Combine(_hostOptions.PreferencesDirectory, "engine-dsp-v1.json")) { Owner = owner };
        _processingWindow.Closed += (_, _) => _processingWindow = null;
        return _processingWindow;
    }
}
