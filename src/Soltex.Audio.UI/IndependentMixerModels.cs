using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;

namespace Soltex.Audio.UI;

public sealed record IndependentEndpointChoice(string Id, string Label)
{
    /// <summary>What a screen reader announces for the list item.</summary>
    public override string ToString() => Label;
    internal static string Describe(HostEndpoint endpoint, string nameSuffix = "") => endpoint.Name + nameSuffix +
        (endpoint.IsCapture ? " · input" : endpoint.TransportId is null ? " · playback output" : " · virtual return");
}

public sealed class IndependentMixerChannel : INotifyPropertyChanged
{
    private bool enabled, mute, manualPin = true, autoReturn = true;
    private string? captureId, renderId;
    private double gainDb, peak = -1;
    private string state = "Not configured", reason = "Choose the source and destination, then apply.", activeDestination = "No active destination";
    public AudioBus Bus { get; }
    public string Title => Bus == AudioBus.Microphone ? "Microphone" : Bus.ToString();
    public string SourceLabel => Bus == AudioBus.Microphone ? "Physical microphone" : "Bus capture source";
    public string DestinationLabel => Bus == AudioBus.Microphone ? "Virtual microphone return" : "Playback destination";
    public string InputSummary => Bus != AudioBus.Microphone ? "" : Dsp.InputChannel is int channel
        ? $"Hardware input {channel + 1} → centered voice" : "All hardware input channels";
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public bool Mute { get => mute; set => Set(ref mute, value); }
    public bool ManualPin { get => manualPin; set => Set(ref manualPin, value); }
    public bool AutoReturn { get => autoReturn; set => Set(ref autoReturn, value); }
    public string? CaptureEndpointId { get => captureId; set => Set(ref captureId, value); }
    public string? RenderEndpointId { get => renderId; set => Set(ref renderId, value); }
    public double GainDb { get => gainDb; set => Set(ref gainDb, value); }
    public double Peak { get => peak; set => Set(ref peak, value); }
    public string State { get => state; set => Set(ref state, value); }
    public string Reason { get => reason; set => Set(ref reason, value); }
    public string ActiveDestination { get => activeDestination; set => Set(ref activeDestination, value); }
    private DspSettings dsp = new();
    public DspSettings Dsp { get => dsp; set { if (dsp == value) return; dsp = value; PropertyChanged?.Invoke(this, new(nameof(Dsp))); PropertyChanged?.Invoke(this, new(nameof(InputSummary))); } }
    public IReadOnlyList<string> OutputPriorityIds { get; set; } = [];
    public IReadOnlyList<string> ExcludedOutputIds { get; set; } = [];
    public ObservableCollection<IndependentEndpointChoice> CaptureChoices { get; } = [];
    public ObservableCollection<IndependentEndpointChoice> RenderChoices { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public IndependentMixerChannel(AudioBus bus) => Bus = bus;
    public HostChannelProfile ToProfile() => new()
    {
        Bus = Bus, Enabled = Enabled, CaptureEndpointId = EmptyToNull(CaptureEndpointId), RenderEndpointId = EmptyToNull(RenderEndpointId),
        GainDb = GainDb, Mute = Mute, Dsp = Dsp, ManualPin = ManualPin, AutoReturn = AutoReturn,
        OutputPriorityIds = OutputPriorityIds.ToArray(), ExcludedOutputIds = ExcludedOutputIds.ToArray()
    };
    internal void Load(HostChannelProfile profile)
    {
        Enabled = profile.Enabled; CaptureEndpointId = profile.CaptureEndpointId ?? ""; RenderEndpointId = profile.RenderEndpointId ?? "";
        GainDb = profile.GainDb; Mute = profile.Mute; ManualPin = profile.ManualPin; AutoReturn = profile.AutoReturn;
        Dsp = profile.Dsp; OutputPriorityIds = profile.OutputPriorityIds.ToArray(); ExcludedOutputIds = profile.ExcludedOutputIds.ToArray();
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new(property)); }
}

/// <summary>A testable UI boundary. Implementations own only Soltex's audio host.</summary>
public interface IIndependentMixerConnection : IAsyncDisposable
{
    Task<HostSnapshot> ConnectAsync(CancellationToken cancellationToken = default);
    Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default);
    Task<HostSnapshot> ApplyAsync(AudioHostProfile profile, CancellationToken cancellationToken = default);
    Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default);
    Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default);
    Task<HostSnapshot> StartStreamMixAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Stream mix unavailable.");
    Task<HostSnapshot> StartMicrophoneTestAsync(string endpointId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Microphone test unavailable.");
    Task<HostSnapshot> StopMicrophoneTestAsync(CancellationToken cancellationToken = default) => ReadAsync(cancellationToken);
}

internal sealed class IndependentMixerConnection(string directory, string executable) : IIndependentMixerConnection
{
    private readonly AudioHostClient client = new(directory, executable);
    public Task<HostSnapshot> ConnectAsync(CancellationToken cancellationToken = default) => client.EnsureConnectedAsync(cancellationToken);
    public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) => client.ReadAsync(cancellationToken);
    public Task<HostSnapshot> ApplyAsync(AudioHostProfile profile, CancellationToken cancellationToken = default) => client.ApplyProfileAsync(profile, cancellationToken);
    public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) => client.StartAsync(cancellationToken);
    public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) => client.StopAsync(cancellationToken);
    public Task<HostSnapshot> StartStreamMixAsync(CancellationToken cancellationToken = default) => client.StartStreamMixAsync(cancellationToken);
    public Task<HostSnapshot> StartMicrophoneTestAsync(string endpointId, CancellationToken cancellationToken = default) => client.StartMicrophoneTestAsync(endpointId, cancellationToken);
    public Task<HostSnapshot> StopMicrophoneTestAsync(CancellationToken cancellationToken = default) => client.StopMicrophoneTestAsync(cancellationToken);
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
