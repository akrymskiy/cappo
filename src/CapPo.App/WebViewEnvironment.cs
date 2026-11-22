using Microsoft.Web.WebView2.Core;

namespace CapPo.App;

/// <summary>
/// The sign-in window's browser: WebView2 sent through CapPo's proxy, so the portal's names resolve with the network's
/// DHCP DNS, inside this window only. Made once per process: the proxy's port doesn't change while CapPo runs.
/// </summary>
internal static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? s_environment;
    private static int s_port;

    public static Task<CoreWebView2Environment> GetAsync(int proxyPort)
    {
        if (s_environment is null || s_port != proxyPort)
        {
            s_port = proxyPort;
            s_environment = CreateAsync(proxyPort);
        }
        return s_environment;
    }

    /// <summary>Microsoft's installer for the WebView2 Runtime (the Evergreen bootstrapper, a small download).</summary>
    public const string DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>Debug builds' --pretend-no-webview2: act as if the runtime were missing, to try what CapPo says then.</summary>
    public static bool PretendMissing { get; set; }

    /// <summary>
    /// Whether the WebView2 Runtime is installed. It comes with Windows 11 and is on most Windows 10 PCs. Checked again
    /// on every call, so installing it while CapPo runs is noticed.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            if (PretendMissing)
                return false;
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString() is { Length: > 0 };
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static async Task<CoreWebView2Environment> CreateAsync(int proxyPort)
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            // Everything through the proxy, even localhost: the proxy refuses to connect to this PC, so a portal's
            // page can't reach local services. No QUIC, which would go around an HTTP proxy.
            AdditionalBrowserArguments = $"--proxy-server=http://127.0.0.1:{proxyPort} --proxy-bypass-list=<-loopback> --disable-quic",
        };
        // Cookies are kept, so portals that remember a device can; nothing else is stored, and nothing leaves the PC.
        var profile = Path.Combine(AppInfo.DataDirectory, "WebView2");
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, options);
        Log.Info($"WebView2 {environment.BrowserVersionString}, proxy 127.0.0.1:{proxyPort}");
        return environment;
    }
}
