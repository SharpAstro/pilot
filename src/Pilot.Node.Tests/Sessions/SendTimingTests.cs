using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>The node times its session starts, resumes and relays, keeps an hour of them, and says median and slowest in <c>pilot status</c> (PIL584).</summary>
public sealed class SendTimingTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly StringWriter _log = new StringWriter();
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;

    public SendTimingTests()
    {
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance);
        _delivery = new FakeSessionDelivery(_agents);
    }

    public void Dispose() => _profile.Dispose();

    private string Cwd => _profile.Folder.Replace("\\", "\\\\", StringComparison.Ordinal);

    private static long Recent => DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();

    private static CommandRequest Say(string id, string text) => new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(id), Args = [text], Who = "tests" };

    [Fact]
    public void AStartIsRecordedWithItsKindAndDurationAndLogged()
    {
        var timings = new SendTimings(TimeProvider.System, _log);

        timings.Record(SendTimings.Start, TimeSpan.FromMilliseconds(1500), "name=de1-x job=abc");

        var row = timings.Rows().ShouldNotBeNull().ShouldHaveSingleItem();
        row.ShouldBe(new SendTimingRow { Kind = "start", Count = 1, MedianMs = 1500, SlowestMs = 1500 });
        _log.ToString().ShouldContain("timing: start name=de1-x job=abc (1500 ms)");
    }

    [Fact]
    public async Task AResumedSayIsRecordedAsAResumeAndARelayedSayAsARelay()
    {
        var timings = new SendTimings(TimeProvider.System, _log);
        var messenger = new SessionMessenger(_state, _tracker, _delivery, _log) { Timings = timings };
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", pid: 4343, status: "idle", cwd: Cwd));
        _delivery.ListingAfterStop = AgentRows.List(AgentRows.Background(AgentRows.SessionB, "pil5-say-bg", "done", cwd: Cwd, startedAt: Recent));
        _delivery.DuringResume = () => Task.Delay(40, Ct);
        await _tracker.PollAsync(Ct);

        (await messenger.SayAsync(Say("b2c3d4e5", "wake"), Ct)).Way.ShouldBe(SayWays.Resumed);

        var resume = timings.Rows().ShouldNotBeNull().ShouldHaveSingleItem();
        resume.Kind.ShouldBe("resume");
        resume.Count.ShouldBe(1);
        resume.SlowestMs.ShouldBeGreaterThanOrEqualTo(30);

        // A session mid-turn is relayed to.
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionA, "pil5-relay-bg", "working", pid: 4344, status: "busy", cwd: Cwd));
        await _tracker.PollAsync(Ct);
        (await messenger.SayAsync(Say("a1b2c3d4", "note"), Ct)).Way.ShouldBe(SayWays.Relayed);

        timings.Rows().ShouldNotBeNull().Select(r => (r.Kind, r.Count)).ShouldBe([("resume", 1), ("relay", 1)]);
        _log.ToString().ShouldContain("timing: relay target=session/a1b2c3d4");
    }

    [Fact]
    public void EntriesOlderThanAnHourDropOutAndTheRowHasMedianAndSlowestPerKind()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var timings = new SendTimings(clock);
        timings.Record(SendTimings.Resume, TimeSpan.FromSeconds(90));

        clock.Now = clock.Now.AddMinutes(30);
        foreach (var s in new[] { 10, 20, 60 })
        {
            timings.Record(SendTimings.Resume, TimeSpan.FromSeconds(s));
        }
        timings.Record(SendTimings.Relay, TimeSpan.FromMilliseconds(200));

        // 31 minutes later the 90 s one is 61 minutes old.
        clock.Now = clock.Now.AddMinutes(31);
        var resume = timings.Rows().ShouldNotBeNull().Single(r => r.Kind == "resume");
        resume.ShouldBe(new SendTimingRow { Kind = "resume", Count = 3, MedianMs = 20_000, SlowestMs = 60_000 });

        clock.Now = clock.Now.AddMinutes(60);
        timings.Rows().ShouldBeNull();
    }

    [Fact]
    public void StatusPrintsTheMedianAndSlowestAndSaysNoneWhenThereWereNone()
    {
        SendTimingRow[] rows = [new() { Kind = "resume", Count = 4, MedianMs = 27_000, SlowestMs = 41_200 }, new() { Kind = "relay", Count = 2, MedianMs = 300, SlowestMs = 450 }];
        var snapshot = new SnapshotEvent { Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [] };

        PilotCommand.Status(snapshot, sendTimings: rows).ShouldContain("sends  resume: median 27.0 s, slowest 41.2 s (4); relay: median 300 ms, slowest 450 ms (2)");
        StatusMarkdown.Build(snapshot, sendTimings: rows).ShouldContain("- **sends** resume: median 27.0 s, slowest 41.2 s (4); relay: median 300 ms, slowest 450 ms (2)");
        PilotCommand.Status(snapshot, sendTimings: []).ShouldContain("sends  none in the last hour");
        StatusMarkdown.Build(snapshot, sendTimings: []).ShouldContain("**sends** none in the last hour");
    }

    [Fact]
    public void TheRowsSurviveTheNodeInfoJsonRoundTrip()
    {
        var info = new NodeInfo
        {
            WireVersion = NodeWire.Version, Version = "1", MachineName = "m", ProcessId = 1, StartedAt = DateTimeOffset.UnixEpoch, DataFolder = "d", SocketPath = "s", Stream = "x",
            SendTimings = [new SendTimingRow { Kind = "start", Count = 1, MedianMs = 5, SlowestMs = 5 }],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(info, PilotJsonContext.Default.NodeInfo);
        System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.NodeInfo)!.SendTimings.ShouldBe(info.SendTimings);
    }
}
