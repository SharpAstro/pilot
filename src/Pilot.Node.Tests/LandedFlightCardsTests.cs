using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A landing closes every open card it settles with "&lt;code&gt; landed" (pilot #1270): beside the flight's and legs' cards, those about its
/// sessions, its jobs and the strays of its sessions' processes; cards of other subjects, and a review's notes, stay open. Fakes only for the
/// processes, jobs and sessions; the plane is a real git worktree, as <see cref="LandedFlightStrayTests"/> has it.
/// </summary>
[Trait("Category", "Slow")]
public sealed class LandedFlightCardsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Began = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly ManualTime _time = new(Began.AddHours(1));
    private readonly NodeState _state;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2c-" + Guid.NewGuid().ToString("N")[..8]);

    public LandedFlightCardsTests() => _state = new NodeState(new InMemoryJournal(), _time);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try
            {
                Git(Path.Combine(_root, "pilot"), "worktree", "prune");
            }
            catch (InvalidOperationException)
            {
            }
            TestDirs.DeleteTree(_root);
        }
    }

    private static void Git(string cwd, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = TestEnvironment.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
        }
    }

    private void PutSession(string id, string? flight, string cwd) => _state.PutSession(new SessionRow
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = "de1-" + id, Kind = SessionKind.Background, Cwd = cwd, Flight = flight,
        Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 200_000, State = SessionState.Ended, SpendWeighted = 0, StartedAt = Began,
        EndedAt = Began.AddMinutes(5), EndReason = SessionEndReasons.Landed,
    });

    private void PutJob(string id, string? flight, string? owner) => _state.PutJob(new JobRow
    {
        Id = id, Command = "dotnet test", Cwd = _root, State = JobState.Succeeded, StartedAt = Began, EndedAt = Began.AddMinutes(1), Flight = flight, Owner = owner,
        LogPath = Path.Combine(_root, id + ".log"), Timeout = TimeSpan.FromMinutes(30),
    });

    private void Card(string kind, string subject, CardBin bin = CardBin.Ring) => _state.RaiseCard(bin, kind, subject, kind + " of " + subject);

    private CardRow Of(string kind, string subject) => _state.Cards().Single(c => c.Kind == kind && c.Subject == subject);

    [Fact]
    public async Task ALandingClosesTheCardsOfItsSessionsJobsAndLegsAndKeepsEveryOtherSubjectsOpen()
    {
        var repo = Path.Combine(_root, "pilot");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var flights = new FlightCommander(_state, new Planes(new GitCli()), _time);
        var made = await flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(repo), Args = ["1270", "Cards"], Who = "tests" }, Ct);
        made.Ok.ShouldBeTrue(made.Error);
        var flight = _state.Flights().Single();
        var code = flight.Code;

        // Its own: a session, two jobs (one owned by the session), a cap on the flight, a push-blocked leg, and review notes.
        PutSession("w1", code, flight.Worktree);
        PutJob("j1", code, "w1");
        PutJob("j2", code, "w1");
        Card(SessionMessenger.StopUnconfirmedKind, Verbs.SessionTarget("w1"));
        Card(JobRunner.LongJobKind, Verbs.JobTarget("j1"));
        Card(JobRunner.UnansweredKind, Verbs.JobTarget("j2"));
        Card(CiBudgets.CapKind, Verbs.FlightTarget(code));
        Card(LegCommander.PushBlockedKind, Verbs.LegTarget(code, "leg1"));
        Card(LegCommander.ReviewNotesKind, Verbs.FlightTarget(code));

        // Not its own: another flight, its session and job, and a person's own job.
        _state.PutFlight(new FlightRow
        {
            Code = "OTH1", Repo = "pilot", RepoPath = repo, Issue = 1, Branch = "flight/oth1", Worktree = Path.Combine(_root, "oth1"), Base = "origin/main",
            State = FlightState.Flying, CreatedAt = Began,
        });
        PutSession("w9", "OTH1", Path.Combine(_root, "oth1"));
        PutJob("j9", "OTH1", "w9");
        PutJob("jp", null, null);
        Card(CiBudgets.CapKind, Verbs.FlightTarget("OTH1"));
        Card(LegCommander.PushBlockedKind, Verbs.LegTarget("OTH1", "leg1"));
        Card(SessionMessenger.StopUnconfirmedKind, Verbs.SessionTarget("w9"));
        Card(JobRunner.LongJobKind, Verbs.JobTarget("j9"));
        Card(JobRunner.LongJobKind, Verbs.JobTarget("jp"));

        var landed = await flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(code), Who = "controller" }, Ct);

        landed.Ok.ShouldBeTrue(landed.Error);
        var outcome = $"{code} landed";
        Of(SessionMessenger.StopUnconfirmedKind, Verbs.SessionTarget("w1")).Outcome.ShouldBe(outcome);
        Of(JobRunner.LongJobKind, Verbs.JobTarget("j1")).Outcome.ShouldBe(outcome);
        Of(JobRunner.UnansweredKind, Verbs.JobTarget("j2")).Outcome.ShouldBe(outcome);
        Of(CiBudgets.CapKind, Verbs.FlightTarget(code)).Outcome.ShouldBe(outcome);
        Of(LegCommander.PushBlockedKind, Verbs.LegTarget(code, "leg1")).Outcome.ShouldBe(outcome);
        Of(LegCommander.ReviewNotesKind, Verbs.FlightTarget(code)).State.ShouldBe(CardState.Open);

        Of(CiBudgets.CapKind, Verbs.FlightTarget("OTH1")).State.ShouldBe(CardState.Open);
        Of(LegCommander.PushBlockedKind, Verbs.LegTarget("OTH1", "leg1")).State.ShouldBe(CardState.Open);
        Of(SessionMessenger.StopUnconfirmedKind, Verbs.SessionTarget("w9")).State.ShouldBe(CardState.Open);
        Of(JobRunner.LongJobKind, Verbs.JobTarget("j9")).State.ShouldBe(CardState.Open);
        Of(JobRunner.LongJobKind, Verbs.JobTarget("jp")).State.ShouldBe(CardState.Open);
    }
}
