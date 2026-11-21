using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CapPo.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        ShowLogo();
        ActualThemeChanged += (_, _) => ShowLogo();
        VersionText.Text = $"Version {AppInfo.Version} · {(AppInfo.IsStoreBuild ? "Microsoft Store" : "GitHub")} build";
        LicenseText.Text = $"CapPo is free software, under the {AppInfo.LicenseName}. Copyright © 2026 Alex Krymskiy.";
        Link(GitHubLink, AppInfo.GitHubUrl);
        Link(SponsorLink, AppInfo.SponsorUrl);
        Link(CoffeeLink, AppInfo.BuyMeACoffeeUrl);
        Link(PrivacyLink, AppInfo.PrivacyUrl);
        Link(LicenseLink, AppInfo.LicenseUrl);
    }

    // Black on light, white on dark, from the big PNG. The .ico is outlined for backgrounds of either colour, and an
    // Image takes its first, smallest frame and stretches it.
    private void ShowLogo()
    {
        var variant = ActualTheme == ElementTheme.Dark ? "unplated" : "lightunplated";
        Logo.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Square44x44Logo.targetsize-256_altform-{variant}.png"));
    }

    // Links not filled in yet (see AppInfo) aren't shown.
    private static void Link(HyperlinkButton button, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            button.NavigateUri = uri;
        else
            button.Visibility = Visibility.Collapsed;
    }

    private async void OnOpenDataFolder(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchFolderPathAsync(AppInfo.DataDirectory);
}
