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
public sealed record ProbeResult(ProbeOutcome Outcome, Uri? PortalUrl, string Detail);

/// <summary>What a network's Captive Portal API (RFC 8908) says.</summary>
public sealed record CaptivePortalApiState(bool Captive, Uri? UserPortalUrl, TimeSpan? SecondsRemaining);

/// <summary>Probes Microsoft's connectivity-test page, and the RFC 8908 API, through <see cref="PortalProxy"/>.</summary>
public sealed class ConnectivityProbe : IDisposable
{
    /// <summary>The page Windows' own network status check (NCSI) fetches.</summary>
    public static readonly Uri ProbeUrl = new("http://www.msftconnecttest.com/connecttest.txt");

    public const string ExpectedBody = "Microsoft Connect Test";

    private const int MaxBodyBytes = 64 * 1024;

    private readonly HttpClient _http;

    /// <param name="url">Null: <see cref="ProbeUrl"/>.</param>
    public ConnectivityProbe(Uri proxy, TimeSpan timeout, Uri? url = null)
    {
        Url = url ?? ProbeUrl;
        _http = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy) { BypassProxyOnLocal = false },
            UseProxy = true,
            // A redirect is the portal's answer; following it would hide that.
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        }, disposeHandler: true)
        {
            Timeout = timeout,
        };
        // As Windows' own check sends it: some portals only redirect clients that look like one.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Microsoft NCSI");
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        _http.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
    }

    /// <summary>The page this probe fetches.</summary>
    public Uri Url { get; }

    public void Dispose() => _http.Dispose();

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            return await ClassifyAsync(response, Url, cancellationToken).ConfigureAwait(false);
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
        if (response.Headers.TryGetValues(PortalProxy.ErrorHeader, out var errors))
            return new(ProbeOutcome.Unreachable, null, $"{errors.First()}: {await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false)}".Trim());

        if (status is >= 300 and < 400)
        {
            var location = response.Headers.Location;
            if (location is not null && !location.IsAbsoluteUri)
                location = new Uri(url, location);
            return new(ProbeOutcome.Portal, location, $"HTTP {status} to {location}");
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (status == 200 && body.Trim() == ExpectedBody)
            return new(ProbeOutcome.Online, null, "HTTP 200, expected body");

        var preview = body.Length > 60 ? body[..60] + "…" : body;
        return new(ProbeOutcome.Portal, null, $"HTTP {status}, unexpected body: {preview.ReplaceLineEndings(" ")}");
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
