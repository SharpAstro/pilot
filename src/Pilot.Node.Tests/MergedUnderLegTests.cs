using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A leg planned or started after GitHub merged the flight's PR is not lost (pilot #342): the node rings at once, the leg's work
/// goes to a new PR from the merged base, and landing never removes a plane holding legs not done, commits past the merged head
/// or changes not committed. Git is real (a bare origin and a plane); GitHub is the fake.
/// </summary>
public sealed class MergedUnderLegTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrUrl = "https://github.com/x/tianwen/pull/63";
    private const string Subject = "flight/TIA1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2m-" + Guid.NewGuid().ToString("N")[..8]);
    private string _mergedHead = "";

    private string Repo => Path.Combine(_root, "tianwen");

    private string Plane => Path.Combine(_root, "plane-TIA1");

    private string Origin => Path.Combine(_root, "origin.git");

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

    private void Commit(string cwd, string file, string subject)
    {
        File.WriteAllText(Path.Combine(cwd, file), subject + "\n");
        Git(cwd, "add", ".");
        Git(cwd, "commit", "-q", "-m", subject);
    }

    /// <summary>
    /// TIA1 with its one leg done and its PR #63 merged on GitHub (squashed: main holds the work under another commit), the plane
    /// holding the merged commit, and the PR-ready card still open.
    /// </summary>
    private CardRow Merged(NodeHarness node)
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        Git(Repo, "config", "user.email", "t@example.com");
        Git(Repo, "config", "user.name", "t");
        Commit(Repo, "README.md", "first");
        Git(_root, "init", "-q", "--bare", "-b", "main", Origin);
        Git(Repo, "remote", "add", "origin", Origin);
        Git(Repo, "push", "-q", "origin", "main");
        Git(Repo, "worktree", "add", "-q", "-b", "flight/tia1", Plane);
        Commit(Plane, "parser.txt", "the parser");
        Git(Plane, "push", "-q", "-u", "origin", "flight/tia1");
        _mergedHead = Git(Plane, "rev-parse", "HEAD");
        // The squash merge: the same work in main under another commit.
        Commit(Repo, "parser.txt", "the parser");
        Git(Repo, "push", "-q", "origin", "main");
        node.State.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = Repo, Issue = 1, Branch = "flight/tia1", Worktree = Plane,
            Base = "main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, PullRequest = 63, PullRequestUrl = PrUrl,
            Review = new ReviewRow { State = ReviewState.Passed, Reviewer = "du1-tia1-review", StartedAt = DateTimeOffset.UtcNow },
        });
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        node.GitHub.Pr = new PullRequest(63, "MERGED", _mergedHead, PrUrl);
        return node.State.RaiseCard(CardBin.Ring, LegCommander.PrReady, Subject, "TIA1's PR #63 is ready to merge", "Planets: 1 leg done.",
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = Subject, Args = [_mergedHead] }], link: PrUrl);
    }

    private static async Task<CommandResult> Send(NodeHarness node, string verb, string target, params string[] args) =>
        (await node.Client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct)).Response.ShouldNotBeNull();

    private static Task Look(NodeHarness node) => node.App.Services.GetRequiredService<PrChecks>().LookAsync(Ct);

    private static string[] Plan => ["name=render", "goal=Draw the planets", "done-when=The capture shows them"];

    private static void PlanNext(NodeHarness node) =>
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "render", Order = 2, Goal = "Draw", DoneWhen = "It draws", State = LegState.Planned });

    private static void ShouldHaveRung(NodeHarness node, CardRow ready)
    {
        var flight = node.State.Flights().Single();
        flight.State.ShouldBe(FlightState.Flying);
        flight.MergedHead.ShouldBe(flight.MergedHead.ShouldNotBeNull());
        flight.PullRequest.ShouldBeNull();
        node.State.Cards().Single(c => c.Id == ready.Id).State.ShouldBe(CardState.Closed);
        var ring = node.State.Cards(openOnly: true).Single(c => c.Kind == FlightCommander.MergedUnderLegKind);
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Detail.ShouldNotBeNull().ShouldContain("new PR");
        node.GitHub.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanLegAfterTheMergeRingsAtOnceAndTheFlightStaysFlying()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Merged(node);

        (await Send(node, Verbs.PlanLeg, Subject, Plan)).Error.ShouldBeNull();

        ShouldHaveRung(node, ready);
        node.State.Flights().Single().MergedHead.ShouldBe(_mergedHead);
        node.State.Legs("TIA1").Select(l => l.Name).ShouldBe(["parser", "render"]);
    }

    [Fact]
    public async Task StartLegAfterTheMergeRingsAtOnce()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Merged(node);
        PlanNext(node);

        (await Send(node, Verbs.StartLeg, "flight/TIA1/render")).Error.ShouldBeNull();

        ShouldHaveRung(node, ready);
    }

    [Fact]
    public async Task ADoneLegGoesToANewPrFromTheMergedBase()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Merged(node);
        (await Send(node, Verbs.PlanLeg, Subject, Plan)).Error.ShouldBeNull();
        ShouldHaveRung(node, ready);

        // The new leg's work, committed in the plane, and the leg done.
        Commit(Plane, "render.txt", "the render");
        node.State.PutLeg(node.State.Legs("TIA1").Single(l => l.Name == "render") with { State = LegState.Done });
        node.GitHub.Created.Clear();

        (await Send(node, Verbs.OpenPr, Subject)).Error.ShouldBeNull();

        var flight = node.State.Flights().Single();
        node.GitHub.Created.ShouldHaveSingleItem().Base.ShouldBe("main");
        flight.PullRequest.ShouldBe(71);
        flight.MergedHead.ShouldBeNull();
        // Rebased onto the base as it now stands: the merged commit is out, only the new leg's is on it.
        Git(Plane, "log", "--format=%s", "main..HEAD").ShouldBe("the render");
        Git(Plane, "rev-parse", "HEAD~1").ShouldBe(Git(Repo, "rev-parse", "main"));
        Git(Origin, "rev-parse", "flight/tia1").ShouldBe(Git(Plane, "rev-parse", "HEAD"));
        node.State.Cards(openOnly: true).Any(c => c.Kind == FlightCommander.MergedUnderLegKind).ShouldBeFalse();
        node.State.Cards().Any(c => c.Kind == LegCommander.MergeTakenBackKind).ShouldBeFalse();
    }

    [Fact]
    public async Task ADoneLegGoesToANewPrFromTheMergedBaseInAMergeWayRepo()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.GitHub.UpdateWay = UpdateWay.Merge;
        var ready = Merged(node);
        (await Send(node, Verbs.PlanLeg, Subject, Plan)).Error.ShouldBeNull();
        ShouldHaveRung(node, ready);

        // The new leg's work, committed in the plane, and the leg done.
        Commit(Plane, "render.txt", "the render");
        node.State.PutLeg(node.State.Legs("TIA1").Single(l => l.Name == "render") with { State = LegState.Done });
        node.GitHub.Created.Clear();

        (await Send(node, Verbs.OpenPr, Subject)).Error.ShouldBeNull();

        var flight = node.State.Flights().Single();
        node.GitHub.Created.ShouldHaveSingleItem().Base.ShouldBe("main");
        flight.PullRequest.ShouldBe(71);
        flight.MergedHead.ShouldBeNull();
        // Rebased onto the base as it now stands: the merged commit is out, only the new leg's is on it.
        Git(Plane, "log", "--format=%s", "main..HEAD").ShouldBe("the render");
        Git(Plane, "rev-parse", "HEAD~1").ShouldBe(Git(Repo, "rev-parse", "main"));
        Git(Origin, "rev-parse", "flight/tia1").ShouldBe(Git(Plane, "rev-parse", "HEAD"));
        node.State.Cards(openOnly: true).Any(c => c.Kind == FlightCommander.MergedUnderLegKind).ShouldBeFalse();
        node.State.Cards().Any(c => c.Kind == LegCommander.MergeTakenBackKind).ShouldBeFalse();
    }

    [Fact]
    public async Task AMergeSeenWhileALegIsPlannedLandsNothingAndRings()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Merged(node);
        PlanNext(node);

        await Look(node);

        ShouldHaveRung(node, ready);
        Directory.Exists(Plane).ShouldBeTrue();
        node.State.Cards().Any(c => c.Kind == "landed").ShouldBeFalse();
    }

    [Fact]
    public async Task AMergeSeenWhileALegFliesLandsNothingAndRings()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var ready = Merged(node);
        node.State.PutLeg(new LegRow { Flight = "TIA1", Name = "render", Order = 2, Goal = "Draw", DoneWhen = "It draws", State = LegState.Flying });

        await Look(node);

        ShouldHaveRung(node, ready);
        Directory.Exists(Plane).ShouldBeTrue();
        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task AMergeSeenWithUncommittedChangesKeepsThePlaneAndRings()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Merged(node);
        File.WriteAllText(Path.Combine(Plane, "half.txt"), "not done\n");

        await Look(node);

        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);
        File.Exists(Path.Combine(Plane, "half.txt")).ShouldBeTrue();
        node.State.Cards(openOnly: true).Single(c => c.Kind == "not-landed").Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    public async Task AMergeSeenWithCommitsPastTheMergedHeadKeepsThePlaneAndRings()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Merged(node);
        Commit(Plane, "late.txt", "a late commit");

        await Look(node);

        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);
        File.Exists(Path.Combine(Plane, "late.txt")).ShouldBeTrue();
        var ring = node.State.Cards(openOnly: true).Single(c => c.Kind == "not-landed");
        ring.Bin.ShouldBe(CardBin.Ring);
        ring.Detail.ShouldNotBeNull().ShouldContain("a late commit");
    }

    [Fact]
    public async Task TheNodesOwnFirstLookIsOverBeforeStartReturnsSoItNeverLandsAStateTheTestPutsAfter()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        // The background loop looks once at start; the harness waited for that look, so none is left to come.
        node.App.Services.GetRequiredService<PrChecks>().FirstLook.IsCompletedSuccessfully.ShouldBeTrue();

        // A merged PR with every leg done, put in after start: flying until the test's own look lands it.
        Merged(node);
        node.State.Flights().Single().State.ShouldBe(FlightState.Flying);
        Directory.Exists(Plane).ShouldBeTrue();

        await Look(node);

        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
    }

    [Fact]
    public async Task AMergeSeenWithNothingLeftStillLandsTheFlight()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        Merged(node);

        await Look(node);

        node.State.Flights().Single().State.ShouldBe(FlightState.Landed);
        Directory.Exists(Plane).ShouldBeFalse();
    }
}
