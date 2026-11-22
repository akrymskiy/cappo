using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CapPo.Core.Network;

/// <summary>
/// Reads how Windows' DNS client is set up: each adapter's DNS servers and their encryption (DNS over HTTPS), the
/// Group Policy on it, and rules that send particular names elsewhere. Reads only; needs no administrator rights.
/// </summary>
public static partial class DnsEncryptionReader
{
    private const string Tcpip4Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string Tcpip6Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";
    private const string DnsCache = @"SYSTEM\CurrentControlSet\Services\Dnscache\";
    private const string DnsPolicy = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";

    public static DnsEncryptionReport Read()
    {
        var policy = DnsEncryption.ParsePolicy(Registry.LocalMachine.OpenSubKey(DnsPolicy)?.GetValue("DoHPolicy"));
        var known = KnownServers();
        var best = ActiveNetworkFinder.BestInterfaceIndex();

        List<AdapterDnsStatus> adapters = [];
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var properties = nic.GetIPProperties();
            int index;
            try
            {
                index = ActiveNetworkFinder.IndexOf(properties);
            }
            catch (NetworkInformationException)
            {
                continue; // Neither IPv4 nor IPv6: nothing to look names up with.
            }
            var carries = index == best;
            var hasGateway = properties.GatewayAddresses.Any(static g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
            // Adapters without a way out (Hyper-V switches and the like) don't take part in internet lookups.
            if (!carries && !hasGateway)
                continue;
            var servers = properties.DnsAddresses
                .Where(static a => !a.IsIPv6SiteLocal) // fec0:0:0:ffff::1-3, Windows' placeholders
                .Distinct()
                .ToList();
            if (servers.Count == 0)
                continue;

            var onAdapter = AdapterEntries(nic.Id);
            var statuses = servers
                .Select(s => DnsEncryption.Classify(s, Find(onAdapter, s), Find(known, s), policy))
                .ToList();
            var isTunnel = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp;
            adapters.Add(new AdapterDnsStatus(nic.Id, nic.Name, nic.Description,
                nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, ServersTypedIn(nic.Id), carries,
                isTunnel || (carries && !hasGateway), statuses));
        }

        adapters.Sort(static (a, b) => b.CarriesInternet.CompareTo(a.CarriesInternet));
        return new DnsEncryptionReport(policy, adapters, NameRules());
    }

    // By address bytes: a link-local server carries the adapter's scope, while saved entries don't.
    private static DohEntry? Find(IReadOnlyList<DohEntry> entries, IPAddress server) =>
        entries.FirstOrDefault(e => e.Address.GetAddressBytes().AsSpan().SequenceEqual(server.GetAddressBytes()));

    private static bool ServersTypedIn(string adapterId)
    {
        using var v4 = Registry.LocalMachine.OpenSubKey(Tcpip4Interfaces + adapterId);
        using var v6 = Registry.LocalMachine.OpenSubKey(Tcpip6Interfaces + adapterId);
        return !string.IsNullOrWhiteSpace(v4?.GetValue("NameServer") as string)
            || !string.IsNullOrWhiteSpace(v6?.GetValue("NameServer") as string);
    }

