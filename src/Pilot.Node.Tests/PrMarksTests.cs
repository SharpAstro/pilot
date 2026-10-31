using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// What a flight's review leaves on its PR (#222): the pilot/review status on the head the reviewer read, the one pilot:
/// label, and the report as a COMMENT review once per go-around; and that a write GitHub refuses is a card, not a failed review.
/// </summary>
public abstract class PrMarksTestsBase : IDisposable
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The head the reviewer reads: the flight's plane as it stands, which GitHub's own (stale for a moment after a push) is not.</summary>
    private protected string Reviewed => Git(_state.Flights().Single().Worktree, "rev-parse", "HEAD");

    private protected const string Moved = "beef1111beef1111";

    private protected readonly string _root = Path.Combine(Path.GetTempPath(), "p2m-" + Guid.NewGuid().ToString("N")[..8]);
    private protected readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private protected readonly FakeWorkers _workers = new();
    private protected readonly FakeGitHub _github = new();
    private protected readonly FlightCommander _flights;
    private protected readonly LegCommander _legs;

    protected PrMarksTestsBase()
    {
        // A repo with a bare origin, so a flight's push is a real one: the seed, built once a run and copied.
        SeedRepo.Make(_root);
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System, _workers);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, _workers, new FakeChecks(), _github, git, new Planes(git), _flights, options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
    }

    private protected string Repo => Path.Combine(_root, "tianwen");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private protected static string Git(string cwd, params string[] args)
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

    private protected Task<CommandResult> Run(string verb, string target, string who, params string[] args) =>
        verb is Verbs.CreateFlight or Verbs.Connect or Verbs.Land
            ? _flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct)
            : _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);

    private protected FlightRow F(string code) => _state.Flights().Single(f => f.Code == code);

    private protected CardRow[] Open() => _state.Cards(openOnly: true);

    private protected static string Reviewer(string code) => $"du1-{code.ToLowerInvariant()}-review";

    /// <summary>A flight with one leg flown as a worker flies it, its PR opened and its reviewer started.</summary>
    private protected async Task<FlightRow> Opened()
    {
        (await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "controller", "1", "Flight 1")).Ok.ShouldBeTrue();
        var flight = _state.Flights().Single();
        (await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", "name=parser", "goal=Do it", "done-when=It is done", "item=The step")).Ok.ShouldBeTrue();
        (await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, "parser"), "controller")).Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single().Worker!;
        File.WriteAllText(Path.Combine(flight.Worktree, "parser.txt"), "work\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "parser work");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, "parser"), worker, "1")).Ok.ShouldBeTrue();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, "parser"), worker, "done", "did it")).Error.ShouldBeNull();
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return F(flight.Code);
    }

    private protected Task<CommandResult> Verdict(string code, string verdict, string report) =>
        Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), verdict, report);

    private protected static string[] Pilot(HashSet<string>? labels) => [.. (labels ?? []).Where(l => l.StartsWith("pilot: ", StringComparison.Ordinal))];
}

