using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CapPo.Core.Network;

namespace CapPo.Core.Tests;

/// <summary>
/// The DHCP-DNS resolver against small DNS servers on 127.0.0.1, set up like an ASUS guest portal before sign-in:
/// the router's DNS fails for outside names (quickly or by not answering) while the ISP's DNS, also handed out by
/// DHCP, answers.
/// </summary>
public sealed class ResolverTests : IDisposable
{
    private static readonly IPAddress Answer = IPAddress.Parse("192.0.2.7");
    private readonly List<FakeDnsServer> _servers = [];

    public void Dispose()
    {
        foreach (var server in _servers)
            server.Dispose();
    }

    [Fact]
    public async Task NoSuchNameFromOneServerDoesNotHideAnotherServersAnswer()
    {
        var resolver = Resolver(Server(FakeDns.NoSuchName), Server(FakeDns.Answers));

        var addresses = await resolver.ResolveAsync("www.msftconnecttest.com", TestContext.Current.CancellationToken);

        Assert.Equal([Answer], addresses);
    }

    [Fact]
    public async Task ASilentServerDoesNotHoldUpAnotherServersAnswer()
    {
        var resolver = Resolver(Server(FakeDns.Silent), Server(FakeDns.Answers));

        var clock = Stopwatch.StartNew();
        var addresses = await resolver.ResolveAsync("www.msftconnecttest.com", TestContext.Current.CancellationToken);

        Assert.Equal([Answer], addresses);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Took {clock.Elapsed}");
    }

    [Fact]
    public async Task WhenNoServerResolvesTheErrorSaysWhatEachOneDid()
    {
        var noSuchName = Server(FakeDns.NoSuchName);
        var silent = Server(FakeDns.Silent);
        var resolver = Resolver(noSuchName, silent);

        var error = await Assert.ThrowsAsync<HostResolutionException>(
            () => resolver.ResolveAsync("www.msftconnecttest.com", TestContext.Current.CancellationToken));

        Assert.Contains($"{noSuchName.EndPoint} said Non-Existent Domain", error.Message);
        Assert.Contains($"{silent.EndPoint} didn't answer", error.Message);
    }

    [Fact]
    public async Task AddressLiteralsAreNotLookedUp()
    {
        var addresses = await new DhcpDnsResolver().ResolveAsync("[::1]", TestContext.Current.CancellationToken);

        Assert.Equal([IPAddress.IPv6Loopback], addresses);
    }

    private FakeDnsServer Server(FakeDns behaviour)
    {
        var server = new FakeDnsServer(behaviour, Answer);
        _servers.Add(server);
        return server;
    }

    private static DhcpDnsResolver Resolver(params FakeDnsServer[] servers)
    {
        var resolver = new DhcpDnsResolver();
        resolver.SetServers(servers.Select(static s => s.EndPoint).ToArray());
        return resolver;
    }
}

internal enum FakeDns
{
    /// <summary>An A record for A queries, nothing (but no error) for others.</summary>
    Answers,
    /// <summary>NXDOMAIN for everything.</summary>
    NoSuchName,
    /// <summary>Reads queries and never replies.</summary>
    Silent,
}

/// <summary>Just enough of a DNS server over UDP for the resolver tests.</summary>
internal sealed class FakeDnsServer : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();

    public FakeDnsServer(FakeDns behaviour, IPAddress answer)
    {
        _ = ServeAsync(behaviour, answer);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

    private async Task ServeAsync(FakeDns behaviour, IPAddress answer)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var query = await _udp.ReceiveAsync(_stop.Token);
                if (behaviour != FakeDns.Silent)
                    await _udp.SendAsync(Reply(query.Buffer, behaviour, answer), query.RemoteEndPoint, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private static byte[] Reply(byte[] query, FakeDns behaviour, IPAddress answer)
    {
        // The question: labels up to a zero byte, then type and class.
        var end = 12;
        while (query[end] != 0)
            end += query[end] + 1;
        end += 5;
        var type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(end - 4));
        var withAnswer = behaviour == FakeDns.Answers && type == 1;

        var reply = new List<byte>(query.AsSpan(0, end).ToArray());
        var rcode = behaviour == FakeDns.NoSuchName ? 3 : 0;
        reply[2] = 0x81;                        // response, recursion desired
        reply[3] = (byte)(0x80 | rcode);        // recursion available
        reply[6] = 0; reply[7] = (byte)(withAnswer ? 1 : 0);
        reply[8] = reply[9] = reply[10] = reply[11] = 0;
        if (withAnswer)
        {
            // Name as a pointer to the question, type A, class IN, TTL 60, four bytes of address.
            reply.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
            reply.AddRange(answer.GetAddressBytes());
        }
        return [.. reply];
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        _stop.Dispose();
    }
}
