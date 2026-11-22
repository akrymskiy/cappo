using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CapPo.Core.Proxy;

namespace CapPo.Core.Detection;

public enum ProbeOutcome
{
    /// <summary>The probe page came back as it should: the internet is reachable.</summary>
    Online,
    /// <summary>Something else answered for the probe page: a captive portal.</summary>
    Portal,
    /// <summary>The probe couldn't get through at all (DNS or connection failed): not a portal we can show.</summary>
    Unreachable,
}

/// <param name="PortalUrl">For <see cref="ProbeOutcome.Portal"/>: where the portal redirected to, if it did.</param>
/// <param name="ProxyError">What the proxy said failed ("dns", "connect", "blocked" or "request"), if it did.</param>
public sealed record ProbeResult(ProbeOutcome Outcome, Uri? PortalUrl, string Detail, string? ProxyError = null);

/// <summary>What a network's Captive Portal API (RFC 8908) says.</summary>
public sealed record CaptivePortalApiState(bool Captive, Uri? UserPortalUrl, TimeSpan? SecondsRemaining);

/// <summary>Probes Microsoft's connectivity-test page, and the RFC 8908 API, through <see cref="PortalProxy"/>.</summary>
public sealed class ConnectivityProbe : IDisposable
{
    /// <summary>The page Windows' own network status check (NCSI) fetches.</summary>
    public static readonly Uri ProbeUrl = new("http://www.msftconnecttest.com/connecttest.txt");

    public const string ExpectedBody = "Microsoft Connect Test";

    /// <summary>
    /// Fetched when the network's DNS won't resolve <see cref="ProbeUrl"/>, which needs no DNS at all. Cloudflare's
    /// resolver address is the same everywhere and answers plain HTTP with a redirect to its own HTTPS page; portals
    /// answer requests to any address with their own page or a redirect to it.
    /// </summary>
    public static readonly Uri AddressProbeUrl = new("http://1.1.1.1/");

    private const int MaxBodyBytes = 64 * 1024;

    private readonly HttpClient _http;
    // Without the proxy: Windows' own DNS and routes, so through a VPN when one carries the traffic.
    private readonly HttpClient _direct;

    /// <param name="url">Null: <see cref="ProbeUrl"/>.</param>
    /// <param name="addressUrl">Null: <see cref="AddressProbeUrl"/>.</param>
    public ConnectivityProbe(Uri proxy, TimeSpan timeout, Uri? url = null, Uri? addressUrl = null)
    {
        Url = url ?? ProbeUrl;
        AddressUrl = addressUrl ?? AddressProbeUrl;
        _http = NewClient(new WebProxy(proxy) { BypassProxyOnLocal = false }, timeout);
        _direct = NewClient(null, timeout);
    }

    private static HttpClient NewClient(WebProxy? proxy, TimeSpan timeout)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = proxy is not null,
            // A redirect is the portal's answer; following it would hide that.
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            // One request per check: no connection kept to land on the wrong network next time.
            PooledConnectionLifetime = TimeSpan.Zero,
        }, disposeHandler: true)
        {
            Timeout = timeout,
        };
        // As Windows' own check sends it: some portals only redirect clients that look like one.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Microsoft NCSI");
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        http.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
        return http;
    }

    /// <summary>The page this probe fetches.</summary>
    public Uri Url { get; }

    /// <summary>The page fetched by address when DNS fails.</summary>
    public Uri AddressUrl { get; }

    public void Dispose()
    {
        _http.Dispose();
        _direct.Dispose();
    }

    public Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
        FetchAsync(_http, Url, ClassifyAsync, cancellationToken);

    /// <summary>Fetches <see cref="AddressUrl"/>, for when the network's DNS fails.</summary>
    public Task<ProbeResult> ProbeAddressAsync(CancellationToken cancellationToken) =>
        FetchAsync(_http, AddressUrl, ClassifyAddressAsync, cancellationToken);

    /// <summary>
    /// Fetches <see cref="Url"/> the way any app would, with Windows' DNS and routes rather than the network's DNS:
    /// through a VPN, when one carries the traffic.
    /// </summary>
    public Task<ProbeResult> ProbeDirectAsync(CancellationToken cancellationToken) =>
        FetchAsync(_direct, Url, ClassifyAsync, cancellationToken);

    private static async Task<ProbeResult> FetchAsync(HttpClient http,
        Uri url, Func<HttpResponseMessage, Uri, CancellationToken, Task<ProbeResult>> classify, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return await classify(response, url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new(ProbeOutcome.Unreachable, null, ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ProbeOutcome.Unreachable, null, "The probe timed out.");
        }
    }

    internal static async Task<ProbeResult> ClassifyAsync(HttpResponseMessage response, Uri url, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (await ProxyErrorAsync(response, cancellationToken).ConfigureAwait(false) is { } error)
            return error;

        if (status is >= 300 and < 400)
        {
            var location = Location(response, url);
            return new(ProbeOutcome.Portal, location, $"HTTP {status} to {location}");
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (status == 200 && body.Trim() == ExpectedBody)
            return new(ProbeOutcome.Online, null, "HTTP 200, expected body");

        var preview = body.Length > 60 ? body[..60] + "…" : body;
        return new(ProbeOutcome.Portal, null, $"HTTP {status}, unexpected body: {preview.ReplaceLineEndings(" ")}");
    }

    /// <summary>
    /// Online when <see cref="AddressUrl"/> redirects to its own host (Cloudflare sending plain HTTP to HTTPS), a portal
    /// for any other answer.
    /// </summary>
    internal static async Task<ProbeResult> ClassifyAddressAsync(HttpResponseMessage response, Uri url, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (await ProxyErrorAsync(response, cancellationToken).ConfigureAwait(false) is { } error)
            return error;

        if (status is >= 300 and < 400)
        {
            var location = Location(response, url);
            return string.Equals(location?.Host, url.Host, StringComparison.OrdinalIgnoreCase)
                ? new(ProbeOutcome.Online, null, $"HTTP {status} to {location}, as {url.Host} answers")
                : new(ProbeOutcome.Portal, location, $"HTTP {status} to {location}");
        }
        return new(ProbeOutcome.Portal, null, $"HTTP {status} from {url.Host}, which only redirects to HTTPS");
    }

    private static async Task<ProbeResult?> ProxyErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.Headers.TryGetValues(PortalProxy.ErrorHeader, out var errors))
            return null;
        var kind = errors.First();
        return new(ProbeOutcome.Unreachable, null,
            $"{kind}: {await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false)}".Trim(), kind);
    }

    private static Uri? Location(HttpResponseMessage response, Uri url)
    {
        var location = response.Headers.Location;
        return location is not null && !location.IsAbsoluteUri ? new Uri(url, location) : location;
    }

    /// <summary>Asks the network's Captive Portal API (from DHCP option 114) whether this device is held captive.</summary>
    /// <returns>Null when the API can't be reached or answers nonsense.</returns>
    public async Task<CaptivePortalApiState?> QueryApiAsync(Uri api, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, api);
            request.Headers.Accept.ParseAdd("application/captive+json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return ParseApi(await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static CaptivePortalApiState? ParseApi(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("captive", out var captive) || captive.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            Uri? portal = null;
            if (root.TryGetProperty("user-portal-url", out var url) && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
                portal = parsed;
            TimeSpan? remaining = root.TryGetProperty("seconds-remaining", out var seconds) && seconds.TryGetDouble(out var s)
                ? TimeSpan.FromSeconds(s)
                : null;
            return new(captive.GetBoolean(), portal, remaining);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxBodyBytes];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            total += read;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