/// <summary>Part 1 of PrMarksTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class PrMarksTests1 : PrMarksTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task OpeningAFlightsPrLabelsItWithThisNodesCodeAndAStateLabelChangeLeavesItBe()
    {
        var flight = await Opened();

        _github.IssueLabels[71].ShouldBe(["pilot node: DU1"]);
        // The state label is the PR's one pilot: label; the node label is another kind and stays through every swap.
        Pilot(_github.Labels[71]).ShouldBe(["pilot: reviewing"]);
        (await Verdict(flight.Code, "pass", "every Done-when holds")).Error.ShouldBeNull();
        Pilot(_github.Labels[71]).ShouldBe(["pilot: passed"]);
        _github.IssueLabels[71].ShouldBe(["pilot node: DU1"]);
    }

    [Fact]
    public async Task APilotLabelSwapLeavesANodeLabelInPlace()
    {
        _github.Labels[5] = ["pilot node: DU1", "pilot: reviewing", "bug"];

        await _github.SetPilotLabelAsync(Repo, 5, "pilot: changes", Ct);

        _github.Labels[5].OrderBy(l => l, StringComparer.Ordinal).ShouldBe(["bug", "pilot node: DU1", "pilot: changes"]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANodeLabelGitHubRefusesOnTheNewPrIsACardNotAFailedPr()
    {
        _github.IssueLabelFails = "label refused";

        var flight = await Opened();

        flight.PullRequest.ShouldBe(71);
        _state.Cards(openOnly: false).Single(c => c.Kind == "node-label-refused").Detail.ShouldNotBeNull().ShouldContain("label refused");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewStartsPendingOnTheHeadItReadsAndTheFlightRowKeepsWhatWasSet()
    {
        var flight = await Opened();

        // GitHub still says the head from before the push (the fake's): the status goes on the plane's, which the reviewer reads.
        _github.Pr!.Head.ShouldNotBe(Reviewed);
        var status = _github.Statuses.ShouldHaveSingleItem();
        (status.Sha, status.State, status.Description, status.Url).ShouldBe((Reviewed, "pending", "reviewing", flight.PullRequestUrl));
        _github.LabelCalls.ShouldBe([(71, "pilot: reviewing")]);
        var review = flight.Review.ShouldNotBeNull();
        (review.ReviewedHead, review.StatusState, review.Label, review.ReviewUrl, review.MarksWhy).ShouldBe((Reviewed, "pending", "pilot: reviewing", null, null));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APassIsPostedAsACommentReviewAndSetOnTheHeadTheReviewerReadNotALaterOne()
    {
        var flight = await Opened();
        // The branch moves after the review started: the pass must not attach to code the reviewer never saw.
        _github.Pr = _github.Pr! with { Head = Moved };

        (await Verdict(flight.Code, "pass", "every Done-when holds")).Error.ShouldBeNull();

        var posted = _github.Posted.ShouldHaveSingleItem();
        (posted.Repo, posted.Number).ShouldBe(("x/tianwen", 71));
        using var payload = System.Text.Json.JsonDocument.Parse(posted.Payload);
        payload.RootElement.GetProperty("event").GetString().ShouldBe("COMMENT");
        payload.RootElement.GetProperty("commit_id").GetString().ShouldBe(Reviewed);
        payload.RootElement.GetProperty("body").GetString().ShouldNotBeNull().ShouldContain("every Done-when holds");
        var last = _github.Statuses.Last();
        (last.Sha, last.State, last.Description).ShouldBe((Reviewed, "success", $"passed at {Reviewed[..8]}"));
        last.Url.ShouldBe("https://github.com/x/tianwen/pull/71#pullrequestreview-9001");
        _github.Statuses.ShouldAllBe(s => s.Sha == Reviewed);
        var review = F(flight.Code).Review.ShouldNotBeNull();
        (review.State, review.StatusState, review.Label, review.ReviewUrl, review.MarksWhy)
            .ShouldBe((ReviewState.Passed, "success", "pilot: passed", "https://github.com/x/tianwen/pull/71#pullrequestreview-9001", null));
        Open().ShouldContain(c => c.Kind == LegCommander.PrReady);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ChangesSetFailureWithTheFirstFindingAndTheLabelsReplaceEachOther()
    {
        var flight = await Opened();
        // A label of someone else's stays; every pilot: label but the current one goes.
        _github.Labels[71] = ["bug", "pilot: conflicts"];

        (await Verdict(flight.Code, "changes", "- the null check in Parse is gone\n- no test for it")).Error.ShouldBeNull();

        _github.Statuses.Last().ShouldBe((Reviewed, "failure", "changes: the null check in Parse is gone", "https://github.com/x/tianwen/pull/71#pullrequestreview-9001"));
        Pilot(_github.Labels[71]).ShouldBe(["pilot: changes"]);
        _github.Labels[71].ShouldContain("bug");

        // A fresh review and its pass replace it in turn: one pilot: label throughout.
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        Pilot(_github.Labels[71]).ShouldBe(["pilot: reviewing"]);
        (await Verdict(flight.Code, "pass", "fixed")).Error.ShouldBeNull();
        Pilot(_github.Labels[71]).ShouldBe(["pilot: passed"]);
        _github.LabelCalls.Select(c => c.Label).ShouldBe(["pilot: reviewing", "pilot: changes", "pilot: reviewing", "pilot: passed"]);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task OneCommentReviewIsPostedPerGoAroundAndTheEarlierOnesAreLeftAlone()
    {
        var flight = await Opened();
        (await Verdict(flight.Code, "changes", "first round")).Error.ShouldBeNull();
        (await Run(Verbs.StartReview, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        F(flight.Code).Review!.ReviewUrl.ShouldBeNull();
        _github.Posted.Count.ShouldBe(1);

        (await Verdict(flight.Code, "pass", "second round")).Error.ShouldBeNull();

        _github.Posted.Count.ShouldBe(2);
        _github.Posted.Select(p => System.Text.Json.JsonDocument.Parse(p.Payload).RootElement.GetProperty("event").GetString()).ShouldAllBe(e => e == "COMMENT");
        _github.Posted[0].Payload.ShouldContain("first round");
        _github.Posted[1].Payload.ShouldContain("second round");
        F(flight.Code).Review!.ReviewUrl.ShouldNotBeNull().ShouldEndWith("pullrequestreview-9002");
    }
}

/// <summary>Part 2 of PrMarksTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class PrMarksTests2 : PrMarksTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWriteGitHubRefusesIsACardAndNeverFailsTheReviewOrLosesTheVerdict()
    {
        var flight = await Opened();
        _github.PostFails = new FlightException("GitHub refused the review: Resource not accessible");
        _github.StatusFails = "GitHub refused the status: no permission";
        _github.LabelFails = "GitHub refused the label: rate limited";

        (await Verdict(flight.Code, "pass", "all good")).Error.ShouldBeNull();

        var review = F(flight.Code).Review.ShouldNotBeNull();
        review.State.ShouldBe(ReviewState.Passed);
        review.MarksWhy.ShouldNotBeNull().ShouldContain("no permission");
        review.MarksWhy.ShouldContain("rate limited");
        review.MarksWhy.ShouldContain("Resource not accessible");
        (review.StatusState, review.Label, review.ReviewUrl).ShouldBe(("pending", "pilot: reviewing", null));
        var card = Open().Single(c => c.Kind == PrMarks.RefusedKind);
        card.Bin.ShouldBe(CardBin.ForYou);
        card.Title.ShouldBe("GitHub refused a mark on TIA1's PR #71");
        // The merge is still offered: the review passed.
        Open().ShouldContain(c => c.Kind == LegCommander.PrReady);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARefusedWriteIsSetAtTheNextLookWithoutPostingTheReviewTwiceAndTheCardCloses()
    {
        var flight = await Opened();
        _github.StatusFails = "GitHub refused the status: rate limited";
        (await Verdict(flight.Code, "pass", "all good")).Error.ShouldBeNull();
        _github.Posted.Count.ShouldBe(1);
        F(flight.Code).Review!.MarksWhy.ShouldNotBeNull();
        var cards = Open().Count(c => c.Kind == PrMarks.RefusedKind);
        cards.ShouldBe(1);

        // Still refused: the same why is not carded again.
        await _legs.RetryMarksAsync(Ct);
        Open().Count(c => c.Kind == PrMarks.RefusedKind).ShouldBe(cards);

        _github.StatusFails = null;
        await _legs.RetryMarksAsync(Ct);

        var review = F(flight.Code).Review!;
        (review.StatusState, review.Label, review.MarksWhy).ShouldBe(("success", "pilot: passed", null));
        _github.Statuses.Last().ShouldBe((Reviewed, "success", $"passed at {Reviewed[..8]}", review.ReviewUrl));
        _github.Posted.Count.ShouldBe(1);
        Open().ShouldNotContain(c => c.Kind == PrMarks.RefusedKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AReviewCommentThatWasRefusedIsPostedAtTheNextLook()
    {
        var flight = await Opened();
        _github.PostFails = new FlightException("GitHub refused the review: nope");
        (await Verdict(flight.Code, "changes", "fix the parser")).Error.ShouldBeNull();
        _github.Posted.ShouldBeEmpty();
        // The status and the label are set without the link; the comment is what is left.
        F(flight.Code).Review!.MarksWhy.ShouldNotBeNull().ShouldContain("nope");
        _github.Statuses.Last().State.ShouldBe("failure");

        _github.PostFails = null;
        await _legs.RetryMarksAsync(Ct);

        _github.Posted.ShouldHaveSingleItem().Payload.ShouldContain("fix the parser");
        var review = F(flight.Code).Review!;
        review.MarksWhy.ShouldBeNull();
        // The status already stood at failure, but linked the PR page: it is set again with the comment's link.
        review.ReviewUrl.ShouldNotBeNull();
        _github.Statuses.Last().Url.ShouldBe(review.ReviewUrl);
        _github.Statuses.Last().State.ShouldBe("failure");
    }

    [Fact]
    public void ThePullRequestPageSaysWhichRepoAReviewIsPostedOn()
    {
        PrMarks.RepoOf("https://github.com/SharpAstro/pilot/pull/222").ShouldBe("SharpAstro/pilot");
        PrMarks.RepoOf("https://example.com/").ShouldBeNull();
        PrMarks.RepoOf(null).ShouldBeNull();
    }

}
