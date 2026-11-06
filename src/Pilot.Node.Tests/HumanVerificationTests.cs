using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Human verification (#751): the label that marks a flight, the third report heading that never blocks, the briefs that carry the rule
/// for a marked flight only, the row's flag read at a start, and the label made in a repo.
/// </summary>
public sealed class HumanVerificationTests : LegCommanderTestsBase
{
    private const string Report = """
        ## Blocking
        - A real bug
          with detail
        ## Not blocking
        - A nit
        ## Human verification
        - Pan the canvas with a touchpad
          Do: open the Flights page and two-finger drag the canvas.
          Done when: it pans smoothly and never jumps back.

        - Plug in the camera
          Do: connect the QHY camera and start an exposure.
          Done when: the frame counter moves.
        """;

    private static FlightRow Row(bool human) => new()
    {
        Code = "TIA1",
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1,
        Branch = "flight/tia1",
        Worktree = "/w/TIA1",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
        HumanVerification = human,
    };

    private static LegRow OneLeg() => new() { Flight = "TIA1", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

    [Fact]
    public void ItemsUnderTheHumanVerificationHeadingAreTheirOwnListWithTheirDetailLinesKept()
    {
        var findings = ReviewFindings.Read(Report);

        findings.Structured.ShouldBeTrue();
        findings.Blocking.ShouldBe(["- A real bug"]);
        findings.NotBlocking.ShouldBe(["- A nit"]);
        findings.HumanVerification.Count.ShouldBe(2);
        findings.HumanVerification[0].ShouldBe(
            "- Pan the canvas with a touchpad\nDo: open the Flights page and two-finger drag the canvas.\nDone when: it pans smoothly and never jumps back.");
        findings.HumanVerification[1].ShouldStartWith("- Plug in the camera\nDo: connect the QHY camera");
    }

    [Theory]
    [InlineData("## Human verification\n- Check the window")]
    [InlineData("**Human verification**\n- Check the window")]
    [InlineData("Human verification:\n- Check the window")]
    public void TheHeadingIsReadInTheUsualSpellings(string report) =>
        ReviewFindings.Read(report).HumanVerification.ShouldBe(["- Check the window"]);

    [Fact]
    public void HumanVerificationNeverBlocksAndAReportWithOnlyItIsStructured()
    {
        var findings = ReviewFindings.Read("## Human verification\n- Check the window\n  Do: open it.\n  Done when: it opens.");

        findings.Structured.ShouldBeTrue();
        findings.Blocking.ShouldBeEmpty();
        findings.HasBlocking.ShouldBeFalse();
    }

    [Fact]
    public void ABlockingFindingStillBlocksBesideHumanVerification() =>
        ReviewFindings.Read(Report).HasBlocking.ShouldBeTrue();

    [Fact]
    public void APlainReportHasNoHumanVerification()
    {
        var findings = ReviewFindings.Read("- It is broken");

        findings.HumanVerification.ShouldBeEmpty();
        findings.HasBlocking.ShouldBeTrue();
    }

    [Fact]
    public void TheReviewerBriefCarriesTheRuleOnlyForAMarkedFlight()
    {
        var plane = new Briefs.PlaneState("", "");

        var marked = Briefs.Review(Row(true), [OneLeg()], plane, "pilot");
        var plain = Briefs.Review(Row(false), [OneLeg()], plane, "pilot");

        marked.ShouldContain("## Human verification");
        marked.ShouldContain("`Do:`");
        marked.ShouldContain("`Done when:`");
        marked.ShouldContain("files each item as an issue");
        plain.ShouldNotContain("Human verification");
        plain.ShouldNotContain("human verification");
    }

    [Fact]
    public void TheWorkerBriefCarriesTheRuleOnlyForAMarkedFlight()
    {
        var marked = Briefs.For(Row(true), OneLeg(), [OneLeg()]);
        var plain = Briefs.For(Row(false), OneLeg(), [OneLeg()]);

        marked.ShouldContain("## Human verification");
        marked.ShouldContain("`Do:`");
        plain.ShouldNotContain("Human verification");
        plain.ShouldNotContain("human verification");
    }

    [Fact]
    public void TheLabelIsOneTheNodeReadsFromPeopleAndMakesWithADescription()
    {
        PilotMarks.HumanVerification.ShouldBe("pilot: human verification");
        PilotMarks.ReadFromPeople.ShouldContain(PilotMarks.HumanVerification);
        PilotMarks.LabelStyle(PilotMarks.HumanVerification).Description.ShouldBe("a person must check this flight's live behaviour; pilot files those checks as issues");
    }

    [Fact]
    public async Task RepoSetupMakesTheLabel()
    {
        var github = new FakeGitHub();
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);

        await new RepoSetup(state).EnsureAsync(new GitHubTracker(github), "/r/tianwen", "TIA1", Ct);

        github.LabelsEnsured.Select(e => e.Label).ShouldContain(PilotMarks.HumanVerification);
    }

    [Fact]
    public async Task AStartSetsTheRowsFlagFromTheIssuesLabelsAndClearsItWhenTheLabelIsGone()
    {
        _state.PutFlight(Row(false) with { Repo = "tianwen", RepoPath = Repo });
        _github.IssueLabels[1] = [PilotMarks.HumanVerification];

        // The base class's commander has no tracker; this one reads the fake GitHub, as the node does.
        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), TimeProvider.System, _workers, _github);
        var legs = new LegCommander(_state, _workers, _checks, _github, git, new Planes(git), flights,
            new NodeOptions(Path.Combine(_root, "node2.sock"), Path.Combine(_root, "data2"), MachinePrefix: "DU1"), TimeProvider.System);
        var start = new CommandRequest { Verb = Verbs.StartReview, Target = "flight/TIA1", Who = "tests" };

        // The start itself is refused (the flight has no PR); what counts is that the labels were read first.
        await legs.RunAsync(start, Ct);
        _state.Flights().Single().HumanVerification.ShouldBeTrue();

        _github.IssueLabels[1].Remove(PilotMarks.HumanVerification);
        await legs.RunAsync(start, Ct);
        _state.Flights().Single().HumanVerification.ShouldBeFalse();
    }
}
