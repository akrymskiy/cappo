using System.Net.NetworkInformation;
using CapPo.Core.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CapPo.App.Pages;

/// <summary>
/// What Windows' DNS is set to: for each connection, its DNS servers and whether Windows encrypts lookups to them, with
/// or without falling back to plain DNS. Read-only; it shows whether CapPo's premise (encrypted only) holds.
/// </summary>
public sealed partial class DnsPage : Page
{
    private const string Encrypted = "";   // lock
    private const string Fallback = "";    // warning
    private const string Plain = "";       // open lock
    private const string NoNetwork = "";   // Wi-Fi off

    private string _settingsUri = "ms-settings:network-status";
    private int _reading;

    public DnsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Controller.Changed += Refresh;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            App.Controller.Changed -= Refresh;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        };
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private async void Refresh()
    {
        // Reading is quick but touches the registry and the network stack: off the UI thread, newest wins.
        var reading = ++_reading;
        DnsEncryptionReport report;
        try
        {
            report = await Task.Run(DnsEncryptionReader.Read);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Couldn't read the DNS settings");
            return;
        }
        if (reading == _reading)
            Show(report);
    }

    private void Show(DnsEncryptionReport report)
    {
        var main = report.Main;
        var name = main is null ? "" : NetworkName(main);
        ShowVerdict(report, main, name);
        _settingsUri = main switch
        {
            null => "ms-settings:network-status",
            { IsVpn: true } => "ms-settings:network-vpn",
            { IsWireless: true } => "ms-settings:network-wifi",
            _ => "ms-settings:network-ethernet",
        };

        PolicyBar.IsOpen = report.Policy is DohPolicy.Required or DohPolicy.Allowed;
        PolicyBar.Message = report.Policy == DohPolicy.Required
            ? "Your organization requires encrypted DNS on this PC: Windows never falls back to plain DNS."
            : "Your organization allows encrypted DNS on this PC; the settings below decide where it's used.";

        AdaptersPanel.Children.Clear();
        foreach (var adapter in report.Adapters)
        {
            var role = adapter.IsVpn ? " · VPN" : "";
            if (adapter.CarriesInternet && report.Adapters.Count > 1)
                role += " · carries internet traffic";
            AdaptersPanel.Children.Add(new TextBlock
            {
                Style = Resource("SectionHeaderStyle"),
                Text = $"{NetworkName(adapter)}{role}",
            });
            AdaptersPanel.Children.Add(AdapterCard(adapter));
        }

        RulesSection.Visibility = report.Rules.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RulesPanel.Children.Clear();
        foreach (var rule in report.Rules)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"{string.Join(", ", rule.Names)} → {string.Join(", ", rule.Servers)}", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock
            {
                Style = Resource("SecondaryTextStyle"),
                Text = rule.FromPolicy ? "Set by your organization's Group Policy." : "Set on this PC, by an app or a command.",
            });
            RulesPanel.Children.Add(panel);
        }
    }

    private void ShowVerdict(DnsEncryptionReport report, AdapterDnsStatus? main, string name)
    {
        var plain = main?.Servers.Where(static s => s.Encryption == ServerEncryption.None).Select(static s => s.Address.ToString()).ToList() ?? [];
        (VerdictIcon.Glyph, VerdictTitle.Text, VerdictDetail.Text) = report.Verdict switch
        {
            DnsVerdict.Strict when main is { IsWireless: true, ServersFrom: DnsServersFrom.Adapter } => (Encrypted, $"Encrypted only · {name}",
                "On every Wi-Fi network, including ones you haven't joined yet, Windows looks up every name with encrypted DNS and never falls back to plain DNS. A sign-in page can't open by itself with settings like these; CapPo opens it."),
            DnsVerdict.Strict => (Encrypted, $"Encrypted only · {name}",
                "On this network, Windows looks up every name with encrypted DNS and never falls back to plain DNS. A sign-in page can't open by itself with settings like these; CapPo opens it."),
            DnsVerdict.Fallback => (Fallback, $"Encrypted, with fallback · {name}",
                "When an encrypted lookup fails, as it does behind a sign-in page, Windows asks again in plain DNS. Sign-in pages usually open by themselves, but some lookups go out unencrypted. To stop that, choose encrypted only in Windows' settings."),
            DnsVerdict.Plain when main!.IsVpn => (Plain, $"Not encrypted by Windows · {name}",
                $"Lookups to {string.Join(", ", plain)} are plain DNS, but they travel inside the VPN."),
            DnsVerdict.Plain => (Plain, $"Not encrypted · {name}",
                $"Windows looks up names in plain DNS with {string.Join(", ", plain)}. Sign-in pages open by themselves, so CapPo isn't needed here, but your lookups aren't private."),
            DnsVerdict.Prohibited => (Plain, "Encrypted DNS is turned off",
                "Your organization's Group Policy doesn't allow encrypted DNS on this PC."),
            _ => (NoNetwork, "No network", "Connect to a network to see its DNS settings."),
        };
        WindowsSettingsButton.Visibility = report.Verdict == DnsVerdict.Prohibited ? Visibility.Collapsed : Visibility.Visible;
    }

    private Grid AdapterCard(AdapterDnsStatus adapter)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Style = Resource("SecondaryTextStyle"),
            Text = adapter.ServersFrom switch
            {
                DnsServersFrom.Adapter when adapter.IsWireless =>
                    "DNS servers typed in for all Wi-Fi networks (Wi-Fi > Hardware properties), so networks you haven't joined yet use them too.",
                DnsServersFrom.Adapter => "DNS servers typed in for this connection.",
                DnsServersFrom.ThisWifiNetwork =>
                    "DNS servers typed in for this Wi-Fi network only. A Wi-Fi network you join for the first time uses its own DNS, unencrypted. To cover every network, set DNS for all Wi-Fi networks (Wi-Fi > Hardware properties).",
                _ => "DNS servers handed out by the network (DHCP).",
            },
        });
        foreach (var server in adapter.Servers)
            panel.Children.Add(ServerRow(server, adapter.IsVpn));

        var card = new Grid { Style = Resource("CardStyle") };
        card.Children.Add(panel);
        return card;
    }

    private UIElement ServerRow(DnsServerStatus server, bool vpn)
    {
        var (glyph, what) = server.Encryption switch
        {
            ServerEncryption.Encrypted => (Encrypted, "Encrypted only"),
            ServerEncryption.EncryptedWithFallback => (Fallback, "Encrypted, falls back to plain DNS"),
            _ => (Plain, vpn ? "Plain DNS, inside the VPN" : "Plain DNS"),
        };
        var detail = server.Source switch
        {
            TemplateSource.TypedIn => $"{server.Template} (typed in)",
            TemplateSource.Automatic => $"{server.Template} (automatic, from Windows' list of known servers)",
            TemplateSource.KnownServer => $"{server.Template} (from Windows' list of known servers, used everywhere)",
            _ => null,
        };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = $"{server.Address} · {what}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (detail is not null)
            text.Children.Add(new TextBlock { Style = Resource("SecondaryTextStyle"), Text = detail, IsTextSelectionEnabled = true });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>The Wi-Fi network's name where Windows gives one, else the connection's, e.g. "Ethernet".</summary>
    private static string NetworkName(AdapterDnsStatus adapter) =>
        adapter.IsWireless ? Engine.NetworkName(adapter.AdapterId, adapter.AdapterName) : adapter.AdapterName;

    private static Style Resource(string key) => (Style)Application.Current.Resources[key];

    private async void OnOpenWindowsSettings(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(_settingsUri));
}
