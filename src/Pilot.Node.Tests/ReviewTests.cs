using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Reviews applied by the node, and phase 3's done-when: two flights, one connected to the other, flown and landed by
/// the node's own machinery (fresh workers, the review, the cards) with no per-flight pilot session anywhere.
/// </summary>
public abstract class ReviewTestsBase : IDisposable
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    private protected readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private protected readonly FakeWorkers _workers = new();
    private protected readonly FakeGitHub _github = new();
    private protected readonly FlightCommander _flights;
    private protected readonly LegCommander _legs;

    protected ReviewTestsBase()
    {
        // A repo with a bare origin, so a flight's push is a real one: the seed, built once a run and copied.
        SeedRepo.Make(_root);
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System, _workers, _github);
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

    private protected async Task<CommandResult> Run(string verb, string target, string who, params string[] args)
    {
        if (verb is Verbs.CreateFlight or Verbs.Connect or Verbs.Land)
        {
            return await _flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);
        }
        var result = await _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);
        if (verb == Verbs.OpenPr && result.Ok)
        {
            await Started(_legs, target[(target.IndexOf('/') + 1)..]);
        }
        return result;
    }

    /// <summary>
    /// Waits until the flight's sessions the command began are recorded: open-pr answers "started" once its reviewer is reserved, and
    /// the review is on the flight only after the reviewer is up, which a loaded machine does not do within the command.
    /// </summary>
    private protected static async Task Started(LegCommander legs, string code)
    {
        var clock = Stopwatch.StartNew();
        while (legs.Starting(code))
        {
            clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), $"{code}'s reviewer was not recorded");
            await Task.Delay(10, Ct);
        }
    }

    private protected FlightRow F(string code) => _state.Flights().Single(f => f.Code == code);

    private protected CardRow[] Open() => _state.Cards(openOnly: true);

    private protected async Task<FlightRow> NewFlight(string issue, string leg)
    {
        (await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "controller", issue, "Flight " + issue)).Ok.ShouldBeTrue();
        var flight = _state.Flights().OrderByDescending(f => f.CreatedAt).First();
        (await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", $"name={leg}", "goal=Do it", "done-when=It is done", "item=The step")).Ok.ShouldBeTrue();
        return flight;
    }

    /// <summary>A leg flown as a worker flies it: started by the node, a commit in the plane, its step ticked, finished by the worker.</summary>
    private protected async Task Fly(FlightRow flight, string leg, int lines = 1)
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

    private protected string Reviewer(string code) => $"du1-{code.ToLowerInvariant()}-review";

    private protected void Usage(double fiveHour, double sevenDay = 10) =>
        _state.PutUsage(new UsageRow
        {
            ReadAt = DateTimeOffset.UtcNow,
            FiveHour = new UsageWindow { UsedPercent = fiveHour, ResetsAt = DateTimeOffset.UtcNow.AddHours(2) },
            SevenDay = new UsageWindow { UsedPercent = sevenDay, ResetsAt = DateTimeOffset.UtcNow.AddDays(3) },
        });

    private protected async Task<WorkerStart> Review(string issue, int lines, double fiveHour, bool priority = false, Dictionary<string, string>? roles = null)
    {
        Usage(fiveHour);
        var flight = await NewFlight(issue, "parser");
        _legs.Models = new RoleModels(new NodeSettings { PriorityRepos = priority ? [flight.Repo] : null, Roles = roles });
        await Fly(flight, "parser", lines);
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
        return _workers.Started.Last();
    }

    private protected static string[] PilotLabels(HashSet<string>? labels) => [.. (labels ?? []).Where(l => l.StartsWith("pilot: ", StringComparison.Ordinal))];

    /// <summary>A flight whose done leg's PR passed its review, so its marks are set and its ready card open.</summary>
    private protected async Task<(FlightRow Flight, int Pr)> Passed(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        (await Run(Verbs.Review, $"flight/{flight.Code}", Reviewer(flight.Code), "pass", "fine")).Ok.ShouldBeTrue();
        return (F(flight.Code), _github.Pr!.Number);
    }

    private protected void Window(double used, TimeSpan resetsIn) =>
        _state.PutUsage(new UsageRow { ReadAt = DateTimeOffset.UtcNow, FiveHour = new UsageWindow { UsedPercent = used, ResetsAt = DateTimeOffset.UtcNow + resetsIn } });

    // A flight with its PR passed and ready, then armed natively on its head while the checks run.
    private protected async Task<(FlightRow Flight, PullRequest Pr, PrChecks Checks)> ArmedPassedFlight(string issue)
    {
        var flight = await NewFlight(issue, "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller");
        (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "fine")).Ok.ShouldBeTrue();
        var pr = _github.Pr.ShouldNotBeNull();
        _github.AutoMergeAllowed = true;
        _github.Summary = new ChecksSummary(ChoiceStatus.Pending, 1, 3);
        (await Run(Verbs.Merge, Verbs.FlightTarget(flight.Code), "controller", pr.Head)).Error.ShouldBeNull();
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull().ShouldContain(LegCommander.ArmedByGitHub);
        _github.AutoMergeArmed.ShouldHaveSingleItem();
        return (F(flight.Code), pr, new PrChecks(_state, _github, TimeProvider.System, legs: _legs));
    }
}

