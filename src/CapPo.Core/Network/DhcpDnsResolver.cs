using System.Net;
using System.Net.Sockets;
using DnsClient;
using DnsClient.Protocol;

namespace CapPo.Core.Network;

/// <summary>Turns a host name into addresses to connect to.</summary>
public interface IHostResolver
{
    /// <returns>At least one address, IPv4 first.</returns>
    /// <exception cref="HostResolutionException">The name doesn't resolve, or no DNS server answered.</exception>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class HostResolutionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Resolves names with plain DNS to the network's own DNS servers (normally the DHCP ones), never Windows' resolver.
/// That's what reaches a captive portal while Windows is set to encrypted DNS without fallback.
/// </summary>
/// <remarks>
/// Nothing is cached: portals answer every name with their own address until you sign in, and an answer kept from
/// then would send the next request back to the portal.
/// </remarks>
public sealed class DhcpDnsResolver : IHostResolver
{
    private volatile LookupClient? _client;

    public IReadOnlyList<IPAddress> Servers { get; private set; } = [];

    /// <summary>Points the resolver at a network's DNS servers; called again whenever the network changes.</summary>
    public void SetServers(IReadOnlyList<IPAddress> servers)
    {
        if (servers.SequenceEqual(Servers))
            return;
        Servers = servers;
        _client = servers.Count == 0 ? null : new LookupClient(new LookupClientOptions(
            servers.Select(static s => new NameServer(s)).ToArray())
        {
            UseCache = false,
            Timeout = TimeSpan.FromSeconds(2),
            Retries = 1,
            UseTcpFallback = true,
            ThrowDnsErrors = false,
            ContinueOnDnsError = true,
            ContinueOnEmptyResponse = true,
            UseRandomNameServer = false,
        });
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return [literal];

        var client = _client ?? throw new HostResolutionException("No DNS server is known for this network yet.");
        try
        {
            var a = client.QueryAsync(host, QueryType.A, QueryClass.IN, cancellationToken);
            var aaaa = client.QueryAsync(host, QueryType.AAAA, QueryClass.IN, cancellationToken);
            var answers = await Task.WhenAll(a, aaaa).ConfigureAwait(false);

            var addresses = answers.SelectMany(static r => r.Answers)
                .Select(static r => r switch
                {
                    ARecord ar => ar.Address,
                    AaaaRecord aaaar => aaaar.Address,
                    _ => null,
                })
                .OfType<IPAddress>()
                .Distinct()
                .OrderBy(static ip => ip.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0)
                .ToList();
            if (addresses.Count > 0)
                return addresses;

            var error = answers.FirstOrDefault(static r => r.HasError)?.ErrorMessage ?? "no addresses";
            throw new HostResolutionException($"{host} didn't resolve via {string.Join(", ", Servers)}: {error}.");
        }
        catch (DnsResponseException ex)
        {
            throw new HostResolutionException($"No answer for {host} from {string.Join(", ", Servers)}: {ex.Message}", ex);
        }
    }
}
