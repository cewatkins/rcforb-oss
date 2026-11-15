using RCForb.Client.Directory;

namespace RCForb.Client.Tests;

/// <summary>
/// The directory feed is a plain XML list. These tests pin the two things the guest path
/// depends on: which entries are usable, and which transport an entry selects.
/// </summary>
public class RemoteDirectoryTests
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <RemoteHamsList>
          <Radio>
            <OrbId>5476</OrbId><Online>true</Online>
            <ServerName>Remote SP</ServerName><RadioName>K3</RadioName>
            <Security>public</Security><CanListen>public</CanListen><CanTransmit>1</CanTransmit>
            <Domain>192.0.2.10</Domain><Port>4525</Port><VoipPort>4524</VoipPort>
            <Version>0.7.7970</Version><Country>Poland</Country>
          </Radio>
          <Radio>
            <OrbId>99</OrbId><Online>true</Online>
            <ServerName>UDP Only</ServerName><RadioName>FLEX</RadioName>
            <Security>member</Security><CanListen>member</CanListen><CanTransmit>0</CanTransmit>
            <Domain>udp.example</Domain><Port>4525</Port><VoipPort>0</VoipPort>
          </Radio>
          <Radio>
            <OrbId>1</OrbId><Online>false</Online>
            <ServerName>No Address</ServerName><RadioName>X</RadioName>
            <Port>4525</Port><VoipPort>4524</VoipPort>
          </Radio>
        </RemoteHamsList>
        """;

    [Fact] // SPEC-TRAN-001: audio port above zero selects the dual-TCP path.
    public void Audio_port_selects_the_dual_tcp_transport()
    {
        var directory = RemoteDirectory.Parse(Feed);

        var remote = directory.Entries.Single(e => e.ServerName == "Remote SP");

        Assert.Equal(TransportGeneration.V07, remote.Transport);
        Assert.Equal(4524, remote.AudioPort);
        Assert.Equal(4525, remote.ControlPort);
    }

    [Fact] // SPEC-TRAN-001: zero or absent audio port selects the single-socket path.
    public void Zero_audio_port_selects_the_single_socket_transport()
    {
        var remote = RemoteDirectory.Parse(Feed).Entries.Single(e => e.ServerName == "UDP Only");

        Assert.Equal(TransportGeneration.V09, remote.Transport);
    }

    [Fact]
    public void Entries_without_a_domain_are_not_listings()
    {
        var directory = RemoteDirectory.Parse(Feed);

        Assert.Equal(2, directory.Entries.Count);
        Assert.DoesNotContain(directory.Entries, e => e.ServerName == "No Address");
    }

    [Fact]
    public void Missing_control_port_defaults_to_4525()
    {
        const string xml = """
            <RemoteHamsList><Radio>
              <ServerName>S</ServerName><Domain>example.test</Domain><VoipPort>4524</VoipPort>
            </Radio></RemoteHamsList>
            """;

        var remote = RemoteDirectory.Parse(xml).Entries.Single();
        Assert.Equal(4525, remote.ControlPort);
    }

    [Fact] // Guest joins by index, not by depending on a server's punctuation.
    public void Find_accepts_a_one_based_index_or_a_unique_name()
    {
        var directory = RemoteDirectory.Parse(Feed);

        Assert.Equal("Remote SP", directory.Find("1").ServerName);
        Assert.Equal("Remote SP", directory.Find("k3").ServerName);
        Assert.Equal("UDP Only", directory.Find("2").ServerName);
    }

    [Fact]
    public void An_ambiguous_name_asks_for_the_index_instead_of_guessing()
    {
        var directory = RemoteDirectory.Parse(Feed);

        var error = Assert.Throws<ArgumentException>(() => directory.Find("o"));
        Assert.Contains("index", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Public_listen_is_reported_from_the_feed_policy()
    {
        var directory = RemoteDirectory.Parse(Feed);

        Assert.True(directory.Find("Remote SP").IsPublicListen);
        Assert.False(directory.Find("UDP Only").IsPublicListen);
    }
}
