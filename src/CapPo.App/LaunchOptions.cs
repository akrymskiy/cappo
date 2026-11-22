namespace CapPo.App;

/// <summary>CapPo's command line.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>--check</c>: one-shot run (On demand mode, from the scheduled task or background task). Probes, and exits
/// within about 2 seconds unless there's a portal; then shows the sign-in window and exits once online.</item>
/// <item><c>--startup</c>: started at sign-in (GitHub build's Run key); stays in the notification area.</item>
/// <item><c>--unregister</c>: removes the scheduled task and the Run key, e.g. before deleting the GitHub build.</item>
/// <item><c>-RegisterForBGTaskServer</c>: started by COM for the Store build's background task.</item>
/// <item><c>--simulate-portal[=minutes]</c> (debug builds, or built with -p:CapPoSimulation=true): use the simulated portal, optionally with sessions that
/// expire, to try the sign-in window without a captive network.</item>
/// <item><c>--pretend-no-webview2</c> (debug builds): act as if the WebView2 Runtime were missing, to try CapPo's
/// warnings about it.</item>
/// </list>
/// </remarks>
internal sealed record LaunchOptions(bool Check, bool Startup, bool Unregister, bool BackgroundTaskServer, TimeSpan? Simulate, bool PretendNoWebView2 = false)
{
    public bool SimulatePortal => Simulate is not null;

    public static LaunchOptions Parse(IEnumerable<string> args)
    {
        var list = args.Select(static a => a.Trim()).ToList();
        bool Has(string name) => list.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        TimeSpan? simulate = null;
#if DEBUG || SIMULATION
        if (list.FirstOrDefault(static a => a.StartsWith("--simulate-portal", StringComparison.OrdinalIgnoreCase)) is { } sim)
            simulate = sim.Split('=') is [_, var minutes] && double.TryParse(minutes, out var m) && m > 0
                ? TimeSpan.FromMinutes(m)
                : Timeout.InfiniteTimeSpan;
#endif
        var pretendNoWebView2 = false;
#if DEBUG
        pretendNoWebView2 = Has("--pretend-no-webview2");
#endif
        return new(Has("--check"), Has("--startup"), Has("--unregister"), Has("-RegisterForBGTaskServer"), simulate, pretendNoWebView2);
    }

    /// <summary>From a launch redirected to the running CapPo: its arguments as one string.</summary>
    public static LaunchOptions Parse(string? commandLine) =>
        Parse((commandLine ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
