using CapPo.App.RunModes;
using CapPo.App.Tray;
using CapPo.Core.Detection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CapPo.App;

/// <summary>
/// Runs CapPo in one of two ways, on the UI thread:
/// <list type="bullet">
/// <item>Tray app (Always running, or opened from Start): an icon in the notification area; checks on network
/// changes and every few minutes; opens the sign-in window when a portal shows up.</item>
/// <item>One-shot (<c>--check</c>, from On demand mode's trigger, once a portal was found): the sign-in window only,
/// exiting once online or when the window is closed. Opening CapPo from Start meanwhile turns it into the tray app.</item>
/// </list>
/// </summary>
internal sealed class AppController
{
    private readonly Engine _engine;
    private readonly PortalMonitor _monitor;
    private TrayController? _tray;
    private PortalWindow? _portal;
    private MainWindow? _main;
    private bool _oneShot;
    private bool _reportNextCheck;
    private bool _exiting;
    // The network's name, looked up once per network.
    private (string? Fingerprint, string Name) _networkName = (null, "the network");

    public AppController(Engine engine)
    {
        _engine = engine;
        _monitor = new PortalMonitor(engine.Detector, Log.Factory.CreateLogger<PortalMonitor>())
        {
            RecheckInterval = Settings.RecheckInterval,
        };
        _monitor.Checked += r => App.Dispatcher.TryEnqueue(() => OnChecked(r));
        _monitor.StateChanged += r => App.Dispatcher.TryEnqueue(() => OnStateChanged(r));
        _monitor.SignedIn += r => App.Dispatcher.TryEnqueue(() => OnSignedIn(r));
    }

    public AppSettings Settings { get; } = AppSettings.Load();

    public LicenseInfo License { get; private set; } = LicenseInfo.Full;

    public DetectionResult? Last => _monitor.Last;

    /// <summary>When Windows refused to register the chosen mode; shown in Settings.</summary>
    public string? RunModeProblem { get; private set; }

    /// <summary>Something the window shows changed. Raised on the UI thread.</summary>
    public event Action? Changed;

    public string NetworkName => _networkName.Name;

    public string StatusTitle => !License.IsUsable
        ? "Trial ended"
        : Last?.State switch
        {
            null => "Checking…",
            NetworkState.Online => $"Online · {NetworkName}",
            NetworkState.Portal => $"Sign-in needed · {NetworkName}",
            NetworkState.Unreachable => $"No internet · {NetworkName}",
            _ => "Not connected",
        };

    public string StatusDetail
    {
        get
        {
            var simulated = _engine.IsSimulated ? " (Simulated portal.)" : "";
            if (Last is not { } last)
                return "Looking at the network." + simulated;
            var dns = last.Network is { } n ? string.Join(", ", n.DnsServers) : "";
            var every = $"CapPo checks again when the network changes, and every {Settings.RecheckMinutes} minutes.";
            return last.State switch
            {
                NetworkState.Online => $"Checked at {last.CheckedAt:t}. {every}",
                NetworkState.Portal => $"The network wants you to sign in. Its sign-in page loads with its own DNS ({dns}), in CapPo's window only.",
                NetworkState.Unreachable => $"Connected, but nothing answers: {last.Detail}",
                _ => "Connect to a network to sign in to it.",
            } + simulated;
        }
    }

    public async void Start(LaunchOptions options, DetectionResult? first)
    {
        License = await StoreLicense.GetAsync();
#if STORE_BUILD
        StoreLicense.Changed += () => App.Dispatcher.TryEnqueue(() => _ = RefreshLicenseAsync());
#endif
        if (options.Check && first is not null)
        {
            _oneShot = true;
            if (!License.IsUsable)
            {
                Exit();
                return;
            }
            _monitor.Start(watchNetwork: false, first);
            OnChecked(first);
            OnStateChanged(first);
        }
        else
        {
            EnterTrayMode(showWindow: !options.Startup);
            _monitor.Start(watchNetwork: true);
#if !DEBUG
            // Keeps Windows' registrations in step with the settings (and with this exe's path). Not for debug runs,
            // which would point them at a build folder.
            await ApplyRunModeAsync();
#endif
        }
    }

