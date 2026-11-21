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
    private Uri _signInUrl;
    private bool _closingForGood;

    internal PortalWindow(int proxyPort, DetectionResult result)
    {
        _proxyPort = proxyPort;
        _signInUrl = result.SignInUrl ?? ConnectivityProbe.ProbeUrl;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
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
        Activate();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = true;
    }

    // Clicked: the user has found it, so it goes back to being an ordinary window.
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.PointerActivated
            && AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true } presenter)
            presenter.IsAlwaysOnTop = false;
    }

    /// <summary>Whether CapPo closed it (signed in), rather than the user.</summary>
    public bool ClosedByCapPo => _closingForGood;

    /// <summary>Shows what the latest check found.</summary>
    internal void Update(DetectionResult result)
    {
        var name = Engine.NetworkName(result);
        Title = AppTitleBar.Title = $"Sign in to {name}";
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
        if (WebViewEnvironment.RuntimeVersion is null)
        {
            ShowError("The sign-in window needs the Microsoft Edge WebView2 Runtime, which isn't installed. Get it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
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
        var scale = Tray.NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min((int)(width * scale), area.Width);
        var h = Math.Min((int)(height * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - w) / 2), area.Y + ((area.Height - h) / 2), w, h));
    }
}
