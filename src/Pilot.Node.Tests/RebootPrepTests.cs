using Pilot.Contracts;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A reboot made ready (#916, <see cref="RebootPrep"/>): maintenance mode, each live session the node started told once to save its
/// state, a file arriving ending its session, a silent one ended at the bound with the node's own file, ready only when all are in,
/// and cancel and done leaving maintenance mode. In memory, on a clock the test moves; the files go in the test run's temp folder.
/// </summary>
public sealed class RebootPrepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset At = new(2026, 11, 9, 19, 12, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(At);
    private readonly NodeState _state;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "reboot-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<(string Id, string Text)> _said = [];
    private readonly List<(string Id, string Why)> _ended = [];
    private readonly List<bool> _maintenance = [];

    public RebootPrepTests() => _state = new NodeState(new InMemoryJournal(), _time);

    private RebootPrep Prep() => new(_state, _time, _folder)
    {
        Say = (id, text, _) => { _said.Add((id, text)); return Task.FromResult(true); },
        End = (id, why, _) => { _ended.Add((id, why)); return Task.CompletedTask; },
        SetMaintenance = _maintenance.Add,
    };

    private void Session(string id, string name, SessionRole role, string? flight, string? startReason, SessionKind kind = SessionKind.Background, SessionState state = SessionState.Working) =>
        _state.PutSession(new SessionRow
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = kind, Cwd = ".", Role = role, ContextTokens = 0, ContextWindow = 200_000,
            State = state, SpendWeighted = 0, StartedAt = At.AddHours(-1), StartReason = startReason, Flight = flight,
        });

    private void ThreeSessions()
    {
        Session("w1", "se1-pil895-teardown", SessionRole.Worker, "PIL895", SessionStartReasons.LegLaunch);
        Session("f1", "se1-fix-pilot-887", SessionRole.AdHoc, null, SessionStartReasons.PrFix);
        Session("t1", "se1-watchtower", SessionRole.Watchtower, null, "watchtower start");
        // Not the node's: a person's interactive session, an ended one.
        Session("p1", "se1-pilot-be", SessionRole.AdHoc, null, null, SessionKind.Interactive);
        Session("e1", "se1-old", SessionRole.Worker, "PIL1", SessionStartReasons.LegLaunch, state: SessionState.Ended);
    }

    [Fact]
    public async Task ARequestTurnsMaintenanceOnAndTellsEachLiveNodeSessionOnce()
    {
        ThreeSessions();
        var prep = Prep();

        await prep.RequestAsync("seb", "kernel paged pool leak", Ct);
        await prep.RequestAsync("seb", "asked twice", Ct);

        _maintenance.ShouldBe([true]);
        _said.Select(s => s.Id).Order().ShouldBe(["f1", "t1", "w1"]);
        _said.Single(s => s.Id == "w1").Text.ShouldContain(Path.Combine(_folder, $"flight-PIL895-reboot-{RebootPrep.Stamp(At)}.md"));
        _said.Single(s => s.Id == "t1").Text.ShouldContain("watchtower-reboot-");
        _said.ShouldAllBe(s => s.Text.Contains("commit and push") && s.Text.Contains("the exact next step"));
        prep.Ready.ShouldBeFalse();
        prep.Status().ShouldStartWith("Preparing to reboot");
        _state.Cards().ShouldContain(c => c.Kind == RebootPrep.Kind && c.Title.StartsWith("Preparing to reboot: 3 sessions"));
    }

    [Fact]
    public async Task AFileArrivingEndsItsSessionAndTheReadyCardComesOnlyWhenAllAreIn()
    {
        ThreeSessions();
        var prep = Prep();
        await prep.RequestAsync("seb", "", Ct);

        foreach (var expected in prep.Sessions.Take(2))
        {
            File.WriteAllText(expected.File, "# state");
        }
        await prep.TickAsync(Ct);

        _ended.Count.ShouldBe(2);
        prep.Ready.ShouldBeFalse();
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == RebootPrep.ReadyKind);

        File.WriteAllText(prep.Sessions.Single(e => e.Outcome is null).File, "# state");
        await prep.TickAsync(Ct);

        prep.Ready.ShouldBeTrue();
        _ended.Count.ShouldBe(3);
        _state.Cards(openOnly: true).Single(c => c.Kind == RebootPrep.ReadyKind).Title.ShouldStartWith("Ready to reboot");
        prep.Status().ShouldStartWith("Ready to reboot: 3 sessions saved, 0 running");
    }

    [Fact]
    public async Task ASilentSessionIsEndedAtTheBoundWithAFileTheNodeWritesFromWhatItKnows()
    {
        Session("w1", "se1-pil895-teardown", SessionRole.Worker, "PIL895", SessionStartReasons.LegLaunch);
        var prep = Prep();
        await prep.RequestAsync("seb", "", Ct);

        _time.Advance(prep.Bound - TimeSpan.FromSeconds(1));
        await prep.TickAsync(Ct);
        _ended.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(2));
        await prep.TickAsync(Ct);

        var expected = prep.Sessions.Single();
        expected.Outcome.ShouldBe("written by the node");
        File.ReadAllText(expected.File).ShouldContain("written by the node");
        _ended.ShouldHaveSingleItem().Id.ShouldBe("w1");
        prep.Ready.ShouldBeTrue();
    }

    [Fact]
    public async Task CancelAndDoneLeaveMaintenanceMode()
    {
        Session("w1", "se1-pil895-teardown", SessionRole.Worker, "PIL895", SessionStartReasons.LegLaunch);
        var prep = Prep();
        await prep.RequestAsync("seb", "", Ct);

        prep.Cancel("seb").Error.ShouldBeNull();
        _maintenance.ShouldBe([true, false]);
        prep.Requested.ShouldBeFalse();
        prep.Cancel("seb").Error.ShouldNotBeNull("nothing to cancel");

        await prep.RequestAsync("seb", "", Ct);
        File.WriteAllText(prep.Sessions.Single().File, "# state");
        await prep.TickAsync(Ct);
        prep.Done("seb").Error.ShouldBeNull();
        _maintenance.ShouldBe([true, false, true, false]);
        _state.Cards().ShouldContain(c => c.Subject == Verbs.FlightTarget("PIL895") && c.Title.Contains("resumes from its reboot file"));
    }
}
