using CapPo.App.RunModes;
using CapPo.Core.Detection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CapPo.App.Pages;

public sealed partial class SettingsPage : Page
{
    private static AppController Controller => App.Controller;

    // Set while filling in the controls, so that doesn't count as the user changing them.
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var minutes in AppSettings.RecheckChoices)
            RecheckBox.Items.Add(minutes == 60 ? "Every hour" : $"Every {minutes} minutes");
        Loaded += OnLoaded;
        Unloaded += (_, _) => Controller.Changed -= Refresh;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Controller.Changed += Refresh;
        var settings = Controller.Settings;
        ModeButtons.SelectedIndex = (int)settings.Mode;
        var recheck = Array.IndexOf(AppSettings.RecheckChoices, settings.RecheckMinutes);
        RecheckBox.SelectedIndex = recheck >= 0 ? recheck : Array.IndexOf(AppSettings.RecheckChoices, 20);
        Refresh();
        await RefreshLaunchAtLoginAsync();
        _loading = false;
    }

    private void Refresh()
    {
        var last = Controller.Last;
        StatusTitle.Text = Controller.StatusTitle;
        StatusDetail.Text = Controller.IsChecking ? "Checking…" : Controller.StatusDetail;
        CheckNowButton.IsEnabled = !Controller.IsChecking;
        StatusIcon.Glyph = last?.State switch
        {
            NetworkState.Online => "",
            NetworkState.Portal => "",
            NetworkState.Unreachable => "",
            _ => "",
        };
        SignInButton.Visibility = last?.State == NetworkState.Portal && Controller.License.IsUsable ? Visibility.Visible : Visibility.Collapsed;
        RefreshWebView();

        var license = Controller.License;
        LicenseBar.IsOpen = AppInfo.IsStoreBuild && (license.IsTrial || !license.IsUsable);
        if (!license.IsUsable)
        {
            LicenseBar.Severity = InfoBarSeverity.Warning;
            LicenseBar.Title = "Your trial has ended";
            LicenseBar.Message = "CapPo still shows the network's status, but no longer opens sign-in pages.";
        }
        else if (license.TrialRemaining is { } remaining)
        {
            LicenseBar.Severity = InfoBarSeverity.Informational;
            LicenseBar.Title = "Trial";
            LicenseBar.Message = remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays} days left." : "Ends today.";
        }

        ProblemBar.IsOpen = Controller.RunModeProblem is not null;
        ProblemBar.Message = Controller.RunModeProblem ?? "";

        var minutes = Controller.Settings.OnDemandMinutes;
        var floor = Controller.Settings.RecheckMinutes < minutes ? " (Windows won't do it more often)" : "";
        OnDemandDescription.Text = AppInfo.IsStoreBuild
            ? $"Windows starts CapPo when the network changes and every {minutes} minutes{floor}, just to check. It opens only when there's a sign-in page."
            : $"Windows starts CapPo when a network connects, when you sign in, and every {minutes} minutes{floor}, just to check. It opens only when there's a sign-in page.";
        var onDemand = Controller.Settings.Mode == RunMode.OnDemand;
        ModeBar.IsOpen = onDemand;
        ModeBar.Message = "CapPo stays open until you exit it. After that, it starts by itself when it's needed.";
        LaunchAtLoginSwitch.IsEnabled = !onDemand;
    }

    /// <summary>Shows or hides the WebView2 warning; the main window calls it again when it comes back to the front.</summary>
    internal void RefreshWebView() => WebViewBar.IsOpen = !WebViewEnvironment.IsInstalled;

    private async void OnGetWebView(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(WebViewEnvironment.DownloadUrl));

    private async Task RefreshLaunchAtLoginAsync()
    {
        var onDemand = Controller.Settings.Mode == RunMode.OnDemand;
        var state = await LaunchAtLogin.GetAsync();
        var wasLoading = _loading;
        _loading = true;
        LaunchAtLoginSwitch.IsOn = state == LaunchAtLoginState.On;
        _loading = wasLoading;
        LaunchAtLoginDetail.Text = onDemand
            ? "Not needed in On demand mode: Windows starts CapPo when a network connects."
            : state == LaunchAtLoginState.DisabledByUser
                ? "Turned off in Task Manager (Startup apps). Turn it back on there."
                : "Start in the notification area when you sign in to Windows.";
    }

    private void OnSignIn(object sender, RoutedEventArgs e) => Controller.ShowSignIn();

    private void OnCheckNow(object sender, RoutedEventArgs e) => Controller.CheckNow(reportResult: false);

    private async void OnBuy(object sender, RoutedEventArgs e) => await Controller.BuyAsync();

    private async void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModeButtons.SelectedIndex < 0)
            return;
        await Controller.SetModeAsync((RunMode)ModeButtons.SelectedIndex);
        await RefreshLaunchAtLoginAsync();
    }

    private async void OnLaunchAtLoginToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        await Controller.SetLaunchAtLoginAsync(LaunchAtLoginSwitch.IsOn);
        await RefreshLaunchAtLoginAsync();
    }

    private async void OnRecheckChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RecheckBox.SelectedIndex < 0)
            return;
        await Controller.SetRecheckAsync(AppSettings.RecheckChoices[RecheckBox.SelectedIndex]);
    }
}
