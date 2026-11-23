using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>An away peer (PIL1312) is said once, on the admission row, in 'pilot status' and its markdown, not as 'did not answer' on every line.</summary>
public sealed class AwayStatusTests
{
    private const string Away = "LE1 away since 14:03 UTC: skipped until it answers";

    private static SnapshotEvent Snapshot(AdmissionRow admission) => new()
    {
        Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [], Admission = admission,
    };

    [Fact]
    public void AnAwayPeerIsOneLineAfterTheOpusLine()
    {
        var lines = PilotCommand.AdmissionLines(Snapshot(new AdmissionRow { OpusCount = 1, OpusCap = 3, PeersAway = [Away] })).ToArray();
        lines.Length.ShouldBe(2);
        lines[0].ShouldStartWith("Opus 1/3");
        lines[1].ShouldBe(Away);
        string.Join("\n", lines).ShouldNotContain("did not answer");
        string.Join("\n", lines).ShouldNotContain("peers not counted");
    }

    [Fact]
    public void TheMarkdownCarriesTheAwayLineToo()
    {
        var markdown = StatusMarkdown.Build(Snapshot(new AdmissionRow { OpusCount = 1, OpusCap = 3, PeersAway = [Away] }));
        markdown.ShouldContain(Away);
        markdown.ShouldNotContain("did not answer");
    }

    [Fact]
    public void NoAwayPeerGivesNoLine()
    {
        var snapshot = Snapshot(new AdmissionRow { OpusCount = 1, OpusCap = 3 });
        PilotCommand.AdmissionLines(snapshot).Count().ShouldBe(1);
        StatusMarkdown.Build(snapshot).ShouldNotContain("away since");
    }

    [Fact]
    public void AnOlderStoreHandsNullAndItReadsAsEmpty() =>
        new AdmissionRow { PeersAway = null! }.PeersAway.ShouldBeEmpty();
}
