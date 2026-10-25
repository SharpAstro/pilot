using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Reviews applied by the node, and phase 3's done-when: two flights, one connected to the other, flown and landed by
/// the node's own machinery (fresh workers, the review, the cards) with no per-flight pilot session anywhere.
/// </summary>
public sealed class ReviewTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FakeGitHub _github = new();
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;

    public ReviewTests()
    {
        // A repo with a bare origin, so a flight's push is a real one.
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(Repo);
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        Git(Repo, "init", "-q", "-b", "main");
        // The node's own rebases make commits, and a CI runner has no identity of its own to make them with.
        Git(Repo, "config", "user.email", "t@example.com");
        Git(Repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        Git(Repo, "remote", "add", "origin", origin);
        Git(Repo, "push", "-q", "-u", "origin", "main");
        Git(Repo, "remote", "set-head", "origin", "main");
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System, _workers);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, _workers, new FakeChecks(), _github, git, new Planes(git), _flights, options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
    }

    private string Repo => Path.Combine(_root, "tianwen");

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

    private Task<CommandResult> Run(string verb, string target, string who, params string[] args) =>
        verb is Verbs.CreateFlight or Verbs.Connect or Verbs.Land
            ? _flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct)
            : _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);

    private FlightRow F(string code) => _state.Flights().Single(f => f.Code == code);

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private async Task<FlightRow> NewFlight(string issue, string leg)
    {
        (await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "controller", issue, "Flight " + issue)).Ok.ShouldBeTrue();
        var flight = _state.Flights().OrderByDescending(f => f.CreatedAt).First();
        (await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", $"name={leg}", "goal=Do it", "done-when=It is done", "item=The step")).Ok.ShouldBeTrue();
        return flight;
    }

    /// <summary>A leg flown as a worker flies it: started by the node, a commit in the plane, its step ticked, finished by the worker.</summary>
    private async Task Fly(FlightRow flight, string leg, int lines = 1)
    {
        var start = await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, leg), "controller");
        start.Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single(l => l.Name == leg).Worker!;
        File.WriteAllText(Path.Combine(flight.Worktree, $"{leg}-{Guid.NewGuid():N}.txt"), string.Concat(Enumerable.Repeat("work\n", lines)));
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", $"{leg} work");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, leg), worker, "1")).Ok.ShouldBeTrue();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, leg), worker, "done", "did it")).Error.ShouldBeNull();
    }

    private string Reviewer(string code) => $"du1-{code.ToLowerInvariant()}-review";

    private void Usage(double fiveHour, double sevenDay = 10) =>
        _state.PutUsage(new UsageRow
        {
            ReadAt = DateTimeOffset.UtcNow,
            FiveHour = new UsageWindow { UsedPercent = fiveHour, ResetsAt = DateTimeOffset.UtcNow.AddHours(2) },
            SevenDay = new UsageWindow { UsedPercent = sevenDay, ResetsAt = DateTimeOffset.UtcNow.AddDays(3) },
        });

    private async Task<WorkerStart> Review(string issue, int lines, double fiveHour, bool priority = false, Dictionary<string, string>? roles = null)
    {
        Usage(fiveHour);
        var flight = await NewFlight(issue, "parser");
        _legs.Models = new RoleModels(new NodeSettings { PriorityRepos = priority ? [flight.Repo] : null, Roles = roles });
        await Fly(flight, "parser", lines);
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return _workers.Started.Last();
    }

    [Fact]
    public async Task HighUsageMakesALightReviewSaidInItsBriefAndOnThePrReadyCard()
    {
        var start = await Review("1", lines: 600, fiveHour: 88, roles: new() { ["reviewer-model"] = "opus", ["reviewer-effort"] = "high" });

        start.Brief.ShouldContain("Depth: light (usage at 88%).");
        start.Brief.ShouldContain("Review lightly: one pass, no subagents.");
        start.Brief.ShouldNotContain("no subagents, or one at most");
        (start.Model, start.Effort).ShouldBe(("opus", "medium"));
        F("TIA1").Review.ShouldNotBeNull().Depth.ShouldBe(ReviewDepth.Light);

        (await Run(Verbs.Review, "flight/TIA1", Reviewer("TIA1"), "pass", "read it")).Ok.ShouldBeTrue();
        Open().Single(c => c.Kind == LegCommander.PrReady).Detail.ShouldNotBeNull().ShouldContain("Reviewed lightly, usage at 88%: read it.");
    }

    [Fact]
    public async Task APriorityRepoIsNeverReviewedLightly()
    {
        var start = await Review("1", lines: 1, fiveHour: 88, priority: true);

        start.Brief.ShouldContain("Depth: standard (priority repo, usage at 88%).");
        start.Brief.ShouldContain("no subagents, or one at most");
        F("TIA1").Review!.Depth.ShouldBe(ReviewDepth.Standard);
    }

    [Fact]
    public async Task HeadroomAndALargeDiffMakeADeepReviewOnTheStrongerModelWithUpToThreeSubagents()
    {
        var start = await Review("1", lines: 500, fiveHour: 20, roles: new() { ["reviewer-model"] = "sonnet", ["reviewer-effort"] = "medium", ["reviewer-deep-model"] = "opus" });

        start.Brief.ShouldContain("Depth: deep (usage at 20%, 500 lines in 1 file).");
        start.Brief.ShouldContain("up to 3");
        (start.Model, start.Effort).ShouldBe(("opus", "high"));
        (await Run(Verbs.Review, "flight/TIA1", Reviewer("TIA1"), "pass", "checked the claims")).Ok.ShouldBeTrue();
        Open().Single(c => c.Kind == LegCommander.PrReady).Detail.ShouldNotBeNull().ShouldContain("Reviewed deeply, usage at 20%, 500 lines in 1 file: checked the claims.");
    }

    [Fact]
    public async Task ASmallDiffOrLittleHeadroomIsAStandardReviewOnTheReviewersOwnModel()
    {
        var small = await Review("1", lines: 5, fiveHour: 20, roles: new() { ["reviewer-model"] = "opus", ["reviewer-deep-model"] = "fable" });
        small.Brief.ShouldContain("Depth: standard (usage at 20%).");
        (small.Model, small.Effort).ShouldBe(("opus", null));

        var tight = await Review("2", lines: 500, fiveHour: 60);
        tight.Brief.ShouldContain("Depth: standard (usage at 60%).");
    }

    [Theory]
    [InlineData(88, 10, false, 1, 1, ReviewDepth.Light, "usage at 88%")]
    [InlineData(79, 95, false, 1, 1, ReviewDepth.Light, "7-day usage at 95%")]
    [InlineData(95, 10, true, 1, 1, ReviewDepth.Standard, "priority repo, usage at 95%")]
    [InlineData(20, 95, true, 900, 30, ReviewDepth.Standard, "priority repo, 7-day usage at 95%")]
    [InlineData(20, 10, false, 399, 14, ReviewDepth.Standard, "usage at 20%")]
    [InlineData(20, 10, false, 400, 3, ReviewDepth.Deep, "usage at 20%, 400 lines in 3 files")]
    [InlineData(20, 10, false, 30, 15, ReviewDepth.Deep, "usage at 20%, 30 lines in 15 files")]
    [InlineData(20, 10, false, 200, 3, ReviewDepth.Standard, "usage at 20%")]
    [InlineData(20, 10, true, 200, 3, ReviewDepth.Deep, "usage at 20%, 200 lines in 3 files")]
    [InlineData(50, 10, false, 900, 30, ReviewDepth.Standard, "usage at 50%")]
    [InlineData(20, 70, false, 900, 30, ReviewDepth.Standard, "usage at 20%")]
    public void TheDepthFollowsUsagePriorityAndTheDiff(double five, double seven, bool priority, int lines, int files, ReviewDepth depth, string why)
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new UsageRow { ReadAt = now, FiveHour = new UsageWindow { UsedPercent = five }, SevenDay = new UsageWindow { UsedPercent = seven } };

        ReviewPlan.Choose(usage, priority, (lines, files), now).ShouldBe(new ReviewPlan(depth, why));
    }

    [Fact]
    public void StaleFiguresASpentWindowOrNoDiffMeasureNeverMakeAReviewLightOrDeep()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = new UsageRow { ReadAt = now - ReviewPlan.FreshFor - TimeSpan.FromMinutes(1), FiveHour = new UsageWindow { UsedPercent = 95 } };
        var reset = new UsageRow { ReadAt = now, FiveHour = new UsageWindow { UsedPercent = 95, ResetsAt = now.AddMinutes(-1) } };
        var low = new UsageRow { ReadAt = now, FiveHour = new UsageWindow { UsedPercent = 10 } };

        ReviewPlan.Choose(stale, false, (900, 30), now).ShouldBe(new ReviewPlan(ReviewDepth.Standard, "no fresh usage figures"));
        ReviewPlan.Choose(null, false, (900, 30), now).Depth.ShouldBe(ReviewDepth.Standard);
        ReviewPlan.Choose(reset, false, (1, 1), now).Depth.ShouldBe(ReviewDepth.Standard);
        ReviewPlan.Choose(low, false, null, now).ShouldBe(new ReviewPlan(ReviewDepth.Standard, "usage at 10%"));
    }

    [Fact]
    public void NumstatCountsLinesAndFilesAndABinaryFileIsAFileOnly()
    {
        ReviewPlan.ParseNumstat("10\t2\ta.cs\n-\t-\timg.png\n3\t0\tb with space.cs\n").ShouldBe((15, 3));
        ReviewPlan.ParseNumstat("").ShouldBeNull();
    }

    [Fact]
    public void AReviewerRunsOneEffortStepBelowOrAboveItsOwnAndTheDeepSettingsWin()
    {
        var own = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { ["reviewer-effort"] = "low", ["reviewer-model"] = "opus" } });
        (own.ReviewerEffort(ReviewDepth.Light), own.ReviewerEffort(ReviewDepth.Standard), own.ReviewerEffort(ReviewDepth.Deep)).ShouldBe(("low", "low", "medium"));

        var deep = new RoleModels(new NodeSettings { Roles = new Dictionary<string, string> { ["reviewer-deep-effort"] = "max", ["reviewer-deep-model"] = "fable" } });
        (deep.ReviewerModel(ReviewDepth.Deep), deep.ReviewerModel(ReviewDepth.Light), deep.ReviewerEffort(ReviewDepth.Deep), deep.ReviewerEffort(ReviewDepth.Light)).ShouldBe(("fable", "opus", "max", "low"));
        (RoleModels.Unset.ReviewerModel(ReviewDepth.Deep), RoleModels.Unset.ReviewerEffort(ReviewDepth.Standard), RoleModels.Unset.ReviewerEffort(ReviewDepth.Deep)).ShouldBe((null, null, "high"));
    }

    [Fact]
    public async Task APrOpensAFreshReviewerAndOnlyItsPassOffersTheMerge()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");

        (await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA1"), "controller")).Error.ShouldBeNull();

        var review = F("TIA1").Review.ShouldNotBeNull();
        (review.State, review.Reviewer).ShouldBe((ReviewState.Reviewing, Reviewer("TIA1")));
        var start = _workers.Started.Last();
        (start.Name, start.Plane).ShouldBe((Reviewer("TIA1"), "tia1"));
        start.Brief.ShouldContain("- parser: Do it Done when: It is done");
        start.Brief.ShouldContain("no subagents, or one at most");
        start.Brief.ShouldContain("review pass");
        Open().ShouldBeEmpty();
        (await Run(Verbs.Merge, "flight/TIA1", "controller", "cafe0000")).Error.ShouldBe("TIA1's review is reviewing, not passed");

        // Only the reviewer gives the verdict; the controller may not pass a review still on.
        (await Run(Verbs.Review, "flight/TIA1", "controller", "pass", "looks fine")).Error.ShouldNotBeNull().ShouldStartWith($"only {Reviewer("TIA1")} gives");
        (await Run(Verbs.Review, "flight/TIA1", Reviewer("TIA1"), "pass", "the step is in; it is done")).Ok.ShouldBeTrue();

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Title).ShouldBe((LegCommander.PrReady, "TIA1's PR #71 is ready to merge"));
        card.Detail.ShouldNotBeNull().ShouldContain("Reviewed at standard depth, no fresh usage figures: the step is in; it is done.");
        card.Link.ShouldBe("https://github.com/x/tianwen/pull/71");

        // The Merge choice says where the PR's checks stand, as they change, and nothing else about the card does.
        var checks = new PrChecks(_state, _github, TimeProvider.System);
        _github.Checks = ChoiceStatus.Pending;
        await checks.LookAsync(Ct);
        Open().Single().Choices.Single().Status.ShouldBe(ChoiceStatus.Pending);
        _github.Checks = ChoiceStatus.Passing;
        await checks.LookAsync(Ct);
        var green = Open().Single();
        (green.Id, green.Choices.Single().Status, green.Choices.Single().Args.Single()).ShouldBe((card.Id, ChoiceStatus.Passing, card.Choices.Single().Args.Single()));
    }

    [Fact]
    public async Task APrMergedOnGitHubItselfClosesItsCardAndLandsItsFlight()
    {
        var flight = await NewFlight("3", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA3"), "controller");
        (await Run(Verbs.Review, "flight/TIA3", Reviewer("TIA3"), "pass", "fine")).Ok.ShouldBeTrue();
        var card = Open().ShouldHaveSingleItem();

        // Merged by a person on GitHub, not through the card: the next look sees it and lands the flight (TIA1658's #1780).
        _github.Pr = _github.Pr! with { State = "MERGED" };
        await new PrChecks(_state, _github, TimeProvider.System, _flights).LookAsync(Ct);

        Open().ShouldHaveSingleItem().Title.ShouldBe("TIA3 landed");
        _state.Cards().Single(c => c.Id == card.Id).Outcome.ShouldBe("merged on GitHub as #71");
        F("TIA3").State.ShouldBe(FlightState.Landed);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task APrThatLeftTheMergeQueueUnmergedIsACardSayingWhyWithMergeAgain()
    {
        var flight = await NewFlight("20", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA20"), "controller");
        (await Run(Verbs.Review, "flight/TIA20", Reviewer("TIA20"), "pass", "fine")).Ok.ShouldBeTrue();
        // Merged through the card, which closes then: on a base with a queue that only queued it.
        var ready = Open().ShouldHaveSingleItem();
        _state.CloseCard(ready.Id, "done", "controller").ShouldBeTrue();
        var head = _github.Pr!.Head;
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights);

        // While it waits, the flight says where; nothing is carded.
        _github.Queue = new MergeQueueState("OPEN", head, Queued: true, Position: 2);
        await checks.LookAsync(Ct);
        F("TIA20").MergeQueue.ShouldBe("in the merge queue, position 2");
        Open().ShouldBeEmpty();

        // The queue's checks failed: out of the queue, still open, and a card says so with the merge again.
        _github.Queue = new MergeQueueState("OPEN", head, Queued: false, RemovedAt: DateTimeOffset.UtcNow.AddSeconds(-5), RemovedReason: "failed checks");
        await checks.LookAsync(Ct);
        F("TIA20").MergeQueue.ShouldBeNull();
        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Bin, card.Title).ShouldBe((PrChecks.QueueDropped, CardBin.ForYou, "TIA20's PR #71 left the merge queue unmerged"));
        card.Detail.ShouldNotBeNull().ShouldContain("failed checks");
        card.Link.ShouldBe("https://github.com/x/tianwen/pull/71");
        var merge = card.Choices.ShouldHaveSingleItem();
        (merge.Verb, merge.Target, merge.Args.Single()).ShouldBe((Verbs.Merge, "flight/TIA20", head));

        // Once a removal: the next look, or a dismissal, raises it no second time.
        await checks.LookAsync(Ct);
        Open().ShouldHaveSingleItem().Id.ShouldBe(card.Id);
        _state.CloseCard(card.Id, "dismissed", "gui").ShouldBeTrue();
        await checks.LookAsync(Ct);
        Open().ShouldBeEmpty();

        // Queued again and dropped again: a new removal, a new card, closed by itself once it is back in the queue.
        _github.Queue = new MergeQueueState("OPEN", head, Queued: false, RemovedAt: DateTimeOffset.UtcNow.AddSeconds(1), RemovedReason: "merge conflict");
        await checks.LookAsync(Ct);
        var again = Open().ShouldHaveSingleItem();
        again.Detail.ShouldNotBeNull().ShouldContain("merge conflict");
        _github.Queue = new MergeQueueState("OPEN", head, Queued: true, Position: 1);
        await checks.LookAsync(Ct);
        Open().ShouldBeEmpty();
        _state.Cards().Single(c => c.Id == again.Id).Outcome.ShouldBe("queued again");
        F("TIA20").MergeQueue.ShouldBe("in the merge queue, position 1");
    }

    private static string[] PilotLabels(HashSet<string>? labels) => [.. (labels ?? []).Where(l => l.StartsWith("pilot: ", StringComparison.Ordinal))];

    /// <summary>A flight whose done leg's PR passed its review, so its marks are set and its ready card open.</summary>
    private async Task<(FlightRow Flight, int Pr)> Passed(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        (await Run(Verbs.Review, $"flight/{flight.Code}", Reviewer(flight.Code), "pass", "fine")).Ok.ShouldBeTrue();
        return (F(flight.Code), _github.Pr!.Number);
    }

    [Fact]
    public async Task AQueuedPrIsLabelledQueuedAndItsRefusedLabelIsSetAtTheNextLookClosingItsCardThenPassedAgainOnceItLeaves()
    {
        var (flight, number) = await Passed("31");
        _state.CloseCard(Open().Single(c => c.Kind == LegCommander.PrReady).Id, "done", "controller").ShouldBeTrue();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        PilotLabels(_github.Labels[number]).ShouldBe(["pilot: passed"]);

        // Joining the queue is a label, and GitHub refusing it is a card, not a failed look.
        _github.Queue = new MergeQueueState("OPEN", _github.Pr!.Head, Queued: true, Position: 2);
        _github.LabelFails = "GitHub refused the label: rate limited";
        await checks.LookAsync(Ct);
        var refused = Open().Single(c => c.Kind == PrMarks.RefusedKind);
        refused.Detail.ShouldNotBeNull().ShouldContain("pilot: queued");
        F(flight.Code).Review!.LabelOverride.ShouldBe("pilot: queued");
        PilotLabels(_github.Labels[number]).ShouldBe(["pilot: passed"]);

        // Still refused: tried again at the look, not carded twice.
        await checks.LookAsync(Ct);
        Open().Count(c => c.Kind == PrMarks.RefusedKind).ShouldBe(1);

        _github.LabelFails = null;
        await checks.LookAsync(Ct);
        PilotLabels(_github.Labels[number]).ShouldBe(["pilot: queued"]);
        Open().ShouldNotContain(c => c.Kind == PrMarks.RefusedKind);
        F(flight.Code).Review!.MarksWhy.ShouldBeNull();
        _github.Statuses.Last().State.ShouldBe("success");

        // Out of the queue (dropped, and carded for that): the review's own label again.
        _github.Queue = new MergeQueueState("OPEN", _github.Pr!.Head, Queued: false);
        await checks.LookAsync(Ct);
        PilotLabels(_github.Labels[number]).ShouldBe(["pilot: passed"]);
        F(flight.Code).Review!.LabelOverride.ShouldBeNull();
    }

    [Fact]
    public async Task AConflictingPrIsLabelledConflictsWhateverItsReviewSaidAndPassedAgainOnceGitHubSaysItMerges()
    {
        var flight = await NewFlight("32", "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA32"), "controller");
        (await Run(Verbs.Review, "flight/TIA32", Reviewer("TIA32"), "pass", "fine")).Ok.ShouldBeTrue();
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", "README on main");
        Git(Repo, "push", "-q", "origin", "main");
        var number = _github.Pr!.Number;
        _github.Pr = _github.Pr with { Mergeable = "CONFLICTING" };
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);

        // Sent back as changes for the conflict, and the label says the conflict, not the changes.
        F("TIA32").Review!.State.ShouldBe(ReviewState.Changes);
        PilotLabels(_github.Labels[number]).ShouldBe(["pilot: conflicts"]);
        _github.Statuses.Last().State.ShouldBe("failure");

        // GitHub working it out again after a push does not lift it; its saying MERGEABLE does.
        _github.Pr = _github.Pr! with { Mergeable = "UNKNOWN" };
        await checks.LookAsync(Ct);
        PilotLabels(_github.Labels[number]).ShouldBe(["pilot: conflicts"]);
        _github.Pr = _github.Pr! with { Mergeable = "MERGEABLE" };
        await checks.LookAsync(Ct);
        PilotLabels(_github.Labels[number]).ShouldBe(["pilot: changes"]);
    }

    [Fact]
    public async Task TheLookKeepsTheHeadAndPilotLabelGitHubHasOnTheFlightAndWhereTheyDisagreeWithTheNode()
    {
        var (flight, number) = await Passed("33");
        var reviewed = F(flight.Code).Review!.ReviewedHead.ShouldNotBeNull();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);

        var agreed = F(flight.Code);
        (agreed.GitHubHead, agreed.GitHubLabel, agreed.GitHubDisagrees).ShouldBe((reviewed, "pilot: passed", null));

        // The label taken off by hand, and the branch pushed to since the reviewer read it.
        _github.Labels[number].Remove("pilot: passed");
        _github.Pr = _github.Pr! with { Head = "beef1111beef1111" };
        await checks.LookAsync(Ct);

        var drifted = F(flight.Code);
        (drifted.GitHubHead, drifted.GitHubLabel).ShouldBe(("beef1111beef1111", null));
        drifted.GitHubDisagrees.ShouldBe($"pilot: passed was removed on GitHub; the head moved to beef1111 since the review read {reviewed[..8]}");
        // Recorded, not put right: the node's own marks stand as it set them.
        (drifted.Review!.Label, drifted.Review.StatusState).ShouldBe(("pilot: passed", "success"));
        _github.LabelCalls.Count(c => c.Number == number).ShouldBe(2);

        // Changed by hand to another pilot: label, then put back and the branch back: they agree again.
        _github.Labels[number].Add("pilot: changes");
        await checks.LookAsync(Ct);
        F(flight.Code).GitHubDisagrees.ShouldNotBeNull().ShouldContain("pilot: passed is pilot: changes on GitHub");
        _github.Labels[number].Clear();
        _github.Labels[number].Add("pilot: passed");
        _github.Pr = _github.Pr! with { Head = reviewed };
        await checks.LookAsync(Ct);
        var again = F(flight.Code);
        (again.GitHubHead, again.GitHubLabel, again.GitHubDisagrees).ShouldBe((reviewed, "pilot: passed", null));
    }

    [Fact]
    public async Task APrThatIsNotOpenIsNotNoted()
    {
        var (flight, _) = await Passed("34");
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);
        _github.Pr = _github.Pr! with { State = "CLOSED" };
        _github.Labels[_github.Pr.Number].Clear();

        await checks.LookAsync(Ct);

        (F(flight.Code).GitHubHead, F(flight.Code).GitHubLabel, F(flight.Code).GitHubDisagrees).ShouldBe((null, null, null));
    }

    [Fact]
    public async Task APrWithItsReadyCardStillOpenOrABaseWithNoQueueIsNoQueueCard()
    {
        var flight = await NewFlight("21", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA21"), "controller");
        (await Run(Verbs.Review, "flight/TIA21", Reviewer("TIA21"), "pass", "fine")).Ok.ShouldBeTrue();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights);

        // Taken out of the queue while its PR-ready card is open: that card offers the merge already.
        _github.Queue = new MergeQueueState("OPEN", _github.Pr!.Head, Queued: false, RemovedAt: DateTimeOffset.UtcNow, RemovedReason: "manually removed");
        await checks.LookAsync(Ct);
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegCommander.PrReady);

        // A base with no queue: nothing to say, and the flight says no queue.
        _github.Queue = null;
        _state.CloseCard(Open().Single().Id, "done", "controller").ShouldBeTrue();
        await checks.LookAsync(Ct);
        Open().ShouldBeEmpty();
        F("TIA21").MergeQueue.ShouldBeNull();
    }

    [Fact]
    public async Task AFlightWhosePrMergedWithNoCardOpenLandsAllTheSame()
    {
        var flight = await NewFlight("5", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA5"), "controller");
        (await Run(Verbs.Review, "flight/TIA5", Reviewer("TIA5"), "pass", "fine")).Ok.ShouldBeTrue();
        // Its card dismissed after a rebase by hand moved its head, then merged on GitHub.
        (await Run(Verbs.Dismiss, $"card/{Open().ShouldHaveSingleItem().Id}", "controller", "rebased by hand")).Ok.ShouldBeTrue();
        _github.Pr = _github.Pr! with { State = "MERGED" };

        await new PrChecks(_state, _github, TimeProvider.System, _flights).LookAsync(Ct);

        F("TIA5").State.ShouldBe(FlightState.Landed);
        Open().ShouldHaveSingleItem().Title.ShouldBe("TIA5 landed");
    }

    [Fact]
    public async Task APlaneIsCutFromTheRemotesDefaultEvenWithoutOriginHeadOrOnAFeatureBranch()
    {
        // The clone has no origin/HEAD and its checkout is on a feature branch: the plane still comes from origin/main.
        Git(Repo, "remote", "set-head", "origin", "--delete");
        Git(Repo, "switch", "-q", "-c", "feat/elsewhere");
        var flight = await NewFlight("4", "parser");

        flight.Base.ShouldBe("origin/main");
        Git(Repo, "symbolic-ref", "--short", "refs/remotes/origin/HEAD").Trim().ShouldBe("origin/main");
    }

    [Fact]
    public async Task AFlightWhoseBaseIsNoBranchOnTheRemoteIsRebasedOntoItsDefaultWhenItsPrOpens()
    {
        // Cut, as PIL126 was, from a feature branch that has since landed on main and gone from the remote.
        Git(Repo, "switch", "-q", "-c", "feat/landed");
        File.WriteAllText(Path.Combine(Repo, "feature.txt"), "landed\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "the feature");
        Git(Repo, "switch", "-q", "main");
        Git(Repo, "cherry-pick", "feat/landed");
        Git(Repo, "push", "-q", "origin", "main");
        var flight = await NewFlight("5", "parser");
        Git(flight.Worktree, "reset", "-q", "--hard", "feat/landed");
        _state.PutFlight(F("TIA5") with { Base = "feat/landed" });
        await Fly(F("TIA5"), "parser");

        (await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA5"), "controller")).Error.ShouldBeNull();

        F("TIA5").Base.ShouldBe("origin/main");
        _github.Created.Last().Base.ShouldBe("main");
        // The feature's commit is on main already, so only the leg's own commit is the PR's.
        Git(F("TIA5").Worktree, "rev-list", "--count", "origin/main..HEAD").Trim().ShouldBe("1");
    }

    [Fact]
    public async Task APassedPrThatConflictsWithItsBaseIsRebasedAndReviewedAfresh()
    {
        var flight = await NewFlight("6", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA6"), "controller");
        (await Run(Verbs.Review, "flight/TIA6", Reviewer("TIA6"), "pass", "fine")).Ok.ShouldBeTrue();
        // main moves on after the review.
        File.WriteAllText(Path.Combine(Repo, "later.txt"), "later\n");
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-q", "-m", "later on main");
        Git(Repo, "push", "-q", "origin", "main");

        (await _legs.RebaseOntoBaseAsync("TIA6", Ct)).ShouldBeNull();

        Git(F("TIA6").Worktree, "merge-base", "--is-ancestor", "origin/main", "HEAD");
        F("TIA6").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task APassedPrWhoseChecksFailIsSentBackAsChangesOnceAHeadSoItsDoneLegGoesAround()
    {
        var flight = await NewFlight("7", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA7"), "controller");
        (await Run(Verbs.Review, "flight/TIA7", Reviewer("TIA7"), "pass", "fine")).Ok.ShouldBeTrue();
        _github.Checks = ChoiceStatus.Failing;
        var checks = new PrChecks(_state, _github, TimeProvider.System, legs: _legs);

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        F("TIA7").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        _github.DraftCalls.ShouldContain((_github.Pr!.Number, true));
        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Title).ShouldBe((LegCommander.ReviewChanges, "TIA7's review asks for changes"));
        card.Detail.ShouldNotBeNull().ShouldStartWith("the PR's checks failed on ");
        // The ring's go-around is the way round for the done leg, as for a review's changes.
        (await Run(Verbs.GoAround, "flight/TIA7/parser", "controller", "fix the checks")).Error.ShouldBeNull();
    }

    private void Window(double used, TimeSpan resetsIn) =>
        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = used, ResetsAt = DateTimeOffset.UtcNow + resetsIn } });

    [Fact]
    public async Task AReviewerOverASpentWindowWaitsSaysUntilWhenAndStartsByItselfOnceItHasReset()
    {
        var flight = await NewFlight("8", "parser");
        await Fly(flight, "parser");
        // Set once the leg is done: with the node's own advance on, finishing it would open the PR before the window is spent.
        _legs.Models = new RoleModels(null);
        Window(95, TimeSpan.FromHours(1));

        (await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA8"), "controller")).Error.ShouldBeNull();

        // The PR is open, and no reviewer yet: the review is kept as none, so nothing reads as reviewed.
        var startedBefore = _workers.Started.Count;
        F("TIA8").PullRequest.ShouldNotBeNull();
        F("TIA8").Review.ShouldBeNull();
        var waits = _state.Cards().Where(c => c.Kind == LegCommander.NextStepKind).ShouldHaveSingleItem();
        (waits.Bin, waits.Subject).ShouldBe((CardBin.Handled, "flight/TIA8"));
        waits.Title.ShouldStartWith("TIA8 waits to start its reviewer: the 5-hour window is 95 % spent");
        (await Run(Verbs.Merge, "flight/TIA8", "controller", "cafe0000")).Error.ShouldBe("TIA8 has not been reviewed; open its PR with the node (flight pr), which starts the review");

        // The sweep says nothing more while it holds.
        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.Count.ShouldBe(startedBefore);
        _state.Cards().Count(c => c.Kind == LegCommander.NextStepKind).ShouldBe(1);

        Window(4, TimeSpan.FromHours(6));
        await _legs.AdvanceAllAsync(Ct);

        var review = F("TIA8").Review.ShouldNotBeNull();
        (review.State, review.Reviewer).ShouldBe((ReviewState.Reviewing, Reviewer("TIA8")));
        _workers.Started.Last().Name.ShouldBe(Reviewer("TIA8"));
        _workers.Started.Count.ShouldBe(startedBefore + 1);
    }

    [Fact]
    public async Task AReviewerOfAPriorityReposFlightUsesItsOwnLimit()
    {
        var flight = await NewFlight("9", "parser");
        await Fly(flight, "parser");
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), "priority-repos", [flight.Repo]).Settings);
        Window(95, TimeSpan.FromHours(1));

        (await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA9"), "controller")).Error.ShouldBeNull();

        F("TIA9").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        Window(99, TimeSpan.FromHours(1));
        (await Run(Verbs.StartReview, Verbs.FlightTarget("TIA9"), "controller")).Error.ShouldBeNull();
        F("TIA9").Review.ShouldBeNull();
    }

    [Fact]
    public async Task APassedPrRebasedOverASpentWindowIsReviewedAfreshOnlyOnceItHasReset()
    {
        var flight = await NewFlight("10", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA10"), "controller");
        (await Run(Verbs.Review, "flight/TIA10", Reviewer("TIA10"), "pass", "fine")).Ok.ShouldBeTrue();
        _legs.Models = new RoleModels(null);
        File.WriteAllText(Path.Combine(Repo, "later.txt"), "later\n");
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-q", "-m", "later on main");
        Git(Repo, "push", "-q", "origin", "main");
        Window(95, TimeSpan.FromHours(1));
        var startedBefore = _workers.Started.Count;

        (await _legs.RebaseOntoBaseAsync("TIA10", Ct)).ShouldBeNull();

        // Rebased and pushed, its old pass is of a head that is gone: no review stands, so no merge goes by it.
        Git(F("TIA10").Worktree, "merge-base", "--is-ancestor", "origin/main", "HEAD");
        F("TIA10").Review.ShouldBeNull();
        _workers.Started.Count.ShouldBe(startedBefore);
        Open().ShouldBeEmpty();
        (await Run(Verbs.Merge, "flight/TIA10", "controller", "cafe0000")).Error.ShouldNotBeNull().ShouldContain("has not been reviewed");

        Window(4, TimeSpan.FromHours(6));
        await _legs.AdvanceAllAsync(Ct);

        F("TIA10").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
    }

    [Fact]
    public async Task ARebaseThatConflictsIsUndoneAndSaysSo()
    {
        var flight = await NewFlight("7", "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", "README on main");
        Git(Repo, "push", "-q", "origin", "main");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");

        (await _legs.RebaseOntoBaseAsync("TIA7", Ct)).ShouldNotBeNull().ShouldContain("conflicts");

        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(head);
        Git(flight.Worktree, "status", "--porcelain").ShouldBeEmpty();
    }

    [Fact]
    public async Task APassedPrWhoseRebaseConflictsIsSentBackAsChangesSoItsDoneLegGoesAround()
    {
        var flight = await NewFlight("8", "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA8"), "controller");
        (await Run(Verbs.Review, "flight/TIA8", Reviewer("TIA8"), "pass", "fine")).Ok.ShouldBeTrue();
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", "README on main");
        Git(Repo, "push", "-q", "origin", "main");
        _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };

        await new PrChecks(_state, _github, TimeProvider.System, legs: _legs).LookAsync(Ct);

        F("TIA8").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        var card = Open().ShouldHaveSingleItem();
        card.Kind.ShouldBe(LegCommander.ReviewChanges);
        card.Detail.ShouldNotBeNull().ShouldStartWith("the PR conflicts with its base");
        (await Run(Verbs.GoAround, "flight/TIA8/parser", "controller", "resolve the conflicts")).Error.ShouldBeNull();
    }

    [Fact]
    public async Task ChangesRingWithAGoAroundWhoseFinishIsReviewedAfreshOrReadyAnyway()
    {
        var flight = await NewFlight("2", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA2"), "controller");

        (await Run(Verbs.Review, "flight/TIA2", Reviewer("TIA2"), "changes", "the parser drops the last line")).Ok.ShouldBeTrue();

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Detail).ShouldBe((LegCommander.ReviewChanges, "the parser drops the last line"));
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Go around parser", Verbs.GoAround), ("Ready anyway", Verbs.Review)]);

        // The go-around reopens the done leg with the review's words; its finish pushes and starts a fresh review.
        var goAround = card.Choices[0];
        (await Run(goAround.Verb, goAround.Target, "controller", goAround.Args)).Error.ShouldBeNull();
        _workers.Started.Last().Brief.ShouldContain("the review asked for changes: the parser drops the last line");
        Open().ShouldBeEmpty();
        var reviewsBefore = _workers.Started.Count(s => s.Name == Reviewer("TIA2"));

        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "keep the last line");
        var worker = _state.Legs("TIA2").Single().Worker!;
        (await Run(Verbs.FinishLeg, "flight/TIA2/parser", worker, "done", "fixed")).Error.ShouldBeNull();

        _workers.Started.Count(s => s.Name == Reviewer("TIA2")).ShouldBe(reviewsBefore + 1);
        F("TIA2").Review!.State.ShouldBe(ReviewState.Reviewing);
        Git(Repo, "ls-remote", "origin", "flight/tia2").ShouldContain(Git(flight.Worktree, "rev-parse", "HEAD"));

        // Asked for changes again, the controller may pass it anyway.
        await Run(Verbs.Review, "flight/TIA2", Reviewer("TIA2"), "changes", "naming");
        (await Run(Verbs.Review, "flight/TIA2", "controller", "pass", "ready anyway")).Ok.ShouldBeTrue();
        F("TIA2").Review!.Report.ShouldBe("ready anyway (passed by controller over: naming)");
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegCommander.PrReady);
    }

    [Fact]
    public async Task AReviewThatAsksForChangesLeavesItsPrADraftAndAPassMakesItReadyBeforeTheMergeCard()
    {
        var flight = await NewFlight("4", "parser");
        await Fly(flight, "parser");

        // The PR opens ready, as the review runs on it: nothing is marked until a verdict.
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA4"), "controller");
        _github.DraftCalls.ShouldBeEmpty();

        await Run(Verbs.Review, "flight/TIA4", Reviewer("TIA4"), "changes", "the parser drops the last line");
        _github.DraftCalls.ShouldBe([(71, true)]);

        // A pass of a later round, or the controller's "ready anyway", makes it ready, and only then is the merge offered.
        await Run(Verbs.Review, "flight/TIA4", "controller", "pass", "ready anyway");
        _github.DraftCalls.ShouldBe([(71, true), (71, false)]);
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegCommander.PrReady);
    }

    [Fact]
    public async Task APassIsKeptOnlyOnceItsPrIsReady()
    {
        var flight = await NewFlight("5", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA5"), "controller");

        // gh could not say: the review stays on, no merge card is raised, and the reviewer may say it again.
        _github.DraftFails = "gh could not mark #71 ready for review: no network";
        (await Run(Verbs.Review, "flight/TIA5", Reviewer("TIA5"), "pass", "fine")).Error.ShouldNotBeNull().ShouldContain("no network");
        F("TIA5").Review!.State.ShouldBe(ReviewState.Reviewing);
        Open().ShouldBeEmpty();

        _github.DraftFails = null;
        (await Run(Verbs.Review, "flight/TIA5", Reviewer("TIA5"), "pass", "fine")).Ok.ShouldBeTrue();
        _github.DraftCalls.ShouldBe([(71, false)]);
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegCommander.PrReady);
    }

    [Fact]
    public async Task ALegCannotBeNamedReviewAndAReviewerIsNoWorker()
    {
        await NewFlight("3", "parser");
        (await Run(Verbs.PlanLeg, "flight/TIA3", "controller", "name=review", "goal=g", "done-when=d")).Error.ShouldNotBeNull().ShouldStartWith("review names the flight's reviewer");
        Pilot.Node.Sessions.SessionRoles.Resolve("du1-tia3-review").ShouldBe(new Pilot.Node.Sessions.SessionRoleInfo(SessionRole.Reviewer, "TIA3", null, null));
    }

    [Fact]
    public async Task TwoConnectedFlightsLandWithNoPilotOfTheirOwn()
    {
        var first = await NewFlight("10", "core");
        var second = await NewFlight("11", "ui");
        (await Run(Verbs.Connect, Verbs.FlightTarget(second.Code), "controller", first.Code)).Ok.ShouldBeTrue();
        F(second.Code).State.ShouldBe(FlightState.Held);
        (await Run(Verbs.StartLeg, Verbs.LegTarget(second.Code, "ui"), "controller")).Error.ShouldNotBeNull().ShouldContain("held");

        foreach (var (flight, leg) in new[] { (first, "core"), (second, "ui") })
        {
            F(flight.Code).State.ShouldBe(FlightState.Flying);
            await Fly(flight, leg);
            _github.Pr = null;
            (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
            (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "checked")).Ok.ShouldBeTrue();

            // The controller answers the PR-ready card: the merge, on the head reviewed, lands the flight.
            var card = Open().Single(c => c.Kind == LegCommander.PrReady);
            var merge = card.Choices.Single();
            (await Run(merge.Verb, merge.Target, "controller", merge.Args)).Error.ShouldBeNull();
            await _flights.LandAfterMergeAsync(flight.Code, merge.Args[0], Ct);
            F(flight.Code).State.ShouldBe(FlightState.Landed);
            // The landed card opens its PR, as the merge card did.
            _state.Cards().First(c => c.Kind == "landed" && c.Subject == Verbs.FlightTarget(flight.Code)).Link.ShouldNotBeNull().ShouldBe(F(flight.Code).PullRequestUrl);
        }

        // Landing stopped every session the flight had, so none still held its plane.
        _workers.Stopped.ShouldContain("job00001");
        _workers.Stopped.ShouldContain("job00002");

        // Every session the node started was a leg's worker or a flight's reviewer: no pilot per flight.
        _workers.Started.Select(s => s.Name).ShouldBe(["du1-tia10-core", Reviewer("TIA10"), "du1-tia11-ui", Reviewer("TIA11")]);
        _github.Merged.Count.ShouldBe(2);
    }
}
