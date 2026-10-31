using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Landing a flight or merging its PR winds down its sessions by the node's own act (#542), and a sweep rings for any that outlive it.</summary>
public sealed class WindDownTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly SessionLedger _ledger;
    private readonly Launcher _workers;
    private readonly List<(string Id, string Text)> _told = [];
    private readonly List<(string Flight, string[] Owners, string Who)> _cancelled = [];
    private readonly FlightCommander _flights;
    private readonly List<CommandRequest> _said = [];

    public WindDownTests()
    {
        _state = new NodeState(new InMemoryJournal(), _time);
        _ledger = new SessionLedger(null, _time);
        _workers = new Launcher(_ledger);
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        _flights = new FlightCommander(_state, new Planes(new GitCli()), _time, _workers)
        {
            Ledger = _ledger,
            FinishGrace = TimeSpan.Zero,
            Say = (id, text, _) =>
            {
                // The reviewer is told while it still runs: the stop comes after.
                _workers.Stopped.ShouldBeEmpty();
                _told.Add((id, text));
                return Task.FromResult(true);
            },
            CancelJobs = (flight, owners, who) =>
            {
                _cancelled.Add((flight, [.. owners], who));
                return 2;
            },
        };
    }

    private string Repo => Path.Combine(_root, "pilot");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private FlightRow F(string code) => _state.Flights().Single(f => f.Code == code);

    // A flight with a reviewer running, as the node left it: its row, its ledger entry, its review state.
    private async Task<FlightRow> ReviewedFlightAsync()
    {
        var made = await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["542", "Wind down"], Who = "tests" }, Ct);
        made.Ok.ShouldBeTrue(made.Error);
        var flight = _state.Flights().Single();
        _ledger.Started("rev11111", "du1-pil542-review", SessionRoles.Resolve("du1-pil542-review", background: true) with { Flight = flight.Code }, SessionStartReasons.FlightReview, "opus");
        _state.PutSession(Row("rev11111", "du1-pil542-review", SessionRole.Reviewer, flight.Code, SessionState.Idle));
        _state.UpdateFlight(flight.Code, f => f with { Review = new ReviewRow { State = ReviewState.Reviewing, Reviewer = "du1-pil542-review", ReviewerJob = "rev11111", StartedAt = Start } });
        return F(flight.Code);
    }

    private static SessionRow Row(string id, string name, SessionRole role, string? flight, SessionState state) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = name,
        Kind = SessionKind.Background,
        Cwd = "/work",
        Role = role,
        Flight = flight,
        ContextTokens = 150_000,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 0,
        LastResponseAt = Start,
        StartedAt = Start,
    };

    [Fact]
    public async Task LandingAFlightTellsItsReviewerToFinishThenStopsItCancelsItsJobsAndEndsItsReview()
    {
        var flight = await ReviewedFlightAsync();

        var landed = await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct);

        landed.Ok.ShouldBeTrue(landed.Error);
        _told.ShouldHaveSingleItem().Id.ShouldBe("rev11111");
        _told[0].Text.ShouldContain("Finish now");
        _workers.Stopped.ShouldBe(["rev11111"]);
        var entry = _ledger.Find(["rev11111"]).ShouldNotBeNull();
        (entry.EndReason, entry.EndedBy).ShouldBe((SessionEndReasons.Landed, "land"));
        entry.EndReason.ShouldBe("stopped on land");
        var cancel = _cancelled.ShouldHaveSingleItem();
        cancel.Flight.ShouldBe(flight.Code);
        cancel.Owners.ShouldBe(["rev11111"]);
        F(flight.Code).Review.ShouldBeNull();
        F(flight.Code).State.ShouldBe(FlightState.Landed);
    }

    [Fact]
    public async Task NoLaterNudgeOrCompactionResumesASessionOfALandedFlight()
    {
        var flight = await ReviewedFlightAsync();
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct)).Ok.ShouldBeTrue();
        // The row is idle with plenty of context, as a stopped reviewer's is until the tracker sees it end, and it waits on a job.
        _state.PutSession(Row("rev11111", "du1-pil542-review", SessionRole.Reviewer, flight.Code, SessionState.Idle));
        _state.PutJob(new JobRow { Id = "j1", Command = "dotnet test", Cwd = "/work", Owner = "rev11111", State = JobState.Running, LogPath = "/tmp/j1.log", StartedAt = Start, Timeout = TimeSpan.FromHours(1) });
        var compacted = new List<CommandRequest>();
        var compactor = new Compactor(_state, new Watchtower(_state, _workers, Run, new NodeOptions("/tmp/n.sock", "/tmp/pilot2", MachinePrefix: "DU1"), _time),
            (c, _) => { compacted.Add(c); return Task.FromResult(_state.Complete(c, null)); }, _time) { Ledger = _ledger };
        _time.Advance(TimeSpan.FromHours(2));

        await compactor.LookAsync(Ct);
        var watch = new LegWatch(_state, Run, _time) { Ledger = _ledger };
        await watch.LookAsync(Ct);

        compacted.ShouldBeEmpty();
        _said.ShouldBeEmpty();
    }

    [Fact]
    public async Task MergingAFlightsPrWindsItsSessionsDownAsAMergeDoes()
    {
        var flight = await ReviewedFlightAsync();
        var head = Git(flight.Worktree, "rev-parse", "HEAD");

        await _flights.LandAfterMergeAsync(flight.Code, head, Ct);

        _told.ShouldHaveSingleItem().Id.ShouldBe("rev11111");
        _workers.Stopped.ShouldBe(["rev11111"]);
        _ledger.Find(["rev11111"])!.EndReason.ShouldBe(SessionEndReasons.Merged);
        _cancelled.ShouldHaveSingleItem().Flight.ShouldBe(flight.Code);
        F(flight.Code).Review.ShouldBeNull();
    }

    [Fact]
    public async Task TheSweepRingsOnceForALiveSessionOfALandedFlightAndOnceForOneTheLedgerDoesNotKnow()
    {
        var flight = await ReviewedFlightAsync();
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.Land, Target = Verbs.FlightTarget(flight.Code), Who = "controller" }, Ct)).Ok.ShouldBeTrue();
        // Still running after the land, and another nobody launched.
        _state.PutSession(Row("rev11111", "du1-pil542-review", SessionRole.Reviewer, flight.Code, SessionState.Working));
        _state.PutSession(Row("zzz99999", "someone-elses", SessionRole.AdHoc, null, SessionState.Working));
        var watch = new LegWatch(_state, Run, _time) { Ledger = _ledger };
        _time.Advance(TimeSpan.FromMinutes(20));

        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);
        await watch.LookAsync(Ct);

        var rings = _state.Cards(openOnly: true).Where(c => c.Kind == LegWatch.StraySession).ToArray();
        rings.Select(c => c.Subject).Order().ShouldBe(["session/rev11111", "session/zzz99999"]);
        rings.ShouldAllBe(c => c.Bin == CardBin.Ring);
        _state.Cards().Count(c => c.Kind == LegWatch.StraySession).ShouldBe(2);
    }

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    /// <summary>A launcher that records stops as the real one does, in the ledger first.</summary>
    private sealed class Launcher(SessionLedger ledger) : IWorkerLauncher
    {
        public List<string> Stopped { get; } = [];

        public Task<string> StartAsync(WorkerStart start, CancellationToken cancellationToken) => Task.FromResult("job00001");

        public Task StopAsync(string job, CancellationToken cancellationToken)
        {
            Stopped.Add(job);
            return Task.CompletedTask;
        }

        public Task StopAsync(string job, string reason, string by, CancellationToken cancellationToken)
        {
            ledger.Stopped([job], reason, by);
            return StopAsync(job, cancellationToken);
        }
    }
}
