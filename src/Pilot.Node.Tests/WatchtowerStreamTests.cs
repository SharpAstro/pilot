using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The watchtower on a StreamSession (PIL855), over a fake claude process: one process the node owns, says and tells written to its
/// stdin at once and queued while it works, nothing stopping it for being quiet, a crash resumed on the same session, a compaction as
/// a /compact on its stdin and a generation of its own. In memory, with the node's real messenger and ledger; no claude runs.
/// </summary>
public sealed class WatchtowerStreamTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    internal sealed class FakeProcess : IStreamProcess
    {
        private readonly Channel<string> _out = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _written = [];

        public bool Killed { get; private set; }

        public string ErrorTail { get; private set; } = "";

        public Task<int> Exited => _exit.Task;

        /// <summary>Every line written to it, as written: the user messages and the control requests.</summary>
        public string[] Lines
        {
            get
            {
                lock (_written)
                {
                    return [.. _written];
                }
            }
        }

        /// <summary>The text of each user message written to it, in order.</summary>
        public string[] Said
        {
            get
            {
                lock (_written)
                {
                    // (Only the user messages: an interrupt is a control request, which has no message.)
                    return [.. _written.Where(l => JsonDocument.Parse(l).RootElement.TryGetProperty("message", out _)).Select(Text)];
                }
            }
        }

        private static string Text(string line) => JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content").GetString()!;

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            lock (_written)
            {
                _written.Add(line);
            }
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _out.Reader.WaitToReadAsync(cancellationToken) && _out.Reader.TryRead(out var line) ? line : null;

        public void Emit(string line) => _out.Writer.TryWrite(line);

        public void Exit(int code, string error = "")
        {
            ErrorTail = error;
            _out.Writer.TryComplete();
            _exit.TrySetResult(code);
        }

        public void Kill()
        {
            Killed = true;
            Exit(-1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FakeLauncher : IStreamProcessLauncher
    {
        public List<(IReadOnlyList<string> Arguments, string Folder, FakeProcess Process)> Started { get; } = [];

        /// <summary>What the next start throws, as claude not being there; null starts it.</summary>
        public Exception? Fail { get; set; }

        public IStreamProcess Start(IReadOnlyList<string> arguments, string workingFolder)
        {
            if (Fail is { } fail)
            {
                throw fail;
            }
            var process = new FakeProcess();
            Started.Add((arguments, workingFolder, process));
            return process;
        }

        public FakeProcess Process(int index) => Started[index].Process;

        public string SessionOf(int index)
        {
            var args = Started[index].Arguments;
            return args[args.ToList().FindIndex(a => a is "--session-id" or "--resume") + 1];
        }
    }

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-wtstream-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly FakeWorkers _workers = new();
    private readonly FakeLauncher _launcher = new();
    private readonly SessionLedger _ledger;
    private readonly TempProfile _profile = new();
    private readonly FakeClaudeAgents _agents = new();
    private readonly FakeSessionDelivery _delivery;
    private readonly SessionTracker _tracker;
    private readonly SessionMessenger _messenger;
    private readonly Watchtower _tower;
    private readonly StringWriter _log = new();

    public WatchtowerStreamTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _ledger = new SessionLedger(SessionLedger.FileIn(_data), _time);
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance)
        {
            Launched = _ledger,
            IsStreamSession = id => _tower?.OwnsSession(id) == true,
        };
        _delivery = new FakeSessionDelivery(_agents);
        _tower = NewTower();
        _messenger = new SessionMessenger(_state, _tracker, _delivery, new StringWriter(), ledger: _ledger) { Streams = () => _tower };
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
    }

    private Watchtower NewTower(FakeLauncher? launcher = null, string pilot = "pilot") => new(_state, _workers, Run, _options, _time)
    {
        Pilot = pilot,
        Streams = launcher ?? _launcher,
        Ledger = _ledger,
        Error = _log,
    };

    public void Dispose()
    {
        _profile.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private readonly List<CommandRequest> _commands = [];

    private async Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _commands.Add(command);
        return command.Verb switch
        {
            Verbs.Say => await _messenger.SayAsync(command, cancellationToken),
            Verbs.Compact => await _messenger.CompactAsync(command, cancellationToken),
            Verbs.EndSession => await _messenger.EndAsync(command, cancellationToken),
            _ => _state.Complete(command, null),
        };
    }

    private Task<CommandResult> Say(string text, string who = "gui") =>
        _messenger.SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(Row().Id), Args = [text], Who = who }, Ct);

    private Task<CommandResult> Compact() =>
        _messenger.CompactAsync(new CommandRequest { Verb = Verbs.Compact, Target = Verbs.SessionTarget(Row().Id), Args = ["keep the flights"], Who = LegWatch.Who }, Ct);

    private SessionRow Row() => _tower.Current().ShouldNotBeNull();

    private static string Init(string id) => $$"""{"type":"system","subtype":"init","session_id":"{{id}}","model":"sonnet"}""";

    private const string Result = """{"type":"result","subtype":"success","is_error":false,"duration_ms":900,"session_id":"x"}""";

    private static string Assistant(int input, int cacheRead) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new
            {
                role = "assistant",
                content = new object[] { new { type = "text", text = "ok" } },
                usage = new { input_tokens = input, cache_creation_input_tokens = 0, cache_read_input_tokens = cacheRead, output_tokens = 5 },
            },
        });

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }
            await Task.Delay(5);
        }
    }

    /// <summary>A look that starts it, and the first reply that leaves it idle: a watchtower that is up and has said what it had.</summary>
    private async Task UpAndIdleAsync()
    {
        await _tower.LookAsync(Ct);
        _launcher.Started.ShouldHaveSingleItem();
        _launcher.Process(0).Emit(Init(_launcher.SessionOf(0)));
        _launcher.Process(0).Emit(Result);
        await Until(() => _tower.Current() is { State: SessionState.Idle }, "the first reply to leave it idle");
    }

    private void Quiet()
    {
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed, LandedAt = _time.GetUtcNow() });
    }

    [Fact]
    public async Task StartingTheWatchtowerStartsOneStreamSessionWithTheBriefAsItsFirstMessageAndPublishesItsRow()
    {
        await _tower.LookAsync(Ct);

        _workers.Started.ShouldBeEmpty();
        var (arguments, folder, process) = _launcher.Started.ShouldHaveSingleItem();
        arguments.ShouldContain("-p");
        arguments.ShouldContain("stream-json");
        arguments.ShouldContain("--session-id");
        arguments.ShouldNotContain("--bg");
        // claude --help: --remote-control starts an interactive session, so the print-mode process starts without it.
        arguments.ShouldNotContain("--remote-control");
        // Its questions reach the person's chat through --permission-prompt-tool stdio: the tool is not taken away on any start.
        arguments.ShouldNotContain("--disallowedTools");
        arguments.ShouldContain("--permission-prompt-tool");
        folder.ShouldBe(_options.WatchtowerFolder);
        File.ReadAllText(_tower.RulesFile).ShouldContain("The AskUserQuestion tool works too");
        // The rules are read in its own folder, where the stream allows a call without asking the person.
        Path.GetDirectoryName(_tower.RulesFile).ShouldBe(_options.WatchtowerFolder);
        var brief = process.Said.ShouldHaveSingleItem();
        brief.Length.ShouldBeLessThanOrEqualTo(6000);
        brief.ShouldContain(_tower.RulesFile);

        var row = Row();
        row.SessionId.ShouldBe(_launcher.SessionOf(0));
        row.Id.ShouldBe(row.SessionId[..8]);
        row.Role.ShouldBe(SessionRole.Watchtower);
        row.Kind.ShouldBe(SessionKind.Background);
        row.State.ShouldBe(SessionState.Working);
        _state.CurrentWatchtower.ShouldBe(row.Id);
        _tower.IsLive().ShouldBeTrue();
        _tower.Generations().ShouldHaveSingleItem().SessionId.ShouldBe(row.SessionId);
        _ledger.Find([row.Id]).ShouldNotBeNull().StartReason.ShouldBe(SessionStartReasons.WatchtowerStart);

        // The reply ends its turn: idle, with the context it read.
        process.Emit(Assistant(100, 40_000));
        process.Emit(Result);
        await Until(() => Row() is { State: SessionState.Idle, ContextTokens: 40_100 }, "it to be idle with its context");
        Row().LastResponseAt.ShouldBe(Start);
    }

    [Fact]
    public async Task ASayIsWrittenToItsStdinWithNoResumeAndNoIntercom()
    {
        await UpAndIdleAsync();

        var said = await Say("how are the flights?");

        said.Ok.ShouldBeTrue(said.Error);
        said.Way.ShouldBe(SayWays.Streamed);
        _launcher.Process(0).Said.ShouldBe([_launcher.Process(0).Said[0], "how are the flights?"]);
        _delivery.Calls.ShouldBeEmpty();
        _delivery.Resumes.ShouldBeEmpty();
        _delivery.Relays.ShouldBeEmpty();
        await Until(() => Row().State == SessionState.Working, "its turn on the say");
    }

    [Fact]
    public async Task ACardTheNodeTellsItIsWrittenToItsStdinToo()
    {
        await UpAndIdleAsync();
        _state.RaiseCard(CardBin.Ring, "leg-failed", "flight/TIA9/parser", "TIA9/parser failed");

        await _tower.LookAsync(Ct);

        var told = _launcher.Process(0).Said.Single(s => s.Contains("TIA9/parser failed"));
        told.ShouldStartWith(NodeVoice.Prefix);
        _delivery.Calls.ShouldBeEmpty();
        _launcher.Started.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AMessageSentWhileItWorksIsWrittenAtOnceAndReadAtItsNextStepNotRefusedOrResumed()
    {
        await _tower.LookAsync(Ct);
        _launcher.Process(0).Emit(Init(_launcher.SessionOf(0)));
        // Still on the brief's turn.
        Row().State.ShouldBe(SessionState.Working);

        var first = await Say("one more thing");
        var second = await Say("and another");

        first.Ok.ShouldBeTrue(first.Error);
        second.Ok.ShouldBeTrue(second.Error);
        first.Way.ShouldBe(SayWays.Streamed);
        _launcher.Process(0).Said.Skip(1).ShouldBe(["one more thing", "and another"]);
        _delivery.Calls.ShouldBeEmpty();

        // Each result hands the turn to the next queued message, so it stays working until the last is answered.
        _launcher.Process(0).Emit(Result);
        _launcher.Process(0).Emit(Result);
        await Until(() => Row().State == SessionState.Working && _launcher.Process(0).Said.Length == 3, "the queue to be taken");
        _launcher.Process(0).Emit(Result);
        await Until(() => Row().State == SessionState.Idle, "the last answer to leave it idle");
    }

    [Fact]
    public async Task NothingStopsOrReplacesItWhenQuietOnCacheAgeOrAfterAFinishedTurn()
    {
        await UpAndIdleAsync();
        var id = Row().Id;

        // A finished turn, hours of nothing, the cache long cold: still the one process.
        _time.Advance(TimeSpan.FromHours(3));
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromHours(3));
        await _tower.LookAsync(Ct);

        // And with nothing left to watch at all, past the quiet time that used to stop it.
        Quiet();
        _time.Advance(TimeSpan.FromHours(3));
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromHours(3));
        await _tower.LookAsync(Ct);

        _launcher.Started.ShouldHaveSingleItem();
        _launcher.Process(0).Killed.ShouldBeFalse();
        _commands.ShouldNotContain(c => c.Verb == Verbs.EndSession);
        Row().Id.ShouldBe(id);
        Row().State.ShouldBe(SessionState.Idle);
        _state.Cards().ShouldNotContain(c => c.Kind == "watchtower-parked");
        _tower.Generations().Count.ShouldBe(1);
    }

    [Fact]
    public async Task ACrashAfterServiceIsStartedAgainOnResumeOfTheSameSessionWithNoFreshBrief()
    {
        await UpAndIdleAsync();
        var id = Row().Id;
        var session = _launcher.SessionOf(0);
        _time.Advance(TimeSpan.FromMinutes(40));

        _launcher.Process(0).Exit(1, "socket hang up");
        await Until(() => Row().State == SessionState.Failed, "the exit to show on its row");
        await _tower.LookAsync(Ct);

        _launcher.Started.Count.ShouldBe(2);
        var arguments = _launcher.Started[1].Arguments;
        arguments.ShouldContain("--resume");
        arguments.ShouldNotContain("--session-id");
        arguments.ShouldNotContain("--disallowedTools");
        _launcher.SessionOf(1).ShouldBe(session);
        _launcher.Process(1).Said.ShouldBeEmpty();
        Row().Id.ShouldBe(id);
        Row().State.ShouldBe(SessionState.Idle);
        _tower.Generations().ShouldHaveSingleItem();
        _commands.ShouldNotContain(c => c.Verb == Verbs.EndSession);
        _ledger.Find([id]).ShouldNotBeNull().Resumes.ShouldContain("crashed");
        _workers.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASayToAWatchtowerThatCrashedStartsItAgainOnResumeAtOnceAndWritesTheSayToIt()
    {
        await UpAndIdleAsync();
        _time.Advance(TimeSpan.FromMinutes(40));
        _launcher.Process(0).Exit(1);
        await Until(() => Row().State == SessionState.Failed, "the exit to show on its row");

        var said = await Say("are you there?");

        said.Ok.ShouldBeTrue(said.Error);
        said.Way.ShouldBe(SayWays.Streamed);
        _launcher.Started.Count.ShouldBe(2);
        _launcher.Process(1).Said.ShouldBe(["are you there?"]);
        _launcher.SessionOf(1).ShouldBe(_launcher.SessionOf(0));
        _delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneThatKeepsEndingAsItStartsIsTriedAgainAfterTheRetryTimeThenParkedWithOneCard()
    {
        await UpAndIdleAsync();
        // Dies at once, as a resume of a session claude cannot find does.
        _launcher.Process(0).Exit(1, "No conversation found");
        await Until(() => Row().State == SessionState.Failed, "the exit to show on its row");

        await _tower.LookAsync(Ct);
        _launcher.Started.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(11));
        await _tower.LookAsync(Ct);
        _launcher.Started.Count.ShouldBe(2);
        _launcher.Process(1).Exit(1, "No conversation found");
        await Until(() => Row().State == SessionState.Failed, "the second exit to show");

        _time.Advance(TimeSpan.FromMinutes(11));
        await _tower.LookAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(11));
        await _tower.LookAsync(Ct);

        // Parked: no third process on a timer, and one card says why.
        _launcher.Started.Count.ShouldBe(2);
        _state.Cards(openOnly: true).Count(c => c.Kind == Watchtower.Down && c.Bin == CardBin.Ring).ShouldBe(1);
    }

    [Fact]
    public async Task ACompactionIsSlashCompactOnItsStdinWhileIdleAndAGenerationOfItsOwn()
    {
        await UpAndIdleAsync();
        var before = _launcher.SessionOf(0);

        var compacted = await Compact();

        compacted.Ok.ShouldBeTrue(compacted.Error);
        compacted.Way.ShouldBe(SayWays.Streamed);
        _launcher.Process(0).Said.Last().ShouldBe("/compact keep the flights");
        _delivery.Resumes.ShouldBeEmpty();
        // Claude Code continues the conversation under a new session id after a compaction.
        const string After = "11111111-2222-4333-8444-555555555555";
        _launcher.Process(0).Emit(Init(After));
        // Between the new session's init and the compaction's result the row carries the new id already: a look then (the node's, or
        // this test's wait) must not write it onto the generation before (PIL770's CI, 2026-11-09).
        await Until(() => Row().SessionId == After, "the row to carry the compaction's session id");
        _tower.Generations().ShouldHaveSingleItem().SessionId.ShouldBe(before);
        _launcher.Process(0).Emit(Result);
        await Until(() => _tower.Generations().Count == 2, "the compaction to be a generation");

        var all = _tower.Generations();
        all[0].SessionId.ShouldBe(before);
        all[0].IsCompaction.ShouldBeFalse();
        all[1].Why.ShouldBe(Watchtower.Compacted);
        all[1].SessionId.ShouldBe(After);
        Row().SessionId.ShouldBe(After);
        Row().EarlierSessionIds.ShouldBe([before]);
        // A compaction is no fresh start: nothing churns, and no process was started.
        _launcher.Started.ShouldHaveSingleItem();
        _tower.Health()!.StartsLastHour.ShouldBe(1);
        _tower.Health()!.Churning.ShouldBeFalse();
    }

    [Fact]
    public async Task ACompactionIsRefusedWhileItWorksSinceItIsOnlyEverItsNextTurn()
    {
        await _tower.LookAsync(Ct);
        _launcher.Process(0).Emit(Init(_launcher.SessionOf(0)));

        var compacted = await Compact();

        compacted.Ok.ShouldBeFalse();
        compacted.Error.ShouldNotBeNull().ShouldContain("not idle");
        _launcher.Process(0).Said.ShouldHaveSingleItem();
        _tower.Generations().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AChangedBriefStartsAFreshWatchtowerAndEndsTheOldProcessThroughTheEndPath()
    {
        await UpAndIdleAsync();
        var old = Row();
        // The next node's brief reads differently from the one the running watchtower was started with (its record is in the data folder).
        var next = NewTower(pilot: "pilot --changed");

        await next.LookAsync(Ct);

        _launcher.Started.Count.ShouldBe(2);
        _launcher.Process(0).Killed.ShouldBeTrue();
        _launcher.Started[1].Arguments.ShouldContain("--session-id");
        _launcher.Process(1).Said.ShouldHaveSingleItem();
        _state.Sessions().Single(r => r.Id == old.Id).State.ShouldBe(SessionState.Ended);
        _ledger.Find([old.Id]).ShouldNotBeNull().EndReason.ShouldStartWith(SessionEndReasons.Watchtower);
        next.Current().ShouldNotBeNull().Id.ShouldNotBe(old.Id);
        // One live watchtower: the old one is ended, the new one is the role's.
        _state.Sessions().Count(r => r.Role == SessionRole.Watchtower && r.State is SessionState.Working or SessionState.Idle).ShouldBe(1);
        next.Generations().Count.ShouldBe(2);
    }

    [Fact]
    public async Task ANodeStopEndsTheProcessAndRecordsItAndTheNextNodeResumesTheSameSession()
    {
        await UpAndIdleAsync();
        var id = Row().Id;
        var session = _launcher.SessionOf(0);

        await _tower.StopAsync(Ct);

        _launcher.Process(0).Killed.ShouldBeTrue();
        var row = _state.Sessions().Single(r => r.Id == id);
        row.State.ShouldBe(SessionState.Ended);
        row.EndedBecause.ShouldBe(SessionEndReasons.NodeStopped);
        _ledger.Find([id]).ShouldNotBeNull().EndReason.ShouldBe(SessionEndReasons.NodeStopped);

        // The next node, in the same data folder, finds the generation the node's own stop ended and goes on with that conversation.
        var launcher = new FakeLauncher();
        var next = NewTower(launcher);
        await next.LookAsync(Ct);

        launcher.Started.ShouldHaveSingleItem();
        launcher.Started[0].Arguments.ShouldContain("--resume");
        launcher.SessionOf(0).ShouldBe(session);
        launcher.Process(0).Said.ShouldBeEmpty();
        next.Generations().Count(g => !g.IsCompaction).ShouldBe(1);
    }

    [Fact]
    public async Task AStartThatCannotRunClaudeRingsAndIsTriedAgainAfterTheRetryTime()
    {
        _launcher.Fail = new FileNotFoundException("claude is not on PATH");

        await _tower.LookAsync(Ct);

        _launcher.Started.ShouldBeEmpty();
        _state.Cards(openOnly: true).ShouldContain(c => c.Kind == Watchtower.Down);
        _launcher.Fail = null;
        _time.Advance(TimeSpan.FromMinutes(11));
        await _tower.LookAsync(Ct);
        _launcher.Started.ShouldHaveSingleItem();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == Watchtower.Down);
    }

    [Fact]
    public async Task TheTrackerLeavesAListedCopyOfTheStreamSessionAloneSoItIsNeverASecondWriterOfTheRow()
    {
        await UpAndIdleAsync();
        var row = Row();
        _agents.Json = AgentRows.List(AgentRows.Background(row.SessionId, "du1-watchtower", "running", pid: 1, status: "busy"));

        await _tracker.PollAsync(Ct);
        await _tracker.PollAsync(Ct);

        Row().ShouldBe(row);
        _state.Sessions().Count(r => r.Role == SessionRole.Watchtower).ShouldBe(1);
    }
}
