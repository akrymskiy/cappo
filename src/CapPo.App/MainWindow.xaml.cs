using CapPo.App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace CapPo.App;

/// <summary>CapPo's window: Settings, DNS and About. Closing it leaves CapPo running in the notification area.</summary>
public sealed partial class MainWindow : Window
{
    private const int OpenWidth = 760, OpenHeight = 640;

    private readonly WindowIcon _icon;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _icon = new WindowIcon(this, AppTitleBar);
        _icon.Show(signIn: false);

        var scale = Tray.NativeMethods.GetDpiForWindow(Handle) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(OpenWidth * scale), area.Width);
        var height = Math.Min((int)(OpenHeight * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));

        // Back from installing the WebView2 Runtime, say: Settings drops its warning without a restart.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && RootFrame.Content is SettingsPage settings)
                settings.RefreshWebView();
        };
    }

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>The feather in the icon while a portal waits for a sign-in, as in the tray.</summary>
    public void ShowSignInIcon(bool signIn) => _icon.Show(signIn);

    public void ShowPage(bool about) =>
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == (about ? "About" : "Settings"));

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "About" => typeof(AboutPage),
            "Dns" => typeof(DnsPage),
            _ => typeof(SettingsPage),
        };
        if (RootFrame.CurrentSourcePageType != page)
            RootFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
