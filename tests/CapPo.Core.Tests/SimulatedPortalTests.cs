using System.Net;
using CapPo.Core.Detection;
using CapPo.Core.Simulation;

namespace CapPo.Core.Tests;

/// <summary>Detection end to end, through the real proxy and probe, against the simulated portal.</summary>
public sealed class SimulatedPortalTests : IAsyncLifetime
{
    // A random port, so tests don't need port 80 or clash with a running CapPo.
    private readonly SimulatedPortal _portal = new(port: 0);
    private PortalDetector _detector = null!;

    public ValueTask InitializeAsync()
    {
        _portal.Start();
        _detector = new PortalDetector(options: new PortalDetectorOptions
        {
            Resolver = _portal,
            AllowDestination = _portal.AllowDestination,
            ProbeUrl = new Uri($"http://www.msftconnecttest.com:{_portal.Port}/connecttest.txt"),
            // The simulation only answers through the proxy, VPN or not.
            ProbeThroughVpn = false,
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _detector.DisposeAsync();
        await _portal.DisposeAsync();
    }

    [Fact]
    public async Task APortalIsFoundThenSigningInMakesItOnline()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await _detector.CheckAsync(ct);
        if (before.State == NetworkState.NoNetwork)
            Assert.Skip("Needs a network adapter with a gateway.");
        Assert.Equal(NetworkState.Portal, before.State);
        Assert.Contains("302", before.Detail);

        await SignInAsync(ct);

        var after = await _detector.CheckAsync(ct);
        Assert.Equal(NetworkState.Online, after.State);
    }

    [Fact]
    public async Task TheMonitorReportsTheSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var monitor = new PortalMonitor(_detector) { PortalPollInterval = TimeSpan.FromMilliseconds(200) };
        var portal = new TaskCompletionSource<DetectionResult>();
        var signedIn = new TaskCompletionSource<DetectionResult>();
        monitor.StateChanged += r =>
        {
            if (r.State is NetworkState.Portal or NetworkState.NoNetwork)
                portal.TrySetResult(r);
        };
        monitor.SignedIn += r => signedIn.TrySetResult(r);
        monitor.Start(watchNetwork: false);

        var found = await portal.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (found.State == NetworkState.NoNetwork)
            Assert.Skip("Needs a network adapter with a gateway.");

        await SignInAsync(ct);
        var result = await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(NetworkState.Online, result.State);
    }

    // What the user does in the sign-in window, through the same proxy.
    private async Task SignInAsync(CancellationToken ct)
    {
        using var browser = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(_detector.Proxy.Address), UseProxy = true });
        using var response = await browser.PostAsync($"http://portal.cappo.test:{_portal.Port}/login",
            new FormUrlEncodedContent([new("accept", "on")]), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_portal.SignedIn);
    }
}
