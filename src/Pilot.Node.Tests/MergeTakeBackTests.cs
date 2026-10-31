using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A merge armed (the node's, or GitHub's auto-merge) or queued in GitHub's merge queue is taken back by plan-leg, start-leg and
/// go-around: disarmed, dequeued, the PR-ready card's Merge closed and a card saying so, and offered again only once the new leg
/// is done and a fresh review passes (pilot #342).
/// </summary>
[Trait("Category", "Slow")]
public sealed class MergeTakeBackTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrHead = "cafe0000aaaabbbbccccdddd";
    private const string PrUrl = "https://github.com/x/tianwen/pull/63";
    private const string Subject = "flight/TIA1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8]);

    private string Repo => Path.Combine(_root, "tianwen");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A temp folder a scanner still holds is the OS's to clear.
        }
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    /// <summary>
    /// TIA1 with its one leg done, its review passed and PR #63 open, in a
    /// real repo, and the PR-ready card offering the merge.
    /// </summary>
    private CardRow Ready(NodeHarness node)
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        Git(Repo, "config", "user.email", "t@example.com");
        Git(Repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-q", "-m", "first");
        Git(Repo, "branch", "flight/tia1");
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Repo, Issue = 1, Branch = "flight/tia1", Worktree = Path.Combine(_root, "plane-TIA1"),
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "OPEN", PrHead, PrUrl);
        return node.State.RaiseCard(CardBin.Ring, LegCommander.PrReady, Subject, "TIA1's PR #63 is ready to merge", "Planets: 1 leg done.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Subject, Args = [PrHead] }], link: PrUrl);
    }

    private static async Task<CommandResult> Send(NodeHarness node, string verb, string target, params string[] args) =>
        (await node.Client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct)).Response.ShouldNotBeNull();

    private static async Task<CommandResult> Answer(NodeHarness node, CardRow card, string choice) =>
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [choice], Who = "gui" }, Ct)).Response.ShouldNotBeNull();

    private static string[] Plan => ["name=render", "goal=Draw the planets", "done-when=The capture shows them"];

    // Planned once the merge stands: a merge is only taken while every leg is done.
    private static void PlanNext(NodeHarness node) =>
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "render", Order = 2, Goal = "Draw", DoneWhen = "It draws", State = LegState.Planned });

    // The leg failed under the merge (a worker that gave up after it was armed): a go-around is what it offers.
    private static void FailLeg(NodeHarness node) =>
        node.State.PutLeg(node.State.Legs("TIA1").Single() with { State = LegState.Failed });

    private static async Task ArmNodeMerge(NodeHarness node, CardRow ready)
    {
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();
        node.State.Flights().Single().MergeWhenGreen.ShouldNotBeNull();
    }

    private static async Task ArmGitHubMerge(NodeHarness node, CardRow ready)
    {
        node.GitHub.AutoMergeAllowed = true;
        await ArmNodeMerge(node, ready);
        node.GitHub.AutoMergeArmed.ShouldHaveSingleItem();
    }

    private static async Task Queue(NodeHarness node, CardRow ready)
    {
        node.GitHub.Queue = new MergeQueueState("OPEN", PrHead, Queued: true, Position: 2);
        node.GitHub.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        (await Answer(node, ready, "Merge")).Error.ShouldBeNull();
    }

    private static void ShouldHaveTakenItBack(NodeHarness node, CardRow ready)
    {
        node.State.Merges().ShouldBeEmpty();
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Changes);
        node.State.Cards().Single(c => c.Id == ready.Id).State.ShouldBe(CardState.Closed);
        node.State.Cards(openOnly: true).Any(c => c.Kind == LegCommander.PrReady).ShouldBeFalse();
        var card = node.State.Cards().Single(c => c.Kind == LegCommander.MergeTakenBackKind);
        card.Title.ShouldContain("taken back");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task PlanLegOnANodeArmedMergeClearsItAndSaysSo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await ArmNodeMerge(node, ready);

        (await Send(node, Verbs.PlanLeg, Subject, Plan)).Error.ShouldBeNull();

        ShouldHaveTakenItBack(node, ready);
        node.State.Legs("TIA1").Select(l => l.Name).ShouldBe(["parser", "render"]);
    }

    [Fact]
    public async Task PlanLegOnAGitHubArmedMergeDisarmsAutoMerge()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await ArmGitHubMerge(node, ready);

        (await Send(node, Verbs.PlanLeg, Subject, Plan)).Error.ShouldBeNull();

        node.GitHub.AutoMergeDisarmed.ShouldContain(63);
        ShouldHaveTakenItBack(node, ready);
        node.State.Cards().Single(c => c.Kind == LegCommander.MergeTakenBackKind).Detail.ShouldNotBeNull().ShouldContain("auto-merge turned off");
    }

    [Fact]
    public async Task PlanLegOnAQueuedMergeDequeuesAndTheCardSaysSo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await Queue(node, ready);

        (await Send(node, Verbs.PlanLeg, Subject, Plan)).Error.ShouldBeNull();

        node.GitHub.Dequeued.ShouldBe([63]);
        ShouldHaveTakenItBack(node, ready);
        node.State.Cards().Single(c => c.Kind == LegCommander.MergeTakenBackKind).Detail.ShouldNotBeNull().ShouldContain("taken out of the merge queue");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task StartLegOnAQueuedMergeDequeues()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await Queue(node, ready);
        PlanNext(node);

        (await Send(node, Verbs.StartLeg, "flight/TIA1/render")).Error.ShouldBeNull();

        node.GitHub.Dequeued.ShouldBe([63]);
        ShouldHaveTakenItBack(node, ready);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task StartLegOnAGitHubArmedMergeDisarmsIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await ArmGitHubMerge(node, ready);
        PlanNext(node);

        (await Send(node, Verbs.StartLeg, "flight/TIA1/render")).Error.ShouldBeNull();

        node.GitHub.AutoMergeDisarmed.ShouldContain(63);
        ShouldHaveTakenItBack(node, ready);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task GoAroundOnAQueuedMergeDequeues()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await Queue(node, ready);
        FailLeg(node);

        (await Send(node, Verbs.GoAround, "flight/TIA1/parser", "fix", "the", "parser")).Error.ShouldBeNull();

        node.GitHub.Dequeued.ShouldBe([63]);
        node.State.Flights().Single().MergeWhenGreen.ShouldBeNull();
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Changes);
        node.State.Legs("TIA1").Single().State.ShouldBe(LegState.Flying);
        node.State.Cards().Single(c => c.Kind == LegCommander.MergeTakenBackKind).Detail.ShouldNotBeNull().ShouldContain("fix the parser");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task GoAroundOnAGitHubArmedMergeDisarmsIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await ArmGitHubMerge(node, ready);
        FailLeg(node);

        (await Send(node, Verbs.GoAround, "flight/TIA1/parser", "fix", "the", "parser")).Error.ShouldBeNull();

        node.GitHub.AutoMergeDisarmed.ShouldContain(63);
        ShouldHaveTakenItBack(node, ready);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task NoMergeIsOfferedUntilAFreshReviewPasses()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await ArmNodeMerge(node, ready);
        (await Send(node, Verbs.PlanLeg, Subject, Plan)).Error.ShouldBeNull();

        // The new leg is not done: the review is turned back, and no card offers a merge.
        node.State.Cards(openOnly: true).Any(c => c.Choices.Any(ch => ch.Verb == Verbs.Merge)).ShouldBeFalse();
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Changes);
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task APlanWithNoMergeStandingTakesNothingBack()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);

        (await Send(node, Verbs.PlanLeg, Subject, Plan)).Error.ShouldBeNull();

        node.GitHub.Dequeued.ShouldBeEmpty();
        node.GitHub.AutoMergeDisarmed.ShouldBeEmpty();
        node.State.Cards().Any(c => c.Kind == LegCommander.MergeTakenBackKind).ShouldBeFalse();
        node.State.Cards().Single(c => c.Id == ready.Id).State.ShouldBe(CardState.Open);
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Passed);
    }

    [Fact]
    public async Task AFailingDequeueRefusesPlanLeg()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await Queue(node, ready);
        node.GitHub.DequeueFails = "GitHub is down";

        var result = await Send(node, Verbs.PlanLeg, Subject, Plan);

        result.Error.ShouldNotBeNull().ShouldContain("GitHub is down");
        node.State.Legs("TIA1").Select(l => l.Name).ShouldBe(["parser"]);
        node.State.Flights().Single().Review!.State.ShouldBe(ReviewState.Passed);
    }

    [Fact]
    public async Task AFailingDisarmRefusesStartLegAndLeavesItArmed()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Ready(node);
        await ArmGitHubMerge(node, ready);
        PlanNext(node);
        node.GitHub.DisarmFails = "no way";

        var result = await Send(node, Verbs.StartLeg, "flight/TIA1/render");

        result.Error.ShouldNotBeNull().ShouldContain("no way");
        node.State.Legs("TIA1").Single(l => l.Name == "render").State.ShouldBe(LegState.Planned);
        node.State.Flights().Single().MergeWhenGreen.ShouldNotBeNull();
    }
}