/// <summary>Part 1 of ReviewTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class ReviewTests1 : ReviewTestsBase
{
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
        // Its issue is closed with it, whatever GitHub made of the PR's Closes line.
        _github.ClosedIssues.ShouldBe([(3, "Landed in #71 (TIA3).")]);
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

    [Fact]
    public async Task OnAutopilotTheNodeMergesAPassedFlightItselfOnceItsChecksAreNotFailingAndOnlyOnce()
    {
        // The watchtower may never merge, so the night's passed PRs sat for seven hours with the autopilot on (SE1, 2026-10-26).
        var (flight, pr) = await Passed("30");
        var ready = Open().Single(c => c.Kind == LegCommander.PrReady);
        _github.Checks = ChoiceStatus.Passing;
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        // Off: the merge is the person's, as ever.
        await checks.LookAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        Open().ShouldContain(c => c.Id == ready.Id);

        // On: the node merges it from its card, closes the card and says so.
        var on = NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!;
        _legs.Models = new RoleModels(on);
        await checks.LookAsync(Ct);
        _github.Merged.ShouldHaveSingleItem().Item1.ShouldBe(pr);
        Open().ShouldNotContain(c => c.Id == ready.Id);
        _state.Cards().ShouldContain(c => c.Kind == CardKinds.AutopilotRan && c.Subject == $"flight/{flight.Code}");

        // Once a head, not every look.
        await checks.LookAsync(Ct);
        _github.Merged.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OnAutopilotAFailingPrIsNotMerged()
    {
        var (_, _) = await Passed("31");
        _github.Checks = ChoiceStatus.Failing;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);

        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnAutopilotFailedChecksRunAgainOnceAHeadBeforeTheLegIsSentRound()
    {
        // A test that fails by chance is not a go-around: the node runs the failed runs again first (2026-10-27).
        var (flight, pr) = await Passed("33");
        _github.Checks = ChoiceStatus.Pending;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var checks = new PrChecks(_state, _github, time, _flights, _legs) { RerunFirst = true };
        await checks.LookAsync(Ct);
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull();

        _github.Checks = ChoiceStatus.Failing;
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        // Run again once, the merge still armed, the review still passed, a Handled line saying so.
        _github.Reruns.ShouldBe([pr]);
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull();
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        _state.Cards().ShouldContain(c => c.Kind == "checks-rerun" && c.Bin == CardBin.Handled);

        // Still failing once the runs had their time: now the leg is sent round, and nothing is run again.
        time.Advance(checks.RerunGrace + TimeSpan.FromMinutes(1));
        await checks.LookAsync(Ct);
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        _github.Reruns.ShouldBe([pr]);
    }

    [Fact]
    public async Task AFailedCheckUnderGitHubsAutoMergeRunsAgainWithTheAutoMergeLeftArmed()
    {
        // Armed through GitHub's own auto-merge: the rerun leaves it armed, so a check green on its second run merges.
        var (flight, pr) = await Passed("34");
        _github.AutoMergeAllowed = true;
        _github.Checks = ChoiceStatus.Pending;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
        var checks = new PrChecks(_state, _github, new ManualTime(DateTimeOffset.UtcNow), _flights, _legs) { RerunFirst = true };
        await checks.LookAsync(Ct);
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull();

        _github.Checks = ChoiceStatus.Failing;
        await checks.LookAsync(Ct);

        _github.Reruns.ShouldBe([pr]);
        _github.AutoMergeDisarmed.ShouldBeEmpty();
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull();
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
    }

    [Fact]
    public async Task OnAutopilotAMergeThatWaitedForChecksWhichThenFailSendsTheLegBackAsChangesNotOntoAForYouCard()
    {
        // With the autopilot off a failing PR is sent back from its PR-ready card; the merge the node armed itself must go round the same way.
        var (flight, pr) = await Passed("32");
        _github.Checks = ChoiceStatus.Pending;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        // Armed by the node: its PR-ready card closes and the merge waits for the checks.
        await checks.LookAsync(Ct);
        _github.Merged.ShouldBeEmpty();
        var armed = F(flight.Code).MergeWhenGreen.ShouldNotBeNull();
        (armed[0], armed[^1]).ShouldBe((_github.Pr!.Head, LegCommander.ArmedByAutopilot));
        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady);

        _github.Checks = ChoiceStatus.Failing;
        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        F(flight.Code).MergeWhenGreen.ShouldBeNull();
        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        _github.DraftCalls.ShouldContain((pr, true));
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.MergeDropped);
        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Title).ShouldBe((LegCommander.ReviewChanges, $"{flight.Code}'s review asks for changes"));
        card.Detail.ShouldNotBeNull().ShouldStartWith("the PR's checks failed on ");
        (await Run(Verbs.GoAround, $"flight/{flight.Code}/parser", "controller", "fix the checks")).Error.ShouldBeNull();
    }

    [Fact]
    public async Task AMergeThePersonPressedWhileChecksRanStillDropsOntoAForYouCardWhenTheyFailOnAutopilot()
    {
        var (flight, _) = await Passed("33");
        _github.Checks = ChoiceStatus.Pending;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
        (await Run(Verbs.Merge, $"flight/{flight.Code}", "controller", _github.Pr!.Head[..8])).Ok.ShouldBeTrue();
        F(flight.Code).MergeWhenGreen.ShouldNotBeNull().ShouldNotContain(LegCommander.ArmedByAutopilot);
        // Choosing Merge on the card closes it.
        _state.CloseCard(Open().Single(c => c.Kind == LegCommander.PrReady).Id, "chose Merge", "controller").ShouldBeTrue();

        _github.Checks = ChoiceStatus.Failing;
        await new PrChecks(_state, _github, TimeProvider.System, _flights, _legs).LookAsync(Ct);

        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
        Open().ShouldContain(c => c.Kind == PrChecks.MergeDropped && c.Bin == CardBin.ForYou);
    }

    [Fact]
    public async Task AnAutopilotMergeRefusedRingsOnceAndItsCardClosesWhenTheFlightIsRebasedAndReviewedAfresh()
    {
        var (flight, _) = await Passed("34");
        var ready = Open().Single(c => c.Kind == LegCommander.PrReady);
        _github.Checks = ChoiceStatus.Passing;
        _legs.Models = new RoleModels(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings!);
        // The PR moved past the head the card offers, so the merge is refused as an unread head.
        _github.Pr = _github.Pr! with { Head = "beef0000beef0000" };
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        _github.Merged.ShouldBeEmpty();
        Open().Where(c => c.Kind == PrChecks.AutopilotMergeRefused).ShouldHaveSingleItem().Subject.ShouldBe($"flight/{flight.Code}");
        Open().ShouldContain(c => c.Id == ready.Id);

        // Main moved: the flight is rebased and reviewed afresh, and the refused merge's card, with its stale head, goes with its PR-ready card.
        _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };
        await checks.LookAsync(Ct);

        F(flight.Code).Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        Open().ShouldNotContain(c => c.Kind == LegCommander.PrReady || c.Kind == PrChecks.AutopilotMergeRefused);
    }

    [Fact]
    public async Task APrDroppedFromTheQueueOnAConflictIsRebasedAndReviewedAfreshNotOfferedMergeAgain()
    {
        // PIL207's #238 left the queue on merge_conflict and its card offered Merge, which could only be refused again (SE1, 2026-10-25).
        var flight = await NewFlight("21", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA21"), "controller");
        (await Run(Verbs.Review, "flight/TIA21", Reviewer("TIA21"), "pass", "fine")).Ok.ShouldBeTrue();
        _state.CloseCard(Open().ShouldHaveSingleItem().Id, "done", "controller").ShouldBeTrue();
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        var startedBefore = _workers.Started.Count;
        _github.Queue = new MergeQueueState("OPEN", _github.Pr!.Head, Queued: false, RemovedAt: DateTimeOffset.UtcNow.AddSeconds(-5), RemovedReason: "merge_conflict");
        await checks.LookAsync(Ct);

        Open().ShouldNotContain(c => c.Choices.Any(ch => ch.Verb == Verbs.Merge));
        // The Handled card is the rebase's success: a failed rebase rings with a card of the same words in a different bin and kind.
        var handled = _state.Cards().Where(c => c.Kind == PrChecks.QueueDropped && c.Subject == "flight/TIA21").ShouldHaveSingleItem();
        handled.Bin.ShouldBe(CardBin.Handled);
        _state.Cards().ShouldNotContain(c => c.Kind == "pr-conflicts");
        // Rebased onto its base and reviewed afresh: the old pass is of a head that is gone, and a review leg was started.
        Git(F("TIA21").Worktree, "merge-base", "--is-ancestor", "origin/main", "HEAD");
        F("TIA21").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        _workers.Started.Count.ShouldBe(startedBefore + 1);
        // Once a removal, as for any other: the rebase count, not only the card count, holds still.
        await checks.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(startedBefore + 1);
        _state.Cards().Count(c => c.Kind == PrChecks.QueueDropped && c.Subject == "flight/TIA21").ShouldBe(1);
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
        // GitHub has caught up with the push: the head the reviewer read in the plane.
        _github.Pr = _github.Pr! with { Head = reviewed };

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
    public async Task TheLookKeepsWhetherGitHubHasThePrAsADraftOnTheFlight()
    {
        var (flight, _) = await Passed("35");
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights, _legs);

        _github.Pr = _github.Pr! with { Draft = true };
        await checks.LookAsync(Ct);
        F(flight.Code).GitHubDraft.ShouldBe(true);

        // Made ready for review (by the node on a pass, or by hand): the next look says so.
        _github.Pr = _github.Pr! with { Draft = false };
        await checks.LookAsync(Ct);
        F(flight.Code).GitHubDraft.ShouldBe(false);
    }
}

