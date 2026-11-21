using CapPo.App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace CapPo.App;

/// <summary>CapPo's window: Settings and About. Closing it leaves CapPo running in the notification area.</summary>
public sealed partial class MainWindow : Window
{
    private const int OpenWidth = 760, OpenHeight = 640;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        var scale = Tray.NativeMethods.GetDpiForWindow(Handle) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(OpenWidth * scale), area.Width);
        var height = Math.Min((int)(OpenHeight * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
    }

    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public void ShowPage(bool about) => NavView.SelectedItem = NavView.MenuItems[about ? 1 : 0];

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string == "About" ? typeof(AboutPage) : typeof(SettingsPage);
        if (RootFrame.CurrentSourcePageType != page)
            RootFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }
}