    /// <summary>Another launch of CapPo was handed to this one.</summary>
    public void OnRedirected(AppActivationArguments activation)
    {
        var arguments = (activation.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments;
        var options = LaunchOptions.Parse(arguments);
        Log.Info($"Launch handed over ({activation.Kind}): {arguments}");
        if (options.Check)
            CheckNow(reportResult: false);
        else if (activation.Kind != ExtendedActivationKind.StartupTask && !options.Startup)
            EnterTrayMode(showWindow: true);
    }

    public void ShowMainWindow(bool about = false)
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) =>
            {
                _main = null;
                ExitIfDone();
            };
        }
        _main.ShowPage(about);
        _main.Activate();
    }

    public void ShowSignIn()
    {
        if (Last is not { State: NetworkState.Portal } last || !License.IsUsable)
            return;
        if (_portal is null)
        {
            _portal = new PortalWindow(_engine.Detector.Proxy.Port, last);
            _portal.Closed += (_, _) =>
            {
                _portal = null;
                ExitIfDone();
            };
        }
        _portal.Present();
    }

    public void CheckNow(bool reportResult)
    {
        _reportNextCheck = reportResult;
        _monitor.CheckNow();
    }

    public async Task SetModeAsync(RunMode mode)
    {
        Settings.Mode = mode;
        Settings.Save();
        await ApplyRunModeAsync();
    }

    public async Task SetLaunchAtLoginAsync(bool on)
    {
        Settings.LaunchAtLogin = on;
        Settings.Save();
        await ApplyRunModeAsync();
    }

    public async Task SetRecheckAsync(int minutes)
    {
        Settings.RecheckMinutes = minutes;
        Settings.Save();
        _monitor.RecheckInterval = Settings.RecheckInterval;
        if (Settings.Mode == RunMode.OnDemand)
            await ApplyRunModeAsync();
        Changed?.Invoke();
    }

    public async Task BuyAsync()
    {
#if STORE_BUILD
        if (_main is null)
            ShowMainWindow();
        if (await StoreLicense.PurchaseAsync(_main!.Handle))
            await RefreshLicenseAsync();
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>Ends CapPo: tray icon, windows, proxy and all.</summary>
    public async void Exit()
    {
        if (_exiting)
            return;
        _exiting = true;
        Log.Info("Exiting");
        _tray?.Dispose();
        _tray = null;
        _portal?.Close();
        _main?.Close();
        await _monitor.DisposeAsync();
        await _engine.DisposeAsync();
        Application.Current.Exit();
    }

    private void EnterTrayMode(bool showWindow)
    {
        _oneShot = false;
        if (_tray is null)
        {
            try
            {
                _tray = new TrayController(this);
            }
            catch (InvalidOperationException ex)
            {
                // Without a tray icon, CapPo is a window that exits when closed.
                Log.Error(ex, "No tray icon");
            }
        }
        _monitor.WatchNetwork();
        if (showWindow || _tray is null)
            ShowMainWindow();
    }

    // A one-shot run ends with its windows; so does a tray app that lost its icon.
    private void ExitIfDone()
    {
        if ((_oneShot || _tray is null) && _portal is null && _main is null)
            Exit();
    }

    private async Task ApplyRunModeAsync()
    {
        RunModeProblem = await RunModeSetup.ApplyAsync(Settings);
        Changed?.Invoke();
    }

#if STORE_BUILD
    private async Task RefreshLicenseAsync()
    {
        License = await StoreLicense.GetAsync();
        _tray?.Update();
        Changed?.Invoke();
        if (License.IsUsable && Last?.State == NetworkState.Portal)
            ShowSignIn();
    }
#endif

    private void OnChecked(DetectionResult result)
    {
        if (result.Network?.Fingerprint != _networkName.Fingerprint)
            _networkName = (result.Network?.Fingerprint, Engine.NetworkName(result));
        _tray?.Update();
        if (_reportNextCheck)
        {
            _reportNextCheck = false;
            _tray?.Notify(StatusTitle, StatusDetail);
        }
        Changed?.Invoke();
    }

    private void OnStateChanged(DetectionResult result)
    {
        switch (result.State)
        {
            case NetworkState.Portal:
                // Opens it once per capture: closed by the user, it stays closed until the state changes.
                ShowSignIn();
                _portal?.Update(result);
                break;
            case NetworkState.Online:
                _portal?.ShowConnectedAndClose();
                break;
            default:
                _portal?.Update(result);
                break;
        }
    }

    private void OnSignedIn(DetectionResult result)
    {
        if (!_oneShot)
            _tray?.Notify("You're online", $"Signed in to {NetworkName}.");
    }
}

