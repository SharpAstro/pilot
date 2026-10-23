using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Reviews applied by the node, and phase 3's done-when: two flights, one connected to the other, flown and landed by
/// the node's own machinery (fresh workers, the review, the cards) with no per-flight pilot session anywhere.
/// </summary>
public sealed class ReviewTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeWorkers _workers = new();
    private readonly FakeGitHub _github = new();
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;

    public ReviewTests()
    {
        // A repo with a bare origin, so a flight's push is a real one.
        var origin = Path.Combine(_root, "origin.git");
        Directory.CreateDirectory(Repo);
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        Git(Repo, "remote", "add", "origin", origin);
        Git(Repo, "push", "-q", "-u", "origin", "main");
        Git(Repo, "remote", "set-head", "origin", "main");
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System, _workers);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, _workers, new FakeChecks(), _github, git, new Planes(git), _flights, options, TimeProvider.System)
        {
            SelfFinishGrace = TimeSpan.FromMilliseconds(1),
        };
    }

    private string Repo => Path.Combine(_root, "tianwen");

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

    private Task<CommandResult> Run(string verb, string target, string who, params string[] args) =>
        verb is Verbs.CreateFlight or Verbs.Connect or Verbs.Land
            ? _flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct)
            : _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, Ct);

    private FlightRow F(string code) => _state.Flights().Single(f => f.Code == code);

    private CardRow[] Open() => _state.Cards(openOnly: true);

    private async Task<FlightRow> NewFlight(string issue, string leg)
    {
        (await Run(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "controller", issue, "Flight " + issue)).Ok.ShouldBeTrue();
        var flight = _state.Flights().OrderByDescending(f => f.CreatedAt).First();
        (await Run(Verbs.PlanLeg, Verbs.FlightTarget(flight.Code), "controller", $"name={leg}", "goal=Do it", "done-when=It is done", "item=The step")).Ok.ShouldBeTrue();
        return flight;
    }

    /// <summary>A leg flown as a worker flies it: started by the node, a commit in the plane, its step ticked, finished by the worker.</summary>
    private async Task Fly(FlightRow flight, string leg)
    {
        var start = await Run(Verbs.StartLeg, Verbs.LegTarget(flight.Code, leg), "controller");
        start.Error.ShouldBeNull();
        var worker = _state.Legs(flight.Code).Single(l => l.Name == leg).Worker!;
        File.WriteAllText(Path.Combine(flight.Worktree, $"{leg}-{Guid.NewGuid():N}.txt"), "work\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", $"{leg} work");
        (await Run(Verbs.Check, Verbs.LegTarget(flight.Code, leg), worker, "1")).Ok.ShouldBeTrue();
        (await Run(Verbs.FinishLeg, Verbs.LegTarget(flight.Code, leg), worker, "done", "did it")).Error.ShouldBeNull();
    }

    private string Reviewer(string code) => $"du1-{code.ToLowerInvariant()}-review";

    [Fact]
    public async Task APrOpensAFreshReviewerAndOnlyItsPassOffersTheMerge()
    {
        var flight = await NewFlight("1", "parser");
        await Fly(flight, "parser");

        (await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA1"), "controller")).Error.ShouldBeNull();

        var review = F("TIA1").Review.ShouldNotBeNull();
        (review.State, review.Reviewer).ShouldBe((ReviewState.Reviewing, Reviewer("TIA1")));
        var start = _workers.Started.Last();
        (start.Name, start.Plane).ShouldBe((Reviewer("TIA1"), "tia1"));
        start.Brief.ShouldContain("- parser: Do it Done when: It is done");
        start.Brief.ShouldContain("no subagents, or one at most");
        start.Brief.ShouldContain("review pass");
        Open().ShouldBeEmpty();
        (await Run(Verbs.Merge, "flight/TIA1", "controller", "cafe0000")).Error.ShouldBe("TIA1's review is reviewing, not passed");

        // Only the reviewer gives the verdict; the controller may not pass a review still on.
        (await Run(Verbs.Review, "flight/TIA1", "controller", "pass", "looks fine")).Error.ShouldNotBeNull().ShouldStartWith($"only {Reviewer("TIA1")} gives");
        (await Run(Verbs.Review, "flight/TIA1", Reviewer("TIA1"), "pass", "the step is in; it is done")).Ok.ShouldBeTrue();

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Title).ShouldBe((LegCommander.PrReady, "TIA1's PR #71 is ready to merge"));
        card.Detail.ShouldNotBeNull().ShouldContain("Reviewed: the step is in; it is done.");
        card.Link.ShouldBe("https://github.com/x/tianwen/pull/71");

        // The Merge choice says where the PR's checks stand, as they change, and nothing else about the card does.
        var checks = new PrChecks(_state, _github, TimeProvider.System);
        _github.Checks = ChoiceStatus.Pending;
        await checks.LookAsync(Ct);
        Open().Single().Choices.Single().Status.ShouldBe(ChoiceStatus.Pending);
        _github.Checks = ChoiceStatus.Passing;
        await checks.LookAsync(Ct);
        var green = Open().Single();
        (green.Id, green.Choices.Single().Status, green.Choices.Single().Args.Single()).ShouldBe((card.Id, ChoiceStatus.Passing, card.Choices.Single().Args.Single()));
    }

    [Fact]
    public async Task APrMergedOnGitHubItselfClosesItsCardAndLandsItsFlight()
    {
        var flight = await NewFlight("3", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA3"), "controller");
        (await Run(Verbs.Review, "flight/TIA3", Reviewer("TIA3"), "pass", "fine")).Ok.ShouldBeTrue();
        var card = Open().ShouldHaveSingleItem();

        // Merged by a person on GitHub, not through the card: the next look sees it and lands the flight (TIA1658's #1780).
        _github.Pr = _github.Pr! with { State = "MERGED" };
        await new PrChecks(_state, _github, TimeProvider.System, _flights).LookAsync(Ct);

        Open().ShouldHaveSingleItem().Title.ShouldBe("TIA3 landed");
        _state.Cards().Single(c => c.Id == card.Id).Outcome.ShouldBe("merged on GitHub as #71");
        F("TIA3").State.ShouldBe(FlightState.Landed);
        _github.Merged.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChangesRingWithAGoAroundWhoseFinishIsReviewedAfreshOrReadyAnyway()
    {
        var flight = await NewFlight("2", "parser");
        await Fly(flight, "parser");
        await Run(Verbs.OpenPr, Verbs.FlightTarget("TIA2"), "controller");

        (await Run(Verbs.Review, "flight/TIA2", Reviewer("TIA2"), "changes", "the parser drops the last line")).Ok.ShouldBeTrue();

        var card = Open().ShouldHaveSingleItem();
        (card.Kind, card.Detail).ShouldBe((LegCommander.ReviewChanges, "the parser drops the last line"));
        card.Choices.Select(c => (c.Label, c.Verb)).ShouldBe([("Go around parser", Verbs.GoAround), ("Ready anyway", Verbs.Review)]);

        // The go-around reopens the done leg with the review's words; its finish pushes and starts a fresh review.
        var goAround = card.Choices[0];
        (await Run(goAround.Verb, goAround.Target, "controller", goAround.Args)).Error.ShouldBeNull();
        _workers.Started.Last().Brief.ShouldContain("the review asked for changes: the parser drops the last line");
        Open().ShouldBeEmpty();
        var reviewsBefore = _workers.Started.Count(s => s.Name == Reviewer("TIA2"));

        File.WriteAllText(Path.Combine(flight.Worktree, "fix.txt"), "fixed\n");
        Git(flight.Worktree, "add", ".");
        Git(flight.Worktree, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "keep the last line");
        var worker = _state.Legs("TIA2").Single().Worker!;
        (await Run(Verbs.FinishLeg, "flight/TIA2/parser", worker, "done", "fixed")).Error.ShouldBeNull();

        _workers.Started.Count(s => s.Name == Reviewer("TIA2")).ShouldBe(reviewsBefore + 1);
        F("TIA2").Review!.State.ShouldBe(ReviewState.Reviewing);
        Git(Repo, "ls-remote", "origin", "flight/tia2").ShouldContain(Git(flight.Worktree, "rev-parse", "HEAD"));

        // Asked for changes again, the controller may pass it anyway.
        await Run(Verbs.Review, "flight/TIA2", Reviewer("TIA2"), "changes", "naming");
        (await Run(Verbs.Review, "flight/TIA2", "controller", "pass", "ready anyway")).Ok.ShouldBeTrue();
        F("TIA2").Review!.Report.ShouldBe("ready anyway (passed by controller over: naming)");
        Open().ShouldHaveSingleItem().Kind.ShouldBe(LegCommander.PrReady);
    }

    [Fact]
    public async Task ALegCannotBeNamedReviewAndAReviewerIsNoWorker()
    {
        await NewFlight("3", "parser");
        (await Run(Verbs.PlanLeg, "flight/TIA3", "controller", "name=review", "goal=g", "done-when=d")).Error.ShouldNotBeNull().ShouldStartWith("review names the flight's reviewer");
        Pilot.Node.Sessions.SessionRoles.Resolve("du1-tia3-review").ShouldBe(new Pilot.Node.Sessions.SessionRoleInfo(SessionRole.Reviewer, "TIA3", null, null));
    }

    [Fact]
    public async Task TwoConnectedFlightsLandWithNoPilotOfTheirOwn()
    {
        var first = await NewFlight("10", "core");
        var second = await NewFlight("11", "ui");
        (await Run(Verbs.Connect, Verbs.FlightTarget(second.Code), "controller", first.Code)).Ok.ShouldBeTrue();
        F(second.Code).State.ShouldBe(FlightState.Held);
        (await Run(Verbs.StartLeg, Verbs.LegTarget(second.Code, "ui"), "controller")).Error.ShouldNotBeNull().ShouldContain("held");

        foreach (var (flight, leg) in new[] { (first, "core"), (second, "ui") })
        {
            F(flight.Code).State.ShouldBe(FlightState.Flying);
            await Fly(flight, leg);
            _github.Pr = null;
            (await Run(Verbs.OpenPr, Verbs.FlightTarget(flight.Code), "controller")).Error.ShouldBeNull();
            (await Run(Verbs.Review, Verbs.FlightTarget(flight.Code), Reviewer(flight.Code), "pass", "checked")).Ok.ShouldBeTrue();

            // The controller answers the PR-ready card: the merge, on the head reviewed, lands the flight.
            var card = Open().Single(c => c.Kind == LegCommander.PrReady);
            var merge = card.Choices.Single();
            (await Run(merge.Verb, merge.Target, "controller", merge.Args)).Error.ShouldBeNull();
            await _flights.LandAfterMergeAsync(flight.Code, merge.Args[0], Ct);
            F(flight.Code).State.ShouldBe(FlightState.Landed);
            // The landed card opens its PR, as the merge card did.
            _state.Cards().First(c => c.Kind == "landed" && c.Subject == Verbs.FlightTarget(flight.Code)).Link.ShouldNotBeNull().ShouldBe(F(flight.Code).PullRequestUrl);
        }

        // Landing stopped every session the flight had, so none still held its plane.
        _workers.Stopped.ShouldContain("job00001");
        _workers.Stopped.ShouldContain("job00002");

        // Every session the node started was a leg's worker or a flight's reviewer: no pilot per flight.
        _workers.Started.Select(s => s.Name).ShouldBe(["du1-tia10-core", Reviewer("TIA10"), "du1-tia11-ui", Reviewer("TIA11")]);
        _github.Merged.Count.ShouldBe(2);
    }
}
