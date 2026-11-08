using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The worker and both reviewers are told a branch takes its base by a merge, that merge commits on it are never a finding,
/// and that a pushed branch is never rebased or force pushed: a review that called them a defect sent TIA1874's worker to
/// rewrite its branch, which then waited on the owner for a force push (DE1, 2026-10-30).
/// </summary>
public class BranchHistoryBriefTests
{
    private static readonly FlightRow Flight = new()
    {
        Code = "PIL283",
        Repo = "pilot",
        RepoPath = "/r/pilot",
        Issue = 283,
        Branch = "flight/pil283",
        Worktree = "/w/PIL283",
        Base = "origin/main",
        State = FlightState.Flying,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static readonly LegRow Leg = new() { Flight = "PIL283", Name = "one", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned };

    private static readonly Briefs.PlaneState Plane = new("", "");

    private static readonly PrReviewRow Review = new()
    {
        Repo = "acme/widget", Number = 7, Url = "https://github.com/acme/widget/pull/7", Base = "main", Head = "4f2c9a01d6e8",
        ClonePath = "/r", Worktree = "/w/review", Reviewer = "du1-review-widget-7", State = PrReviewState.Reviewing, StartedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void TheWorkerAndBothReviewersAreToldMergeCommitsAreFineAndNothingIsRebasedOrForcePushed()
    {
        string[] briefs =
        [
            Briefs.For(Flight, Leg, [Leg]),
            Briefs.Review(Flight, [Leg], Plane, "pilot"),
            Briefs.ReviewPr(Review, Plane, null, "pilot"),
        ];

        foreach (var brief in briefs)
        {
            brief.ShouldContain("Merge commits on a working branch are");
            brief.ShouldContain("fine and never a finding");
            brief.ShouldContain("Never rebase a");
            brief.ShouldContain("never push with --force or --force-with-lease, and never ask for either.");
        }
    }

    /// <summary>
    /// #896: a chained, quoted or variable call of pilot misses the allow rules and goes to the auto-mode classifier, which denied the
    /// watchtower's card answers. Every session that runs pilot is told to run it alone, as written, and to keep notes through the node.
    /// </summary>
    [Fact]
    public void EverySessionThatRunsPilotIsToldToRunItAloneAsWrittenAndNeverToWriteUnderClaude()
    {
        string[] briefs =
        [
            Briefs.For(Flight, Leg, [Leg]),
            Briefs.Review(Flight, [Leg], Plane, "pilot"),
            Briefs.ReviewPr(Review, Plane, null, "pilot"),
            Briefs.WatchtowerRules("wt", "pilot"),
        ];

        foreach (var brief in briefs)
        {
            brief.ShouldContain(Briefs.CommandShapeRule);
            brief.ShouldContain("no `cd … &&` before it");
            brief.ShouldContain("`git -C <folder> …`");
            brief.ShouldContain("never as a\n  file under ~/.claude");
        }
    }

    /// <summary>#896: the watchtower answers and dismisses a card through pilot's MCP tools, with no shell for a rule to miss.</summary>
    [Fact]
    public void PilotsMcpServerOffersAnswerAndDismiss()
    {
        var tools = Pilot.Commands.McpServer.Tools(Pilot.Commands.PilotCommand.Build(Path.GetTempPath(), TextWriter.Null, TextWriter.Null, "mcp", agent: true));

        tools.Keys.ShouldContain("answer");
        tools.Keys.ShouldContain("dismiss");
    }
}
