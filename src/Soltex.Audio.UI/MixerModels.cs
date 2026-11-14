using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Soltex.Audio.UI;

internal abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}

internal sealed record DeviceChoice(string Id, string Label)
{
    public string ShortLabel => Label.Length > 27 ? Label[..24] + "…" : Label;
}
internal sealed record RouteChoice(string Id, string Name);

internal sealed class ChannelModel(string id, string name, string role, string number) : ObservableModel
{
    private double _level;
    private bool _muted, _ready, _canSelectDevice;
    private string? _deviceId;
    private string _status = "Not connected", _deviceName = "Unavailable";
    private double _peak = -1;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Role { get; } = role;
    public string Number { get; } = number;
    public string DestinationCaption => Id == "mic" ? "INPUT" : Id == "master" ? "PLAYBACK" : "OUTPUT";
    public ObservableCollection<DeviceChoice> Devices { get; } = [];
    public string? VirtualDeviceId { get; set; }
    public string? DeviceId { get => _deviceId; set => Set(ref _deviceId, value); }
    public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value); }
    public bool CanSelectDevice { get => _canSelectDevice; set => Set(ref _canSelectDevice, value); }
    public bool IsReady { get => _ready; set => Set(ref _ready, value); }
    public double Level { get => _level; set { if (Set(ref _level, value)) Changed(nameof(LevelText)); } }
    public string LevelText => Level.ToString("0", CultureInfo.InvariantCulture);
    public bool Muted { get => _muted; set { if (Set(ref _muted, value)) { Changed(nameof(MuteLabel)); Changed(nameof(MuteAccessibleName)); } } }
    public string MuteLabel => Muted ? "Muted" : "Mute";
    public string Status { get => _status; set => Set(ref _status, value); }
    public double Peak { get => _peak; set { if (Set(ref _peak, value)) Changed(nameof(PeakText)); } }
    public string PeakText => Peak < 0 ? "—" : Peak < 0.001 ? "−∞" : (20 * Math.Log10(Peak)).ToString("0", CultureInfo.InvariantCulture);
    public string DeviceAccessibleName => Name + (Id == "mic" ? " microphone input" : " output device");
    public string FaderAccessibleName => Name + " volume";
    public string NumericAccessibleName => Name + " volume percent";
    public string MuteAccessibleName => (Muted ? "Unmute " : "Mute ") + Name;
    public string MeterAccessibleName => Name + " level";
}

internal sealed class ApplicationModel(string key, uint processId, string name, string flow) : ObservableModel
{
    private string? _routeId;
    private string _status = "";
    public string Key { get; } = key;
    public uint ProcessId { get; } = processId;
    public string Name { get; } = name;
    public string Flow { get; } = flow;
    public IReadOnlyList<string> SessionIds { get; set; } = Array.Empty<string>();
    public string Detail => Flow == "capture" ? "Microphone" : "Playback";
    public ObservableCollection<RouteChoice> Routes { get; } = [];
    public string? RouteId { get => _routeId; set => Set(ref _routeId, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string AccessibleName => "Channel for " + Name;
}

internal sealed class MixerModel
{
    public ObservableCollection<DeviceChoice> Outputs { get; } = [];
    public ObservableCollection<ChannelModel> Channels { get; } =
    [new("master", "Master", "Master", "Σ"), new("game", "Game", "game", "01"),
     new("chat", "Chat", "chatRender", "02"), new("media", "Media", "media", "03"),
     new("aux", "Aux", "aux", "04"), new("mic", "Mic", "chatCapture", "IN")];
    public ObservableCollection<ApplicationModel> Applications { get; } = [];
}
