using System.Net;
using CapPo.Core.Network;

namespace CapPo.Core.Tests;

public class DnsEncryptionTests
{
    private static readonly IPAddress Server = IPAddress.Parse("45.90.28.0");
    private const string Template = "https://dns.example/dns-query";

    private static DohEntry Entry(ulong flags, string? template = Template) => new(Server, template, flags);

    [Fact]
    public void TypedInTemplateWithoutFallbackIsEncryptedOnly()
    {
        var status = DnsEncryption.Classify(Server, Entry(DohEntry.Enable), null, DohPolicy.NotConfigured);
        Assert.Equal((ServerEncryption.Encrypted, TemplateSource.TypedIn, Template), (status.Encryption, status.Source, status.Template));
    }

    [Fact]
    public void FallbackFlagAllowsPlainDns()
    {
        var status = DnsEncryption.Classify(Server, Entry(DohEntry.Enable | DohEntry.FallbackToUdp), null, DohPolicy.NotConfigured);
        Assert.Equal(ServerEncryption.EncryptedWithFallback, status.Encryption);
    }

    [Fact]
    public void RequiringDohByPolicyLeavesNoFallback()
    {
        var status = DnsEncryption.Classify(Server, Entry(DohEntry.Enable | DohEntry.FallbackToUdp), null, DohPolicy.Required);
        Assert.Equal(ServerEncryption.Encrypted, status.Encryption);
    }

    [Fact]
    public void AutomaticTemplateComesFromTheKnownServers()
    {
        var status = DnsEncryption.Classify(Server, Entry(DohEntry.EnableAuto, null), Entry(0), DohPolicy.NotConfigured);
        Assert.Equal((ServerEncryption.Encrypted, TemplateSource.Automatic, Template), (status.Encryption, status.Source, status.Template));
    }

    [Fact]
    public void AutomaticWithoutAKnownTemplateIsPlain() =>
        Assert.Equal(ServerEncryption.None, DnsEncryption.Classify(Server, Entry(DohEntry.EnableAuto, null), null, DohPolicy.NotConfigured).Encryption);

    [Fact]
    public void KnownServerMarkedAutoUpgradeIsEncryptedWithoutAnAdapterEntry()
    {
        var status = DnsEncryption.Classify(Server, null, Entry(DohEntry.AutoUpgrade), DohPolicy.NotConfigured);
        Assert.Equal((ServerEncryption.Encrypted, TemplateSource.KnownServer), (status.Encryption, status.Source));
    }

    [Fact]
    public void KnownServerWithoutAutoUpgradeIsPlain() =>
        Assert.Equal(ServerEncryption.None, DnsEncryption.Classify(Server, null, Entry(0), DohPolicy.NotConfigured).Encryption);

    [Fact]
    public void AdapterEntrySetToOffWinsOverAutoUpgrade() =>
        Assert.Equal(ServerEncryption.None, DnsEncryption.Classify(Server, Entry(0), Entry(DohEntry.AutoUpgrade), DohPolicy.NotConfigured).Encryption);

    [Fact]
    public void ProhibitedByPolicyIsPlain() =>
        Assert.Equal(ServerEncryption.None, DnsEncryption.Classify(Server, Entry(DohEntry.Enable), null, DohPolicy.Prohibited).Encryption);

    [Theory]
    [InlineData(1, DohPolicy.Prohibited)]
    [InlineData(2, DohPolicy.Allowed)]
    [InlineData(3, DohPolicy.Required)]
    [InlineData(0, DohPolicy.NotConfigured)]
    [InlineData(null, DohPolicy.NotConfigured)]
    public void PolicyValues(int? value, DohPolicy expected) => Assert.Equal(expected, DnsEncryption.ParsePolicy(value));

    [Theory]
    [InlineData(new[] { ServerEncryption.Encrypted, ServerEncryption.Encrypted }, DnsVerdict.Strict)]
    [InlineData(new[] { ServerEncryption.Encrypted, ServerEncryption.EncryptedWithFallback }, DnsVerdict.Fallback)]
    [InlineData(new[] { ServerEncryption.Encrypted, ServerEncryption.None }, DnsVerdict.Plain)]
    [InlineData(new ServerEncryption[0], DnsVerdict.NoNetwork)]
    public void VerdictFollowsTheWeakestServer(ServerEncryption[] servers, DnsVerdict expected)
    {
        var adapter = new AdapterDnsStatus("{id}", "Wi-Fi", "", true, true, true, false,
            servers.Select(e => new DnsServerStatus(Server, e, null, TemplateSource.None)).ToList());
        Assert.Equal(expected, DnsEncryptionReport.Judge(DohPolicy.NotConfigured, adapter));
    }
}
