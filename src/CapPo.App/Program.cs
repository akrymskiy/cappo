using CapPo.App.RunModes;
using CapPo.Core.Detection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CapPo.App;

/// <summary>
/// Starts CapPo: as the tray app, as a one-shot <c>--check</c> (which needs no UI at all unless there's a portal),
/// or as the Store build's background task server. Hands launches to the copy already running.
/// </summary>
public static class Program
{
    // A one-shot check retries a little while the network comes up, then gives up.
    private const int CheckAttempts = 3;
    private static readonly TimeSpan CheckRetryDelay = TimeSpan.FromSeconds(1);

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var options = LaunchOptions.Parse(args);

#if STORE_BUILD
        if (options.BackgroundTaskServer)
            return Background.ComServer.Run();
#endif
        if (options.Unregister)
        {
            Task.Run(RunModeSetup.RemoveAllAsync).GetAwaiter().GetResult();
            return 0;
        }

        // One CapPo per user. A second launch (e.g. --check while the tray app runs) goes to the first.
        var main = AppInstance.FindOrRegisterForKey("main");
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (!main.IsCurrent)
        {
            // From another thread: blocking this STA thread on it can deadlock (as in Firmly).
            Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        if (activation.Kind == ExtendedActivationKind.StartupTask)
            options = options with { Startup = true };
        // A Run key or startup task left over from Always running mode.
        if (options.Startup && AppSettings.Load().Mode == RunMode.OnDemand)
            return 0;

        var engine = Engine.Create(options);
        DetectionResult? first = null;
        if (options.Check)
        {
            first = Task.Run(() => CheckOnceAsync(engine)).GetAwaiter().GetResult();
            if (first.State != NetworkState.Portal)
            {
                Task.Run(() => engine.DisposeAsync().AsTask()).Wait();
                return 0;
            }
        }

        main.Activated += (_, e) => App.Dispatcher?.TryEnqueue(() => App.Controller?.OnRedirected(e));
        Application.Start(_1 =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(options, engine, first);
        });
        return 0;
    }

    private static async Task<DetectionResult> CheckOnceAsync(Engine engine)
    {
        DetectionResult result = null!;
        for (var attempt = 1; attempt <= CheckAttempts; attempt++)
        {
            result = await engine.Detector.CheckAsync();
            // Online or portal: settled. No network or no answer yet: DHCP or the portal may still be coming up.
            if (result.State is NetworkState.Online or NetworkState.Portal)
                break;
            if (attempt < CheckAttempts)
                await Task.Delay(CheckRetryDelay);
        }
        Log.Info($"One-shot check: {result.State}");
        return result;
    }
}
