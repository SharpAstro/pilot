using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL1111: a hold is the flight's own. With two live flights on one issue, ground and unground of one leave the other as it was and
/// touch no label; a label put on from GitHub holds both and says so; its removal releases the label's part only.
/// </summary>
public sealed class SiblingHoldTests : ReviewTestsBase
{
    private async Task<(FlightRow A, FlightRow B)> Two()
    {
        var a = await NewFlight("1", "parser");
        var made = await _flights.RunAsync(new CommandRequest
        {
            Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Who = "controller", Why = "a second approach",
            Args = ["1", "Flight 1b", Verbs.AnywayFlag],
        }, Ct);
        made.Ok.ShouldBeTrue(made.Error);
        return (a, F(made.Made!));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task GroundOfOneFlightLeavesItsSiblingFlyingAndPutsNoLabelOnTheIssue()
    {
        var (a, b) = await Two();

        var result = await Run(Verbs.Ground, Verbs.FlightTarget(a.Code), "me", "waiting for a decision".Split(' '));

        result.Error.ShouldBeNull();
        result.Made.ShouldNotBeNull().ShouldContain("this flight's only");
        result.Made.ShouldContain(b.Code);
        F(a.Code).OnHold.ShouldNotBeNull().Own.ShouldBe(true);
        F(b.Code).OnHold.ShouldBeNull();
        (_github.IssueLabels.GetValueOrDefault(a.Issue)?.Keys ?? []).ShouldNotContain(PilotMarks.Hold);
        _github.IssueComments.ShouldNotContain(c => c.Body.StartsWith(PilotMarks.HoldCommentStart, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UngroundOfOneFlightLeavesItsSiblingAsItWasAndTakesNoLabelOff()
    {
        var (a, b) = await Two();
        (await Run(Verbs.Ground, Verbs.FlightTarget(a.Code), "me", "one".Split(' '))).Error.ShouldBeNull();
        (await Run(Verbs.Ground, Verbs.FlightTarget(b.Code), "me", "two".Split(' '))).Error.ShouldBeNull();
        _github.SetIssueLabels(a.Issue, PilotMarks.Hold);

        (await Run(Verbs.Unground, Verbs.FlightTarget(a.Code), "me", "go")).Error.ShouldBeNull();

        F(a.Code).OnHold.ShouldBeNull();
        F(b.Code).OnHold.ShouldNotBeNull().Reason.ShouldBe("two");
        _github.IssueLabels[a.Issue].Keys.ShouldContain(PilotMarks.Hold);
    }

    [Fact]
    public void TheStatusWordsSayWhoHoldsIt()
    {
        var row = new FlightRow { Code = "TIA1", Repo = "tianwen", RepoPath = "/r", Issue = 7, Branch = "b", Worktree = "/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UnixEpoch };
        FlightOnHold Hold(bool? own, bool? label) => new() { Reason = "why", Since = DateTimeOffset.UnixEpoch, By = "me", Own = own, FromLabel = label };

        FlightWaiting.OnHoldText(row with { OnHold = Hold(null, true) }).ShouldBe("on hold by the label pilot: hold on #7 (me: why)");
        // With no hold comment the label's default reason is left out: the label already says it.
        FlightWaiting.OnHoldText(row with { OnHold = Hold(null, true) with { By = "the owner", Reason = FlightOnHold.LabelReason } }).ShouldBe("on hold by the label pilot: hold on #7");
        FlightWaiting.OnHoldText(row with { OnHold = Hold(true, true) }).ShouldNotBeNull().ShouldContain("the label pilot: hold on #7 and by me");
        FlightWaiting.OnHoldText(row with { OnHold = Hold(true, null) }).ShouldNotBeNull().ShouldContain("this flight only");
        FlightWaiting.OnHoldText(row with { OnHold = Hold(null, null) }).ShouldBe("on hold by me: why");
    }

    /// <summary>The repo sweep with two live flights on one issue.</summary>
    public sealed class Sweep : IDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2s-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        private readonly FakeGitHub _github = new();
        private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);

        public void Dispose() => TestDirs.DeleteTree(_root);

        private string Clone()
        {
            var git = Directory.CreateDirectory(Path.Combine(_root, "tianwen", ".git")).FullName;
            File.WriteAllText(Path.Combine(git, "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/tianwen.git\n");
            File.WriteAllText(Path.Combine(_root, "tianwen", RepoCatalog.MarkerFile), "# PILOT.md\n");
            return Path.Combine(_root, "tianwen");
        }

        private void Fly(string repo, string code, FlightOnHold? hold = null) =>
            _state.PutFlight(new FlightRow
            {
                Code = code, Repo = "tianwen", RepoPath = repo, Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}",
                Worktree = Path.Combine(repo, ".claude", "worktrees", code.ToLowerInvariant()), Base = "origin/main", State = FlightState.Flying,
                CreatedAt = DateTimeOffset.UnixEpoch, OnHold = hold,
            });

        private Task<RepoStatus[]> Look() =>
            new RepoWatch(_state, new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), new Planes(new GitCli()), _github, TimeProvider.System)
            {
                Machine = "DE1",
                Parallelism = 1,
            }.RefreshAsync(Ct);

        private FlightRow F(string code) => _state.Flights().Single(f => f.Code == code);

        private static FlightOnHold Own() => new() { Reason = "mine", Since = DateTimeOffset.UnixEpoch, By = "me", Own = true };

        [Fact]
        [Trait("Category", "Slow")]
        public async Task TheLabelHoldsBothFlightsAndBothSayTheLabelHoldsThem()
        {
            var repo = Clone();
            _github.Issues[1] = "labelled";
            _github.SetIssueLabels(1, PilotMarks.Hold, "pilot node: DE1");
            Fly(repo, "TIA1");
            Fly(repo, "TIA9");

            await Look();

            foreach (var code in new[] { "TIA1", "TIA9" })
            {
                F(code).OnHold.ShouldNotBeNull().FromLabel.ShouldBe(true);
                FlightWaiting.OnHoldText(F(code)).ShouldBe("on hold by the label pilot: hold on #1");
            }
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task TheLabelsRemovalReleasesBothFlights()
        {
            var repo = Clone();
            _github.Issues[1] = "labelled";
            _github.SetIssueLabels(1, PilotMarks.Hold, "pilot node: DE1");
            Fly(repo, "TIA1");
            Fly(repo, "TIA9");
            await Look();

            _github.SetIssueLabels(1, "pilot node: DE1");
            await Look();

            F("TIA1").OnHold.ShouldBeNull();
            F("TIA9").OnHold.ShouldBeNull();
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task TheLabelsRemovalLeavesTheFlightWhoseOwnRowAlsoHoldsItHeldAndNeverRelabels()
        {
            var repo = Clone();
            _github.Issues[1] = "labelled";
            _github.SetIssueLabels(1, PilotMarks.Hold, "pilot node: DE1");
            Fly(repo, "TIA1", Own());
            Fly(repo, "TIA9");
            await Look();
            F("TIA1").OnHold.ShouldNotBeNull().FromLabel.ShouldBe(true);
            F("TIA1").OnHold!.Own.ShouldBe(true);

            _github.SetIssueLabels(1, "pilot node: DE1");
            await Look();

            F("TIA9").OnHold.ShouldBeNull();
            var kept = F("TIA1").OnHold.ShouldNotBeNull();
            kept.Own.ShouldBe(true);
            kept.FromLabel.ShouldBeNull();
            FlightWaiting.OnHoldText(F("TIA1")).ShouldNotBeNull().ShouldContain("this flight only");

            await Look();
            F("TIA1").OnHold.ShouldNotBeNull();
            _github.IssueLabels[1].Keys.ShouldNotContain(PilotMarks.Hold);
        }
    }
}
