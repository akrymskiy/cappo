// CapPo proof of concept: finds the network's DHCP DNS, runs the proxy, probes through it.
//   dotnet run --project src/CapPo.Poc                 one check, then exit
//   dotnet run --project src/CapPo.Poc -- --serve      keep the proxy up and keep checking (Ctrl+C to stop),
//                                                      e.g. for: curl -x http://127.0.0.1:<port> http://example.com
//   dotnet run --project src/CapPo.Poc -- --flush      also flush Windows' DNS cache
using System.Diagnostics;
using CapPo.Core.Detection;
using CapPo.Core.Network;
using Microsoft.Extensions.Logging;

var serve = args.Contains("--serve");
using var logs = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
    .SetMinimumLevel(args.Contains("-v") || serve ? LogLevel.Debug : LogLevel.Information));

var network = ActiveNetworkFinder.Find();
Console.WriteLine(network is null ? "No active network." : $"Active network: {network}");
if (network is not null)
{
    var stopwatch = Stopwatch.StartNew();
    var api = await DhcpCaptivePortalOption.TryGetAsync(network.AdapterId, TimeSpan.FromSeconds(3));
    Console.WriteLine($"DHCP option 114 (RFC 8910): {api?.ToString() ?? "none"} ({stopwatch.ElapsedMilliseconds} ms)");

    var resolver = new DhcpDnsResolver();
    resolver.SetServers(network.DnsServers);
    stopwatch.Restart();
    try
    {
        var addresses = await resolver.ResolveAsync(ConnectivityProbe.ProbeUrl.Host, CancellationToken.None);
        Console.WriteLine($"{ConnectivityProbe.ProbeUrl.Host} via {string.Join(", ", network.DnsServers)}: {string.Join(", ", addresses)} ({stopwatch.ElapsedMilliseconds} ms)");
    }
    catch (HostResolutionException ex)
    {
        Console.WriteLine($"DNS failed: {ex.Message}");
    }
}

await using var detector = new PortalDetector(logs);
Console.WriteLine($"Proxy: {detector.Proxy.Address}");

var timer = Stopwatch.StartNew();
var result = await detector.CheckAsync();
Console.WriteLine($"Result: {result.State} in {timer.ElapsedMilliseconds} ms. {result.Detail}");
if (result.State == NetworkState.Portal)
    Console.WriteLine($"Sign in at {result.SignInUrl} with --proxy-server={detector.Proxy.Address.Authority}");

if (args.Contains("--flush"))
    Console.WriteLine($"DNS cache flushed: {DnsCache.Flush()}");

if (serve)
{
    await using var monitor = new PortalMonitor(detector, logs.CreateLogger<PortalMonitor>()) { RecheckInterval = TimeSpan.FromMinutes(1) };
    monitor.StateChanged += r => Console.WriteLine($"==> {r.State}: {r.Detail}");
    monitor.SignedIn += _ => Console.WriteLine("==> Signed in; DNS cache flushed.");
    monitor.Start(watchNetwork: true, first: result);
    Console.WriteLine("Serving; Ctrl+C to stop.");
    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
    await done.Task;
}

return result.State == NetworkState.Online ? 0 : 1;
