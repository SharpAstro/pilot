using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The ticker's brief (PIL804): it names the steps, the worker's commit range, the check verb and both finish outcomes.</summary>
public class TickerBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "PIL804",
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 804,
        Branch = "flight/pil804",
        Worktree = "/w/PIL804",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly LegRow Leg = new()
    {
        Flight = "PIL804",
        Name = "start-head",
        Order = 1,
        Goal = "Record the plane's HEAD at start.",
        Checklist = [new CheckItem { Text = "Add StartHead to LegRow." }, new CheckItem { Text = "Set it at the worker start." }],
        DoneWhen = "LegRow has a StartHead.",
        Check = "dotnet test Pilot.Node.Tests -c Release",
        State = LegState.Flying,
        StartHead = "abc123def456",
    };

    [Fact]
    public void TheBriefNamesTheStepsTheCommitRangeTheCheckVerbAndBothOutcomes()
    {
        var brief = Briefs.Tick(Flight, Leg, [Leg], "pilot", "DU1");

        brief.ShouldContain("ticker of leg `start-head`");
        brief.ShouldContain("1. [ ] Add StartHead to LegRow.");
        brief.ShouldContain("2. [ ] Set it at the worker start.");
        brief.ShouldContain("LegRow has a StartHead.");
        brief.ShouldContain("`dotnet test Pilot.Node.Tests -c Release`");
        brief.ShouldContain("abc123def456..HEAD");
        brief.ShouldContain("git log --stat abc123def456..HEAD");
        brief.ShouldContain("`pilot check <step number>`");
        brief.ShouldContain("pilot leg finish done \"ticked by the node's ticking pass: <which commits covered which steps>\"");
        brief.ShouldContain("pilot leg finish failed \"<the steps not covered, or the check's failure>\"");
        brief.ShouldContain("Change and commit nothing");
        brief.ShouldContain("no subagents");
        brief.ShouldContain("about thirty tool calls");
        brief.ShouldContain("is named `du1-<flight or card>-<what>`, with the machine's code `du1` written out");
    }

    [Fact]
    public void TheBriefSaysNotToEndTheTurnBeforeLegFinishHasAnswered()
    {
        var brief = Briefs.Tick(Flight, Leg, [Leg], "pilot");

        brief.ShouldContain("End your turn only after `leg finish` has answered");
        brief.ShouldContain("never before you have run it");
        brief.ShouldContain("its check is rerunning");
        brief.ShouldContain("the node finishes the leg when the rerun ends");
    }

    [Fact]
    public void TheBriefQuotesTheCommitsWhenTheNodeHasThem()
    {
        var brief = Briefs.Tick(Flight, Leg, [Leg], "pilot", commits: "9f8e7d6c Add StartHead\n");

        brief.ShouldContain("9f8e7d6c Add StartHead");
        brief.ShouldNotContain("git log --stat");
    }

    [Fact]
    public void ABriefWithoutARecordedStartHeadTellsTheTickerToFindTheCommits()
    {
        var brief = Briefs.Tick(Flight, Leg with { StartHead = null }, [Leg], "pilot");

        brief.ShouldContain("was not recorded");
        brief.ShouldContain("git log --oneline");
    }

    [Fact]
    public void TheBriefHasNoEmDash()
    {
        Briefs.Tick(Flight, Leg, [Leg], "pilot", "DU1").ShouldNotContain("—");
    }

    [Fact]
    public void TheWorkerBriefTellsAWorkerToTickRightAfterEachCommit()
    {
        var brief = Briefs.For(Flight, Leg, [Leg], machine: "DU1");

        brief.ShouldContain("Tick each step as soon as its work is committed, before you start the next");
        brief.ShouldContain("Never leave ticking to the end");
        brief.ShouldContain("every step is ticked and you have run `pilot leg finish done");
    }
}
