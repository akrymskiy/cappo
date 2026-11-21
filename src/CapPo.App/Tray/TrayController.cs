using CapPo.Core.Detection;
using Microsoft.Win32;
using static CapPo.App.Tray.NativeMethods;

namespace CapPo.App.Tray;

/// <summary>
/// The notification area icon: drawn for the network's state, with a tooltip and a menu, as in Firmly. Clicking it
/// opens the sign-in window when a portal is waiting, and CapPo's window otherwise.
/// </summary>
internal sealed unsafe class TrayController : IDisposable
{
    private const int SignInId = 1, CheckId = 2, SettingsId = 3, AboutId = 4, ExitId = 5, BuyId = 6;

    private readonly AppController _app;
    private readonly TrayIcon _icon = new();
    private nint _iconHandle;
    private (TrayGlyph Glyph, int Size, bool Light)? _drawn;

    public TrayController(AppController app)
    {
        _app = app;
        // The icon's events arrive inside its window procedure; act once that has returned (as Firmly does).
        _icon.Selected += (_, _) => App.Dispatcher.TryEnqueue(OnSelected);
        _icon.MenuRequested += OnMenuRequested;
        _icon.ThemeChanged += Update;
        Update();
    }

    public void Dispose()
    {
        _icon.Dispose();
        DestroyIcon(_iconHandle);
    }

    public void Notify(string title, string text, bool warning = false) => _icon.Notify(title, text, warning);

    public void Update()
    {
        var drawn = (Glyph(), GetSystemMetricsForDpi(SM_CXSMICON, _icon.Dpi), TaskbarIsLight());
        nint previous = 0;
        if (_drawn != drawn)
        {
            previous = _iconHandle;
            _iconHandle = CreateIcon(TrayIconRenderer.Render(drawn.Item1, drawn.Item2, drawn.Item3), drawn.Item2);
            _drawn = drawn;
        }
        _icon.Update(_iconHandle, $"{AppInfo.Name}\n{_app.StatusTitle}");
        if (previous != 0)
            DestroyIcon(previous);
    }

    private TrayGlyph Glyph() => !_app.License.IsUsable
        ? TrayGlyph.Idle
        : _app.Last?.State switch
        {
            NetworkState.Online => TrayGlyph.Online,
            NetworkState.Portal => TrayGlyph.Portal,
            NetworkState.Unreachable => TrayGlyph.Offline,
            NetworkState.NoNetwork => TrayGlyph.Disconnected,
            _ => TrayGlyph.Idle,
        };

    private void OnSelected()
    {
        if (_app.Last?.State == NetworkState.Portal && _app.License.IsUsable)
            _app.ShowSignIn();
        else
            _app.ShowMainWindow();
    }

    private void OnMenuRequested(int x, int y)
    {
        List<TrayMenuItem> items = [new(_app.StatusTitle)];
        if (!_app.License.IsUsable)
            items.Add(new("Buy CapPo…", BuyId));
        else if (_app.Last?.State == NetworkState.Portal)
            items.Add(new("Sign in…", SignInId));
        items.AddRange(
        [
            new("Check now", CheckId),
            TrayMenuItem.Separator,
            new("Settings", SettingsId),
            new("About CapPo", AboutId),
            TrayMenuItem.Separator,
            new("Exit", ExitId),
        ]);

        var choice = _icon.ShowMenu(items, x, y);
        App.Dispatcher.TryEnqueue(() =>
        {
            switch (choice)
            {
                case SignInId:
                    _app.ShowSignIn();
                    break;
                case CheckId:
                    _app.CheckNow(reportResult: true);
                    break;
                case SettingsId:
                    _app.ShowMainWindow();
                    break;
                case AboutId:
                    _app.ShowMainWindow(about: true);
                    break;
                case BuyId:
                    _app.ShowMainWindow();
                    break;
                case ExitId:
                    _app.Exit();
                    break;
            }
        });
    }

    // The notification area follows the system theme (not the apps' one).
    private static bool TaskbarIsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
    }

    // As in Firmly: a 32-bit colour bitmap whose alpha does the masking, plus an all-zero mask.
    private static nint CreateIcon(uint[] pixels, int size)
    {
        nint color;
        fixed (uint* bits = pixels)
            color = CreateBitmap(size, size, 1, 32, bits);
        var maskBits = new byte[(size + 15) / 16 * 2 * size];
        nint mask;
        fixed (byte* bits = maskBits)
            mask = CreateBitmap(size, size, 1, 1, bits);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(&info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }
}
