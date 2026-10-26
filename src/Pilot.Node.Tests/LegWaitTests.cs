using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A leg waits on a file or a time without a job (<c>pilot leg wait</c>): its worker ends its turn, and the node's watch wakes it
/// once when the file exists or the time comes, rings when a file wait runs past its --until, and does not nudge a worker idle on
/// a leg that waits. Real git repo and commanders, fake time, workers and a temp file.
/// </summary>
public sealed class LegWaitTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly LegCommander _legs;
    private readonly LegWatch _watch;
    private readonly List<CommandRequest> _said = [];

    public LegWaitTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        _state = new NodeState(new InMemoryJournal(), _time);
        var git = new GitCli();
        var flights = new FlightCommander(_state, new Planes(git), _time);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, _workers, new FakeChecks(), new FakeGitHub(), git, new Planes(git), flights, options, _time);
        _watch = new LegWatch(_state, Run, _time);
        flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct).GetAwaiter().GetResult().Ok.ShouldBeTrue();
        Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse", "done-when=It parses").GetAwaiter().GetResult().Ok.ShouldBeTrue();
        Leg(Verbs.StartLeg, "flight/TIA1/parser").GetAwaiter().GetResult().Ok.ShouldBeTrue();
    }

    private string Repo => Path.Combine(_root, "tianwen");

    private string Flag => Path.Combine(_root, "done.flag");

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

    private Task<CommandResult> Run(CommandRequest command, CancellationToken cancellationToken)
    {
        _said.Add(command);
        return Task.FromResult(_state.Complete(command, null));
    }

    private Task<CommandResult> Leg(string verb, string target, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private LegRow Parser => _state.Legs("TIA1").Single(l => l.Name == "parser");

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private CardRow[] Late() => Open().Where(c => c.Kind == LegCommander.WaitLateKind).ToArray();

    private void Idle() =>
        _state.PutSession(new SessionRow
        {
            Id = Parser.WorkerJob!,
            SessionId = Parser.WorkerJob + "-0000-4000-8000-000000000000",
            Name = Parser.Worker!,
            Kind = SessionKind.Background,
            Cwd = "/work/tia1",
            Role = SessionRole.Worker,
            Flight = "TIA1",
            Leg = "parser",
            ContextTokens = 0,
            ContextWindow = 200_000,
            State = SessionState.Idle,
            SpendWeighted = 0,
            LastResponseAt = Start,
            StartedAt = Start,
        });

    [Fact]
    public async Task AFileWaitWakesTheWorkerExactlyOnceWhenTheFileAppearsAndNotBefore()
    {
        var result = await Leg(Verbs.WaitLeg, "flight/TIA1/parser", $"path={Flag}", "the", "render", "to", "land");
        result.Ok.ShouldBeTrue();
        result.Made.ShouldNotBeNull().ShouldContain("end your turn");
        Parser.Wait.ShouldNotBeNull().Why.ShouldBe("the render to land");
        FlightWaiting.LegState(Parser).ShouldBe($"waiting for {Flag}");

        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _said.ShouldBeEmpty();
        Parser.Wait.ShouldNotBeNull();

        await File.WriteAllTextAsync(Flag, "x", Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);

        var say = _said.ShouldHaveSingleItem();
        (say.Verb, say.Target).ShouldBe((Verbs.Say, $"session/{Parser.WorkerJob}"));
        say.Args[0].ShouldContain(Flag);
        say.Args[0].ShouldContain("the render to land");
        Parser.Wait.ShouldBeNull();
        FlightWaiting.LegState(Parser).ShouldBe("flying");

        _time.Advance(TimeSpan.FromMinutes(1));
        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AWaitOnATimeAloneWakesTheWorkerAtTheTime()
    {
        var at = Start.AddMinutes(30);
        (await Leg(Verbs.WaitLeg, "flight/TIA1/parser", $"until={at:o}")).Ok.ShouldBeTrue();
        FlightWaiting.LegState(Parser).ShouldStartWith("waiting until ");

        _time.Advance(TimeSpan.FromMinutes(29));
        await _watch.LookAsync(Ct);
        _said.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(2));
        await _watch.LookAsync(Ct);
        _said.ShouldHaveSingleItem().Verb.ShouldBe(Verbs.Say);
        Parser.Wait.ShouldBeNull();

        await _watch.LookAsync(Ct);
        _said.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFileWaitPastItsUntilRingsOnceAndKeepsWaiting()
    {
        (await Leg(Verbs.WaitLeg, "flight/TIA1/parser", $"path={Flag}", $"until={Start.AddMinutes(10):o}")).Ok.ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);
        await _watch.LookAsync(Ct);

        var ring = Late().ShouldHaveSingleItem();
        (ring.Bin, ring.Kind, ring.Subject).ShouldBe((CardBin.Ring, LegCommander.WaitLateKind, "flight/TIA1/parser"));
        _said.ShouldBeEmpty();
        Parser.Wait.ShouldNotBeNull().Rung.ShouldBeTrue();

        // The file comes late: the worker is woken all the same, and the ring closes.
        await File.WriteAllTextAsync(Flag, "x", Ct);
        await _watch.LookAsync(Ct);
        _said.ShouldHaveSingleItem();
        Late().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIdleWorkerOnALegThatWaitsIsNotNudgedOrRungFor()
    {
        (await Leg(Verbs.WaitLeg, "flight/TIA1/parser", $"path={Flag}")).Ok.ShouldBeTrue();
        Idle();

        foreach (var _ in Enumerable.Range(0, 6))
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            await _watch.LookAsync(Ct);
        }

        _said.ShouldBeEmpty();
        Open().ShouldBeEmpty();
    }

    [Fact]
    public async Task AWaitIsRefusedWithoutAFileOrATimeOrWithATimePastOrOnALegNotFlying()
    {
        (await Leg(Verbs.WaitLeg, "flight/TIA1/parser")).Error.ShouldNotBeNull().ShouldContain("needs a file");
        (await Leg(Verbs.WaitLeg, "flight/TIA1/parser", $"until={Start.AddMinutes(-1):o}")).Error.ShouldNotBeNull().ShouldContain("in the past");
        Parser.Wait.ShouldBeNull();

        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw", "done-when=It draws")).Ok.ShouldBeTrue();
        (await Leg(Verbs.WaitLeg, "flight/TIA1/render", $"path={Flag}")).Error.ShouldNotBeNull().ShouldContain("not flying");
    }

    [Fact]
    public async Task AGoAroundClearsTheWait()
    {
        (await Leg(Verbs.WaitLeg, "flight/TIA1/parser", $"path={Flag}")).Ok.ShouldBeTrue();

        (await Leg(Verbs.GoAround, "flight/TIA1/parser", "start", "over")).Ok.ShouldBeTrue();

        Parser.Wait.ShouldBeNull();
    }
}
