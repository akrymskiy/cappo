using System.Net;

namespace CapPo.Core.Network;

/// <summary>Group Policy's say on encrypted DNS ("Configure DNS over HTTPS (DoH) name resolution").</summary>
public enum DohPolicy
{
    NotConfigured,
    Prohibited,
    Allowed,
    Required,
}

/// <summary>How Windows sends lookups to one DNS server.</summary>
public enum ServerEncryption
{
    /// <summary>Plain DNS.</summary>
    None,
    /// <summary>Encrypted, and plain DNS is used when the encrypted lookup fails.</summary>
    EncryptedWithFallback,
    /// <summary>Encrypted only.</summary>
    Encrypted,
}

/// <summary>Where a server's encrypted DNS address (its DoH template) comes from.</summary>
public enum TemplateSource
{
    None,
    /// <summary>Typed in for this server on this adapter.</summary>
    TypedIn,
    /// <summary>"Automatic" on this adapter: Windows' list of known servers supplies it.</summary>
    Automatic,
    /// <summary>Not set on the adapter; Windows' list of known servers says to always encrypt this server.</summary>
    KnownServer,
}

/// <summary>A DoH entry as Windows keeps it, per adapter or in its list of known servers.</summary>
/// <param name="Flags">DNS_DOH_SERVER_SETTINGS flags (netioapi.h), which the registry's DohFlags and Flags hold too.</param>
public sealed record DohEntry(IPAddress Address, string? Template, ulong Flags)
{
    public const ulong EnableAuto = 0x1;
    public const ulong Enable = 0x2;
    public const ulong FallbackToUdp = 0x4;
    public const ulong AutoUpgrade = 0x8;
}

public sealed record DnsServerStatus(IPAddress Address, ServerEncryption Encryption, string? Template, TemplateSource Source);

/// <summary>Where an adapter's DNS servers come from.</summary>
public enum DnsServersFrom
{
    /// <summary>Handed out by the network (DHCP).</summary>
    Network,
    /// <summary>
    /// Typed in for the adapter: on Wi-Fi, "for all Wi-Fi networks" (Wi-Fi, Hardware properties), which also covers
    /// networks not joined yet, and overrides any network's own setting.
    /// </summary>
    Adapter,
    /// <summary>Typed in for this Wi-Fi network only, in its properties.</summary>
    ThisWifiNetwork,
}

/// <summary>One adapter's DNS servers and how each is used.</summary>
/// <param name="CarriesInternet">Windows routes internet traffic through this adapter.</param>
/// <param name="IsVpn">A VPN or other tunnel: it carries internet traffic but has no gateway, or is a tunnel.</param>
public sealed record AdapterDnsStatus(
    string AdapterId,
    string AdapterName,
    string Description,
    bool IsWireless,
    DnsServersFrom ServersFrom,
    bool CarriesInternet,
    bool IsVpn,
    IReadOnlyList<DnsServerStatus> Servers);

/// <summary>A Name Resolution Policy Table rule: names that go to particular servers, whatever the adapter says.</summary>
/// <param name="FromPolicy">Set by Group Policy, not by an app or the local configuration.</param>
public sealed record NameRule(IReadOnlyList<string> Names, IReadOnlyList<string> Servers, bool FromPolicy);

/// <summary>How the DNS of the adapter carrying internet traffic stands, from CapPo's point of view.</summary>
public enum DnsVerdict
{
    /// <summary>No adapter with DNS servers.</summary>
    NoNetwork,
    /// <summary>Every server is encrypted with no fallback: Windows can't reach a portal by name, and CapPo is needed.</summary>
    Strict,
    /// <summary>Encrypted, but plain DNS is used when that fails: sign-in pages usually work without CapPo.</summary>
    Fallback,
    /// <summary>Some or all servers get plain DNS.</summary>
    Plain,
    /// <summary>Group Policy turns encrypted DNS off.</summary>
    Prohibited,
}

public sealed record DnsEncryptionReport(DohPolicy Policy, IReadOnlyList<AdapterDnsStatus> Adapters, IReadOnlyList<NameRule> Rules)
{
    /// <summary>The adapter internet traffic goes through (a VPN if one is up), else the first one listed.</summary>
    public AdapterDnsStatus? Main => Adapters.FirstOrDefault(static a => a.CarriesInternet) ?? Adapters.FirstOrDefault();

    public DnsVerdict Verdict => Judge(Policy, Main);

    public static DnsVerdict Judge(DohPolicy policy, AdapterDnsStatus? adapter)
    {
        if (adapter is null || adapter.Servers.Count == 0)
            return DnsVerdict.NoNetwork;
        if (policy == DohPolicy.Prohibited)
            return DnsVerdict.Prohibited;
        if (adapter.Servers.Any(static s => s.Encryption == ServerEncryption.None))
            return DnsVerdict.Plain;
        return adapter.Servers.All(static s => s.Encryption == ServerEncryption.Encrypted) ? DnsVerdict.Strict : DnsVerdict.Fallback;
    }
}

/// <summary>Works out how Windows treats one DNS server. Pure, so it's tested without touching Windows.</summary>
public static class DnsEncryption
{
    /// <param name="onAdapter">The adapter's own DoH entry for this server, if it has one.</param>
    /// <param name="known">Windows' list of known DoH servers' entry for it, if listed.</param>
    public static DnsServerStatus Classify(IPAddress server, DohEntry? onAdapter, DohEntry? known, DohPolicy policy)
    {
        if (policy == DohPolicy.Prohibited)
            return new DnsServerStatus(server, ServerEncryption.None, null, TemplateSource.None);

        // An entry on the adapter decides, even when it says off.
        if (onAdapter is not null)
        {
            if ((onAdapter.Flags & DohEntry.Enable) != 0 && !string.IsNullOrWhiteSpace(onAdapter.Template))
                return Encrypted(onAdapter.Template, TemplateSource.TypedIn, onAdapter.Flags);
            if ((onAdapter.Flags & DohEntry.EnableAuto) != 0 && !string.IsNullOrWhiteSpace(known?.Template))
                return Encrypted(known.Template, TemplateSource.Automatic, onAdapter.Flags);
            return new DnsServerStatus(server, ServerEncryption.None, null, TemplateSource.None);
        }

        if (known is not null && (known.Flags & DohEntry.AutoUpgrade) != 0 && !string.IsNullOrWhiteSpace(known.Template))
            return Encrypted(known.Template, TemplateSource.KnownServer, known.Flags);
        return new DnsServerStatus(server, ServerEncryption.None, null, TemplateSource.None);

        DnsServerStatus Encrypted(string template, TemplateSource source, ulong flags) =>
            // Requiring DoH by policy leaves no fallback.
            new(server, (flags & DohEntry.FallbackToUdp) != 0 && policy != DohPolicy.Required
                ? ServerEncryption.EncryptedWithFallback
                : ServerEncryption.Encrypted, template, source);
    }

    /// <summary>The policy's DoHPolicy value: 1 prohibits, 2 allows, 3 requires.</summary>
    public static DohPolicy ParsePolicy(object? value) => value switch
    {
        1 => DohPolicy.Prohibited,
        2 => DohPolicy.Allowed,
        3 => DohPolicy.Required,
        _ => DohPolicy.NotConfigured,
    };
}
