using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CapPo.Core.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CapPo.Core.Proxy;

/// <summary>
/// An HTTP proxy on 127.0.0.1 (a random port) that looks names up with <see cref="IHostResolver"/> (the network's DHCP
/// DNS) rather than Windows' resolver. Plain http requests are forwarded; https goes through CONNECT tunnels, so the
/// proxy never sees inside them.
/// </summary>
/// <remarks>
/// Each client connection carries one request: the proxy sends "Connection: close" both ways, so a browser never reuses
/// a connection for another host. That keeps forwarding to copying bytes, with no need to parse bodies.
/// </remarks>
public sealed class PortalProxy : IAsyncDisposable
{
    /// <summary>On the proxy's own error responses, saying what failed: "dns", "connect", "blocked" or "request".</summary>
    public const string ErrorHeader = "X-CapPo-Error";

    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    private readonly IHostResolver _resolver;
    private readonly ILogger _log;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Socket, byte> _connections = new();
    private Task _acceptLoop = Task.CompletedTask;

    public PortalProxy(IHostResolver resolver, ILogger? log = null)
    {
        _resolver = resolver;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>The port it listens on, once started.</summary>
    public int Port { get; private set; }

    /// <summary>For --proxy-server and <see cref="WebProxy"/>.</summary>
    public Uri Address => new($"http://127.0.0.1:{Port}/");

    /// <summary>
    /// Which addresses the proxy will connect to. By default, anything but this PC itself, so a page can't use the
    /// portal's DNS to point the proxy at local services. Portals do live on private addresses, so those are allowed.
    /// </summary>
    public Func<IPAddress, bool> AllowDestination { get; init; } = IsAllowedByDefault;

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
        _log.LogInformation("Proxy listening on 127.0.0.1:{Port}", Port);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var socket in _connections.Keys)
            socket.Dispose();
        await _acceptLoop.ConfigureAwait(false);
        _stop.Dispose();
    }

    public static bool IsAllowedByDefault(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return !IPAddress.IsLoopback(address)
            && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
            && !address.Equals(IPAddress.Broadcast)
            && !address.IsIPv6Multicast
            && !(address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is >= 224 and <= 239);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "Accept failed");
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(Socket clientSocket)
    {
        clientSocket.NoDelay = true;
        _connections.TryAdd(clientSocket, 0);
        var client = new NetworkStream(clientSocket, ownsSocket: true);
        try
        {
            var reader = new HeadReader(client);
            HttpHead? request;
            using (var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                headTimeout.CancelAfter(HeadTimeout);
                request = await reader.ReadHeadAsync(headTimeout.Token).ConfigureAwait(false);
            }
            if (request is null)
                return;

            if (request.StartLine.Split(' ') is not [var method, var target, var version])
                throw new ProxyException(400, "request", "Malformed request line.");

            if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                await TunnelAsync(client, reader, target).ConfigureAwait(false);
            else
                await ForwardAsync(client, reader, request, method, target, version).ConfigureAwait(false);
        }
        catch (ProxyException ex)
        {
            _log.LogDebug("{Status} {Kind}: {Message}", ex.Status, ex.Kind, ex.Message);
            await TryWriteErrorAsync(client, ex).ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            await TryWriteErrorAsync(client, new ProxyException(400, "request", ex.Message)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // A side hung up, or the proxy is stopping.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Proxy connection failed");
        }
        finally
        {
            _connections.TryRemove(clientSocket, out _);
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    // CONNECT host:port: open a raw tunnel (https, or anything else the browser tunnels).
    private async Task TunnelAsync(NetworkStream client, HeadReader reader, string authority)
    {
        var (host, port) = ParseAuthority(authority, 443);
        _log.LogDebug("CONNECT {Host}:{Port}", host, port);
        await using var upstream = await ConnectAsync(host, port).ConfigureAwait(false);

        await client.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), _stop.Token).ConfigureAwait(false);
        var early = reader.TakeBuffered();
        if (!early.IsEmpty)
            await upstream.WriteAsync(early, _stop.Token).ConfigureAwait(false);

        // Either side closing ends the tunnel.
        var up = client.CopyToAsync(upstream, _stop.Token);
        var down = upstream.CopyToAsync(client, _stop.Token);
        await Task.WhenAny(up, down).ConfigureAwait(false);
    }

    // GET http://host/path: send it on in origin form, and the response back.
    private async Task ForwardAsync(NetworkStream client, HeadReader reader, HttpHead request, string method, string target, string version)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
            throw new ProxyException(400, "request", "This is CapPo's sign-in proxy; it takes absolute http:// URLs and CONNECT.");

        // The path exactly as the client sent it; Uri would re-escape it.
        var pathStart = target.IndexOf('/', "http://".Length);
        var path = pathStart < 0 ? "/" : target[pathStart..];
        var upgrade = request.Get("Upgrade") is not null;

        request.StartLine = $"{method} {path} {version}";
        if (request.Get("Host") is null)
            request.Set("Host", uri.Authority);
        request.Remove("Proxy-Connection");
        request.Remove("Proxy-Authorization");
        request.Remove("Keep-Alive");
        // Only "100-continue" exists; answering it would need body tracking, and senders go ahead without it.
        request.Remove("Expect");
        if (!upgrade)
            request.Set("Connection", "close");

        _log.LogDebug("{Method} {Host}{Path}", method, uri.Authority, path.Length > 80 ? path[..80] + "…" : path);
        await using var upstream = await ConnectAsync(uri.IdnHost, uri.Port).ConfigureAwait(false);
        await upstream.WriteAsync(request.ToBytes(), _stop.Token).ConfigureAwait(false);
        var body = reader.TakeBuffered();
        if (!body.IsEmpty)
            await upstream.WriteAsync(body, _stop.Token).ConfigureAwait(false);

        // The rest of the request body (a portal's sign-in form), while the response comes back.
        _ = client.CopyToAsync(upstream, _stop.Token).ContinueWith(static _ => { }, TaskScheduler.Default);

        var responses = new HeadReader(upstream);
        while (true)
        {
            var response = await responses.ReadHeadAsync(_stop.Token).ConfigureAwait(false)
                ?? throw new ProxyException(502, "connect", $"{uri.Authority} closed the connection without answering.");
            var status = response.StatusCode;
            // 100 Continue and 103 Early Hints come before the real response.
            var interim = status is >= 100 and < 200 and not 101;
            if (!interim && !(upgrade && status == 101))
            {
                response.Remove("Keep-Alive");
                response.Remove("Proxy-Connection");
                response.Set("Connection", "close");
            }
            await client.WriteAsync(response.ToBytes(), _stop.Token).ConfigureAwait(false);
            if (!interim)
                break;
        }

        var rest = responses.TakeBuffered();
        if (!rest.IsEmpty)
            await client.WriteAsync(rest, _stop.Token).ConfigureAwait(false);
        // Until the server closes: it was asked to, after this response.
        await upstream.CopyToAsync(client, _stop.Token).ConfigureAwait(false);
    }

    private async Task<NetworkStream> ConnectAsync(string host, int port)
    {
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(host, _stop.Token).ConfigureAwait(false);
        }
        catch (HostResolutionException ex)
        {
            throw new ProxyException(502, "dns", ex.Message);
        }

        var allowed = addresses.Where(AllowDestination).ToList();
        if (allowed.Count == 0)
            throw new ProxyException(403, "blocked", $"{host} points at this PC ({string.Join(", ", addresses)}), which the sign-in proxy doesn't connect to.");

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !_stop.IsCancellationRequested)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw new ProxyException(502, "connect",
            $"Couldn't connect to {host}:{port} ({string.Join(", ", allowed)}): {(last is OperationCanceledException ? "timed out" : last?.Message)}");
    }

    internal static (string Host, int Port) ParseAuthority(string authority, int defaultPort)
    {
        // [v6]:port, host:port, or host alone.
        string host;
        var portText = "";
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
                throw new ProxyException(400, "request", $"Bad address: {authority}");
            host = authority[1..close];
            if (close + 1 < authority.Length && authority[close + 1] == ':')
                portText = authority[(close + 2)..];
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            portText = colon < 0 ? "" : authority[(colon + 1)..];
        }

        var port = defaultPort;
        if (portText.Length > 0 && (!int.TryParse(portText, out port) || port is <= 0 or > 65535))
            throw new ProxyException(400, "request", $"Bad port: {authority}");
        if (host.Length == 0)
            throw new ProxyException(400, "request", $"No host: {authority}");
        return (host, port);
    }

    private async Task TryWriteErrorAsync(Stream client, ProxyException error)
    {
        var body = Encoding.UTF8.GetBytes($"CapPo couldn't reach this page.\r\n\r\n{error.Message}\r\n");
        var reason = error.Status switch { 400 => "Bad Request", 403 => "Forbidden", _ => "Bad Gateway" };
        var head = $"HTTP/1.1 {error.Status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            $"{ErrorHeader}: {error.Kind}\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n";
        try
        {
            await client.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token).ConfigureAwait(false);
            await client.WriteAsync(body, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The client is gone.
        }
    }
}

internal sealed class ProxyException(int status, string kind, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Kind { get; } = kind;
}