/// <summary>Part 2 of ReviewTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class ReviewTests2 : ReviewTestsBase
{
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
        _github.CloseIssueFails = "HTTP 403";

        await new PrChecks(_state, _github, TimeProvider.System, _flights).LookAsync(Ct);

        F("TIA5").State.ShouldBe(FlightState.Landed);
        // An issue gh would not close is the person's, said once; the flight lands all the same.
        Open().Select(c => c.Title).ShouldBe(["TIA5 landed, but issue #5 is still open", "TIA5 landed"], ignoreOrder: true);
    }

    [Fact]
    public async Task AFlightWhosePrWasClosedWithNothingOfItsOwnLeftLandsAndOneWithWorkRings()
    {
        // PIL274: its work went in through another PR, a rebase left its branch empty, its PR was closed, and it stayed
        // flying while reviewers were started on the empty PR.
        var empty = await NewFlight("6", "parser");
        await Fly(empty, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA6"), "controller");
        Git(empty.Worktree, "reset", "-q", "--hard", "origin/main");
        _github.Pr = _github.Pr! with { State = "CLOSED" };
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights);

        await checks.LookAsync(Ct);

        F("TIA6").State.ShouldBe(FlightState.Landed);
        _state.Cards().ShouldContain(c => c.Kind == PrChecks.ClosedLanded && c.Subject == "flight/TIA6");
    }

    [Fact]
    public async Task AFlightWhosePrWasClosedWithWorkOfItsOwnRingsOnceAndStaysFlying()
    {
        var flight = await NewFlight("7", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA7"), "controller");
        _github.Pr = _github.Pr! with { State = "CLOSED" };
        var checks = new PrChecks(_state, _github, TimeProvider.System, _flights);

        await checks.LookAsync(Ct);
        await checks.LookAsync(Ct);

        F("TIA7").State.ShouldBe(FlightState.Flying);
        var ring = Open().Where(c => c.Kind == PrChecks.ClosedWithWork).ShouldHaveSingleItem();
        ring.Title.ShouldBe($"TIA7's PR #{F("TIA7").PullRequest} was closed without a merge");
        ring.Detail.ShouldNotBeNull().ShouldContain("not in its base");

        // Dismissed, it is the person's answer: the next look does not ring it again.
        _state.CloseCard(ring.Id, "known", "controller").ShouldBeTrue();
        await checks.LookAsync(Ct);
        Open().ShouldNotContain(c => c.Kind == PrChecks.ClosedWithWork);
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
    public async Task TheNodesOwnRebaseOfAnArmedPrLetsGoOfTheMergeAndGitHubsAutoMergeAndRaisesNoDroppedMergeCard()
    {
        var flight = await NewFlight("16", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA16"), "controller");
        (await Run(Verbs.Review, "flight/TIA16", Reviewer("TIA16"), "pass", "fine")).Ok.ShouldBeTrue();
        var pr = _github.Pr.ShouldNotBeNull();
        // Armed natively on the reviewed head, then main moves on.
        _state.PutFlight(F("TIA16") with { MergeWhenGreen = [pr.Head, "rebase", LegCommander.ArmedByGitHub] });
        _github.AutoMergeArmed.Add((pr.Number, pr.Head, "rebase"));
        File.WriteAllText(Path.Combine(Repo, "later16.txt"), "later\n");
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-q", "-m", "later on main");
        Git(Repo, "push", "-q", "origin", "main");

        (await _legs.RebaseOntoBaseAsync("TIA16", Ct)).ShouldBeNull();
        _github.Pr = pr with { Head = Git(F("TIA16").Worktree, "rev-parse", "HEAD") };
        await new PrChecks(_state, _github, TimeProvider.System, legs: _legs).LookAsync(Ct);

        F("TIA16").MergeWhenGreen.ShouldBeNull();
        _github.AutoMergeDisarmed.ShouldBe([pr.Number]);
        _state.Cards().Where(c => c.Kind == PrChecks.MergeDropped || c.Kind == LegCommander.MergeFailed).ShouldBeEmpty();
        F("TIA16").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Reviewing);
        _github.Merged.ShouldBeEmpty();
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

    [Fact]
    public async Task PastThreeReviewGoAroundsTheWatchtowerMayNotSendTheLegRoundAgainButThePersonMay()
    {
        // TIA1241 went round six times overnight on review findings the worker could not act on (SE1, 2026-10-26).
        var flight = await NewFlight("40", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA40"), "controller");
        var leg = _state.Legs("TIA40").Single();
        _state.PutLeg(leg with { GoArounds = LegCommander.MaxReviewGoArounds, ReviewGoArounds = LegCommander.MaxReviewGoArounds });
        (await Run(Verbs.Review, "flight/TIA40", Reviewer("TIA40"), "changes", "the PR's body closes the round's issue")).Ok.ShouldBeTrue();

        var card = Open().Single(c => c.Kind == LegCommander.ReviewChanges);
        card.Detail.ShouldNotBeNull().ShouldContain("this is yours to decide");
        // What the review asks comes first, where a card cut to its first lines still shows it (PIL294's #357).
        card.Detail.ShouldStartWith("the PR's body closes the round's issue");

        // The note is for the person: the go-around's args become the next worker's brief, which is told what the review asked.
        card.Choices.Single(c => c.Verb == Verbs.GoAround).Args.ShouldNotBeNull().ShouldAllBe(a => !a.Contains("yours to decide"));

        (await Run(Verbs.GoAround, "flight/TIA40/parser", "du1-watchtower", "the review asked for changes")).Error.ShouldNotBeNull().ShouldContain("the person's call");
        // Nor the node's own, as on a conflict (PIL338 went round seven times).
        (await Run(Verbs.GoAround, "flight/TIA40/parser", LegWatch.Who, "the PR conflicts with its base")).Error.ShouldNotBeNull().ShouldContain("the person's call");
        // "Ready anyway" is the same way round the person, so the watchtower may not take it either.
        (await Run(Verbs.Review, "flight/TIA40", "du1-watchtower", "pass", "ready anyway")).Error.ShouldNotBeNull().ShouldContain("the person's call");
        F("TIA40").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Changes);
        (await Run(Verbs.GoAround, "flight/TIA40/parser", "controller", "once more, with the PR edited")).Error.ShouldBeNull();
    }

    [Fact]
    public async Task PastThreeReviewGoAroundsThePersonMayPassTheReviewReadyAnyway()
    {
        var flight = await NewFlight("41", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA41"), "controller");
        var leg = _state.Legs("TIA41").Single();
        _state.PutLeg(leg with { GoArounds = LegCommander.MaxReviewGoArounds, ReviewGoArounds = LegCommander.MaxReviewGoArounds });
        (await Run(Verbs.Review, "flight/TIA41", Reviewer("TIA41"), "changes", "the PR's body closes the round's issue")).Ok.ShouldBeTrue();

        (await Run(Verbs.Review, "flight/TIA41", "controller", "pass", "ready anyway")).Error.ShouldBeNull();

        F("TIA41").Review.ShouldNotBeNull().State.ShouldBe(ReviewState.Passed);
    }

    [Fact]
    public async Task GoAroundsOfALegThatFailedOrStalledDoNotCountTowardsTheReviewCap()
    {
        var flight = await NewFlight("42", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA42"), "controller");
        // Three go-arounds, none of them on the review: the leg went round for its own failures before it was done.
        var leg = _state.Legs("TIA42").Single();
        _state.PutLeg(leg with { GoArounds = LegCommander.MaxReviewGoArounds, ReviewGoArounds = 0 });
        (await Run(Verbs.Review, "flight/TIA42", Reviewer("TIA42"), "changes", "a test is missing")).Ok.ShouldBeTrue();

        var card = Open().Single(c => c.Kind == LegCommander.ReviewChanges);
        card.Detail.ShouldNotBeNull().ShouldNotContain("yours to decide");
        (await Run(Verbs.Review, "flight/TIA42", "du1-watchtower", "pass", "ready anyway")).Error.ShouldBeNull();
    }

    [Fact]
    public async Task AGoAroundOnAReviewsChangesIsCountedApartFromOneOnAFailedLeg()
    {
        var flight = await NewFlight("43", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA43"), "controller");
        (await Run(Verbs.Review, "flight/TIA43", Reviewer("TIA43"), "changes", "a test is missing")).Ok.ShouldBeTrue();

        (await Run(Verbs.GoAround, "flight/TIA43/parser", "du1-watchtower", "the review asked for changes")).Error.ShouldBeNull();

        var gone = _state.Legs("TIA43").Single();
        (gone.GoArounds, gone.ReviewGoArounds).ShouldBe((1, 1));
    }

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
}

/// <summary>Part 3 of ReviewTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class ReviewTests3 : ReviewTestsBase
{
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

    // A merge that conflicts exits non-zero, and leaves the conflict in the tree for the test to resolve.
    private static void MergeLeavingConflict(string cwd, string other)
    {
        try
        {
            Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "--no-commit", other);
        }
        catch (InvalidOperationException)
        {
        }
    }

    [Fact]
    public async Task APlaneBehindItsBranchPushedFromElsewhereWithTheConflictResolvedTakesItAndRaisesNoConflict()
    {
        var flight = await NewFlight("17", "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA17"), "controller");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", "README on main");
        Git(Repo, "push", "-q", "origin", "main");
        // The controller resolves the conflict on another clone and pushes the branch (#344): the plane is behind it.
        var branch = F("TIA17").Branch;
        var elsewhere = Path.Combine(_root, "elsewhere");
        Git(_root, "clone", "-q", Git(Repo, "remote", "get-url", "origin").Trim(), elsewhere);
        Git(elsewhere, "checkout", "-q", branch);
        MergeLeavingConflict(elsewhere, "origin/main");
        File.WriteAllText(Path.Combine(elsewhere, "README.md"), "resolved\n");
        Git(elsewhere, "add", ".");
        Git(elsewhere, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "resolve");
        Git(elsewhere, "push", "-q", "origin", $"HEAD:{branch}");
        var resolved = Git(elsewhere, "rev-parse", "HEAD").Trim();

        (await _legs.RebaseOntoBaseAsync("TIA17", Ct)).ShouldBeNull();

        Git(F("TIA17").Worktree, "rev-parse", "HEAD").Trim().ShouldBe(resolved);
        File.ReadAllText(Path.Combine(F("TIA17").Worktree, "README.md")).Trim().ShouldBe("resolved");
        Open().ShouldNotContain(c => c.Kind == "pr-conflicts");
        Open().ShouldNotContain(c => c.Kind == LegCommander.MergeFailed);
    }

    [Fact]
    public async Task ABranchThatAlreadyHoldsItsBaseIsPushedWithNoRebase()
    {
        var flight = await NewFlight("18", "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", "README on main");
        Git(Repo, "push", "-q", "origin", "main");
        // The base is merged into the branch with the conflict resolved: a rebase of its commits would conflict again.
        var wt = F("TIA18").Worktree;
        Git(wt, "fetch", "-q", "origin");
        MergeLeavingConflict(wt, "origin/main");
        File.WriteAllText(Path.Combine(wt, "README.md"), "resolved\n");
        Git(wt, "add", ".");
        Git(wt, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "merge main");
        var head = Git(wt, "rev-parse", "HEAD").Trim();

        (await _legs.RebaseOntoBaseAsync("TIA18", Ct)).ShouldBeNull();

        Git(wt, "rev-parse", "HEAD").Trim().ShouldBe(head);
        Git(wt, "rev-parse", $"origin/{F("TIA18").Branch}").Trim().ShouldBe(head);
        Open().ShouldNotContain(c => c.Kind == "pr-conflicts");
    }

    [Fact]
    public async Task APassedPrWhoseRebaseConflictsIsSentBackAsChangesAndItsDoneLegGoesAroundByItself()
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
        // No one is asked: the node sends the done leg round to resolve it.
        _state.Legs("TIA8").ShouldHaveSingleItem().State.ShouldBe(LegState.Flying);
        Open().ShouldNotContain(c => c.Kind == "pr-conflicts");
    }

    [Fact]
    public async Task APrThatConflictsUnderAReviewAskingForChangesGoesAroundByItselfOnceAHead()
    {
        var flight = await NewFlight("9", "parser");
        File.WriteAllText(Path.Combine(flight.Worktree, "README.md"), "theirs\n");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA9"), "controller");
        (await Run(Verbs.Review, "flight/TIA9", Reviewer("TIA9"), "changes", "the parser drops the last line")).Ok.ShouldBeTrue();
        File.WriteAllText(Path.Combine(Repo, "README.md"), "ours\n");
        Git(Repo, "commit", "-qam", "README on main");
        Git(Repo, "push", "-q", "origin", "main");
        _github.Pr = _github.Pr! with { Mergeable = "CONFLICTING" };
        var checks = new PrChecks(_state, _github, TimeProvider.System, legs: _legs);

        await checks.LookAsync(Ct);

        var leg = _state.Legs("TIA9").ShouldHaveSingleItem();
        leg.State.ShouldBe(LegState.Flying);
        leg.GoArounds.ShouldBe(1);
        // Once a head: the same conflicting head looked at again sends no second worker.
        await checks.LookAsync(Ct);
        _state.Legs("TIA9").ShouldHaveSingleItem().GoArounds.ShouldBe(1);
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
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Redo leg parser", Verbs.GoAround), ("Ready anyway", Verbs.Review)]);

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
        (await Run(Verbs.PlanLeg, "flight/TIA3", "controller", "name=review", "goal=Do the work", "done-when=Its check passes")).Error.ShouldNotBeNull().ShouldStartWith("review names the flight's reviewer");
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

    [Fact]
    public async Task AGoAroundAnsweredFromTheMergeFailedCardIsPushedAndReviewedAfreshWhenItsLegIsDone()
    {
        var (flight, pr, checks) = await ArmedPassedFlight("31");
        var code = flight.Code;

        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", "https://github.com/x/tianwen/actions/runs/1/job/2");
        await checks.LookAsync(Ct);
        var card = Open().ShouldHaveSingleItem();
        card.Kind.ShouldBe(LegCommander.MergeFailed);
        _github.AutoMergeDisarmed.ShouldBe([pr.Number]);

        var goAround = card.Choices.Single(c => c.Verb == Verbs.GoAround);
        (await Run(goAround.Verb, goAround.Target, "controller", goAround.Args)).Error.ShouldBeNull();

        // Sent back as the node's own sends are: the PR a draft, the review turned to Changes, no card left offering a stale merge.
        _github.DraftCalls.ShouldContain((pr.Number, true));
        F(code).Review!.State.ShouldBe(ReviewState.Changes);
        Open().ShouldBeEmpty();
        _workers.Started.Last().Brief.ShouldContain("checks failed");

        File.WriteAllText(Path.Combine(flight.Worktree, "fix31.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "make the build pass");
        var reviewsBefore = _workers.Started.Count(s => s.Name == Reviewer(code));
        var worker = _state.Legs(code).Single().Worker!;
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(code, "parser"), worker, "done", "fixed")).Error.ShouldBeNull();

        // The finished go-around went up, and a fresh review reads it: the flight is never left with every leg done and no card.
        Git(Repo, "ls-remote", "origin", "flight/tia31").ShouldContain(Git(flight.Worktree, "rev-parse", "HEAD"));
        _workers.Started.Count(s => s.Name == Reviewer(code)).ShouldBe(reviewsBefore + 1);
        F(code).Review!.State.ShouldBe(ReviewState.Reviewing);
        (await Run(Verbs.Review, Verbs.FlightTarget(code), Reviewer(code), "pass", "fine now")).Ok.ShouldBeTrue();
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegCommander.PrReady);
    }

    [Fact]
    public async Task ADisarmThatFailsKeepsTheArmSoItIsRetriedAndTheNodesRebaseRefuses()
    {
        var (flight, pr, checks) = await ArmedPassedFlight("33");
        var code = flight.Code;

        // GitHub shows it conflicting; turning its auto-merge off fails.
        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        _github.Merge = new MergeState(MergeKinds.Conflicts, 3, 3);
        _github.DisarmFails = "gh could not turn auto-merge off on #71: HTTP 502";
        await checks.LookAsync(Ct);

        // Still armed on GitHub, so still armed here; the card says why, and the rebase of the same look did not run.
        F(code).MergeWhenGreen.ShouldNotBeNull();
        _github.AutoMergeDisarmed.ShouldBeEmpty();
        Open().Single().Detail.ShouldNotBeNull().ShouldContain("could not be turned off");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        (await _legs.RebaseOntoBaseAsync(code, Ct)).ShouldNotBeNull().ShouldContain("could not be turned off");
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(head);

        // GitHub answers again: the next look turns it off.
        _github.DisarmFails = null;
        await checks.LookAsync(Ct);
        _github.AutoMergeDisarmed.ShouldBe([pr.Number]);
        F(code).MergeWhenGreen.ShouldBeNull();
    }

    [Theory]
    [InlineData(MergeKinds.ChangesRequested)]
    [InlineData(MergeKinds.Blocked)]
    public async Task EveryStateAnArmedMergeLeavesTurnsGitHubsAutoMergeOff(string kind)
    {
        var (flight, pr, checks) = await ArmedPassedFlight("34");

        _github.Summary = new ChecksSummary(ChoiceStatus.Passing, 3, 3);
        _github.Merge = new MergeState(kind, 3, 3, Reason: kind == MergeKinds.Blocked ? "a review is required" : null);
        await checks.LookAsync(Ct);

        _github.AutoMergeDisarmed.ShouldBe([pr.Number]);
        F(flight.Code).MergeWhenGreen.ShouldBeNull();
    }

    [Fact]
    public async Task APushNeverReachesAPrWhileGitHubsAutoMergeIsArmedOnIt()
    {
        var (flight, pr, checks) = await ArmedPassedFlight("35");
        var code = flight.Code;

        // The failed-check path cleared the arm here although GitHub would not turn its auto-merge off.
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 2, 3, "build (linux)", null);
        _github.DisarmFails = "gh could not turn auto-merge off on #71: HTTP 502";
        await checks.LookAsync(Ct);
        F(code).MergeWhenGreen.ShouldBeNull();
        _github.AutoMergeArmed.ShouldHaveSingleItem();
        _github.AutoMergeDisarmed.ShouldBeEmpty();
        var card = Open().ShouldHaveSingleItem();
        var goAround = card.Choices.Single(c => c.Verb == Verbs.GoAround);
        var pushedBefore = Git(Repo, "ls-remote", "origin", "flight/tia35");

        // The go-around takes the merge back first, and is refused while GitHub's auto-merge cannot be turned off: no leg flies under an armed PR.
        (await Run(goAround.Verb, goAround.Target, "controller", goAround.Args)).Error.ShouldNotBeNull().ShouldContain("could not be turned off");
        _state.Legs(code).Single().State.ShouldBe(LegState.Done);
        _github.AutoMergeDisarmed.ShouldBeEmpty();

        // Opening the PR again by hand is refused too, and nothing is pushed.
        File.WriteAllText(Path.Combine(flight.Worktree, "fix35.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "fix");
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(code), "controller")).Error.ShouldNotBeNull().ShouldContain("not pushed");
        Git(Repo, "ls-remote", "origin", "flight/tia35").ShouldBe(pushedBefore);

        // GitHub answers again: it is turned off first, and the push goes up.
        _github.DisarmFails = null;
        (await Run(Verbs.OpenPr, Verbs.FlightTarget(code), "controller")).Error.ShouldBeNull();
        _github.AutoMergeDisarmed.ShouldBe([pr.Number]);
        Git(Repo, "ls-remote", "origin", "flight/tia35").ShouldContain(Git(flight.Worktree, "rev-parse", "HEAD"));
    }
}
