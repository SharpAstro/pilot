using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The watchtower: kept running with Remote Control on, fed the cards, kept with its conversation, and rung for when it cannot run.</summary>
public sealed class WatchtowerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly List<CommandRequest> _said = [];
    // A data folder of its own: the watchtower keeps the fingerprint of its brief there.
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-watchtower-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly Watchtower _tower;
    // Claude Code's state.json of each job, by job id.
    private readonly Dictionary<string, Pilot.Node.Sessions.ClaudeJobRecord> _records = [];

    /// <summary>A look, a failed record for the job it started, and the retry time: one failed start of the fake job <paramref name="job"/>.</summary>
    private async Task FailedStart(string job, string detail = "ENAMETOOLONG")
    {
        await _tower.LookAsync(Ct);
        _records[job] = new Pilot.Node.Sessions.ClaudeJobRecord("failed", detail);
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(10));
    }

    /// <summary>Two starts that fail with the same reason: parked, with its one card.</summary>
    private async Task Park()
    {
        await FailedStart("job00001");
        await FailedStart("job00002");
        _workers.Started.Count.ShouldBe(2);
        await _tower.LookAsync(Ct);
    }

    public WatchtowerTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _tower = new Watchtower(_state, _workers, Run, _options, _time)
        {
            Pilot = "pilot",
            JobRecord = job => _records.TryGetValue(job, out var record) ? record : null,
            Version = "2.0.1",
        };
        // Something to watch: a flight in the air with a leg flying. Tests of a quiet node land it (Quiet).
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private string CurrentBrief() => Briefs.WatchtowerFingerprint("du1-watchtower", "pilot", _tower.Models.Model(RoleModels.Watchtower), _options.Machine);

    /// <summary>Nothing left to watch: the flight lands, its leg done.</summary>
    private void Quiet()
    {
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed, LandedAt = _time.GetUtcNow() });
    }

    // A say the intercom does not deliver, as when a session cannot be resumed.
    private bool _refuseSays;

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_refuseSays && command.Verb == Verbs.Say
            ? new CommandResult { Seq = 0, Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = false, Error = "the intercom did not deliver it" }
            : _state.Complete(command, null));
    }

    /// <summary>The watchtower's row, as the tracker would put it; its id is the job the fake workers handed out.</summary>
    private void Row(string id, SessionState state, DateTimeOffset? lastResponse = null, long context = 10_000, string? apiError = null, DateTimeOffset? resetsAt = null, string name = "du1-watchtower") =>
        _state.PutSession(new SessionRow
        {
            Id = id,
            SessionId = id + "-0000-4000-8000-000000000000",
            Name = name,
            Kind = SessionKind.Background,
            Cwd = _options.WatchtowerFolder,
            Role = SessionRole.Watchtower,
            ContextTokens = context,
            ContextWindow = 200_000,
            State = state,
            SpendWeighted = 0,
            ApiError = apiError,
            ApiErrorResetsAt = resetsAt,
            LastResponseAt = lastResponse ?? _time.GetUtcNow(),
            StartedAt = Start,
        });

    private CardRow[] Open() => _state.Cards(openOnly: true);

    [Fact]
    public async Task UnreadHeadBriefNamesTheShapeAndTheReviewCommand()
    {
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser failed");

        await _tower.LookAsync(Ct);

        var brief = _workers.Started.ShouldHaveSingleItem().Brief;
        brief.ShouldContain("whose legs are all done, whose PR carries `pilot: changes` and whose PR head is not the head its");
        brief.ShouldContain("`read head`");
        brief.ShouldContain("start it yourself with `pilot flight review <code>`");
    }

    [Fact]
    public async Task OneIsStartedWhenNoneRunsWithRemoteControlInItsOwnFolderBriefedFromTheState()
    {
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser failed");

        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        var start = _workers.Started.ShouldHaveSingleItem();
        (start.Name, start.RepoPath, start.Plane, start.RemoteControl).ShouldBe(("du1-watchtower", _options.WatchtowerFolder, (string?)null, true));
        start.Brief.ShouldContain("You are du1-watchtower");
        start.Brief.ShouldContain("c1 [ring] about flight/TIA1/parser: TIA1/parser failed");
        _state.Cards().Single(c => c.Bin == CardBin.Handled).Title.ShouldBe("Started a fresh watchtower: TIA9 is in the air, and none was running");

        // In its brief already: the card is not said to it again once it runs.
        Row("job00001", SessionState.Idle);
        await _tower.LookAsync(Ct);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARenamedWatchtowerIsStillTheNodesSoItIsStoppedAndAFreshOneStarts()
    {
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);
        _time.Advance(TimeSpan.FromMinutes(3));
        Row("job00001", SessionState.Idle);
        _state.PutSession(_state.Sessions().Single() with { Name = "du1-pil269-stale-worktree-duplicate" });

        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldContain("job00001");
        _workers.Started.Count.ShouldBe(2);
        _tower.Current().ShouldBeNull();
    }

    [Fact]
    public async Task WithNothingToWatchNoneIsStartedAndAnIdleOneIsStoppedOnceQuietLongEnough()
    {
        Quiet();
        await _tower.LookAsync(Ct);
        _workers.Started.ShouldBeEmpty();

        // One already running when there is nothing to watch: kept for QuietFor, then stopped, once.
        Row("job00007", SessionState.Idle);
        _time.Advance(TimeSpan.FromMinutes(14));
        await _tower.LookAsync(Ct);
        _workers.Stopped.ShouldBeEmpty();
        _time.Advance(TimeSpan.FromMinutes(2));
        await _tower.LookAsync(Ct);
        _workers.Stopped.ShouldBe(["job00007"]);
        _state.Cards().Single(c => c.Bin == CardBin.Handled).Title.ShouldStartWith("Stopped the watchtower: nothing to watch");
        _said.ShouldBeEmpty();

        // The next thing to watch starts a fresh one, and says why.
        Row("job00007", SessionState.Ended);
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);
        _workers.Started.ShouldHaveSingleItem();
        _state.Cards().ShouldContain(c => c.Title == "Started a fresh watchtower: c2 rings, and the last one ended");
    }

    [Fact]
    public async Task IdleLongOrPastMostOfItsWindowItIsKeptWithItsConversationAndSentNoRound()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle, lastResponse: Start, context: 150_000);
        _time.Advance(TimeSpan.FromHours(2));

        await _tower.LookAsync(Ct);

        // The compactor's to compact, not the watchtower's to replace or keep warm.
        _workers.Started.Count.ShouldBe(1);
        _workers.Stopped.ShouldBeEmpty();
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task NewCardsAreSaidToItOnceButNotItsOwnNorHandledLines()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Working);

        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge", choices:
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["cafe"] }]);
        _state.RaiseCard(CardBin.Handled, "worker-nudged", "flight/TIA1/parser", "nudged");
        _state.RaiseCard(CardBin.ForYou, Watchtower.RaisedKind, "flight/TIA1", "raised by the watchtower");
        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target, say.Who).ShouldBe((Verbs.Say, "session/job00001", "node"));
        say.Args[0].ShouldContain("A card opened:");
        say.Args[0].ShouldContain("TIA1's PR #7 is ready to merge. Choices: 1 Merge.");
        say.Args[0].ShouldNotContain("raised by the watchtower");
    }

    [Fact]
    public async Task OneThatEndedWithNothingToSayIsLeftNotReplacedEveryTurn()
    {
        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);

        // Its turn over, the session ended (as SE1's did after every turn): a flight in the air alone wakes nothing.
        _time.Advance(TimeSpan.FromMinutes(3));
        Row("job00001", SessionState.Ended);
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromHours(2));
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(1);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneThatEndedIsResumedInPlaceForANewCardWhileItsCacheIsWarm()
    {
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(3));
        Row("job00001", SessionState.Ended, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(50));

        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        // The say resumes the same session, its context read from the cache: no fresh one, no brief read again.
        _workers.Started.Count.ShouldBe(1);
        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target).ShouldBe((Verbs.Say, "session/job00001"));
        say.Args[0].ShouldContain("TIA9/parser failed");
    }

    [Fact]
    public async Task ARingCardItWasToldAndLeftOpenWakesItOnceMoreAfterHalfAnHourNotEveryLook()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Working);
        _state.RaiseCard(CardBin.Ring, "review-changes", "flight/TIA9", "TIA9's review asks for changes");
        await _tower.LookAsync(Ct);
        _said.Count.ShouldBe(1);

        // Its turn ended with the card still open: told once more after RetellAfter, which resumes it, and not again.
        _time.Advance(TimeSpan.FromMinutes(5));
        Row("job00001", SessionState.Ended, lastResponse: _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(26));
        await _tower.LookAsync(Ct);
        _said.Count.ShouldBe(2);
        _said[1].Args[0].ShouldContain("TIA9's review asks for changes");
        _time.Advance(TimeSpan.FromMinutes(31));
        await _tower.LookAsync(Ct);
        _said.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OneThatEndedIsReplacedForANewCardOnceItsCacheIsColdOrItsWindowMostlySpent()
    {
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(3));
        Row("job00001", SessionState.Ended, lastResponse: _time.GetUtcNow());

        // Past the cache's hour, a resume would read its whole context again at full price: a fresh one reads only its brief.
        _time.Advance(TimeSpan.FromMinutes(56));
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(2);
        _said.ShouldBeEmpty();
        _state.Cards().ShouldContain(c => c.Bin == CardBin.Handled && c.Title.StartsWith("Started a fresh watchtower: ", StringComparison.Ordinal)
            && c.Title.EndsWith(", and the last one ended", StringComparison.Ordinal));

        // Warm, but three quarters of its window spent: replaced too.
        _time.Advance(TimeSpan.FromMinutes(3));
        Row("job00002", SessionState.Ended, lastResponse: _time.GetUtcNow(), context: 160_000);
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/lexer", "TIA9/lexer failed");
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(3);
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneThatEndedAndCannotBeResumedIsReplacedAtTheNextLook()
    {
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(3));
        Row("job00001", SessionState.Ended, lastResponse: _time.GetUtcNow());
        _refuseSays = true;

        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        _workers.Started.Count.ShouldBe(1);

        // Not said again into a session that did not take it: a fresh one, with the card in its brief.
        await _tower.LookAsync(Ct);
        _said.Count.ShouldBe(1);
        _workers.Started.Count.ShouldBe(2);
        _workers.Started[1].Brief.ShouldContain("TIA9/parser failed");
    }

    [Fact]
    public async Task ARowItStartedThatTurnsUpRenamedIsNotedAsSoAndTheFreshStartSaysSo()
    {
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(3));
        Row("job00001", SessionState.Idle, name: "my-own-name");
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(2);
        _state.Sessions().Single(s => s.Id == "job00001").EndedBecause.ShouldBe("renamed to my-own-name");
        _state.Cards().First(c => c.Bin == CardBin.Handled).Title.ShouldContain("the one it started was renamed to my-own-name");
    }

    [Fact]
    public async Task AnUntrustedFolderRingsWithTheFixAndIsTriedAgainLater()
    {
        _workers.Fail = new FlightException("claude --bg did not start du1-watchtower (1): Workspace not trusted. Run `claude` in /tmp/pilot2/watchtower once");

        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        var card = Open().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Subject).ShouldBe((CardBin.Ring, Watchtower.Down, Verbs.WatchtowerTarget));
        card.Detail.ShouldNotBeNull().ShouldContain($"Run `claude` in {_options.WatchtowerFolder} once");
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.RestartWatchtower);

        _workers.Fail = null;
        _time.Advance(TimeSpan.FromMinutes(10));
        await _tower.LookAsync(Ct);
        _workers.Started.ShouldHaveSingleItem();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoStartsFailingForTheSameReasonRingOnceAndAreNotTriedAgainHoweverManyLooksPass()
    {
        await Park();

        var card = Open().Single(c => c.Kind == Watchtower.Down);
        (card.Bin, card.Title).ShouldBe((CardBin.Ring, "The watchtower cannot start"));
        card.Detail.ShouldNotBeNull().ShouldContain("ENAMETOOLONG");
        card.Detail.ShouldContain("new node release");
        card.Choices.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.RestartWatchtower);

        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(30));
            await _tower.LookAsync(Ct);
        }
        _workers.Started.Count.ShouldBe(2);
        Open().Count(c => c.Kind == Watchtower.Down).ShouldBe(1);
    }

    [Fact]
    public async Task TwoLauncherRefusalsWithTheSameReasonParkItToo()
    {
        _workers.Fail = new FlightException("claude refused it");
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(10));
        await _tower.LookAsync(Ct);
        _workers.Fail = null;
        for (var i = 0; i < 10; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(30));
            await _tower.LookAsync(Ct);
        }

        _workers.Started.ShouldBeEmpty();
        var card = Open().ShouldHaveSingleItem();
        (card.Title, card.Detail.ShouldNotBeNull().Contains("claude refused it")).ShouldBe(("The watchtower cannot start", true));
    }

    [Fact]
    public async Task ARestartAskedStartsAParkedWatchtowerAgain()
    {
        await Park();

        (await _tower.RestartAsync("tried again from its card", Ct)).ShouldBeNull();

        _workers.Started.Count.ShouldBe(3);
        Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task ANewReleaseStartsAParkedWatchtowerAgain()
    {
        await Park();

        _tower.Version = "2.0.2";
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ACardBeingClosedStartsAParkedWatchtowerAgain()
    {
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser failed");
        await Park();
        _workers.Started.Count.ShouldBe(2);

        _state.CloseCardsAbout("flight/TIA1/parser", "handled", "leg-failed");
        await _tower.LookAsync(Ct);

        _workers.Started.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ADifferentReasonCountsAfreshAndASuccessfulStartClearsTheCount()
    {
        await FailedStart("job00001", "ENAMETOOLONG");
        await FailedStart("job00002", "something else");
        _workers.Started.Count.ShouldBe(2);
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(3);
        Open().ShouldNotContain(c => c.Title == "The watchtower cannot start");

        // job00003 comes up: the count is gone, so one more failure of the first reason is a first.
        Row("job00003", SessionState.Idle);
        await _tower.LookAsync(Ct);
        Row("job00003", SessionState.Ended);
        _state.PutSession(_state.Sessions().Single() with { State = SessionState.Failed });
        _tower.Version = "2.0.1";
        await FailedStart("job00004", "ENAMETOOLONG");
        _workers.Started.Count.ShouldBe(4);
        await _tower.LookAsync(Ct);
        Open().ShouldNotContain(c => c.Title == "The watchtower cannot start");
    }

    [Fact]
    public async Task OneTheUsageLimitStoppedIsResumedAfterTheReset()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle, apiError: "rate_limit", resetsAt: Start.AddHours(1));

        await _tower.LookAsync(Ct);
        // A stop with a reset time needs no one: a Handled line, not an open card.
        Open().ShouldBeEmpty();
        _state.Cards(openOnly: false).Single(c => c.Kind == Watchtower.Stopped).Bin.ShouldBe(CardBin.Handled);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2));
        await _tower.LookAsync(Ct);

        _said.ShouldHaveSingleItem().Args[0].ShouldContain("limit that stopped you has reset");
        Open().ShouldBeEmpty();
        _workers.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OneAlreadyRunningWhenTheNodeStartsIsKeptAndNotToldTheCardsAgain()
    {
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser failed");
        Row("a1b2c3d4", SessionState.Idle);
        Directory.CreateDirectory(_data);
        File.WriteAllText(_tower.BriefFile, CurrentBrief());

        await _tower.LookAsync(Ct);

        (_workers.Started.Count, _said.Count).ShouldBe((0, 0));
        _tower.TellTarget().ShouldBe(("session/a1b2c3d4", (string?)null));

        // Told as before: a card that opens later is said to it, on the brief it has.
        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge");
        await _tower.LookAsync(Ct);
        (_workers.Started.Count, _workers.Stopped.Count, _said.Count).ShouldBe((0, 0, 1));
    }

    [Fact]
    public async Task OneBriefedByAnOlderNodeIsReplacedByAFreshOneBeforeAnythingIsSaidToIt()
    {
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA1/parser", "TIA1/parser failed");
        Row("a1b2c3d4", SessionState.Idle);
        Directory.CreateDirectory(_data);
        File.WriteAllText(_tower.BriefFile, "0123456789abcdef");

        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldBe(["a1b2c3d4"]);
        var start = _workers.Started.ShouldHaveSingleItem();
        start.Brief.ShouldContain("TIA1/parser failed");
        _said.ShouldBeEmpty();
        _state.Cards().ShouldContain(c => c.Bin == CardBin.Handled && c.Title == "Started a fresh watchtower: its brief changed since the node started it");
        File.ReadAllText(_tower.BriefFile).ShouldBe(CurrentBrief());

        // The fresh one carries the current brief: it is told what opens next, not started again.
        Row("job00001", SessionState.Idle);
        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge");
        await _tower.LookAsync(Ct);
        (_workers.Started.Count, _said.Count).ShouldBe((1, 1));
    }

    [Fact]
    public async Task OneWithNoRecordOfItsBriefIsTakenAsBriefedByAnOlderNode()
    {
        Row("a1b2c3d4", SessionState.Idle);

        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldBe(["a1b2c3d4"]);
        _workers.Started.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AFreshStartForAChangedBriefThatFailsWaitsTheRetryTimeBeforeTryingAgain()
    {
        Row("a1b2c3d4", SessionState.Idle);
        _workers.Fail = new InvalidOperationException("claude refused it");

        await _tower.LookAsync(Ct);
        await _tower.LookAsync(Ct);

        _workers.Stopped.ShouldBe(["a1b2c3d4"]);
        _said.ShouldBeEmpty();

        _workers.Fail = null;
        _time.Advance(TimeSpan.FromMinutes(30));
        await _tower.LookAsync(Ct);
        _workers.Started.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task TheFingerprintOfAStartedWatchtowerSurvivesTheNodeRestarting()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle);
        File.ReadAllText(_tower.BriefFile).ShouldBe(CurrentBrief());

        // A node restarted over the same data folder finds the one it started, with its brief, and keeps it.
        var restarted = new Watchtower(_state, _workers, Run, _options, _time) { Pilot = "pilot" };
        _state.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge");
        await restarted.LookAsync(Ct);
        (_workers.Started.Count, _workers.Stopped.Count, _said.Count).ShouldBe((1, 0, 0));

        // The node updated to a changed brief (here the command it names to the watchtower): the one left running is replaced.
        var updated = new Watchtower(_state, _workers, Run, _options, _time) { Pilot = "pilot2" };
        await updated.LookAsync(Ct);
        _workers.Stopped.ShouldBe(["job00001"]);
        _workers.Started.Count.ShouldBe(2);
    }

    /// <summary>Starts a watchtower, lets it reply, and has it end a minute later (reason given); the next look starts another.</summary>
    private async Task ChurnOnceAsync(int n, string? because = null)
    {
        // An ended watchtower is replaced, not resumed, only when there is a card to say and its window is full.
        _state.RaiseCard(CardBin.Ring, "leg-failed", $"flight/TIA{n}/parser", $"TIA{n}/parser failed");
        await _tower.LookAsync(Ct);
        var id = $"job{n:D5}";
        _time.Advance(TimeSpan.FromSeconds(30));
        Row(id, SessionState.Idle, lastResponse: _time.GetUtcNow(), context: 180_000);
        _time.Advance(TimeSpan.FromSeconds(60));
        _state.PutSession(_state.Sessions().Single(r => r.Id == id) with { State = SessionState.Ended, EndedBecause = because });
    }

    [Fact]
    public async Task ManyShortLivedWatchtowersMarkItChurningAndRaiseOneCardThatIsUpdatedAndClosed()
    {
        for (var n = 1; n <= 5; n++)
        {
            await ChurnOnceAsync(n, "process gone");
        }
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA6/parser", "TIA6/parser failed");
        await _tower.LookAsync(Ct);

        var health = _tower.Health().ShouldNotBeNull();
        (health.StartsLastHour, health.StartsLast24h, health.Churning, health.LastEndedBecause).ShouldBe((6, 6, true, "process gone"));
        health.TypicalLifetimeAfterReply.ShouldBe(TimeSpan.FromSeconds(60));
        health.Summary.ShouldStartWith("6 watchtowers in the last hour");
        var card = Open().Single(c => c.Kind == CardKinds.WatchtowerChurning);
        (card.Bin, card.Subject, card.Link).ShouldBe((CardBin.ForYou, Verbs.WatchtowerTarget, InAppLinks.Fleet));
        card.Detail.ShouldStartWith("6 watchtowers");

        // One more start: the same card, its text moved on.
        Row("job00006", SessionState.Ended, context: 180_000);
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA7/parser", "TIA7/parser failed");
        await _tower.LookAsync(Ct);
        var again = Open().Single(c => c.Kind == CardKinds.WatchtowerChurning);
        again.Id.ShouldBe(card.Id);
        again.Detail.ShouldStartWith("7 watchtowers");

        // A quiet spell: nothing in the last hour, so the card closes.
        _time.Advance(TimeSpan.FromHours(2));
        Row("job00007", SessionState.Idle);
        await _tower.LookAsync(Ct);
        _tower.Health()!.Churning.ShouldBeFalse();
        Open().ShouldNotContain(c => c.Kind == CardKinds.WatchtowerChurning);
    }

    [Fact]
    public async Task ASteadyWatchtowerIsNotChurningAndGetsNoCard()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle);
        await _tower.LookAsync(Ct);

        _tower.Health()!.Churning.ShouldBeFalse();
        Open().ShouldNotContain(c => c.Kind == CardKinds.WatchtowerChurning);
    }

    [Fact]
    public void AnOldSessionsFileWithoutEndedColumnsStillLoads()
    {
        Directory.CreateDirectory(_data);
        File.WriteAllLines(_tower.SessionsFile, [string.Join('\t', "job00009", "sess", "2026-10-20T08:00:00.0000000+00:00", "older why")]);

        var generation = _tower.Generations().ShouldHaveSingleItem();
        (generation.Job, generation.Why, generation.EndedAt, generation.LastReplyAt, generation.EndedBecause).ShouldBe(("job00009", "older why", null, null, null));
    }

    [Fact]
    public async Task WhenAGenerationEndedIsKeptAndSurvivesAReload()
    {
        await ChurnOnceAsync(1, "SessionEnd: logout");
        _tower.Generations();

        var reloaded = new Watchtower(_state, _workers, Run, _options, _time).Generations().Single();
        (reloaded.EndedBecause, reloaded.EndedAt is not null, reloaded.LastReplyAt is not null).ShouldBe(("SessionEnd: logout", true, true));
    }

    [Fact]
    public async Task AFailedSayLeavesItsCardsUntoldAndIsNotRetriedEveryLookOnlyOneDownRingIsRaised()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle);
        _refuseSays = true;
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/lexer", "TIA9/lexer failed");
        await _tower.LookAsync(Ct);
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            await _tower.LookAsync(Ct);
        }

        _said.Count(s => s.Verb == Verbs.Say).ShouldBe(1);
        var down = Open().Where(c => c.Kind == Watchtower.Down).ShouldHaveSingleItem();
        down.Detail!.ShouldContain("2 cards");

        // Past the retry time it is tried once more.
        _time.Advance(TimeSpan.FromMinutes(11));
        await _tower.LookAsync(Ct);
        _said.Count(s => s.Verb == Verbs.Say).ShouldBe(2);
        Open().Count(c => c.Kind == Watchtower.Down).ShouldBe(1);
    }

    [Fact]
    public async Task CardsOpeningWhileTheWatchtowerIsDownStayHeldAndTheNextOneGetsThemAndTheDownRingCloses()
    {
        await _tower.LookAsync(Ct);
        Row("job00001", SessionState.Idle);
        _refuseSays = true;
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");
        await _tower.LookAsync(Ct);
        Open().ShouldContain(c => c.Kind == Watchtower.Down);

        // It is gone: the next one starts fresh with the held card in its brief, and the down ring closes.
        Row("job00001", SessionState.Failed);
        _time.Advance(TimeSpan.FromMinutes(11));
        await _tower.LookAsync(Ct);
        _workers.Started.Count.ShouldBe(2);
        _workers.Started[1].Brief.ShouldContain("TIA9/parser failed");
        Open().ShouldNotContain(c => c.Kind == Watchtower.Down);
        Open().Count(c => c.Bin == CardBin.Ring && c.Kind != Watchtower.RaisedKind).ShouldBe(1);
    }
}

