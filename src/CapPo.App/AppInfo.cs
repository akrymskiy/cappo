using System.Reflection;
using System.Runtime.InteropServices;

namespace CapPo.App;

/// <summary>What CapPo is, which build this is, and where it keeps its files.</summary>
internal static partial class AppInfo
{
    public const string Name = "CapPo";

    // Links on the About page. An empty one isn't shown.
    public const string GitHubUrl = "https://github.com/akrymskiy/cappo";
    public const string SponsorUrl = "https://github.com/sponsors/akrymskiy";
    /// <summary>Placeholder: fill in the Buy Me a Coffee page (as in .github/FUNDING.yml) to show its link.</summary>
    public const string BuyMeACoffeeUrl = "";
    public const string PrivacyUrl = GitHubUrl + "/blob/main/PRIVACY.md";
    public const string LicenseUrl = GitHubUrl + "/blob/main/LICENSE";
    public const string LicenseName = "GNU General Public License v3.0";

#if STORE_BUILD
    public const bool IsStoreBuild = true;
#else
    public const bool IsStoreBuild = false;
#endif

    /// <summary>e.g. "0.1.0".</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "?";

    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CapPo.exe");

    /// <summary>Settings, the log and the sign-in window's browser profile.</summary>
    /// <remarks>
    /// The Store build keeps them in its package's own folder (removed with the app); the GitHub build in
    /// %LocalAppData%\CapPo.
    /// </remarks>
    public static string DataDirectory { get; } = FindDataDirectory();

    private static string FindDataDirectory()
    {
        string path;
#if STORE_BUILD
        path = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
#else
        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
#endif
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Whether this process has package identity (the Store build, installed).</summary>
    public static bool IsPackaged
    {
        get
        {
            uint length = 0;
            // APPMODEL_ERROR_NO_PACKAGE (15700) when unpackaged; ERROR_INSUFFICIENT_BUFFER (122) when packaged.
            return GetCurrentPackageFullName(ref length, 0) != 15700;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}
