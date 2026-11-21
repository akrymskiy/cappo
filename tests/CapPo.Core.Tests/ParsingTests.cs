using System.Net;
using System.Text;
using CapPo.Core.Detection;
using CapPo.Core.Network;
using CapPo.Core.Proxy;

namespace CapPo.Core.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("192.168.1.1", new[] { "192.168.1.1" })]
    [InlineData("10.0.0.1 8.8.8.8", new[] { "10.0.0.1", "8.8.8.8" })]
    [InlineData(" 10.0.0.1,10.0.0.2 ", new[] { "10.0.0.1", "10.0.0.2" })]
    [InlineData("0.0.0.0", new string[0])]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void DhcpNameServerIsSplitOnSpacesAndCommas(string? value, string[] expected) =>
        Assert.Equal(expected, ActiveNetworkFinder.ParseNameServers(value).Select(ip => ip.ToString()));

    [Fact]
    public void Dhcpv6ServersGetTheInterfaceAsScopeWhenLinkLocal()
    {
        var bytes = IPAddress.Parse("fe80::1").GetAddressBytes().Concat(IPAddress.Parse("2001:db8::53").GetAddressBytes()).ToArray();
        var servers = ActiveNetworkFinder.ParseIPv6Servers(bytes, 19);
        Assert.Equal(["fe80::1%19", "2001:db8::53"], servers.Select(ip => ip.ToString()));
    }

    [Theory]
    [InlineData("https://portal.example/api", "https://portal.example/api")]
    [InlineData("https://portal.example/api\0", "https://portal.example/api")]
    [InlineData("urn:ietf:params:capport:unrestricted", null)]
    [InlineData("http://insecure.example/api", null)]
    [InlineData("", null)]
    public void Option114IsAnHttpsUri(string text, string? expected) =>
        Assert.Equal(expected, DhcpCaptivePortalOption.Parse(Encoding.ASCII.GetBytes(text))?.ToString());

    [Theory]
    [InlineData("example.com:443", "example.com", 443)]
    [InlineData("example.com", "example.com", 443)]
    [InlineData("[2001:db8::1]:8443", "2001:db8::1", 8443)]
    [InlineData("10.0.0.1:8080", "10.0.0.1", 8080)]
    public void AuthorityIsSplitIntoHostAndPort(string authority, string host, int port) =>
        Assert.Equal((host, port), PortalProxy.ParseAuthority(authority, 443));

    [Theory]
    [InlineData("example.com:0")]
    [InlineData("example.com:99999")]
    [InlineData(":443")]
    [InlineData("[::1")]
    public void BadAuthoritiesAreRefused(string authority) =>
        Assert.Throws<ProxyException>(() => PortalProxy.ParseAuthority(authority, 443));

    [Fact]
    public async Task HeadsSplitAcrossReadsAreReassembledAndTheBodyKept()
    {
        var stream = new TrickleStream(Encoding.ASCII.GetBytes("POST /a HTTP/1.1\r\nHost: x\r\nContent-Length: 4\r\n\r\nbody"), chunk: 3);
        var reader = new HeadReader(stream);
        var head = await reader.ReadHeadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("POST /a HTTP/1.1", head!.StartLine);
        Assert.Equal("x", head.Get("host"));
        var buffered = Encoding.ASCII.GetString(reader.TakeBuffered().Span);
        Assert.StartsWith(buffered, "body", StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlongHeadsAreRefused()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX: " + new string('a', 70_000) + "\r\n\r\n"));
        await Assert.ThrowsAsync<FormatException>(() => new HeadReader(stream).ReadHeadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("239.255.255.250", false)]
    [InlineData("10.0.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("1.1.1.1", true)]
    public void TheProxyWontConnectToThisPc(string address, bool allowed) =>
        Assert.Equal(allowed, PortalProxy.IsAllowedByDefault(IPAddress.Parse(address)));

    [Fact]
    public void CaptivePortalApiJsonIsRead()
    {
        var state = ConnectivityProbe.ParseApi("""{"captive": true, "user-portal-url": "https://login.example/portal", "seconds-remaining": 300}""");
        Assert.Equal(new CaptivePortalApiState(true, new Uri("https://login.example/portal"), TimeSpan.FromMinutes(5)), state);
        Assert.Equal(new CaptivePortalApiState(false, null, null), ConnectivityProbe.ParseApi("""{"captive": false}"""));
        Assert.Null(ConnectivityProbe.ParseApi("""{"user-portal-url": "https://x"}"""));
        Assert.Null(ConnectivityProbe.ParseApi("not json"));
    }

    [Theory]
    [InlineData(200, "Microsoft Connect Test", null, ProbeOutcome.Online)]
    [InlineData(200, "Microsoft Connect Test\n", null, ProbeOutcome.Online)]
    [InlineData(200, "<html>Welcome to Hotel Wi-Fi</html>", null, ProbeOutcome.Portal)]
    [InlineData(302, "", "http://portal.example/login", ProbeOutcome.Portal)]
    [InlineData(511, "", null, ProbeOutcome.Portal)]
    public async Task ProbeResponsesAreClassified(int status, string body, string? location, ProbeOutcome expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        if (location is not null)
            response.Headers.Location = new Uri(location);
        var result = await ConnectivityProbe.ClassifyAsync(response, ConnectivityProbe.ProbeUrl, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(location, result.PortalUrl?.ToString());
    }

    [Fact]
    public async Task TheProxysOwnErrorsMeanUnreachableNotPortal()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("no dns") };
        response.Headers.Add(PortalProxy.ErrorHeader, "dns");
        var result = await ConnectivityProbe.ClassifyAsync(response, ConnectivityProbe.ProbeUrl, TestContext.Current.CancellationToken);
        Assert.Equal(ProbeOutcome.Unreachable, result.Outcome);
    }

    /// <summary>Hands out a few bytes per read, like a slow network.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }
}
