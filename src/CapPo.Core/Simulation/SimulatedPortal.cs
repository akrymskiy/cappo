using System.Net;
using System.Net.Sockets;
using System.Text;
using CapPo.Core.Detection;
using CapPo.Core.Network;
using CapPo.Core.Proxy;

namespace CapPo.Core.Simulation;

/// <summary>
/// A pretend captive portal for trying CapPo without a hotel: a small web server on 127.0.0.2:80 and a resolver that
/// sends every name to it, as a real portal's DNS does. Until you press "Accept" on its page it redirects everything
/// there, the probe included; afterwards it answers the probe as Microsoft's server would. Sessions can expire, to try
/// being captured again.
/// </summary>
/// <remarks>For development only: the app enables it from a debug build's command line.</remarks>
public sealed class SimulatedPortal : IHostResolver, IAsyncDisposable
{
    public static readonly IPAddress DefaultAddress = IPAddress.Parse("127.0.0.2");
    private const string PortalHost = "portal.cappo.test";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan? _sessionLength;
    private Task _loop = Task.CompletedTask;
    private DateTimeOffset? _signedInUntil;

    /// <param name="sessionLength">How long a sign-in lasts; null for good.</param>
    public SimulatedPortal(IPAddress? address = null, int port = 80, TimeSpan? sessionLength = null)
    {
        Address = address ?? DefaultAddress;
        _listener = new TcpListener(Address, port);
        _sessionLength = sessionLength;
    }

    public IPAddress Address { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool SignedIn => _signedInUntil is { } until && DateTimeOffset.Now < until;

    /// <summary>Lets the proxy connect to the simulated portal, which is on this PC.</summary>
    public bool AllowDestination(IPAddress address) => address.Equals(Address) || PortalProxy.IsAllowedByDefault(address);

    public void Start()
    {
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    public void SignOut() => _signedInUntil = null;

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IPAddress>>([Address]);

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client));
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stop.IsCancellationRequested)
                    break;
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            var reader = new HeadReader(stream);
            var request = await reader.ReadHeadAsync(_stop.Token).ConfigureAwait(false);
            if (request is null)
                return;
            var parts = request.StartLine.Split(' ');
            var (method, path) = (parts[0], parts.Length > 1 ? parts[1] : "/");
            var host = request.Get("Host") ?? "";

            // Read the form before answering: closing with it unread would reset the connection.
            if (int.TryParse(request.Get("Content-Length"), out var bodyLength) && bodyLength is > 0 and < 65536)
            {
                var remaining = bodyLength - reader.TakeBuffered().Length;
                var buffer = new byte[4096];
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), _stop.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    remaining -= read;
                }
            }

            if (method == "POST" && path.StartsWith("/login", StringComparison.Ordinal))
            {
                _signedInUntil = _sessionLength is { } length ? DateTimeOffset.Now + length : DateTimeOffset.MaxValue;
                await RespondAsync(stream, 200, Page("You're online",
                    "<p>The simulated portal has let you in. CapPo should notice within a few seconds and close this window.</p>")).ConfigureAwait(false);
            }
            else if (SignedIn && path.StartsWith("/connecttest.txt", StringComparison.Ordinal))
            {
                await RespondAsync(stream, 200, ConnectivityProbe.ExpectedBody, "text/plain").ConfigureAwait(false);
            }
            else if (host.StartsWith(PortalHost, StringComparison.OrdinalIgnoreCase) && path.StartsWith("/login", StringComparison.Ordinal))
            {
                await RespondAsync(stream, 200, ShowcaseName is { } name ? ShowcasePage(name) : Page("Welcome to Simulated Hotel Wi-Fi",
                    $"""
                    <p>This is CapPo's pretend captive portal. {(_sessionLength is { } l ? $"A sign-in lasts {l.TotalMinutes:0.#} minutes." : "")}</p>
                    <form method="post" action="/login"><label><input type="checkbox" required> I accept the terms</label>
                    <p><button type="submit">Accept and connect</button></p></form>
                    """)).ConfigureAwait(false);
            }
            else if (SignedIn)
            {
                await RespondAsync(stream, 200, Page("Simulated internet", $"<p>{WebUtility.HtmlEncode(host + path)}</p>")).ConfigureAwait(false);
            }
            else
            {
                // A real portal's move: send everything to its sign-in page.
                await RespondAsync(stream, 302, "", location: $"http://{PortalHost}/login?from={Uri.EscapeDataString(host + path)}").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or FormatException or OperationCanceledException)
        {
            // Not worth more for a simulation.
        }
    }

    /// <summary>
    /// Set: the sign-in page looks like a real hotel's, under this name, for screenshots (CapPo's --screenshots).
    /// Signing in works the same.
    /// </summary>
    public string? ShowcaseName { get; init; }

    private static string ShowcasePage(string name) =>
        $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <title>{{WebUtility.HtmlEncode(name)}}</title><style>
        body{margin:0;font-family:Georgia,serif;background:#f4efe6;color:#2b2621}
        header{background:#1f3a4d;color:#f4efe6;padding:2.2em 1em;text-align:center}
        header h1{margin:0;font-weight:normal;letter-spacing:.06em}header p{margin:.4em 0 0;opacity:.8;font-style:italic}
        main{max-width:26em;margin:2em auto;padding:2em;background:#fff;border-radius:6px;box-shadow:0 2px 12px #0002;font-family:Segoe UI,sans-serif}
        label{display:block;margin:0 0 1em;font-size:.95em}input[type=text]{display:block;width:100%;box-sizing:border-box;margin-top:.3em;padding:.55em;font:inherit;border:1px solid #c9c1b4;border-radius:4px}
        .terms{display:flex;gap:.5em;align-items:center}button{width:100%;padding:.75em;font:inherit;font-weight:600;color:#fff;background:#b0603a;border:0;border-radius:4px}
        footer{text-align:center;font-size:.8em;color:#7a7065;font-family:Segoe UI,sans-serif}
        </style></head><body>
        <header><h1>{{WebUtility.HtmlEncode(name)}}</h1><p>Complimentary Wi-Fi for our guests</p></header>
        <main><form method="post" action="/login">
        <label>Room number<input type="text" value="1204"></label>
        <label>Last name<input type="text" value="Corleone"></label>
        <label class="terms"><input type="checkbox" required> I accept the terms of use</label>
        <button type="submit">Connect</button></form></main>
        <footer>Need help? Call the front desk at extension 0.</footer>
        </body></html>
        """;

    private static string Page(string title, string body) =>
        $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <title>{{title}}</title><style>body{font-family:Segoe UI,sans-serif;max-width:36em;margin:3em auto;padding:0 1em}
        button{font:inherit;padding:.5em 1.5em}</style></head><body><h1>{{title}}</h1>{{body}}</body></html>
        """;

    private static async Task RespondAsync(Stream stream, int status, string body, string type = "text/html", string? location = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 302 => "Found", _ => "Error" };
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}; charset=utf-8\r\n")
            .Append($"Content-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n");
        if (location is not null)
            head.Append($"Location: {location}\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString())).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
