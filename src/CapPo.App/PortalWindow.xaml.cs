using CapPo.App.Tray;
using CapPo.Core.Detection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace CapPo.App;

/// <summary>
/// Shows the captive portal's sign-in page in WebView2, through CapPo's proxy. CapPo keeps checking meanwhile
/// (<see cref="PortalMonitor"/>); once online, the window says so and closes.
/// </summary>
public sealed partial class PortalWindow : Window
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(1.5);

    private readonly int _proxyPort;
    private readonly WindowIcon _icon;
    private Uri _signInUrl;
    private bool _closingForGood;
    private bool _clicked;

    internal PortalWindow(int proxyPort, DetectionResult result)
    {
        _proxyPort = proxyPort;
        _signInUrl = result.SignInUrl ?? ConnectivityProbe.ProbeUrl;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _icon = new WindowIcon(this, AppTitleBar);
        Size(1000, 780);
        Update(result);
        Activated += OnActivated;
        Closed += (_, _) => Web.Close();
        _ = StartAsync();
    }

    /// <summary>
    /// Shows the window above all others, and keeps it there until the user clicks it. Windows won't let a background
    /// app take the foreground, so Activate alone leaves it behind whatever the user is in; and the browser Windows
    /// opens for the portal a moment later would cover it again. Being on top without focus also leaves the user's
    /// typing where it was.
    /// </summary>
    internal void Present()
    {
        _clicked = false;
        Activate();
        BringIntoView();
        Log.Info($"Sign-in window shown: {Placement()}");
        if (NativeMethods.GetForegroundWindow() != Handle)
            Flash();
        _ = CheckShownAsync();
    }

    // Once, opened at login, the window was shown yet not to be seen until opened again from the tray. This records
    // where it was, and brings it into view again.
    private async Task CheckShownAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (_closingForGood || _clicked)
            return;
        Log.Info($"Sign-in window 3 s later: {Placement()}");
        if (!InView())
        {
            BringIntoView();
            Log.Info($"Sign-in window shown again: {Placement()}");
        }
    }

    // Restored, on top, and on the virtual desktop the user is looking at.
    private void BringIntoView()
    {
        if (VirtualDesktop.IsOnCurrentDesktop(Handle) == false)
            VirtualDesktop.MoveTo(Handle, NativeMethods.GetForegroundWindow());
        AppWindow.Show(activateWindow: false);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Minimized)
                presenter.Restore();
            presenter.IsAlwaysOnTop = true;
        }
    }

    private bool InView() =>
        NativeMethods.IsWindowVisible(Handle) && !NativeMethods.IsIconic(Handle) && Cloaked() == 0
        && (NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0
        && VirtualDesktop.IsOnCurrentDesktop(Handle) != false;

    // Non-zero when DWM hides the window though it's shown: 1 by the app, 2 by the shell (another virtual desktop), 4 by its owner.
    private unsafe int Cloaked()
    {
        int cloaked;
        return NativeMethods.DwmGetWindowAttribute(Handle, NativeMethods.DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 ? cloaked : 0;
    }

    // Clicked: the user has found it, so it goes back to being an ordinary window.
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.PointerActivated)
            return;
        _clicked = true;
        if (AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true } presenter)
            presenter.IsAlwaysOnTop = false;
    }

    private nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    // The taskbar button flashes until the window comes to the foreground.
    private unsafe void Flash()
    {
        var info = new NativeMethods.FLASHWINFO
        {
            cbSize = (uint)sizeof(NativeMethods.FLASHWINFO),
            hwnd = Handle,
            dwFlags = NativeMethods.FLASHW_TRAY | NativeMethods.FLASHW_TIMERNOFG,
        };
        NativeMethods.FlashWindowEx(&info);
    }

    private unsafe string Placement()
    {
        var hwnd = Handle;
        var states = new List<string> { NativeMethods.IsWindowVisible(hwnd) ? "visible" : "hidden" };
        if (NativeMethods.IsIconic(hwnd))
            states.Add("minimized");
        if ((NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0)
            states.Add("on top");
        if (Cloaked() is var cloaked and not 0)
            states.Add($"cloaked ({cloaked})");
        states.Add(VirtualDesktop.IsOnCurrentDesktop(hwnd) switch
        {
            true => "on this desktop",
            false => "on another desktop",
            null => "desktop unknown",
        });
        var at = AppWindow.Position;
        var size = AppWindow.Size;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        var foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        var title = stackalloc char[100];
        var length = NativeMethods.GetWindowText(foreground, title, 100);
        string process;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch (ArgumentException)
        {
            process = "?";
        }
        return $"{string.Join(", ", states)}, at {at.X},{at.Y} {size.Width}×{size.Height} on screen {area.X},{area.Y} "
            + $"{area.Width}×{area.Height}; foreground {(foreground == hwnd ? "this window" : $"{process} \"{new string(title, 0, length)}\"")}";
    }

    /// <summary>Whether CapPo closed it (signed in), rather than the user.</summary>
    public bool ClosedByCapPo => _closingForGood;

    /// <summary>Shows what the latest check found.</summary>
    internal void Update(DetectionResult result)
    {
        var name = Engine.NetworkName(result);
        Title = AppTitleBar.Title = $"Sign in to {name}";
        _icon.Show(signIn: result.State == NetworkState.Portal);
        StatusText.Text = result.State switch
        {
            NetworkState.Portal => $"Waiting for you to sign in to {name}. CapPo checks every few seconds.",
            NetworkState.Online => "You're online.",
            NetworkState.NoNetwork => "Not connected to a network.",
            _ => $"Connected to {name}, but the internet isn't reachable yet.",
        };
    }

    /// <summary>Opens a sign-in page again, e.g. when the portal's API names a new one.</summary>
    internal void Navigate(Uri url)
    {
        _signInUrl = url;
        if (Web.CoreWebView2 is { } core)
            core.Navigate(url.AbsoluteUri);
    }

    /// <summary>Says "You're online", then closes.</summary>
    internal async void ShowConnectedAndClose()
    {
        if (_closingForGood)
            return;
        _closingForGood = true;
        ConnectedPanel.Visibility = Visibility.Visible;
        StatusText.Text = "You're online.";
        await Task.Delay(CloseDelay);
        Close();
    }

    private async Task StartAsync()
    {
        if (!WebViewEnvironment.IsInstalled)
        {
            Log.Info("No WebView2 Runtime: the sign-in page can't be shown");
            ShowError("This window needs the Microsoft Edge WebView2 Runtime, which isn't installed, so it can't show the "
                + $"sign-in page. Once you're online, install it from {WebViewEnvironment.DownloadUrl}");
            return;
        }
        try
        {
            await Web.EnsureCoreWebView2Async(await WebViewEnvironment.GetAsync(_proxyPort));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 didn't start");
            ShowError($"The sign-in page couldn't be shown: {ex.Message}");
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = true;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        // Portals that open a new window for their terms or sign-in: keep it here, behind the same proxy.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.SourceChanged += (_, _) => ShowAddress(core.Source);
        core.HistoryChanged += (_, _) => BackButton.IsEnabled = core.CanGoBack;
        core.NavigationCompleted += (_, e) =>
        {
            var host = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) ? uri.Host : "?";
            Log.Info(e.IsSuccess ? $"Sign-in window loaded {host} (HTTP {e.HttpStatusCode})" : $"Sign-in window: {host} didn't load: {e.WebErrorStatus}");
        };
        core.Navigate(_signInUrl.AbsoluteUri);
    }

    private void ShowAddress(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            AddressText.Text = source;
            LockIcon.Visibility = Visibility.Collapsed;
            return;
        }
        AddressText.Text = uri.Host;
        LockIcon.Visibility = uri.Scheme == Uri.UriSchemeHttps ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void OnBack(object sender, RoutedEventArgs e) => Web.CoreWebView2?.GoBack();

    private void OnReload(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    // The probe page: portals redirect it to their sign-in page, which brings back a page closed by mistake.
    private void OnStartOver(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Navigate(_signInUrl.AbsoluteUri);

    private void Size(int width, int height)
    {
        var scale = NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min((int)(width * scale), area.Width);
        var h = Math.Min((int)(height * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h));
    }
}
