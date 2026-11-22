using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CapPo.Core.Network;

/// <summary>Where <see cref="ActiveNetwork.DnsServers"/> came from.</summary>
public enum DnsSource
{
    /// <summary>The DHCP server's DNS servers (option 6, or DHCPv6).</summary>
    Dhcp,
    /// <summary>No DHCP DNS (e.g. a static address): the default gateway, which on most small networks answers DNS.</summary>
    Gateway,
}

/// <summary>The adapter that carries internet traffic, and the DNS servers its network handed out.</summary>
/// <param name="AdapterId">The adapter's GUID in braces, as the registry and the DHCP client API name it.</param>
/// <param name="AdapterName">What Windows calls the connection, e.g. "Wi-Fi".</param>
public sealed record ActiveNetwork(
    string AdapterId,
    string AdapterName,
    int InterfaceIndex,
    IReadOnlyList<IPAddress> DnsServers,
    DnsSource DnsSource,
    IReadOnlyList<IPAddress> Gateways)
{
    /// <summary>Changes when the network does in a way that matters here: another adapter, DNS or gateway.</summary>
    public string Fingerprint =>
        $"{AdapterId}|{string.Join(",", DnsServers)}|{string.Join(",", Gateways)}";

    public override string ToString() =>
        $"{AdapterName} {AdapterId}, DNS {string.Join(", ", DnsServers)} ({DnsSource}), gateway {string.Join(", ", Gateways)}";
}

/// <summary>Finds the <see cref="ActiveNetwork"/>. Reads only; needs no administrator rights.</summary>
public static partial class ActiveNetworkFinder
{
    private const string Tcpip4Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string Tcpip6Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";

    /// <returns>Null when no adapter is up with a gateway: there's no network to sign in to.</returns>
    public static ActiveNetwork? Find()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(static n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(static n => (Interface: n, Properties: n.GetIPProperties()))
            .Where(static c => Gateways(c.Properties).Count > 0)
            .ToList();
        if (candidates.Count == 0)
            return null;

        // The adapter Windows would route internet traffic through goes first. A VPN can be that adapter yet have
        // no DHCP DNS; the physical one behind it then still gets its turn.
        var best = BestInterfaceIndex();
        foreach (var (nic, properties) in candidates.OrderByDescending(c => IndexOf(c.Properties) == best))
        {
            var dns = DhcpDnsServers(nic.Id, IndexOf(properties));
            if (dns.Count > 0)
                return new ActiveNetwork(nic.Id, nic.Name, IndexOf(properties), dns, DnsSource.Dhcp, Gateways(properties));
        }

        var (fallback, fallbackProperties) = candidates.OrderByDescending(c => IndexOf(c.Properties) == best).First();
        var gateways = Gateways(fallbackProperties);
        return new ActiveNetwork(fallback.Id, fallback.Name, IndexOf(fallbackProperties), gateways, DnsSource.Gateway, gateways);
    }

    /// <summary>The DNS servers DHCP gave this adapter, IPv4 first, as the registry keeps them.</summary>
    public static IReadOnlyList<IPAddress> DhcpDnsServers(string adapterId, int interfaceIndex)
    {
        List<IPAddress> servers = [];
        using (var key = Registry.LocalMachine.OpenSubKey(Tcpip4Interfaces + adapterId))
            servers.AddRange(ParseNameServers(key?.GetValue("DhcpNameServer") as string));
        using (var key = Registry.LocalMachine.OpenSubKey(Tcpip6Interfaces + adapterId))
            servers.AddRange(ParseIPv6Servers(key?.GetValue("Dhcpv6DNSServers") as byte[], interfaceIndex));
        return servers.Distinct().ToList();
    }

    /// <summary>Parses DhcpNameServer: addresses separated by spaces (or, on some versions, commas).</summary>
    public static IReadOnlyList<IPAddress> ParseNameServers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        return value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static s => IPAddress.TryParse(s, out var ip) ? ip : null)
            .OfType<IPAddress>()
            .Where(static ip => !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.None))
            .ToList();
    }

    /// <summary>Parses Dhcpv6DNSServers: 16-byte addresses back to back.</summary>
    /// <param name="interfaceIndex">Scope for link-local addresses, which mean nothing without one.</param>
    public static IReadOnlyList<IPAddress> ParseIPv6Servers(byte[]? value, int interfaceIndex)
    {
        if (value is null)
            return [];
        List<IPAddress> servers = [];
        for (var i = 0; i + 16 <= value.Length; i += 16)
        {
            var ip = new IPAddress(value.AsSpan(i, 16));
            if (ip.Equals(IPAddress.IPv6Any))
                continue;
            if (ip.IsIPv6LinkLocal)
                ip.ScopeId = interfaceIndex;
            servers.Add(ip);
        }
        return servers;
    }

    private static List<IPAddress> Gateways(IPInterfaceProperties properties) =>
        properties.GatewayAddresses
            .Select(static g => g.Address)
            .Where(static a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
            .OrderBy(static a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0)
            .ToList();

    internal static int IndexOf(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties()?.Index ?? properties.GetIPv6Properties().Index;
        }
        catch (NetworkInformationException)
        {
            return properties.GetIPv6Properties().Index;
        }
    }

    // A route lookup only: nothing is sent to this address.
    internal static int BestInterfaceIndex()
    {
        var destination = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes());
        return GetBestInterface(destination, out var index) == 0 ? (int)index : -1;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterface(uint destinationAddress, out uint bestIfIndex);
}
