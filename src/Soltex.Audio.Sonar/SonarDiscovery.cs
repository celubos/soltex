using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Soltex.Audio.Sonar;

/// <summary>Discovers only loopback listeners owned by the exact installed Sonar executable. No port scan or process-secret access.</summary>
internal static class SonarDiscovery
{
    internal static IReadOnlyList<Uri> GetCandidates()
    {
        var allowedPath = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "SteelSeries", "GG", "apps", "sonar", "SteelSeriesSonar.exe"));
        var processIds = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("SteelSeriesSonar"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null && Path.GetFullPath(path).Equals(allowedPath, StringComparison.OrdinalIgnoreCase))
                        processIds.Add(checked((uint)process.Id));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // An exited or unreadable process is not an authenticated discovery candidate.
                }
            }
        }
        if (processIds.Count == 0) return [];
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (status != 122 || size is <= 0 or > 16 * 1024 * 1024)
            throw new SonarUnavailableException($"Windows could not inventory Sonar listeners (error {status}).");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0);
            if (status != 0) throw new SonarUnavailableException($"Windows listener inventory changed (error {status}); retry discovery.");
            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || 4L + count * 24L > size) throw new SonarUnavailableException("Windows returned an invalid listener table.");
            var candidates = new List<Uri>();
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(buffer, 4 + i * 24);
                var pid = unchecked((uint)Marshal.ReadInt32(row, 20));
                if (!processIds.Contains(pid) || Marshal.ReadInt32(row) != 2) continue;
                var address = unchecked((uint)Marshal.ReadInt32(row, 4));
                if (address != 0x0100007f) continue;
                var port = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);
                if (port != 0) candidates.Add(new Uri($"http://127.0.0.1:{port}/"));
            }
            return candidates.Distinct().ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint addressFamily, uint tableClass, uint reserved);
}
