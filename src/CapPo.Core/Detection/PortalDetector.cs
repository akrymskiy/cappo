using CapPo.Core.Network;
using CapPo.Core.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CapPo.Core.Detection;

/// <summary>Defaults suit real networks; tests and the simulated portal change them.</summary>
public sealed record PortalDetectorOptions
{
    /// <summary>Null: a <see cref="DhcpDnsResolver"/>.</summary>
    public IHostResolver? Resolver { get; init; }

    /// <summary>Null: <see cref="PortalProxy.IsAllowedByDefault"/>.</summary>
    public Func<System.Net.IPAddress, bool>? AllowDestination { get; init; }

    /// <summary>Null: <see cref="ConnectivityProbe.ProbeUrl"/>.</summary>
    public Uri? ProbeUrl { get; init; }

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(4);
}

public enum NetworkState
{
    /// <summary>No adapter is connected.</summary>
    NoNetwork,
    /// <summary>The internet is reachable.</summary>
    Online,
    /// <summary>A captive portal wants you to sign in.</summary>
    Portal,
    /// <summary>Connected, but nothing answers: no internet, and no portal to show.</summary>
    Unreachable,
}

/// <param name="SignInUrl">For <see cref="NetworkState.Portal"/>: what the sign-in window opens.</param>
/// <param name="Detail">What the check saw, for the log and the tray tooltip.</param>
public sealed record DetectionResult(
    NetworkState State,
    ActiveNetwork? Network,
    Uri? SignInUrl,
    string Detail,
    DateTimeOffset CheckedAt)
{
    /// <summary>Whether this means the same as <paramref name="other"/>: state and network, not the details.</summary>
    public bool SameAs(DetectionResult? other) =>
        other is not null && other.State == State && other.Network?.Fingerprint == Network?.Fingerprint;
}

/// <summary>
/// One check of the current network: finds it, points the proxy at its DHCP DNS, and probes. Owns the proxy, which
/// keeps its port for the detector's life, so a sign-in window can rely on it.
/// </summary>
public sealed class PortalDetector : IAsyncDisposable
{
    private static readonly TimeSpan DhcpOptionTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(3);

    private readonly ILogger _log;
    private readonly ConnectivityProbe _probe;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    // Option 114, read once per network.
    private string? _apiFingerprint;
    private Task<Uri?>? _apiUrl;

    public PortalDetector(ILoggerFactory? logs = null, PortalDetectorOptions? options = null)
    {
        options ??= new();
        logs ??= NullLoggerFactory.Instance;
        _log = logs.CreateLogger<PortalDetector>();
        Resolver = options.Resolver ?? new DhcpDnsResolver();
        Proxy = new PortalProxy(Resolver, logs.CreateLogger<PortalProxy>())
        {
            AllowDestination = options.AllowDestination ?? PortalProxy.IsAllowedByDefault,
        };
        Proxy.Start();
        _probe = new ConnectivityProbe(Proxy.Address, options.ProbeTimeout, options.ProbeUrl);
    }

    /// <summary>Normally a <see cref="DhcpDnsResolver"/>, pointed at each network's DNS as it's found.</summary>
    public IHostResolver Resolver { get; }

    public PortalProxy Proxy { get; }

    public async Task<DetectionResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
            _log.LogInformation("{State}: {Detail} [{Network}]", result.State, result.Detail, result.Network?.ToString() ?? "no network");
            return result;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<DetectionResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        var network = ActiveNetworkFinder.Find();
        if (network is null)
            return Result(NetworkState.NoNetwork, null, null, "No connected adapter has a gateway.");
        (Resolver as DhcpDnsResolver)?.SetServers(network.DnsServers);

        // Read option 114 alongside the probe; it only matters when the probe doesn't say "online".
        if (_apiFingerprint != network.Fingerprint)
        {
            _apiFingerprint = network.Fingerprint;
            _apiUrl = DhcpCaptivePortalOption.TryGetAsync(network.AdapterId, DhcpOptionTimeout);
        }
        var probe = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (probe.Outcome == ProbeOutcome.Online)
            return Result(NetworkState.Online, network, null, probe.Detail);

        var api = await QueryApiAsync(cancellationToken).ConfigureAwait(false);
        if (api is { Captive: true })
            return Result(NetworkState.Portal, network, api.UserPortalUrl ?? _probe.Url,
                $"Captive Portal API says captive; probe: {probe.Detail}");

        // Open the probe page itself rather than where it redirected: redirects often carry one-time tokens.
        return probe.Outcome == ProbeOutcome.Portal
            ? Result(NetworkState.Portal, network, api?.UserPortalUrl ?? _probe.Url, probe.Detail)
            : Result(NetworkState.Unreachable, network, null, probe.Detail);
    }

    private async Task<CaptivePortalApiState?> QueryApiAsync(CancellationToken cancellationToken)
    {
        if (_apiUrl is null || await _apiUrl.ConfigureAwait(false) is not { } url)
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        var state = await _probe.QueryApiAsync(url, timeout.Token).ConfigureAwait(false);
        _log.LogInformation("Captive Portal API {Url}: {State}", url, state?.ToString() ?? "no answer");
        return state;
    }

    private static DetectionResult Result(NetworkState state, ActiveNetwork? network, Uri? signIn, string detail) =>
        new(state, network, signIn, detail, DateTimeOffset.Now);

    public async ValueTask DisposeAsync()
    {
        _probe.Dispose();
        await Proxy.DisposeAsync().ConfigureAwait(false);
        _oneAtATime.Dispose();
    }
}
