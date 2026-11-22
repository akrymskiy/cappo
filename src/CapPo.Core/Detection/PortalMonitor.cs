using System.Net.NetworkInformation;
using CapPo.Core.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CapPo.Core.Detection;

/// <summary>
/// Keeps checking the network with <see cref="PortalDetector"/>: right away, when the network changes, every few
/// seconds while a portal holds you, and now and then otherwise, to catch portals that capture you again once a
/// session expires. Used by the tray app, and by a one-shot <c>--check</c> run while its sign-in window is open.
/// </summary>
/// <remarks>Events are raised on a thread-pool thread.</remarks>
public sealed class PortalMonitor : IAsyncDisposable
{
    // After a network change, DHCP and the portal can take a while to come up; check often until then.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettlePoll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NetworkChangeDebounce = TimeSpan.FromSeconds(1.5);

    private readonly PortalDetector _detector;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Timer _debounce;
    private Task _loop = Task.CompletedTask;
    private DateTimeOffset _settleUntil;
    private bool _watching;

    public PortalMonitor(PortalDetector detector, ILogger? log = null)
    {
        _detector = detector;
        _log = log ?? NullLogger.Instance;
        _debounce = new Timer(_ => OnNetworkSettled());
    }

    /// <summary>How often to re-check while online (or unreachable).</summary>
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>How often to check while a portal holds you, to notice the sign-in.</summary>
    public TimeSpan PortalPollInterval { get; set; } = TimeSpan.FromSeconds(3);

    public DetectionResult? Last { get; private set; }

    /// <summary>After every check.</summary>
    public event Action<DetectionResult>? Checked;

    /// <summary>When the state or the network changed since the check before.</summary>
    public event Action<DetectionResult>? StateChanged;

    /// <summary>A portal let you through; the DNS cache has been flushed.</summary>
    public event Action<DetectionResult>? SignedIn;

    /// <summary>Starts checking. With <paramref name="watchNetwork"/>, network changes trigger a check too.</summary>
    /// <param name="first">A check already made (e.g. by a one-shot run), so it isn't repeated at once.</param>
    public void Start(bool watchNetwork, DetectionResult? first = null)
    {
        Last = first;
        _settleUntil = DateTimeOffset.Now + SettleTime;
        if (watchNetwork)
            WatchNetwork();
        _loop = RunAsync(skipFirst: first is not null);
    }

    /// <summary>From now on, network changes trigger a check too (e.g. once a one-shot run becomes the tray app).</summary>
    public void WatchNetwork()
    {
        if (_watching)
            return;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _watching = true;
    }

    /// <summary>Checks as soon as possible; the result arrives through the events.</summary>
    public void CheckNow() => _wake.Release();

    /// <summary>A check is running; its result arrives through <see cref="Checked"/>.</summary>
    public bool IsChecking { get; private set; }

    /// <summary>A check started or ended (<see cref="IsChecking"/>). Raised on the monitor's thread.</summary>
    public event Action? CheckingChanged;

    /// <summary>
    /// Something outside the adapters changed, e.g. Windows' own connectivity check now says no internet: a VPN's
    /// tunnel losing its way out changes no address. Checked like a network change.
    /// </summary>
    public void NetworkMayHaveChanged() => OnNetworkChanged(null, EventArgs.Empty);

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        // Address changes come in bursts (IPv4, IPv6, each adapter); act once they stop.
        _debounce.Change(NetworkChangeDebounce, Timeout.InfiniteTimeSpan);

    private void OnNetworkSettled()
    {
        _log.LogInformation("Network changed");
        _settleUntil = DateTimeOffset.Now + SettleTime;
        _wake.Release();
    }

    private async Task RunAsync(bool skipFirst)
    {
        var skip = skipFirst;
        while (!_stop.IsCancellationRequested)
        {
            if (!skip)
            {
                IsChecking = true;
                CheckingChanged?.Invoke();
                try
                {
                    var result = await _detector.CheckAsync(_stop.Token).ConfigureAwait(false);
                    IsChecking = false;
                    Publish(result);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Check failed");
                }
                finally
                {
                    IsChecking = false;
                    CheckingChanged?.Invoke();
                }
            }
            skip = false;

            try
            {
                await _wake.WaitAsync(NextDelay(), _stop.Token).ConfigureAwait(false);
                // Several wake-ups at once need one check only.
                while (_wake.CurrentCount > 0)
                    await _wake.WaitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private TimeSpan NextDelay() => Last?.State switch
    {
        NetworkState.Portal => PortalPollInterval,
        NetworkState.NoNetwork or NetworkState.Unreachable when DateTimeOffset.Now < _settleUntil => SettlePoll,
        _ => RecheckInterval,
    };

    private void Publish(DetectionResult result)
    {
        var previous = Last;
        Last = result;
        Checked?.Invoke(result);
        if (result.SameAs(previous))
            return;

        StateChanged?.Invoke(result);
        if (previous?.State == NetworkState.Portal && result.State == NetworkState.Online)
        {
            // While the portal blocked encrypted DNS, Windows cached the failures.
            var flushed = DnsCache.Flush();
            _log.LogInformation("Signed in; DNS cache flushed: {Flushed}", flushed);
            SignedIn?.Invoke(result);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watching)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        }
        await _debounce.DisposeAsync().ConfigureAwait(false);
        _stop.Cancel();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
        _wake.Dispose();
    }
}
