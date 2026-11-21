using System.Runtime.InteropServices;
using System.Text;

namespace CapPo.Core.Network;

/// <summary>
/// Reads DHCP option 114 (RFC 8910): the URL of the network's Captive Portal API (RFC 8908), for networks that send it.
/// </summary>
/// <remarks>
/// Uses DhcpRequestParams, which WinHTTP also calls from ordinary user processes for WPAD (option 252); it needs no
/// administrator rights. When the option isn't cached, the DHCP client may ask the server (DHCPINFORM), so this can
/// take a moment; callers cap the wait.
/// </remarks>
public static partial class DhcpCaptivePortalOption
{
    public const uint OptionCaptivePortal = 114;

    private const uint DHCPCAPI_REQUEST_SYNCHRONOUS = 0x02;
    private const uint ERROR_MORE_DATA = 234;

    /// <returns>The API URL, or null when the network doesn't send one or it can't be read in time.</returns>
    public static async Task<Uri?> TryGetAsync(string adapterId, TimeSpan timeout)
    {
        // The call blocks and can't be cancelled, so it runs on its own and is abandoned when slow.
        var read = Task.Run(() => TryRead(adapterId));
        var finished = await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false);
        return finished == read ? Parse(await read.ConfigureAwait(false)) : null;
    }

    /// <summary>Option 114 holds the URI as ASCII, without a terminator (RFC 8910 section 2.1).</summary>
    public static Uri? Parse(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return null;
        var text = Encoding.ASCII.GetString(data).TrimEnd('\0').Trim();
        // RFC 8910: the URI is https, and "urn:ietf:params:capport:unrestricted" means there's no portal.
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static byte[]? TryRead(string adapterId)
    {
        try
        {
            if (DhcpCApiInitialize(out _) != 0)
                return null;
            try
            {
                return Request(adapterId);
            }
            finally
            {
                DhcpCApiCleanup();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static unsafe byte[]? Request(string adapterId)
    {
        var size = 1024u;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = NativeMemory.Alloc(size);
            try
            {
                // The API writes the option's location (inside buffer) back into this struct.
                var param = new DHCPCAPI_PARAMS { OptionId = OptionCaptivePortal };
                var received = new DHCPCAPI_PARAMS_ARRAY { nParams = 1, Params = &param };
                var send = default(DHCPCAPI_PARAMS_ARRAY);
                var error = DhcpRequestParams(DHCPCAPI_REQUEST_SYNCHRONOUS, 0, adapterId, 0, send, received,
                    (byte*)buffer, &size, 0);
                if (error == ERROR_MORE_DATA)
                    continue;
                if (error != 0 || param.Data == null || param.nBytesData == 0)
                    return null;
                return new ReadOnlySpan<byte>(param.Data, (int)param.nBytesData).ToArray();
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DHCPCAPI_PARAMS
    {
        public uint Flags;
        public uint OptionId;
        public int IsVendor;
        public byte* Data;
        public uint nBytesData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DHCPCAPI_PARAMS_ARRAY
    {
        public uint nParams;
        public DHCPCAPI_PARAMS* Params;
    }

    [LibraryImport("dhcpcsvc.dll")]
    private static partial uint DhcpCApiInitialize(out uint version);

    [LibraryImport("dhcpcsvc.dll")]
    private static partial void DhcpCApiCleanup();

    [LibraryImport("dhcpcsvc.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint DhcpRequestParams(uint flags, nint reserved, string adapterName, nint classId,
        DHCPCAPI_PARAMS_ARRAY sendParams, DHCPCAPI_PARAMS_ARRAY recdParams, byte* buffer, uint* size, nint requestIdStr);
}