/// <summary>The watchtower's verbs on the node's route, and the controller's gates it cannot pass.</summary>
public sealed class WatchtowerRouteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMergeOrALandingFromTheWatchtowerIsRefusedACardsIncluded()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var watchtower = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR #7 is ready to merge", choices:
            [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1", Args = ["cafe0000"] }]);

        var answered = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = "card/c1", Args = ["1"], Who = watchtower }, Ct);
        var landed = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Land, Target = "flight/TIA1", Who = watchtower }, Ct);

        // The answer runs the choice as the watchtower's, so the merge in it is what is refused.
        answered.Response.ShouldNotBeNull().Error.ShouldBe("Merge was refused: merge is the controller's, not the watchtower's: leave its card open for them");
        landed.Response.ShouldNotBeNull().Error.ShouldStartWith("land is the controller's");
        node.State.Cards(openOnly: true).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task FoldAndSeparateFromTheWatchtowerAreRefusedACardsIncludedAndLeaveThePairPendingAndTheCardOpen()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var watchtower = $"{node.Options.Machine.ToLowerInvariant()}-watchtower";
        foreach (var code in new[] { "TIA1", "TIA2" })
        {
            node.State.PutFlight(new FlightRow
            {
                Code = code, Repo = "tianwen", RepoPath = "/r", Issue = int.Parse(code[3..]), Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"/r/{code}", Base = "main",
                State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
                Overlaps = code == "TIA2" ? [new OverlapRow { Other = "TIA1", Why = "same parser", Answer = OverlapAnswer.Pending, At = DateTimeOffset.UtcNow }] : [],
            });
        }
        node.State.RaiseCard(CardBin.Ring, "flight-overlap", "flight/TIA2", "TIA2 looks like part of TIA1: same parser", choices:
        [
            new CardChoice { Label = "Fold into TIA1", Verb = Verbs.Fold, Target = "flight/TIA2", Args = ["TIA1"] },
            new CardChoice { Label = "Plan separately", Verb = Verbs.Separate, Target = "flight/TIA2", Args = ["TIA1"] },
        ]);

        var folded = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = "card/c1", Args = ["1"], Who = watchtower }, Ct);
        var separated = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = "card/c1", Args = ["2"], Who = watchtower }, Ct);
        var direct = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Fold, Target = "flight/TIA2", Args = ["TIA1"], Who = watchtower }, Ct);
        var directSeparate = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Separate, Target = "flight/TIA2", Args = ["TIA1"], Who = watchtower }, Ct);

        folded.Response.ShouldNotBeNull().Error.ShouldBe("Fold into TIA1 was refused: fold is the controller's, not the watchtower's: leave its card open for them");
        separated.Response.ShouldNotBeNull().Error.ShouldBe("Plan separately was refused: separate is the controller's, not the watchtower's: leave its card open for them");
        direct.Response.ShouldNotBeNull().Error.ShouldStartWith("fold is the controller's");
        directSeparate.Response.ShouldNotBeNull().Error.ShouldStartWith("separate is the controller's");
        node.State.Cards(openOnly: true).ShouldHaveSingleItem();
        var asker = node.State.Flights().Single(f => f.Code == "TIA2");
        asker.Overlaps.ShouldHaveSingleItem().Answer.ShouldBe(OverlapAnswer.Pending);
        (asker.State, asker.FoldedInto).ShouldBe((FlightState.Flying, null));
        node.State.Flights().Single(f => f.Code == "TIA1").FoldedIn.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARaiseIsACardFromWhoeverRaisedItAndATellNeedsAWatchtower()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = "flight/TIA1", Args = ["ring", "TIA1 forks: SQLite or JSON?", "both work"], Who = "du1-watchtower" }, Ct))
            .Success.ShouldBeTrue();
        var bad = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Raise, Target = "node", Args = ["loud", "x"], Who = "tests" }, Ct);
        var tell = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Tell, Target = Verbs.WatchtowerTarget, Args = ["hello"], Who = "tests" }, Ct);

        var card = node.State.Cards().ShouldHaveSingleItem();
        (card.Bin, card.Kind, card.Title, card.Detail).ShouldBe((CardBin.Ring, Watchtower.RaisedKind, "TIA1 forks: SQLite or JSON?", "both work (from du1-watchtower)"));
        bad.Response.ShouldNotBeNull().Error.ShouldStartWith("raise takes ring, for-you or handled");
        tell.Response.ShouldNotBeNull().Error.ShouldBe("this node keeps no watchtower (--no-watchtower)");
    }
}
