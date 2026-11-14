using NAudio.CoreAudioApi;

namespace Soltex.Audio.Sessions.Tests;

internal static class ComCoexistence
{
    // Real regression for shared-MTA COM identity. No stream is opened and no setter is called.
    internal static async Task CheckAsync()
    {
        AudioDeviceService devices = new();
        AudioSessionService sessions = new();
        MMDeviceEnumerator? naudio = null;
        try
        {
            await VerifyCoreAsync(devices, sessions);
            naudio = new(); // Core first, NAudio second: original InvalidCastException trigger.
            ReadNAudio(naudio);
            await VerifyCoreAsync(devices, sessions);
            await devices.DisposeAsync(); await sessions.DisposeAsync();
            ReadNAudio(naudio); // Core release must not poison NAudio's RCW.
            devices = new(); sessions = new(); // NAudio first, Core second.
            await VerifyCoreAsync(devices, sessions);
            naudio.Dispose(); naudio = null;
            await VerifyCoreAsync(devices, sessions); // NAudio release must not poison Core's RCW.
            using MMDeviceEnumerator recreated = new();
            ReadNAudio(recreated);
            AudioEndpointSnapshot provider = await AudioEndpointProvider.CaptureAsync();
            if (provider.State == AudioObservationState.Unavailable)
                throw new InvalidOperationException("Independent provider failed while NAudio and both services were alive.");
        }
        finally { naudio?.Dispose(); await sessions.DisposeAsync(); await devices.DisposeAsync(); }
    }

    private static async Task VerifyCoreAsync(AudioDeviceService devices, AudioSessionService sessions)
    {
        AudioDeviceSnapshot endpoints = await devices.GetSnapshotAsync();
        AudioSessionSnapshot snapshot = await sessions.GetSnapshotAsync();
        if (endpoints.State == AudioObservationState.Unavailable || snapshot.Errors.Count != 0)
            throw new InvalidOperationException("Core Audio coexistence read failed: " + string.Join("; ", snapshot.Errors));
    }

    private static void ReadNAudio(MMDeviceEnumerator enumerator)
    {
        MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, DeviceState.Active);
        for (int index = 0; index < endpoints.Count; index++)
        {
            using MMDevice endpoint = endpoints[index];
            _ = endpoint.ID; _ = endpoint.State;
        }
    }
}
