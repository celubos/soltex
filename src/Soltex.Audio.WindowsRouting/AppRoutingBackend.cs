using Soltex.Audio.Sessions;

namespace Soltex.Audio.WindowsRouting;

internal interface IAppRoutingTarget : IDisposable
{
    IReadOnlyList<ProcessPolicy> Read();
    ProcessPolicy CompareSet(RoutingDirection direction, RoutingRole role, string? expected, string? desired);
}
internal interface IAppRoutingBackend : IDisposable
{
    AudioDeviceSnapshot Devices();
    AudioSessionSnapshot Sessions();
    IAppRoutingTarget Open(AudioSessionInfo selected);
}

internal sealed class AppRoutingBackend : IAppRoutingBackend
{
    private readonly AudioDeviceService devices = new();
    private readonly AudioSessionService sessions = new();
    private WindowsAudioPolicy? policy;
    public AudioDeviceSnapshot Devices() => devices.GetSnapshotAsync().GetAwaiter().GetResult();
    public AudioSessionSnapshot Sessions() => sessions.GetSnapshotAsync().GetAwaiter().GetResult();
    public IAppRoutingTarget Open(AudioSessionInfo selected)
    {
        AppRoutingService.ValidateSelection(selected, Sessions());
        policy ??= new();
        return new Target(policy.OpenSelected(selected));
    }
    private sealed class Target(WindowsAudioPolicy.SelectedRoutingProcess process) : IAppRoutingTarget
    {
        public IReadOnlyList<ProcessPolicy> Read() => process.Read();
        public ProcessPolicy CompareSet(RoutingDirection direction, RoutingRole role, string? expected, string? desired) => process.CompareSet(direction, role, expected, desired);
        public void Dispose() => process.Dispose();
    }
    public void Dispose()
    {
        try { policy?.Dispose(); }
        finally
        {
            try { sessions.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { devices.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }
}
