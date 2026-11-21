using System.Net;
using System.Net.Sockets;
using System.Text;
using CapPo.Core.Network;
using CapPo.Core.Proxy;

namespace CapPo.Core.Tests;

/// <summary>The proxy against a small origin server on 127.0.0.1, with names resolved by a stub.</summary>
public sealed class ProxyTests : IAsyncLifetime
{
    private readonly TcpListener _origin = new(IPAddress.Loopback, 0);
    private readonly List<string> _requestsSeen = [];
    private PortalProxy _proxy = null!;

    private int OriginPort => ((IPEndPoint)_origin.LocalEndpoint).Port;

    public ValueTask InitializeAsync()
    {
        _origin.Start();
        _ = ServeOriginAsync();
        _proxy = new PortalProxy(new StubResolver()) { AllowDestination = _ => true };
        _proxy.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _proxy.DisposeAsync();
        _origin.Stop();
    }

    [Fact]
    public async Task PlainHttpIsForwardedInOriginFormWithConnectionClose()
    {
        using var http = Client();
        var body = await http.GetStringAsync($"http://origin.test:{OriginPort}/path?q=%2F1", TestContext.Current.CancellationToken);

        Assert.Equal("hello from origin", body);
        var request = Assert.Single(_requestsSeen);
        Assert.StartsWith("GET /path?q=%2F1 HTTP/1.1\r\n", request);
        Assert.Contains("Connection: close", request);
        Assert.DoesNotContain("Proxy-Connection", request);
    }

    [Fact]
    public async Task PostBodiesReachTheOrigin()
    {
        using var http = Client();
        using var response = await http.PostAsync($"http://origin.test:{OriginPort}/login",
            new FormUrlEncodedContent([new("user", "guest"), new("room", "101")]), TestContext.Current.CancellationToken);
        Assert.Equal("hello from origin", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.EndsWith("user=guest&room=101", Assert.Single(_requestsSeen));
    }

    [Fact]
    public async Task ConnectOpensATunnel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _proxy.Port, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT origin.test:{OriginPort} HTTP/1.1\r\nHost: origin.test\r\n\r\n"), ct);
        var reply = await ReadUntilAsync(stream, "\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", reply);

        // Through the tunnel, the origin sees exactly what's sent.
        await stream.WriteAsync("GET /tunnelled HTTP/1.1\r\nHost: origin.test\r\n\r\n"u8.ToArray(), ct);
        var response = await ReadUntilAsync(stream, "hello from origin");
        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.StartsWith("GET /tunnelled HTTP/1.1", Assert.Single(_requestsSeen));
    }

    [Fact]
    public async Task UnresolvableHostsGetAMarked502()
    {
        using var http = Client();
        using var response = await http.GetAsync("http://missing.test/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("dns", response.Headers.GetValues(PortalProxy.ErrorHeader).Single());
    }

    [Fact]
    public async Task OriginFormRequestsToTheProxyItselfAreRefused()
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(_proxy.Address, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient Client() => new(new SocketsHttpHandler { Proxy = new WebProxy(_proxy.Address), UseProxy = true });

    private async Task ServeOriginAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _origin.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using var _ = client;
                var stream = client.GetStream();
                var request = await ReadUntilAsync(stream, "\r\n\r\n");
                var length = request.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (length is not null)
                {
                    var want = int.Parse(length.Split(':')[1].Trim());
                    var have = request.Length - request.IndexOf("\r\n\r\n", StringComparison.Ordinal) - 4;
                    if (have < want)
                        request += await ReadCountAsync(stream, want - have);
                }
                lock (_requestsSeen)
                    _requestsSeen.Add(request);
                // Keep-alive on purpose: the proxy must still close, as it told the client.
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 17\r\nConnection: keep-alive\r\n\r\nhello from origin"u8.ToArray());
                await Task.Delay(200);
            });
        }
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string marker)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        return text.ToString();
    }

    private static async Task<string> ReadCountAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count && await stream.ReadAsync(buffer.AsMemory(total)) is var read and > 0)
            total += read;
        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    private sealed class StubResolver : IHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            host == "origin.test"
                ? Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback])
                : throw new HostResolutionException($"{host}: no such host");
    }
}
