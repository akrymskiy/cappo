using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CapPo.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version} · {(AppInfo.IsStoreBuild ? "Microsoft Store" : "GitHub")} build";
        LicenseText.Text = $"CapPo is free software, under the {AppInfo.LicenseName}. Copyright © 2026 Alex Krymskiy.";
        Link(GitHubLink, AppInfo.GitHubUrl);
        Link(SponsorLink, AppInfo.SponsorUrl);
        Link(CoffeeLink, AppInfo.BuyMeACoffeeUrl);
        Link(PrivacyLink, AppInfo.PrivacyUrl);
        Link(LicenseLink, AppInfo.LicenseUrl);
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
