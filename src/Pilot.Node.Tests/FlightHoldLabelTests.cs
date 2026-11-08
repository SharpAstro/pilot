using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// PIL771: the owner's hold lives on the flight's issue as <c>pilot: hold</c>, so every node sees it from GitHub. Hold and unhold write
/// and remove the label (with a comment), a repo sweep holds or releases a local flight from it, a failed write keeps the hold and is
/// retried, and a code this node has no flight for is labelled by its prefix and digits.
/// </summary>
public sealed class FlightHoldLabelTests : ReviewTestsBase
{
    private const string Why = "waiting for the release";

    private Task<CommandResult> Hold(string code, string who = "me", string why = Why) =>
        Run(Verbs.Ground, Verbs.FlightTarget(code), who, why.Split(' '));

    [Fact]
    [Trait("Category", "Slow")]
    public async Task HoldLabelsTheIssueNeverThePrAndCommentsAndUnholdTakesTheLabelOff()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        var pr = F(flight.Code).PullRequest!.Value;

        (await Hold(flight.Code)).Error.ShouldBeNull();

        F(flight.Code).OnHold!.Labelled.ShouldBe(true);
        _github.IssueLabels[flight.Issue].ShouldContain(PilotMarks.Hold);
        _github.IssueLabels.GetValueOrDefault(pr, []).ShouldNotContain(PilotMarks.Hold);
        var comment = _github.IssueComments.Single(c => c.Number == flight.Issue && c.Body.StartsWith(PilotMarks.HoldCommentStart, StringComparison.Ordinal)).Body;
        comment.ShouldContain("me");
        comment.ShouldContain("DU1");
        comment.ShouldContain(Why);

