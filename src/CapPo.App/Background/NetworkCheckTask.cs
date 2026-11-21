#if STORE_BUILD
using System.Runtime.InteropServices;
using CapPo.Core.Detection;
using Windows.ApplicationModel.Background;

namespace CapPo.App.Background;

/// <summary>
/// The Store build's On demand trigger: Windows runs this on network changes and on a timer, in a short-lived CapPo
/// process started through COM (<see cref="ComServer"/>). It only detects: background tasks get about 30 seconds and
/// may be held back under Battery Saver. When there's a portal, it starts CapPo with --check for the sign-in window.
/// </summary>
[ComVisible(true)]
[Guid(Clsid)]
public sealed partial class NetworkCheckTask : IBackgroundTask
{
    /// <summary>As com:Class in Package.appxmanifest.</summary>
    public const string Clsid = "7C3E1A52-4B8D-4F2E-9A61-0D5B8E7C2F14";

    private const string NamePrefix = "CapPo ";
    private const string NetworkTaskName = NamePrefix + "network change";
    private const string TimerTaskName = NamePrefix + "re-check";

    public static bool IsRegistered =>
        BackgroundTaskRegistration.AllTasks.Values.Any(static t => t.Name == NetworkTaskName);

    public static async Task RegisterAsync(TimeSpan recheck)
    {
        var access = await BackgroundExecutionManager.RequestAccessAsync();
        Log.Info($"Background access: {access}");
        if (access is BackgroundAccessStatus.DeniedBySystemPolicy or BackgroundAccessStatus.DeniedByUser)
            throw new InvalidOperationException(
                "Windows doesn't let CapPo run in the background. Allow it in Settings > Apps > Installed apps > CapPo > Advanced options.");

        Unregister();
        Register(NetworkTaskName, new SystemTrigger(SystemTriggerType.NetworkStateChange, oneShot: false));
        // Time triggers run every 15 minutes at the most.
        Register(TimerTaskName, new TimeTrigger((uint)Math.Max(15, recheck.TotalMinutes), oneShot: false));
        Log.Info($"Registered background tasks (re-check every {recheck.TotalMinutes:0} min)");
    }

    public static void Unregister()
    {
        foreach (var task in BackgroundTaskRegistration.AllTasks.Values.Where(static t => t.Name.StartsWith(NamePrefix, StringComparison.Ordinal)))
        {
            task.Unregister(cancelTask: true);
            Log.Info($"Removed background task \"{task.Name}\"");
        }
    }

    private static void Register(string name, IBackgroundTrigger trigger)
    {
        var builder = new Microsoft.Windows.ApplicationModel.Background.BackgroundTaskBuilder { Name = name };
        builder.SetTrigger(trigger);
        builder.SetTaskEntryPointClsid(typeof(NetworkCheckTask).GUID);
        builder.Register();
    }

    public void Run(IBackgroundTaskInstance taskInstance)
    {
        var deferral = taskInstance.GetDeferral();
        // Not disposed: Canceled can still come after Run has returned.
        var cancel = new CancellationTokenSource();
        taskInstance.Canceled += (_, reason) =>
        {
            Log.Info($"Background check cancelled: {reason}");
            cancel.Cancel();
        };
        try
        {
            CheckAsync(taskInstance.Task?.Name, cancel.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Background check failed");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            deferral.Complete();
            ComServer.SignalDone();
        }
    }

    private static async Task CheckAsync(string? trigger, CancellationToken cancellationToken)
    {
        var settings = AppSettings.Load();
        // Left over from On demand mode, or the trial has ended: nothing to do.
        if (settings.Mode != RunMode.OnDemand || !(await StoreLicense.GetAsync()).IsUsable)
            return;

        await using var engine = Engine.Create(LaunchOptions.Parse([]));
        var result = await engine.Detector.CheckAsync(cancellationToken);
        Log.Info($"Background check ({trigger}): {result.State}");
        if (result.State == NetworkState.Portal)
            AppLauncher.Launch("--check");
    }
}
#endif
