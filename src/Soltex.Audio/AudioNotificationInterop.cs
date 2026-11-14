using System.Runtime.InteropServices;

namespace Soltex.Audio;

[ComVisible(true)]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(DataFlow flow, DeviceRole role, [MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
    // The id stays a raw LPWSTR: most property notifications are ignored, and marshaling a string for each one
    // (about 16 a second on a PC with SteelSeries GG) was the audio helper's entire remaining idle cost.
    [PreserveSig] int OnPropertyValueChanged(IntPtr deviceId, PropertyKey key);
}

[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
    [PreserveSig] int GetMeteringChannelCount(out uint channelCount);
    [PreserveSig] int GetChannelsPeakValues(uint channelCount, IntPtr peaks);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
}
