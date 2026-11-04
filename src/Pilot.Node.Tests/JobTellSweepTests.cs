using System.Collections.Concurrent;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The JobRunner's periodic pass tells every ended, owned, untold row once (#700).</summary>
public sealed class JobTellSweepTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateTempSubdirectory("tell-sweep-").FullName;
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly ConcurrentQueue<CommandRequest> _said = new();
    private readonly JobRunner _jobs;
    private bool _refuse;
    private int _throwTimes;

    public JobTellSweepTests()
    {
        _jobs = new JobRunner(_state, new NodeOptions(Path.Combine(_root, "n.sock"), Path.Combine(_root, "data")), TimeProvider.System)
        {
            PumpEvery = TimeSpan.FromMilliseconds(50),
            Say = (command, _) =>
            {
                _said.Enqueue(command);
                if (_throwTimes > 0)
                {
                    _throwTimes--;
                    throw new InvalidOperationException("the say blew up");
                }
                return Task.FromResult(_refuse ? _state.Complete(command, "refused") : _state.Complete(command, null));
            },
        };
    }

    public void Dispose()
    {
        _jobs.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Seed(string id, JobKind kind = JobKind.Run, string? owner = "w0000001") =>
        _state.PutJob(new JobRow
        {
            Id = id, Command = "echo hi", Cwd = _root, Owner = owner, Kind = kind, State = JobState.Succeeded, ExitCode = 0,
            LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(1),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2), EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

    private async Task<JobRow> Until(string id, Func<JobRow, bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (_state.Jobs().Single(j => j.Id == id) is var row && condition(row))
            {
                return row;
            }
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException(id);
    }

    [Fact]
    public async Task AnEndedUntoldRowIsToldByTheSweepOnceAndNotAgain()
    {
        Seed("j1");
        Seed("j2", JobKind.Check);
        Seed("j3", owner: null);
        await _jobs.StartAsync(CancellationToken.None);

        await Until("j1", j => j.Told);
        await Task.Delay(400, Ct);

        _said.Count.ShouldBe(1);
        _state.Jobs().Single(j => j.Id == "j2").Told.ShouldBeFalse();
        _state.Jobs().Single(j => j.Id == "j3").Told.ShouldBeFalse();
    }

    [Fact]
    public async Task ARefusingOwnerRingsOneCardAcrossSeveralTicks()
    {
        _refuse = true;
        Seed("j1");
        await _jobs.StartAsync(CancellationToken.None);

        await Task.Delay(600, Ct);

        _said.Count.ShouldBe(1);
        var card = _state.Cards(openOnly: true).ShouldHaveSingleItem();
        (card.Kind, card.Subject).ShouldBe((JobRunner.UntoldKind, Verbs.JobTarget("j1")));
        _state.Jobs().Single(j => j.Id == "j1").Told.ShouldBeFalse();
    }

    [Fact]
    public async Task ASayThatThrowsOnceRingsOnceAndIsToldByALaterSweep()
    {
        _throwTimes = 1;
        Seed("j1");
        await _jobs.StartAsync(CancellationToken.None);

        await Until("j1", j => j.Told);
        await Task.Delay(300, Ct);

        _said.Count.ShouldBe(2);
        _state.Cards(openOnly: true).ShouldHaveSingleItem().Kind.ShouldBe(JobRunner.UntoldKind);
    }

    [Fact]
    public async Task TellUntoldOfTellsOnlyTheNamedOwnersJobs()
    {
        Seed("j1", owner: "w0000001");
        Seed("j2", owner: "w0000002");
        _jobs.PumpEvery.ShouldBe(TimeSpan.FromMilliseconds(50));

        (await _jobs.TellUntoldOf(["w0000002"], Ct)).ShouldBeTrue();

        _state.Jobs().Single(j => j.Id == "j2").Told.ShouldBeTrue();
        _state.Jobs().Single(j => j.Id == "j1").Told.ShouldBeFalse();
        (await _jobs.TellUntoldOf(["w0000002"], Ct)).ShouldBeFalse();
    }
}
