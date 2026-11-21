using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CapPo.App;

/// <summary>
/// A window's icon (taskbar, Alt+Tab) and its title bar's: CapPo's hat, with the tray icon's feather while a portal
/// waits for a sign-in.
/// </summary>
internal sealed class WindowIcon(Window window, TitleBar titleBar)
{
    private bool? _signIn;

    public void Show(bool signIn)
    {
        if (_signIn == signIn)
            return;
        _signIn = signIn;
        var file = signIn ? "AppIconSignIn.ico" : "AppIcon.ico";
        window.AppWindow.SetIcon($"Assets/{file}");
        titleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri($"ms-appx:///Assets/{file}")) };
    }
}