    /// <summary>Windows' list of known DoH servers, with the ones the user added ("Set-DnsClientDohServerAddress").</summary>
    private static List<DohEntry> KnownServers()
    {
        List<DohEntry> entries = [];
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DnsCache + @"Parameters\DohWellKnownServers");
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                using var server = key!.OpenSubKey(name);
                if (IPAddress.TryParse(name, out var ip))
                    entries.Add(new DohEntry(ip, server?.GetValue("Template") as string, ToFlags(server?.GetValue("Flags"))));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return entries;
    }

    /// <summary>The adapter's own DoH entries: from Windows' API, or the registry where the API isn't there.</summary>
    private static List<DohEntry> AdapterEntries(string adapterId)
    {
        if (!Guid.TryParse(adapterId, out var guid))
            return [];
        try
        {
            if (FromApi(guid, ipv6: false) is { } v4 && FromApi(guid, ipv6: true) is { } v6)
                return [.. v4, .. v6];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return FromRegistry(adapterId);
    }

    private static List<DohEntry> FromRegistry(string adapterId)
    {
        List<DohEntry> entries = [];
        foreach (var family in new[] { "Doh", "Doh6" })
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{DnsCache}InterfaceSpecificParameters\{adapterId}\DohInterfaceSettings\{family}");
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                using var server = key!.OpenSubKey(name);
                if (IPAddress.TryParse(name, out var ip))
                    entries.Add(new DohEntry(ip, server?.GetValue("DohTemplate") as string, ToFlags(server?.GetValue("DohFlags"))));
            }
        }
        return entries;
    }

    private static ulong ToFlags(object? value) => value switch
    {
        long l => (ulong)l,
        int i => (uint)i,
        _ => 0,
    };

    /// <summary>Rules from the Name Resolution Policy Table, local and from Group Policy, that name DNS servers.</summary>
    private static List<NameRule> NameRules()
    {
        List<NameRule> rules = [];
        Add(DnsCache + @"Parameters\DnsPolicyConfig", fromPolicy: false);
        Add(DnsPolicy + @"\DnsPolicyConfig", fromPolicy: true);
        return rules;

        void Add(string path, bool fromPolicy)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                foreach (var name in key?.GetSubKeyNames() ?? [])
                {
                    using var rule = key!.OpenSubKey(name);
                    var names = rule?.GetValue("Name") switch
                    {
                        string[] many => many,
                        string one => [one],
                        _ => [],
                    };
                    var servers = (rule?.GetValue("GenericDNSServers") as string ?? "")
                        .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (names.Length > 0 && servers.Length > 0)
                        rules.Add(new NameRule(names, servers, fromPolicy));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    // GetInterfaceDnsSettings, version 3 (Windows 11): the static servers and the DoH settings for each.
    private static unsafe List<DohEntry>? FromApi(Guid adapter, bool ipv6)
    {
        var settings = new DNS_INTERFACE_SETTINGS3 { Version = DNS_INTERFACE_SETTINGS_VERSION3, Flags = ipv6 ? DNS_SETTING_IPV6 : 0 };
        if (GetInterfaceDnsSettings(adapter, &settings) != 0)
            return null;
        try
        {
            List<DohEntry> entries = [];
            AddAll(Marshal.PtrToStringUni(settings.NameServer), settings.cServerProperties, settings.ServerProperties);
            AddAll(Marshal.PtrToStringUni(settings.ProfileNameServer), settings.cProfileServerProperties, settings.ProfileServerProperties);
            return entries;

            void AddAll(string? nameServers, uint count, DNS_SERVER_PROPERTY* properties)
            {
                var servers = ActiveNetworkFinder.ParseNameServers(nameServers);
                for (var i = 0; i < count; i++)
                {
                    var property = properties[i];
                    if (property.Type != DnsServerDohProperty || property.DohSettings == null || property.ServerIndex >= servers.Count)
                        continue;
                    entries.Add(new DohEntry(servers[(int)property.ServerIndex],
                        Marshal.PtrToStringUni(property.DohSettings->Template), property.DohSettings->Flags));
                }
            }
        }
        finally
        {
            FreeInterfaceDnsSettings(&settings);
        }
    }

    private const uint DNS_INTERFACE_SETTINGS_VERSION3 = 3;
    private const ulong DNS_SETTING_IPV6 = 0x1;
    private const int DnsServerDohProperty = 1;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DNS_INTERFACE_SETTINGS3
    {
        public uint Version;
        public ulong Flags;
        public nint Domain;
        public nint NameServer;
        public nint SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLLMNR;
        public uint QueryAdapterName;
        public nint ProfileNameServer;
        public uint DisableUnconstrainedQueries;
        public nint SupplementalSearchList;
        public uint cServerProperties;
        public DNS_SERVER_PROPERTY* ServerProperties;
        public uint cProfileServerProperties;
        public DNS_SERVER_PROPERTY* ProfileServerProperties;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DNS_SERVER_PROPERTY
    {
        public uint Version;
        public uint ServerIndex;
        public int Type;
        public DNS_DOH_SERVER_SETTINGS* DohSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_DOH_SERVER_SETTINGS
    {
        public nint Template;
        public ulong Flags;
    }

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial uint GetInterfaceDnsSettings(Guid adapter, DNS_INTERFACE_SETTINGS3* settings);

    [LibraryImport("iphlpapi.dll")]
    private static unsafe partial void FreeInterfaceDnsSettings(DNS_INTERFACE_SETTINGS3* settings);
}