        (await Run(Verbs.Unground, Verbs.FlightTarget(flight.Code), "me", "go")).Error.ShouldBeNull();
        F(flight.Code).OnHold.ShouldBeNull();
        _github.IssueLabels[flight.Issue].ShouldNotContain(PilotMarks.Hold);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AHoldWhoseLabelWriteFailedKeepsHoldingSaysSoOnItsCardAndUnholdNeedsNoLabel()
    {
        var flight = await NewFlight("2", "parser");
        _github.IssueLabelFails = "no permission";

        var result = await Hold(flight.Code);

        result.Error.ShouldBeNull();
        result.Made.ShouldNotBeNull().ShouldContain("no permission");
        F(flight.Code).OnHold!.Labelled.ShouldBe(false);
        _state.Cards(openOnly: false).Single(c => c.Kind == LegCommander.OnHoldKind && c.Title.Contains("is on hold")).Detail.ShouldNotBeNull()
            .ShouldContain("sweep tries the label again");
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "node")).Error.ShouldNotBeNull().ShouldContain("on hold");

        (await Run(Verbs.Unground, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldBeNull();
        F(flight.Code).OnHold.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task UnholdThatCannotTakeTheLabelOffRefusesAndTheFlightStaysHeld()
    {
        var flight = await NewFlight("3", "parser");
        (await Hold(flight.Code)).Error.ShouldBeNull();
        _github.IssueLabelFails = "no permission";

        (await Run(Verbs.Unground, Verbs.FlightTarget(flight.Code), "me")).Error.ShouldNotBeNull().ShouldContain("stays on hold");
        F(flight.Code).OnHold.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task HoldOnACodeThisNodeFliesNoFlightOfResolvesRepoFromPrefixAndIssueFromDigits()
    {
        var flight = await NewFlight("4", "parser");
        var prefix = FlightCodes.PrefixOf(flight.Code);
        var other = $"{prefix}2000a";

        var held = await Run(Verbs.Ground, Verbs.FlightTarget(other), "me", Why.Split(' '));

        held.Error.ShouldBeNull();
        held.Made.ShouldBe($"{other} is not flown here; its issue {flight.Repo}#2000 now carries pilot: hold, which the node flying it reads on its next repo sweep");
        _github.IssueLabels[2000].ShouldContain(PilotMarks.Hold);
        _github.IssueComments.ShouldContain(c => c.Number == 2000 && c.Body.Contains(Why));
        _state.Flights().ShouldNotContain(f => f.Code == other);

        (await Run(Verbs.Unground, Verbs.FlightTarget(other), "me")).Error.ShouldBeNull();
        _github.IssueLabels[2000].ShouldNotContain(PilotMarks.Hold);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task HoldOnACodeWhosePrefixThisNodeDoesNotKnowIsRefusedWithWhy()
    {
        await NewFlight("5", "parser");

        var refused = await Run(Verbs.Ground, Verbs.FlightTarget("ZZZ12"), "me", Why.Split(' '));

        refused.Error.ShouldNotBeNull().ShouldContain("no flight of this node has the prefix ZZZ");
        _github.IssueLabels.ShouldNotContainKey(12);
        (await Run(Verbs.Ground, Verbs.FlightTarget("12"), "me", Why.Split(' '))).Error.ShouldNotBeNull();
    }

    /// <summary>The repo sweep reads the label off a local flight's issue (PIL771).</summary>
    public sealed class Sweep : IDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2h-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        private readonly FakeGitHub _github = new();
        private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }

        private string Clone()
        {
            var git = Directory.CreateDirectory(Path.Combine(_root, "tianwen", ".git")).FullName;
            File.WriteAllText(Path.Combine(git, "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/tianwen.git\n");
            File.WriteAllText(Path.Combine(_root, "tianwen", RepoCatalog.MarkerFile), "# PILOT.md\n");
            return Path.Combine(_root, "tianwen");
        }

        private FlightRow Fly(string repo, int issue, FlightOnHold? hold = null, FlightState state = FlightState.Flying)
        {
            var flight = new FlightRow
            {
                Code = $"TIA{issue}", Repo = "tianwen", RepoPath = repo, Issue = issue, Branch = $"flight/tia{issue}",
                Worktree = Path.Combine(repo, ".claude", "worktrees", $"tia{issue}"), Base = "origin/main", State = state,
                CreatedAt = DateTimeOffset.UnixEpoch, OnHold = hold,
            };
            _state.PutFlight(flight);
            return flight;
        }

        private Task<RepoStatus[]> Look(string repo)
        {
            var watch = new RepoWatch(_state, new RepoCatalog(_state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), new Planes(new GitCli()), _github, TimeProvider.System)
            {
                Machine = "DE1",
                Parallelism = 1,
            };
            return watch.RefreshAsync(Ct);
        }

        private FlightRow F(int issue) => _state.Flights().Single(f => f.Code == $"TIA{issue}");

        private static FlightOnHold Held(bool? labelled) => new() { Reason = Why, Since = DateTimeOffset.UnixEpoch, By = "me", Labelled = labelled };

        [Fact]
        [Trait("Category", "Slow")]
        public async Task TheLabelOnALocalFlightsIssueHoldsItByTheLabelWithTheWhyFromTheLatestHoldComment()
        {
            var repo = Clone();
            _github.Issues[1] = "labelled";
            _github.Issues[2] = "labelled, no comment";
            _github.Issues[3] = "not labelled";
            _github.IssueLabels[1] = [PilotMarks.Hold, "pilot node: DE1"];
            _github.IssueLabels[2] = [PilotMarks.Hold, "pilot node: DE1"];
            _github.PrConversation.Add((1, new PrComment("seb", "pilot: on hold by seb from SE1: the first reason", DateTimeOffset.UtcNow.AddMinutes(-9))));
            _github.PrConversation.Add((1, new PrComment("seb", "unrelated chatter: not this", DateTimeOffset.UtcNow.AddMinutes(-1))));
            _github.PrConversation.Add((1, new PrComment("seb", "pilot: on hold by seb from SE1: the newest reason", DateTimeOffset.UtcNow.AddMinutes(-5))));
            Fly(repo, 1);
            Fly(repo, 2);
            Fly(repo, 3);

            await Look(repo);

            var one = F(1).OnHold.ShouldNotBeNull();
            (one.By, one.Reason, one.Labelled).ShouldBe(("seb", "the newest reason", true));
            (F(2).OnHold!.By, F(2).OnHold!.Reason).ShouldBe((LegCommander.TheOwner, "held on GitHub"));
            F(3).OnHold.ShouldBeNull();
            _state.Cards(openOnly: false).Where(c => c.Kind == LegCommander.OnHoldKind && c.Bin == CardBin.Handled).Select(c => c.Subject)
                .ShouldBe([Verbs.FlightTarget("TIA1"), Verbs.FlightTarget("TIA2")], ignoreOrder: true);
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task ALabelPutOnByHandAfterAReleaseReadsAsHeldOnGitHubByTheOwnerNotByTheEarlierHoldCommentsPerson()
        {
            var repo = Clone();
            _github.Issues[1] = "held and released by pilot, label put back by hand";
            _github.Issues[2] = "held, released, held again by pilot";
            _github.IssueLabels[1] = [PilotMarks.Hold, "pilot node: DE1"];
            _github.IssueLabels[2] = [PilotMarks.Hold, "pilot node: DE1"];
            _github.PrConversation.Add((1, new PrComment("seb", "pilot: on hold by seb from SE1: the earlier reason", DateTimeOffset.UtcNow.AddMinutes(-9))));
            _github.PrConversation.Add((1, new PrComment("seb", "pilot: off hold, released by seb from SE1", DateTimeOffset.UtcNow.AddMinutes(-5))));
            _github.PrConversation.Add((2, new PrComment("seb", "pilot: on hold by seb from SE1: the earlier reason", DateTimeOffset.UtcNow.AddMinutes(-9))));
            _github.PrConversation.Add((2, new PrComment("seb", "pilot: off hold, released by seb from SE1", DateTimeOffset.UtcNow.AddMinutes(-5))));
            _github.PrConversation.Add((2, new PrComment("ana", "pilot: on hold by ana from DE1: the newer reason", DateTimeOffset.UtcNow.AddMinutes(-1))));
            Fly(repo, 1);
            Fly(repo, 2);

            await Look(repo);

            (F(1).OnHold!.By, F(1).OnHold!.Reason).ShouldBe((LegCommander.TheOwner, "held on GitHub"));
            (F(2).OnHold!.By, F(2).OnHold!.Reason).ShouldBe(("ana", "the newer reason"));
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task ALabelledHoldWhoseLabelIsGoneIsReleasedAndAHoldWithoutTheLabelStays()
        {
            var repo = Clone();
            _github.Issues[1] = "labelled hold, label gone";
            _github.Issues[2] = "hold put on by this node's command alone (no tracker label)";
            _github.IssueLabels[1] = ["pilot node: DE1"];
            Fly(repo, 1, Held(true));
            Fly(repo, 2, Held(null));

            await Look(repo);

            F(1).OnHold.ShouldBeNull();
            F(2).OnHold.ShouldNotBeNull();
            _state.Cards(openOnly: false).ShouldContain(c => c.Kind == LegCommander.OnHoldKind && c.Bin == CardBin.Handled && c.Title.Contains("TIA1 is off hold"));
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task ALabelledHoldIsNotReleasedByAConfirmingReadThatFailedOnlyByOneThatSawNoLabel()
        {
            var repo = Clone();
            _github.Issues[1] = "labelled hold, the list read before the label went on";
            // The list shows no label, and the confirming read fails: gh answering nothing is not the label taken off.
            _github.IssueLabels[1] = ["pilot node: DE1"];
            Fly(repo, 1, Held(true));
            _github.IssueLabelsReadFails = "gh exited 1";

            var status = (await Look(repo)).Single();

            F(1).OnHold.ShouldNotBeNull();
            status.Errors.ShouldContain(e => e.Contains("gh exited 1"));
            _state.Cards(openOnly: false).ShouldNotContain(c => c.Title.Contains("TIA1 is off hold"));

            // Read again, and the label is really not there: now it is released.
            _github.IssueLabelsReadFails = null;
            await Look(repo);
            F(1).OnHold.ShouldBeNull();
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task ASweepThatPutsAHoldOnKeepsWhatACommandRecordedWhileItsGhCallWasOut()
        {
            var repo = Clone();
            _github.Issues[1] = "labelled";
            _github.IssueLabels[1] = [PilotMarks.Hold, "pilot node: DE1"];
            Fly(repo, 1);
            // While the hold comments are read, a command records a PR and a queue wait on the flight.
            _github.OnIssueComments = () => _state.UpdateFlight("TIA1", f => f with { PullRequest = 77, Queued = "waiting for a slot" });

            await Look(repo);

            var row = F(1);
            row.OnHold.ShouldNotBeNull();
            row.PullRequest.ShouldBe(77);
            row.Queued.ShouldBe("waiting for a slot");
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task AHoldReadFromTheLabelLetsGoOfTheMergeTheFlightArmedLikeHoldDoes()
        {
            var repo = Clone();
            _github.Issues[1] = "labelled, merge armed";
            _github.IssueLabels[1] = [PilotMarks.Hold, "pilot node: DE1"];
            var flight = Fly(repo, 1);
            var pr = new PullRequest(7, "OPEN", "abc12345abc12345", "https://github.com/SharpAstro/tianwen/pull/7");
            _github.PrOf[flight.Branch] = pr;
            _github.AutoMergeArmed.Add((7, pr.Head, "squash"));
            _github.Queue = new MergeQueueState("OPEN", pr.Head, Queued: true, Position: 1);
            _state.PutFlight(F(1) with { PullRequest = 7, MergeWhenGreen = ["abc12345", "now", LegCommander.ArmedByGitHub] });

            await Look(repo);

            F(1).OnHold.ShouldNotBeNull();
            F(1).MergeWhenGreen.ShouldBeNull();
            _github.AutoMergeDisarmed.ShouldContain(7);
            _github.Dequeued.ShouldContain(7);
            _state.Cards(openOnly: false).Single(c => c.Kind == LegCommander.OnHoldKind && c.Title.Contains("TIA1 is on hold")).Detail.ShouldNotBeNull()
                .ShouldContain("taken out of the merge queue");
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task AHoldWhoseLabelWriteFailedStaysHeldAndTheSweepPutsTheLabelOnAgain()
        {
            var repo = Clone();
            _github.Issues[1] = "label write failed";
            Fly(repo, 1, Held(false));

            // The label still will not go on: the hold stands, and the failure is the repo status's, not a release.
            _github.IssueLabelFails = "no permission";
            var status = (await Look(repo)).Single();
            F(1).OnHold!.Labelled.ShouldBe(false);
            status.Errors.ShouldContain(e => e.Contains("no permission"));

            _github.IssueLabelFails = null;
            await Look(repo);
            F(1).OnHold!.Labelled.ShouldBe(true);
            _github.IssueLabels[1].ShouldContain(PilotMarks.Hold);
            await Look(repo);
            F(1).OnHold.ShouldNotBeNull();
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async Task ALandedFlightIsLeftAloneAndAReadThatFailsLeavesTheRowsAsTheyWere()
        {
            var repo = Clone();
            _github.Issues[1] = "landed, label left";
            _github.Issues[2] = "held";
            _github.IssueLabels[1] = [PilotMarks.Hold];
            _github.IssueLabels[2] = [PilotMarks.Hold];
            Fly(repo, 1, state: FlightState.Landed);
            Fly(repo, 2, Held(true));
            _github.IssueLabels[2] = [];
            _github.OpenIssuesFail = "gh is down";

            await Look(repo);
            F(1).OnHold.ShouldBeNull();
            F(2).OnHold.ShouldNotBeNull();
        }
    }
}
