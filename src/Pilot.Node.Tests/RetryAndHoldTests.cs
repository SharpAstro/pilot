using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// What a PR does once its failure is under investigation (PIL716, rules 1 and 2). A first failure in a test the PR does not touch is
/// retried once (a queue removal queued again through the landing routine, a failed CI job run again), with a line naming the
/// investigation. The same failure again, in the same PR after its retry or in another flight, puts the tower on alert: one Ring card,
/// each flight held with no go-around, re-queue or rerun, a hand PR listed and not held. A later match joins the alert. Landing the
/// investigation flight releases every hold. Run against the in-memory node state and the fake GitHub; the live run is the controller's.
/// </summary>
public sealed class RetryAndHoldTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Test = "Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing";
    private const string Message = "(503, No answer within 10s) should be (409,";

    private static string Log(string test = Test) => $"""
        [xUnit.net 00:00:41.52]     {test} [FAIL]
        [xUnit.net 00:00:41.52]       Shouldly.ShouldAssertException : {Message} "a refused start says why")
          Failed {test} [10 s]
          Error Message:
           Shouldly.ShouldAssertException : {Message} "a refused start says why")
          Stack Trace:
             at {test}() in /_/src/Pilot.Node.Tests/NodePlanetaryTests.cs:line 88
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2rh-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-07T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly NodeState _state;
    private readonly FakeGitHub _github = new() { Queues = true };
    private readonly List<CommandRequest> _flights = [];
    private readonly List<string> _legChecks = [];
    private readonly FailureAlerts _alerts;
    private readonly LegCommander _legs;
    private readonly PrChecks _checks;

    public RetryAndHoldTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        var failures = new FailureInvestigations(_state, _github, new FailureLedger(), _time)
        {
            CreateFlight = (command, _) =>
            {
                _flights.Add(command);
                return Task.FromResult(new CommandResult { Seq = 0, Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = true, Made = $"TIA{900 + _flights.Count}" });
            },
        };
        _alerts = new FailureAlerts(_state, _github, failures, _time)
        {
            RerunLegCheck = (code, _) =>
            {
                _legChecks.Add(code);
                return Task.FromResult("its check was run again");
            },
        };
        var git = new GitCli();
        var planes = new Planes(git);
        var workers = new FakeWorkers();
        var flights = new FlightCommander(_state, planes, _time, workers, _github);
        _legs = new LegCommander(_state, workers, new FakeChecks(), _github, git, planes, flights,
            new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1"), _time)
        {
            Alerts = _alerts,
        };
        _alerts.Legs = _legs;
        _checks = new PrChecks(_state, _github, _time, flights, _legs) { RerunFirst = true, Investigations = failures, Alerts = _alerts };
    }

    public void Dispose() => TestDirs.DeleteTree(_root);

    private static string Head(char c) => new(c, 40);

    private static string Branch(string code) => $"flight/{code.ToLowerInvariant()}";

    private void Flight(string code, int pr, string[]? armed = null, LegState leg = LegState.Done)
    {
        _state.PutFlight(new FlightRow
        {
            Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = int.Parse(code[3..], System.Globalization.CultureInfo.InvariantCulture), Branch = Branch(code),
            Worktree = Path.Combine(_root, "plane", code), Base = "origin/main", State = FlightState.Flying, CreatedAt = _time.GetUtcNow(),
            PullRequest = pr, PullRequestUrl = $"https://github.com/x/tianwen/pull/{pr}", MergeWhenGreen = armed,
        });
        _state.PutLeg(new LegRow { Flight = code, Name = "a", Order = 1, Goal = "g", DoneWhen = "d", State = leg, Check = "dotnet test" });
    }

    // A flight whose merge the autopilot armed, at a head whose checks fail.
    private void CiFlight(string code, int pr, char head, long run, string? log = null, string[]? files = null)
    {
        Flight(code, pr, armed: [Head(head), "squash", LegCommander.ArmedByAutopilot]);
        _github.PrFiles[pr] = files ?? ["src/Pilot.Node/Flights/PrChecks.cs"];
        _github.PrOf[Branch(code)] = new PullRequest(pr, "OPEN", Head(head), $"https://github.com/x/tianwen/pull/{pr}");
        _github.Summary = new ChecksSummary(ChoiceStatus.Failing, 1, 2, "build", $"https://github.com/x/tianwen/actions/runs/{run}");
        _github.Merge = new MergeState(MergeKinds.Blocked, 1, 2, Reason: "build failed", FailedName: "build");
        FailedRun(pr, queue: false, run, log);
    }

    private void FailedRun(int pr, bool queue, long id, string? log = null) =>
        _github.FailedRuns[(pr, queue)] = new FailedRun(id, $"https://github.com/x/tianwen/actions/runs/{id}", ["build"], log ?? Log());

    private void QueueRemoval(string head, string reason = "failed checks") =>
        _github.Queue = new MergeQueueState("OPEN", head, Queued: false, RemovedAt: _time.GetUtcNow().AddSeconds(-5), RemovedReason: reason);

    // A flight whose queue entry was taken out on the failed test, looked at.
    private async Task QueueFlight(string code, int pr, char head, long run)
    {
        Flight(code, pr);
        _github.PrFiles[pr] = ["src/Pilot.Node/Flights/PrChecks.cs"];
        _github.PrOf[Branch(code)] = new PullRequest(pr, "OPEN", Head(head), $"https://github.com/x/tianwen/pull/{pr}");
        FailedRun(pr, queue: true, run);
        QueueRemoval(Head(head));
        await _checks.LookAsync(Ct);
    }

    private CardRow[] Open(string kind) => _state.Cards(openOnly: true).Where(c => c.Kind == kind).ToArray();

    private CardRow RetriedLine() => _state.Cards().Where(c => c.Kind == FailureAlerts.RetriedKind).ShouldHaveSingleItem();

    [Fact]
    public async Task AFirstUntouchedQueueRemovalIsQueuedAgainOnceAtTheSameHeadWithALineNamingTheInvestigation()
    {
        await QueueFlight("TIA71", 71, 'a', 5001);

        // The investigation is filed as before, and the PR is queued again once through the landing routine, at the head it was removed at.
        _github.IssuesCreated.ShouldHaveSingleItem();
        _flights.ShouldHaveSingleItem();
        _github.Merged.ShouldBe([(71, Head('a'), "squash")]);
        var line = RetriedLine();
        line.Kind.ShouldBe(FailureAlerts.RetriedKind);
        (line.Bin, line.Subject).ShouldBe((CardBin.Handled, "flight/TIA71"));
        line.Detail.ShouldNotBeNull().ShouldContain("TIA901 investigates");
        line.Title.ShouldContain("NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing");
        line.Title.ShouldContain("queued again at " + Head('a')[..7]);
        // The removal is no card for the person: it was dealt with, and it is not a flake or a chance.
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.QueueDropped);
        _state.Cards().ShouldNotContain(c => c.Kind == FailureAlerts.AlertKind);
        _alerts.HoldOf("TIA71").ShouldBeNull();

        // The same removal is not queued again at the next look.
        await _checks.LookAsync(Ct);
        _github.Merged.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFirstUntouchedCiFailureRunsTheFailedJobOnceWithALineNamingTheInvestigation()
    {
        CiFlight("TIA72", 72, 'b', 5002);

        await _checks.LookAsync(Ct);

        _github.Reruns.ShouldBe([72]);
        _github.IssuesCreated.ShouldHaveSingleItem();
        var line = RetriedLine();
        (line.Bin, line.Subject).ShouldBe((CardBin.Handled, "flight/TIA72"));
        line.Detail.ShouldNotBeNull().ShouldContain("TIA901 investigates");
        line.Title.ShouldContain("running again");
        _state.Cards().ShouldNotContain(c => c.Kind == "checks-rerun");
        _alerts.HoldOf("TIA72").ShouldBeNull();

        // Once a head: the next looks, inside the time its runs are given, run nothing again.
        _time.Advance(TimeSpan.FromSeconds(30));
        await _checks.LookAsync(Ct);
        _github.Reruns.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TheSameHeadFailingAgainAfterItsCiRetryRaisesOneAlertCardAndHoldsTheFlightWithNoGoAround()
    {
        CiFlight("TIA72", 72, 'b', 5002);
        await _checks.LookAsync(Ct);
        _github.Reruns.Count.ShouldBe(1);

        // The retry's own result is read once the time it needs has passed: the same failure, in the same PR at the same head.
        _time.Advance(TimeSpan.FromMinutes(3));
        FailedRun(72, queue: false, 5002, Log().Replace("10s", "11s"));
        await _checks.LookAsync(Ct);

        var card = Open(FailureAlerts.AlertKind).ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.Ring, "flight/TIA901"));
        card.Title.ShouldContain("TIA901");
        card.Title.ShouldContain("NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing");
        card.Title.ShouldContain("1 flight held");
        card.Detail.ShouldNotBeNull().ShouldContain("TIA72 (PR #72)");
        card.Detail.ShouldContain("(503, No answer within 11s) should be (409,");
        _alerts.HoldOf("TIA72").ShouldNotBeNull().Reason.ShouldBe("held for TIA901: NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing");
        // No second investigation, and the flight is neither run again nor sent round, not even after the rerun grace.
        _github.IssuesCreated.Count.ShouldBe(1);
        _flights.Count.ShouldBe(1);
        _time.Advance(_checks.RerunGrace + TimeSpan.FromMinutes(1));
        await _checks.LookAsync(Ct);
        await _checks.LookAsync(Ct);
        _github.Reruns.Count.ShouldBe(1);
        _github.DraftCalls.ShouldBeEmpty();
        _state.Cards().ShouldNotContain(c => c.Kind == "pr-checks-failed");
        Open(FailureAlerts.AlertKind).Length.ShouldBe(1);
    }

    [Fact]
    public async Task TheSameHeadRemovedFromTheQueueAgainAfterItsRequeueRaisesTheAlertAndIsQueuedNoMore()
    {
        await QueueFlight("TIA71", 71, 'a', 5001);
        _github.Merged.Count.ShouldBe(1);

        // GitHub takes it out again at the same head, on a new run of the same failure.
        _time.Advance(TimeSpan.FromMinutes(20));
        FailedRun(71, queue: true, 5011);
        QueueRemoval(Head('a'));
        await _checks.LookAsync(Ct);

        Open(FailureAlerts.AlertKind).ShouldHaveSingleItem().Bin.ShouldBe(CardBin.Ring);
        _alerts.HoldOf("TIA71").ShouldNotBeNull().Where.ShouldBe(FailureWhere.Queue);
        _github.Merged.Count.ShouldBe(1);
        _github.IssuesCreated.Count.ShouldBe(1);
        // The alert says it: the removal has no card of its own, and nothing queues it again.
        _state.Cards().ShouldNotContain(c => c.Kind == PrChecks.QueueDropped);
        _time.Advance(TimeSpan.FromMinutes(5));
        await _checks.LookAsync(Ct);
        _github.Merged.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ASecondFlightFailingTheSameTestRaisesOneAlertHoldingBothAndOpensNoSecondInvestigation()
    {
        await QueueFlight("TIA71", 71, 'a', 5001);
        _github.Queue = null;
        _github.Merged.Count.ShouldBe(1);

        // Another flight, a first failure for it, of the same test: the repeat that puts the tower on alert, with no retry for it.
        CiFlight("TIA74", 74, 'd', 5003, Log().Replace("10s", "12s"));
        await _checks.LookAsync(Ct);

        var card = Open(FailureAlerts.AlertKind).ShouldHaveSingleItem();
        card.Title.ShouldContain("2 flights held");
        card.Detail.ShouldNotBeNull().ShouldContain("TIA71 (PR #71)");
        card.Detail.ShouldContain("TIA74 (PR #74)");
        _alerts.HoldOf("TIA71").ShouldNotBeNull().Reason.ShouldContain("held for TIA901");
        _alerts.HoldOf("TIA74").ShouldNotBeNull().Where.ShouldBe(FailureWhere.Ci);
        _github.IssuesCreated.Count.ShouldBe(1);
        _flights.Count.ShouldBe(1);
        _github.Reruns.ShouldBeEmpty();
        _github.IssueComments.ShouldHaveSingleItem().Body.ShouldContain("tianwen#74 (TIA74)");
        _state.Cards().Where(c => c.Kind == FailureAlerts.RetriedKind).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AThirdFlightJoinsTheHoldWithNoSecondCard()
    {
        await QueueFlight("TIA71", 71, 'a', 5001);
        _github.Queue = null;
        CiFlight("TIA74", 74, 'd', 5003);
        await _checks.LookAsync(Ct);
        var alert = Open(FailureAlerts.AlertKind).ShouldHaveSingleItem();

        CiFlight("TIA75", 75, 'e', 5004);
        await _checks.LookAsync(Ct);

        var card = Open(FailureAlerts.AlertKind).ShouldHaveSingleItem();
        card.Id.ShouldBe(alert.Id);
        card.Title.ShouldContain("3 flights held");
        card.Detail.ShouldNotBeNull().ShouldContain("TIA75 (PR #75)");
        _alerts.HoldOf("TIA75").ShouldNotBeNull();
        _alerts.Open().ShouldHaveSingleItem().Holds.Select(h => h.Flight).ShouldBe(["TIA71", "TIA74", "TIA75"], ignoreOrder: true);
        _github.IssuesCreated.Count.ShouldBe(1);
        _github.Reruns.ShouldBeEmpty();
    }

    [Fact]
    public async Task AHeldFlightGetsNoGoAroundNoRequeueAndNoRerun()
    {
        await QueueFlight("TIA71", 71, 'a', 5001);
        _github.Queue = null;
        CiFlight("TIA74", 74, 'd', 5003);
        await _checks.LookAsync(Ct);
        _alerts.HoldOf("TIA71").ShouldNotBeNull();

        // No go-around, from the watchtower or the node: the failure is not in its work. The person's word is another matter.
        _state.PutLeg(_state.Legs("TIA74")[0] with { State = LegState.Failed });
        var refused = await _legs.RunAsync(new CommandRequest { Verb = Verbs.GoAround, Target = Verbs.LegTarget("TIA74", "a"), Args = ["fix it"], Who = "du1-watchtower" }, Ct);
        refused.Ok.ShouldBeFalse();
        refused.Error.ShouldNotBeNull().ShouldContain("held for TIA901");
        _state.Legs("TIA74")[0].GoArounds.ShouldBe(0);

        // No re-queue: it is taken out of the queue once more, and the held flight is left alone.
        _time.Advance(TimeSpan.FromMinutes(20));
        FailedRun(71, queue: true, 5012);
        QueueRemoval(Head('a'));
        await _checks.LookAsync(Ct);
        _github.Merged.Count.ShouldBe(1);

        // No rerun, however long its checks have failed.
        _time.Advance(TimeSpan.FromHours(1));
        await _checks.LookAsync(Ct);
        _github.Reruns.ShouldBeEmpty();
        _github.DraftCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailureInATestThePrDoesTouchKeepsTodaysHandlingAndIsNotRunAgain()
    {
        CiFlight("TIA76", 76, 'f', 5005, files: ["src/Pilot.Node.Tests/NodePlanetaryTests.cs"]);

        await _checks.LookAsync(Ct);

        // The PR's own test: no investigation, no rerun, no hold, and none of the lines or cards of this rule.
        _github.Reruns.ShouldBeEmpty();
        _github.IssuesCreated.ShouldBeEmpty();
        _flights.ShouldBeEmpty();
        _alerts.HoldOf("TIA76").ShouldBeNull();
        _state.Cards().ShouldNotContain(c => c.Kind == FailureAlerts.RetriedKind || c.Kind == FailureAlerts.AlertKind || c.Kind == "checks-rerun");
    }

    [Fact]
    public async Task AFailureInALegCheckOfASecondFlightHoldsItAndLandingTheInvestigationReleasesEveryHoldAndDoesWhatEachWasHeldFrom()
    {
        // One of each: a queue removal, a CI failure and a leg check, all of the same test.
        await QueueFlight("TIA71", 71, 'a', 5001);
        _github.Queue = null;
        CiFlight("TIA74", 74, 'd', 5003);
        await _checks.LookAsync(Ct);
        Flight("TIA77", 77, leg: LegState.Failed);
        var held = await _alerts.LookAtCheckAsync(new FailureSite("tianwen", "/r/tianwen", 77, "TIA77", Head('g'), FailureWhere.LegCheck, "https://github.com/x/tianwen/pull/77"),
            Log(), () => Task.FromResult<string[]>(["src/Pilot.Node/Flights/PrChecks.cs"]), Ct);
        held.Answer.ShouldBe(FailureAnswer.Held);
        _alerts.Open().ShouldHaveSingleItem().Holds.Select(h => (h.Flight, h.Where)).ShouldBe(
            [("TIA71", FailureWhere.Queue), ("TIA74", FailureWhere.Ci), ("TIA77", FailureWhere.LegCheck)], ignoreOrder: true);
        _github.Merged.Count.ShouldBe(1);
        _github.Reruns.ShouldBeEmpty();
        var alert = Open(FailureAlerts.AlertKind).ShouldHaveSingleItem();

        // The investigation flight lands.
        _state.PutFlight(new FlightRow
        {
            Code = "TIA901", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Branch = Branch("TIA901"), Worktree = "/r/plane/901", Base = "origin/main",
            State = FlightState.Landed, CreatedAt = _time.GetUtcNow(),
        });
        _github.Reruns.Clear();
        await _checks.LookAsync(Ct);

        // Every hold is released with a Handled line, and what each was held from is done: queued again, CI run again, leg check run again.
        _alerts.Open().ShouldBeEmpty();
        foreach (var code in new[] { "TIA71", "TIA74", "TIA77" })
        {
            _alerts.HoldOf(code).ShouldBeNull();
        }
        var lines = _state.Cards().Where(c => c.Kind == FailureAlerts.ReleasedKind).ToArray();
        lines.Length.ShouldBe(3);
        lines.ShouldAllBe(c => c.Bin == CardBin.Handled && c.Title.Contains("TIA901 landed"));
        lines.Single(c => c.Subject == "flight/TIA71").Title.ShouldContain("#71 is queued again");
        lines.Single(c => c.Subject == "flight/TIA74").Title.ShouldContain("#74's failed checks run again");
        lines.Single(c => c.Subject == "flight/TIA77").Title.ShouldContain("its check was run again");
        _github.Merged.Count.ShouldBe(2);
        _github.Merged[1].ShouldBe((71, Head('a'), "squash"));
        _github.Reruns.ShouldBe([74]);
        _legChecks.ShouldBe(["TIA77"]);
        _state.Cards().Single(c => c.Id == alert.Id).State.ShouldBe(CardState.Closed);
        Open(FailureAlerts.AlertKind).ShouldBeEmpty();

        // Nothing is released twice.
        await _checks.LookAsync(Ct);
        _state.Cards().Count(c => c.Kind == FailureAlerts.ReleasedKind).ShouldBe(3);
        _github.Merged.Count.ShouldBe(2);
        _legChecks.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnAlertAndItsHoldsAreKeptAcrossARestart()
    {
        var file = Path.Combine(_root, "data", "failure-alerts.json");
        var kept = new FailureAlerts(_state, _github, _alerts.Investigations, _time, file);
        CiFlight("TIA72", 72, 'b', 5002);
        var site = new FailureSite("tianwen", "/r/tianwen", 72, "TIA72", Head('b'), FailureWhere.Ci);
        kept.MarkRetried(site, new FailureFinding(Test, Message, "test:" + Test, "TIA901", 1, 5002, Others: false));

        // The retry is not read before its time; after it the same failure is the repeat, and the alert is written.
        (await kept.LookAtRunAsync(site, mergeGroup: false, Ct)).Answer.ShouldBe(FailureAnswer.Retrying);
        _time.Advance(TimeSpan.FromMinutes(3));
        (await kept.LookAtRunAsync(site, mergeGroup: false, Ct)).Answer.ShouldBe(FailureAnswer.Held);

        var again = new FailureAlerts(_state, _github, _alerts.Investigations, _time, file);
        again.HoldOf("TIA72").ShouldNotBeNull().Reason.ShouldContain("held for TIA901");
        again.Open().ShouldHaveSingleItem().Signature.ShouldBe("test:" + Test);
    }

    [Fact]
    public async Task ARerunOfAHeldLegCheckWithNoFailedLegSaysSoAndRunsNothing()
    {
        Flight("TIA78", 78);

        (await _legs.RerunHeldCheckAsync("TIA78", Ct)).ShouldContain("no failed leg");
    }

    [Fact]
    public async Task TheRealRerunOfAHeldLegCheckAsksTheNodeToVerifyTheFailedLegAndSaysItStaysFailedWhenItCannot()
    {
        // The real routine, not the fake RerunLegCheck: it finds the failed leg that has a check and asks the finish to verify it. The
        // plane's folder does not exist here, so the verify is refused, and the line for the release says the leg stays failed and why.
        Flight("TIA79", 79, leg: LegState.Failed);

        var said = await _legs.RerunHeldCheckAsync("TIA79", Ct);

        said.ShouldContain("a's check was run again and the leg stays failed");
        said.ShouldContain("plane's folder does not exist");
        _state.Legs("TIA79").Single().State.ShouldBe(LegState.Failed);
    }
}

/// <summary>
/// The hand PR side of <see cref="RetryAndHoldTests"/>: a hand-made PR pilot reviewed has its failed jobs run again once on a first
/// failure of a test it does not touch, and is named on the alert, not held, when the same failure comes again.
/// </summary>
[Trait("Category", "Slow")]
public sealed class RetryAndHoldHandPrTests : PrReviewTestsBase
{
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-07T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly List<CommandRequest> _flights = [];

    private const string Test = "Pilot.Node.Tests.NodePlanetaryTests.ARefusedStartSaysWhyAndHoldsNothing";

    private static string Log() => $"""
        [xUnit.net 00:00:41.52]     {Test} [FAIL]
        [xUnit.net 00:00:41.52]       Shouldly.ShouldAssertException : (503, No answer within 10s) should be (409,
          Failed {Test} [10 s]
          Error Message:
           Shouldly.ShouldAssertException : (503, No answer within 10s) should be (409,
          Stack Trace:
             at {Test}() in /_/src/Pilot.Node.Tests/NodePlanetaryTests.cs:line 88
        """;

    private FailureAlerts Wire()
    {
        var failures = new FailureInvestigations(_state, _github, new FailureLedger(), _time)
        {
            CreateFlight = (command, _) =>
            {
                _flights.Add(command);
                return Task.FromResult(new CommandResult { Seq = 0, Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = true, Made = $"TIA{900 + _flights.Count}" });
            },
        };
        var alerts = new FailureAlerts(_state, _github, failures, _time);
        _reviews.Investigations = failures;
        _reviews.Alerts = alerts;
        _github.PrFiles[7] = ["README.md"];
        _github.FailedRuns[(7, false)] = new FailedRun(6001, "https://github.com/acme/widget/actions/runs/6001", ["build"], Log());
        return alerts;
    }

    [Fact]
    public async Task AFirstUntouchedFailureOfAReviewedHandPrRunsItsFailedJobsOnceWithALineNamingTheInvestigationAndARepeatOfItIsListedNotHeld()
    {
        var alerts = Wire();
        await ReviewedWidget("approve", checks: ChoiceStatus.Failing, queue: false);

        // Run again once, the investigation filed, and no note on the review so a later sweep reads the result.
        _github.Reruns.ShouldBe([7]);
        _github.IssuesCreated.ShouldHaveSingleItem();
        var line = _state.Cards().Where(c => c.Kind == FailureAlerts.RetriedKind).ShouldHaveSingleItem();
        (line.Bin, line.Subject).ShouldBe((CardBin.Handled, "pr/acme/widget#7"));
        line.Detail.ShouldNotBeNull().ShouldContain("TIA901 investigates");
        Row("acme/widget", 7).QueueNote.ShouldBeNull();
        await _reviews.SweepAsync(Ct);
        _github.Reruns.Count.ShouldBe(1);

        // The same failure after its retry: the PR is named on the alert, one Ring card, and nothing holds it.
        _time.Advance(TimeSpan.FromMinutes(3));
        await _reviews.SweepAsync(Ct);

        var card = _state.Cards(openOnly: true).Where(c => c.Kind == FailureAlerts.AlertKind).ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.Ring, "flight/TIA901"));
        card.Title.ShouldContain("1 hand PR listed");
        card.Detail.ShouldNotBeNull().ShouldContain("widget#7");
        var alert = alerts.Open().ShouldHaveSingleItem();
        alert.Holds.ShouldBeEmpty();
        alert.Listed.ShouldBe(["widget#7"]);
        _github.IssuesCreated.Count.ShouldBe(1);
        _github.Reruns.Count.ShouldBe(1);
        Row("acme/widget", 7).QueueNote.ShouldNotBeNull().ShouldContain("checks fail");
    }
}
