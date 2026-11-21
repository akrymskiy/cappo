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
/// <para>Nothing is cached: portals answer every name with their own address until you sign in, and an answer kept
/// from then would send the next request back to the portal.</para>
/// <para>Every server is asked at once and the first with addresses wins. Portals' own DNS often fails for outside
/// names before sign-in, quickly ("no such name") or by not answering, while another server the network hands out
/// answers; an ASUS guest portal does both (router, then the ISP's DNS). A resolver that walks the list stops at the
/// first "no such name", and one that waits out a silent server runs past the probe's timeout.</para>
/// </remarks>
public sealed class DhcpDnsResolver : IHostResolver
{
    private volatile State? _state;

    private sealed record State(IReadOnlyList<IPEndPoint> Servers, LookupClient Client);

    public IReadOnlyList<IPEndPoint> Servers => _state?.Servers ?? [];

    /// <summary>Points the resolver at a network's DNS servers; called again whenever the network changes.</summary>
    public void SetServers(IReadOnlyList<IPAddress> servers) =>
        SetServers(servers.Select(static s => new IPEndPoint(s, NameServer.DefaultPort)).ToArray());

    /// <summary>Servers on any port, for tests.</summary>
    internal void SetServers(IReadOnlyList<IPEndPoint> servers)
    {
        if (servers.SequenceEqual(Servers))
            return;
        _state = servers.Count == 0 ? null : new State(servers.ToArray(), new LookupClient(new LookupClientOptions(
            servers.Select(static s => new NameServer(s)).ToArray())
        {
            UseCache = false,
            Timeout = TimeSpan.FromSeconds(2),
            Retries = 1,
            UseTcpFallback = true,
            ThrowDnsErrors = false,
            ContinueOnDnsError = false,
            ContinueOnEmptyResponse = false,
            // Plain queries, without an EDNS record, as nslookup sends them. An ASUS guest portal (CoovaChilli)
            // answered CapPo's EDNS queries with "no such name", even ones to the ISP's DNS, while nslookup's got
            // through. Longer answers still arrive over TCP.
            ExtendedDnsBufferSize = 512,
        }));
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return [literal];

        var state = _state ?? throw new HostResolutionException("No DNS server is known for this network yet.");
        using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = state.Servers.Select(server => AskAsync(state.Client, server, host, done.Token)).ToList();
        var failures = new List<string>();
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            var (addresses, failure) = await finished.ConfigureAwait(false);
            if (addresses.Count > 0)
            {
                await done.CancelAsync().ConfigureAwait(false);
                return addresses;
            }
            failures.Add(failure!);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new HostResolutionException($"{host} didn't resolve: {string.Join("; ", failures)}.");
    }

    private static string Name(IPEndPoint server) =>
        server.Port == NameServer.DefaultPort ? server.Address.ToString() : server.ToString();

    /// <returns>Addresses, IPv4 first; or none and why, prefixed with the server.</returns>
    private static async Task<(IReadOnlyList<IPAddress> Addresses, string? Failure)> AskAsync(
        LookupClient client, IPEndPoint server, string host, CancellationToken cancellationToken)
    {
        NameServer[] only = [new NameServer(server)];
        try
        {
            var answers = await Task.WhenAll(
                client.QueryServerAsync(only, host, QueryType.A, QueryClass.IN, cancellationToken),
                client.QueryServerAsync(only, host, QueryType.AAAA, QueryClass.IN, cancellationToken)).ConfigureAwait(false);

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
            return addresses.Count > 0
                ? (addresses, null)
                : ([], $"{Name(server)} said {answers.FirstOrDefault(static r => r.HasError)?.ErrorMessage ?? "no addresses"}");
        }
        catch (OperationCanceledException)
        {
            return ([], $"{Name(server)} was cancelled");
        }
        catch (DnsResponseException ex)
        {
            return ([], $"{Name(server)} didn't answer ({ex.Message})");
        }
    }
}
