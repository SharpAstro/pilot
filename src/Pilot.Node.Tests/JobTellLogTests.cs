using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Every job tell is logged with its outcome and the target's state, and the told row says when and how (#756).</summary>
public sealed class JobTellLogTests : IDisposable
{
    private readonly string _root = TestTemp.Create();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly StringWriter _log = new();
    private readonly JobRunner _jobs;
    private Func<CommandRequest, CommandResult> _say = _ => throw new InvalidOperationException("not set");

    public JobTellLogTests()
    {
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            Log = TextWriter.Synchronized(_log),
            Say = (command, _) => Task.FromResult(_say(command)),
        };
        _state.PutSession(new SessionRow
        {
            Id = "w0000001", SessionId = "w0000001-0000-4000-8000-000000000000", Name = "de1-tell-w1", Kind = SessionKind.Background, Cwd = _root,
            Role = SessionRole.Worker, Flight = "TELL1", Leg = "one", ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0, State = SessionState.Idle,
            LastResponseAt = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
        });
    }

    public void Dispose()
    {
        _jobs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            TestTemp.Delete(_root);
        }
        catch (IOException)
        {
        }
    }

    private JobRow Seed(string id)
    {
        var row = new JobRow
        {
            Id = id, Command = "echo hi", Cwd = _root, Owner = "w0000001", State = JobState.Succeeded, ExitCode = 0,
            LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(1),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        _state.PutJob(row);
        return row;
    }

    private string LineOf(string id) => _log.ToString().Split('\n').Single(l => l.Contains($"job tell {id} "));

    [Theory]
    [InlineData(SayWays.Relayed)]
    [InlineData(SayWays.Resumed)]
    public async Task ASaidTellIsLoggedWithItsWayAndTheRowRecordsWhenAndHow(string way)
    {
        _say = c => _state.Complete(c, null, way: way);
        var row = Seed("j1");

        await _jobs.TellAsync(row, TestContext.Current.CancellationToken);

        var line = LineOf("j1");
        line.ShouldContain("owner=w0000001");
        line.ShouldContain("target=w0000001");
        line.ShouldContain($"outcome={way}");
        line.ShouldContain("state=Idle");
        var told = _state.Jobs().Single(j => j.Id == "j1");
        told.Told.ShouldBeTrue();
        told.ToldWay.ShouldBe(way);
        told.ToldAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task ARefusedSayIsLoggedWithItsError()
    {
        _say = c => _state.Complete(c, "no such session");
        await _jobs.TellAsync(Seed("j2"), TestContext.Current.CancellationToken);

        var line = LineOf("j2");
        line.ShouldContain("outcome=refused: no such session");
        line.ShouldContain("state=Idle");
        _state.Jobs().Single(j => j.Id == "j2").Told.ShouldBeFalse();
    }

    [Fact]
    public async Task AThrowingSayIsLoggedAsFailed()
    {
        _say = _ => throw new InvalidOperationException("the say blew up");
        await _jobs.TellAsync(Seed("j3"), TestContext.Current.CancellationToken);

        var line = LineOf("j3");
        line.ShouldContain("outcome=failed: InvalidOperationException: the say blew up");
        line.ShouldContain("state=Idle");
        var row = _state.Jobs().Single(j => j.Id == "j3");
        row.Told.ShouldBeFalse();
        row.ToldAt.ShouldBeNull();
    }
}
