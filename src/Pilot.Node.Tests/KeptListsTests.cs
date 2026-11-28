using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node's newest-first lists are sorted once and kept until what they list changes (pilot #1443, #1365's second point): Jobs() and
/// Sessions() sorted every row at every call, a key array on the large object heap each time on a node of thousands of jobs.
/// </summary>
public sealed class KeptListsTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);

    private static JobRow Job(string id, DateTimeOffset started, JobState state = JobState.Running) => new JobRow
    {
        Id = id, Command = "dotnet test", Cwd = "/repo", State = state, LogPath = $"/logs/{id}.log", Timeout = TimeSpan.FromMinutes(30), StartedAt = started,
    };

    private static SessionRow Session(string id, DateTimeOffset started) => new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "session " + id, Kind = SessionKind.Background, Cwd = "/work/" + id,
        Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Idle, SpendWeighted = 0, StartedAt = started,
    };

    [Fact]
    public void TheJobsAreSortedOnceAndSortedAgainWhenOneIsAddedOrChanges()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutJob(Job("j1", At));
        state.PutJob(Job("j2", At.AddMinutes(5)));

        var first = state.Jobs();
        first.Select(j => j.Id).ShouldBe(["j2", "j1"]);
        state.Jobs().ShouldBeSameAs(first);

        state.PutJob(Job("j3", At.AddMinutes(10)));
        var added = state.Jobs();
        added.ShouldNotBeSameAs(first);
        added.Select(j => j.Id).ShouldBe(["j3", "j2", "j1"]);

        state.PutJob(Job("j1", At, JobState.Succeeded));
        state.Jobs().Single(j => j.Id == "j1").State.ShouldBe(JobState.Succeeded);
    }

    [Fact]
    public void TheSessionsAreSortedOnceAndSortedAgainWhenOneIsAddedChangesOrGoes()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutSession(Session("a", At));
        state.PutSession(Session("b", At.AddMinutes(1)));

        var first = state.Sessions();
        first.Select(s => s.Id).ShouldBe(["b", "a"]);
        state.Sessions().ShouldBeSameAs(first);

        state.PutSession(Session("a", At) with { State = SessionState.Working });
        var changed = state.Sessions();
        changed.ShouldNotBeSameAs(first);
        changed.Single(s => s.Id == "a").State.ShouldBe(SessionState.Working);
    }
}
