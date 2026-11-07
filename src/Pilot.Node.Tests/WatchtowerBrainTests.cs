using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The watchtower as the node's brain (PIL855): the model and effort of Settings passed on every start, crash restart and compaction,
/// a changed setting restarting it at its next idle in the same conversation, and the compactor writing /compact to an idle brain over
/// the context threshold, once, never to a working one. In memory over a fake claude process.
/// </summary>
public sealed class WatchtowerBrainTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pilot-wtbrain-" + Guid.NewGuid().ToString("N"));
    private readonly NodeOptions _options;
    private readonly WatchtowerStreamTests.FakeLauncher _launcher = new();
    private readonly SessionLedger _ledger;
    private readonly TempProfile _profile = new();
    private readonly FakeClaudeAgents _agents = new();
    private readonly SessionMessenger _messenger;
    private readonly Watchtower _tower;
    private readonly Compactor _compactor;

    public WatchtowerBrainTests()
    {
        _options = new NodeOptions("/tmp/n.sock", _data, MachinePrefix: "DU1");
        _state = new NodeState(new InMemoryJournal(), _time);
        _ledger = new SessionLedger(SessionLedger.FileIn(_data), _time);
        var tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, _time, NullLogger<SessionTracker>.Instance)
        {
            Launched = _ledger,
            IsStreamSession = id => _tower?.OwnsSession(id) == true,
        };
        _tower = new Watchtower(_state, new FakeWorkers(), Run, _options, _time)
        {
            Pilot = "pilot",
            Streams = _launcher,
            Ledger = _ledger,
            Error = new StringWriter(),
            Models = ModelsOf("opus-high..opus-high"),
        };
        _messenger = new SessionMessenger(_state, tracker, new FakeSessionDelivery(_agents), new StringWriter(), ledger: _ledger) { Streams = () => _tower };
        _compactor = new Compactor(_state, _tower, Run, _time) { Models = _tower.Models, Error = new StringWriter() };
        _state.PutFlight(new FlightRow
        {
            Code = "TIA9", Repo = "tianwen", RepoPath = "/r", Issue = 9, Branch = "flight/tia9", Worktree = "/r/w", Base = "main",
            State = FlightState.Flying, CreatedAt = Start,
        });
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying });
    }

    public void Dispose()
    {
        _profile.Dispose();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private static RoleModels ModelsOf(string watchtowerRange, params (string Key, string Value)[] more)
    {
        var settings = NodeSettings.With(new NodeSettings(), RoleModels.RangeKey(RoleModels.Watchtower), [watchtowerRange]).Settings!;
        foreach (var (key, value) in more)
        {
            settings = NodeSettings.With(settings, key, [value]).Settings!;
        }
        return new RoleModels(settings);
    }

    private async Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken) =>
        command.Verb switch
        {
            Verbs.Say => await _messenger.SayAsync(command, cancellationToken),
            Verbs.Compact => await _messenger.CompactAsync(command, cancellationToken),
            _ => _state.Complete(command, null),
        };

    private SessionRow Row() => _tower.Current().ShouldNotBeNull();

    private static string Init(string id) => $$"""{"type":"system","subtype":"init","session_id":"{{id}}","model":"opus"}""";

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

    /// <summary>Starts it, and its first reply (reading <paramref name="context"/> tokens) leaves it idle.</summary>
    private async Task UpAndIdleAsync(int context = 40_000)
    {
        await _tower.LookAsync(Ct);
        _launcher.Started.ShouldHaveSingleItem();
        _launcher.Process(0).Emit(Init(_launcher.SessionOf(0)));
        _launcher.Process(0).Emit(Assistant(100, context));
        _launcher.Process(0).Emit(Result);
        await Until(() => Row() is { State: SessionState.Idle } row && row.ContextTokens == context + 100, "the first reply to leave it idle");
    }

    private static string[] ModelAndEffort(IReadOnlyList<string> arguments) =>
        [arguments[arguments.ToList().IndexOf("--model") + 1], arguments[arguments.ToList().IndexOf("--effort") + 1]];

    private static int Compactions(WatchtowerStreamTests.FakeProcess process) => process.Said.Count(s => s.StartsWith("/compact", StringComparison.Ordinal));

    [Fact]
    public async Task TheModelAndEffortOfSettingsArePassedOnStartAndOnACrashRestart()
    {
        await UpAndIdleAsync();
        ModelAndEffort(_launcher.Started[0].Arguments).ShouldBe(["opus", "high"]);

        _time.Advance(TimeSpan.FromMinutes(40));
        _launcher.Process(0).Exit(1, "socket hang up");
        await Until(() => Row().State == SessionState.Failed, "the exit to show on its row");
        await _tower.LookAsync(Ct);

        _launcher.Started.Count.ShouldBe(2);
        _launcher.Started[1].Arguments.ShouldContain("--resume");
        ModelAndEffort(_launcher.Started[1].Arguments).ShouldBe(["opus", "high"]);
    }

    [Fact]
    public async Task ACrashRestartAfterAChangedSettingRunsOnTheNewModelAndEffort()
    {
        await UpAndIdleAsync();
        _tower.Models = ModelsOf("sonnet-low..sonnet-low");
        _time.Advance(TimeSpan.FromMinutes(40));
        // Dies mid-turn, before an idle look could restart it on the setting.
        _launcher.Process(0).Exit(1, "socket hang up");
        await Until(() => Row().State == SessionState.Failed, "the exit to show on its row");
        await _tower.LookAsync(Ct);

        _launcher.Started.Count.ShouldBe(2);
        ModelAndEffort(_launcher.Started[1].Arguments).ShouldBe(["sonnet", "low"]);
    }

    [Fact]
    public async Task ACompactionIsOnTheSameProcessSoTheModelStaysAndTheRestartAfterItPassesItAgain()
    {
        await UpAndIdleAsync();

        var compacted = await _messenger.CompactAsync(new CommandRequest { Verb = Verbs.Compact, Target = Verbs.SessionTarget(Row().Id), Args = ["keep the flights"], Who = LegWatch.Who }, Ct);
        compacted.Ok.ShouldBeTrue(compacted.Error);
        _launcher.Process(0).Emit(Init("11111111-2222-4333-8444-555555555555"));
        _launcher.Process(0).Emit(Result);
        await Until(() => _tower.Generations().Count == 2, "the compaction to be a generation");

        // No process was started for it: it runs on what it was started with, and a crash after it resumes on the setting.
        _launcher.Started.ShouldHaveSingleItem();
        ModelAndEffort(_launcher.Started[0].Arguments).ShouldBe(["opus", "high"]);
        _time.Advance(TimeSpan.FromMinutes(40));
        _launcher.Process(0).Exit(1);
        await Until(() => Row().State == SessionState.Failed, "the exit to show on its row");
        await _tower.LookAsync(Ct);
        _launcher.Started.Count.ShouldBe(2);
        _launcher.SessionOf(1).ShouldBe("11111111-2222-4333-8444-555555555555");
        ModelAndEffort(_launcher.Started[1].Arguments).ShouldBe(["opus", "high"]);
    }

    [Fact]
    public async Task AChangedModelOrEffortRestartsItOnResumeAtItsNextIdleAndNotBefore()
    {
        await _tower.LookAsync(Ct);
        _launcher.Process(0).Emit(Init(_launcher.SessionOf(0)));
        await Until(() => Row().State == SessionState.Working, "its first turn");
        var session = _launcher.SessionOf(0);
        _tower.Models = ModelsOf("sonnet-medium..sonnet-medium");

        // Mid-turn: nothing happens.
        _time.Advance(TimeSpan.FromMinutes(1));
        await _tower.LookAsync(Ct);
        _launcher.Started.ShouldHaveSingleItem();
        _launcher.Process(0).Killed.ShouldBeFalse();

        // Idle: the same conversation goes on, on the new model and effort.
        _launcher.Process(0).Emit(Result);
        await Until(() => Row().State == SessionState.Idle, "it to be idle");
        await _tower.LookAsync(Ct);

        _launcher.Started.Count.ShouldBe(2);
        _launcher.Started[1].Arguments.ShouldContain("--resume");
        _launcher.SessionOf(1).ShouldBe(session);
        ModelAndEffort(_launcher.Started[1].Arguments).ShouldBe(["sonnet", "medium"]);
        _launcher.Process(1).Said.ShouldHaveSingleItem().ShouldContain("sonnet");
        _tower.Generations().ShouldHaveSingleItem();
        Row().Model.ShouldBe("sonnet");
        Row().Effort.ShouldBe("medium");

        // Settled: a further look does not restart it again.
        _launcher.Process(1).Emit(Init(session));
        _launcher.Process(1).Emit(Result);
        await Until(() => Row().State == SessionState.Idle, "it to be idle again");
        await _tower.LookAsync(Ct);
        _launcher.Started.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AChangedEffortAloneRestartsItToo()
    {
        await UpAndIdleAsync();
        _tower.Models = ModelsOf("opus-xhigh..opus-xhigh");

        await _tower.LookAsync(Ct);

        _launcher.Started.Count.ShouldBe(2);
        ModelAndEffort(_launcher.Started[1].Arguments).ShouldBe(["opus", "xhigh"]);
    }

    [Fact]
    public async Task AnIdleBrainOverTheContextThresholdIsSentSlashCompactOnce()
    {
        await UpAndIdleAsync(context: 150_000);

        // Not yet idle for long enough.
        _time.Advance(TimeSpan.FromMinutes(40));
        await _compactor.LookAsync(Ct);
        Compactions(_launcher.Process(0)).ShouldBe(0);

        _time.Advance(TimeSpan.FromMinutes(11));
        await _compactor.LookAsync(Ct);
        Compactions(_launcher.Process(0)).ShouldBe(1);
        _launcher.Process(0).Said.Last().ShouldStartWith("/compact");

        // Its compaction is its turn now: the next look finds it working and writes nothing more.
        await _compactor.LookAsync(Ct);
        Compactions(_launcher.Process(0)).ShouldBe(1);
        _launcher.Started.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AWorkingBrainIsNeverSentSlashCompact()
    {
        await UpAndIdleAsync(context: 150_000);
        var said = await _messenger.SayAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(Row().Id), Args = ["how are the flights?"], Who = "gui" }, Ct);
        said.Ok.ShouldBeTrue(said.Error);
        Row().State.ShouldBe(SessionState.Working);

        _time.Advance(TimeSpan.FromHours(3));
        await _compactor.LookAsync(Ct);

        Compactions(_launcher.Process(0)).ShouldBe(0);
        _launcher.Process(0).Said.Last().ShouldBe("how are the flights?");
    }

    [Fact]
    public async Task AnIdleBrainUnderTheThresholdIsLeftAloneHoweverLongItIsIdle()
    {
        await UpAndIdleAsync(context: 40_000);

        _time.Advance(TimeSpan.FromHours(3));
        await _compactor.LookAsync(Ct);

        Compactions(_launcher.Process(0)).ShouldBe(0);
    }

    [Fact]
    public async Task AnIdleBrainWithNothingLeftToWatchIsCompactedToo()
    {
        await UpAndIdleAsync(context: 150_000);
        _state.PutLeg(new LegRow { Flight = "TIA9", Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Done });
        _state.PutFlight(_state.Flights().Single() with { State = FlightState.Landed, LandedAt = _time.GetUtcNow() });
        _tower.Keeping().ShouldBeNull();

        _time.Advance(TimeSpan.FromMinutes(51));
        await _compactor.LookAsync(Ct);

        Compactions(_launcher.Process(0)).ShouldBe(1);
    }

    [Fact]
    public async Task TheIdleTimeAndTheContextThresholdAreSettings()
    {
        var models = ModelsOf("opus-high..opus-high", (NodeSettings.WatchtowerCompactIdleKey, "10"), (NodeSettings.WatchtowerCompactAboveKey, "20"));
        _tower.Models = models;
        _compactor.Models = models;
        await UpAndIdleAsync(context: 30_000);

        _time.Advance(TimeSpan.FromMinutes(11));
        await _compactor.LookAsync(Ct);

        Compactions(_launcher.Process(0)).ShouldBe(1);
        models.WatchtowerCompactIdle.ShouldBe(TimeSpan.FromMinutes(10));
        models.WatchtowerCompactAbove.ShouldBe(20_000);
        var defaults = new RoleModels(new NodeSettings());
        (defaults.WatchtowerCompactIdle, defaults.WatchtowerCompactAbove).ShouldBe((TimeSpan.FromMinutes(50), 100_000));
    }

    [Theory]
    [InlineData(NodeSettings.WatchtowerCompactIdleKey, "0")]
    [InlineData(NodeSettings.WatchtowerCompactIdleKey, "soon")]
    [InlineData(NodeSettings.WatchtowerCompactAboveKey, "-1")]
    [InlineData(NodeSettings.WatchtowerCompactAboveKey, "lots")]
    public void ABadIdleTimeOrThresholdIsRefusedAndAClearReturnsTheDefault(string key, string value)
    {
        NodeSettings.With(new NodeSettings(), key, [value]).Error.ShouldNotBeNull();

        var set = NodeSettings.With(new NodeSettings(), key, ["30"]).Settings!;
        set.Get(key).ShouldBe(["30"]);
        NodeSettings.With(set, key, ["default"]).Settings!.Get(key).ShouldBeNull();
    }
}
