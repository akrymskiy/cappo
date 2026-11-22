using CapPo.Core.Detection;
using CapPo.Core.Simulation;
using Windows.Networking.Connectivity;

namespace CapPo.App;

/// <summary>The detector (and its proxy), pointed at real networks or at the simulated portal.</summary>
internal sealed class Engine : IAsyncDisposable
{
    private readonly SimulatedPortal? _simulation;

    private Engine(PortalDetector detector, SimulatedPortal? simulation)
    {
        Detector = detector;
        _simulation = simulation;
    }

    public PortalDetector Detector { get; }

    public bool IsSimulated => _simulation is not null;

    public static Engine Create(LaunchOptions options)
    {
        // A one-shot check shouldn't keep a process around for long on a dead network.
        var probeTimeout = TimeSpan.FromSeconds(options.Check ? 2 : 4);
        if (options.Simulate is not { } session)
            return new Engine(new PortalDetector(Log.Factory, new PortalDetectorOptions { ProbeTimeout = probeTimeout }), null);

        var simulation = new SimulatedPortal(sessionLength: session == Timeout.InfiniteTimeSpan ? null : session);
        simulation.Start();
        Log.Info($"Simulated portal on {simulation.Address}:{simulation.Port}");
        return new Engine(new PortalDetector(Log.Factory, new PortalDetectorOptions
        {
            Resolver = simulation,
            AllowDestination = simulation.AllowDestination,
            ProbeTimeout = probeTimeout,
        }), simulation);
    }

    /// <summary>What to call the network in the UI: the Wi-Fi name if Windows will say, else the adapter's.</summary>
    public static string NetworkName(DetectionResult? result) =>
        result?.Network is { } network ? NetworkName(network.AdapterId, network.AdapterName) : "the network";

    /// <param name="adapterName">What to say when Windows doesn't name the network, e.g. "Wi-Fi".</param>
    public static string NetworkName(string adapterId, string adapterName)
    {
        try
        {
            // Every saved Wi-Fi profile is listed against the adapter, connected or not; only the connected one has a
            // connectivity level (behind a portal it's LocalAccess or ConstrainedInternetAccess, not None).
            if (Guid.TryParse(adapterId, out var adapter)
                && NetworkInformation.GetConnectionProfiles().FirstOrDefault(p =>
                    p.NetworkAdapter?.NetworkAdapterId == adapter
                    && p.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None) is { } profile
                && !string.IsNullOrWhiteSpace(profile.ProfileName))
                return profile.ProfileName;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't read the network's name");
        }
        return adapterName;
    }

    public async ValueTask DisposeAsync()
    {
        await Detector.DisposeAsync();
        if (_simulation is not null)
            await _simulation.DisposeAsync();
    }
}
