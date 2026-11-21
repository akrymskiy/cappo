using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CapPo.Core.Network;

/// <summary>Empties Windows' DNS cache, as <c>ipconfig /flushdns</c> does; no administrator rights needed.</summary>
/// <remarks>
/// While the portal blocked encrypted DNS, lookups failed, and Windows caches failures. Without a flush, apps could
/// keep failing for a while after signing in.
/// </remarks>
public static partial class DnsCache
{
    public static bool Flush()
    {
        try
        {
            if (DnsFlushResolverCache())
                return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not exported on this Windows: ipconfig does the same.
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (process is null)
                return false;
            process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Exported by dnsapi.dll but undocumented; ipconfig /flushdns calls it.
    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}
