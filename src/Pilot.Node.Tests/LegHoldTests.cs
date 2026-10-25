using System.Diagnostics;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A leg whose restore fails NU1101/NU1102 on a package a sibling is publishing holds instead of failing: no ring, its worker
/// stopped, the node looking for the package on nuget.org and starting the leg afresh once it is there. Real git repos (the
/// flight's and a sibling's), the real commanders and watch, fakes for the workers, the checks, GitHub and nuget.org.
/// </summary>
public sealed class LegHoldTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private const string Nu1102 = "/w/src/App/App.csproj : error NU1102: Unable to find package Drawboard.Core with version (>= 1.2.7) [/w/src/App/App.csproj]";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2h-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(Start);
    private readonly NodeState _state;
    private readonly FakeWorkers _workers = new();
    private readonly FakeChecks _checks = new();
    private readonly FakeGitHub _github = new();
    private readonly FakeNuGetFeed _feed = new();
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;
    private readonly PackageWatch _watch;

    public LegHoldTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Commit(Repo, "first");
        _state = new NodeState(new InMemoryJournal(), _time);
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), _time);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, _workers, _checks, _github, git, new Planes(git), _flights, options, _time)
        {
            Releases = new PackageReleases(git, _feed, _time),
        };
        _watch = new PackageWatch(_state, _flights, _feed, _github, _time, _legs);
    }

    private string Repo => Path.Combine(_root, "tianwen");

    private string Core => Path.Combine(_root, "Drawboard.Core");

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

    private static void Commit(string repo, string message)
    {
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }

    /// <summary>A sibling clone of the org whose merged main releases Drawboard.Core in the given line.</summary>
    private void MakeSiblingARelease(string line = "1.2")
    {
        Directory.CreateDirectory(Core);
        Git(Core, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Core, "Directory.Build.props"), $"<Project><PropertyGroup><VersionMajorMinor>{line}</VersionMajorMinor></PropertyGroup></Project>\n");
        Directory.CreateDirectory(Path.Combine(Core, "src", "Core"));
        File.WriteAllText(Path.Combine(Core, "src", "Core", "Core.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Drawboard.Core</PackageId></PropertyGroup></Project>\n");
        // Its CI pushes the packages on a push to main, which is what makes the repo a release.
        Directory.CreateDirectory(Path.Combine(Core, ".github", "workflows"));
        File.WriteAllText(Path.Combine(Core, ".github", "workflows", "dotnet.yml"),
            "name: CI/CD\non:\n  push:\n    branches: [ main ]\njobs:\n  publish:\n    if: github.ref == 'refs/heads/main'\n    steps:\n    - run: dotnet nuget push **/*.nupkg -s nuget.org\n");
        Commit(Core, "releases");
    }

    private Task<CommandResult> Leg(string verb, string target, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private LegRow L(string name) => _state.Legs("TIA1").Single(l => l.Name == name);

    /// <summary>TIA1 with two legs, parser started and flying.</summary>
    private async Task FlyingParser(string? check = null)
    {
        (await _flights.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        var parser = new List<string> { "name=parser", "goal=Parse the ephemeris", "done-when=The parser tests pass", "item=Write the parser" };
        if (check is not null)
        {
            parser.Add($"check={check}");
        }
        (await Leg(Verbs.PlanLeg, "flight/TIA1", [.. parser])).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw the planets", "done-when=The capture shows them")).Ok.ShouldBeTrue();
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();
        L("parser").State.ShouldBe(LegState.Flying);
    }

    [Fact]
    public async Task AWorkerFailingOnAPackageASiblingIsPublishingHoldsItsLegWithNoRingAndItsWorkerStopped()
    {
        MakeSiblingARelease();
        _feed.Publish("Drawboard.Core", "1.2.5");
        await FlyingParser();
        var job = L("parser").WorkerJob.ShouldNotBeNull();

        var result = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);

        result.Ok.ShouldBeTrue();
        var leg = L("parser");
        (leg.State, leg.WorkerJob).ShouldBe((LegState.Planned, null));
        var hold = leg.Hold.ShouldNotBeNull();
        (hold.Id, hold.Wanted, hold.Line, hold.Producer, hold.RepoPath).ShouldBe(("Drawboard.Core", "1.2.7", "1.2", "Drawboard.Core", Core));
        hold.Commit.ShouldBe(Git(Core, "rev-parse", "HEAD"));
        leg.Report.ShouldNotBeNull().ShouldStartWith("holding: Drawboard.Core 1.2.7 not on nuget.org yet");
        leg.Report.ShouldContain(Nu1102);
        _workers.Stopped.ShouldContain(job);
        // Nobody is rung: a note at most.
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        _state.Cards().Where(c => c.Kind == LegCommander.HoldKind).ShouldHaveSingleItem().Bin.ShouldBe(CardBin.Handled);
        L("render").Hold.ShouldBeNull();
    }

    [Fact]
    public async Task ANodeRerunOfALegsCheckWhoseOutputHasAnNu1102HoldsItToo()
    {
        MakeSiblingARelease();
        await FlyingParser(check: "dotnet build");
        await Leg(Verbs.Check, "flight/TIA1/parser", "1");
        var job = L("parser").WorkerJob.ShouldNotBeNull();
        _checks.Next = new CheckRun(1, $"Build FAILED.\n{Nu1102}\n    1 Error(s)");

        var result = await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "all good");

        result.Ok.ShouldBeTrue();
        var leg = L("parser");
        (leg.State, leg.Hold?.Id).ShouldBe((LegState.Planned, "Drawboard.Core"));
        _workers.Stopped.ShouldContain(job);
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
        // Its ticks stay: the next worker is not asked to redo them.
        leg.Checklist.ShouldAllBe(i => i.Done);
    }

    [Fact]
    public async Task ANu1101WithoutAVersionHoldsForTheReleasesLine()
    {
        MakeSiblingARelease();
        await FlyingParser();

        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "error NU1101: Unable to find package Drawboard.Core. No packages exist with this id in source(s): nuget.org");

        var hold = L("parser").Hold.ShouldNotBeNull();
        (hold.Id, hold.Wanted, hold.Line).ShouldBe(("Drawboard.Core", null, "1.2"));
        FlightWaiting.HoldText(hold).ShouldBe("holding: Drawboard.Core 1.2 not on nuget.org yet");
    }

    [Fact]
    public async Task ARecordedReleaseOfAConnectedFlightCountsAsBeingPublished()
    {
        // No sibling clone: the release is the one TIA2's wait records.
        await FlyingParser();
        var flight = _state.Flights().Single();
        _state.PutFlight(flight with
        {
            PackageWaits = [new PackageWait { Id = "Drawboard.Core", Line = "1.2", Producer = "TIA9", Since = Start, Commit = "abc1234" }],
        });
        // The first flight's own row (flying, not waiting): the record is read from every flight, so it counts for this leg.
        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);

        var hold = L("parser").Hold.ShouldNotBeNull();
        (hold.Producer, hold.Commit).ShouldBe(("TIA9", "abc1234"));
    }

    [Fact]
    public async Task AnNu1102OnAPackageNobodyIsPublishingFailsAndRingsAsBefore()
    {
        // The sibling is on another line than the one asked for, and no flight records a release.
        MakeSiblingARelease(line: "1.3");
        await FlyingParser();

        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);

        var leg = L("parser");
        (leg.State, leg.Hold).ShouldBe((LegState.Failed, null));
        _state.Cards().Where(c => c.Kind == "leg-failed").ShouldHaveSingleItem().Bin.ShouldBe(CardBin.Ring);
    }

    [Fact]
    public async Task AnNu1102OnAPackageNuGetAlreadyServesIsNotAHold()
    {
        MakeSiblingARelease();
        _feed.Publish("Drawboard.Core", "1.2.8");
        await FlyingParser();

        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);

        L("parser").State.ShouldBe(LegState.Failed);
    }

    [Fact]
    public async Task AnNu1102WhenNuGetCannotBeReadFailsAsBefore()
    {
        MakeSiblingARelease();
        _feed.Offline = true;
        await FlyingParser();

        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);

        L("parser").State.ShouldBe(LegState.Failed);
    }

    [Fact]
    public async Task AFailureThatNamesNoMissingPackageFailsAsBefore()
    {
        MakeSiblingARelease();
        await FlyingParser();

        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", "the build is red: CS0103 name does not exist");

        L("parser").State.ShouldBe(LegState.Failed);
    }

    [Fact]
    public async Task TheAutopilotStartsNothingWhileALegHoldsAndTheNodeStartsItAfreshWhenTheFakeFeedListsTheVersion()
    {
        MakeSiblingARelease();
        _feed.Publish("Drawboard.Core", "1.2.5");
        await FlyingParser();
        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);
        var started = _workers.Started.Count;
        _legs.Models = new RoleModels(null);
        // The sibling's publish run is still going, so the package is not yet late.
        _github.Runs[L("parser").Hold!.Commit!] = [new WorkflowRun(4711, "CI/CD", "in_progress", null, "https://github.com/x/core/actions/runs/4711", "push", null)];

        // The sweep: the held leg is not started, and neither is the leg after it.
        await _legs.AdvanceAllAsync(Ct);
        _workers.Started.Count.ShouldBe(started);
        L("parser").State.ShouldBe(LegState.Planned);

        // A person's start is refused, with why, unless they say anyway.
        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Error.ShouldNotBeNull().ShouldContain("holding: Drawboard.Core 1.2.7 not on nuget.org yet");

        // Listed but not fetchable, then below the version asked for: still held.
        _feed.Publish("Drawboard.Core", "1.2.7", nupkg: false);
        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);
        L("parser").Hold.ShouldNotBeNull();
        _feed.Publish("Drawboard.Core", "1.2.6");
        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);
        L("parser").Hold.ShouldNotBeNull();
        _workers.Started.Count.ShouldBe(started);

        _feed.Publish("Drawboard.Core", "1.2.8");
        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);

        var leg = L("parser");
        (leg.State, leg.Hold).ShouldBe((LegState.Flying, null));
        leg.Report.ShouldNotBeNull().ShouldBe("resumed: Drawboard.Core 1.2.8 is up");
        leg.WorkerJob.ShouldNotBeNull();
        var worker = _workers.Started.Skip(started).ShouldHaveSingleItem();
        worker.Name.ShouldBe("du1-tia1-parser");
        worker.Brief.ShouldContain("## Resumed after a hold");
        worker.Brief.ShouldContain("resumed: Drawboard.Core 1.2.8 is up");
        _state.Cards().ShouldNotContain(c => c.Bin == CardBin.Ring);
    }

    [Fact]
    public async Task APersonCanStartAHeldLegAnywayAndItIsFlownFromWhatThePlaneHolds()
    {
        MakeSiblingARelease();
        await FlyingParser();
        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);
        var started = _workers.Started.Count;

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser", "anyway")).Ok.ShouldBeTrue();

        var leg = L("parser");
        (leg.State, leg.Hold).ShouldBe((LegState.Flying, null));
        _workers.Started.Skip(started).ShouldHaveSingleItem().Brief.ShouldContain("## Resumed after a hold");
    }

    [Fact]
    public async Task ALateHeldPackageIsOneCardNamingTheFailedRunAndTheLegStillHolds()
    {
        MakeSiblingARelease();
        _feed.Publish("Drawboard.Core", "1.2.5");
        await FlyingParser();
        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);
        _legs.Models = new RoleModels(null);
        var commit = L("parser").Hold!.Commit!;
        _github.Runs[commit] = [new WorkflowRun(4711, "CI/CD", "completed", "failure", "https://github.com/x/core/actions/runs/4711", "push", Start.AddMinutes(3))];

        // A failed publish run is rung for at once, once, whatever the backoff does.
        await _watch.LookAsync(Ct);
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(11));
            await _watch.LookAsync(Ct);
        }

        var card = _state.Cards().Where(c => c.Kind == PackageWatch.LateKind).ShouldHaveSingleItem();
        (card.Bin, card.Subject).ShouldBe((CardBin.Ring, "flight/TIA1/parser"));
        card.Title.ShouldBe("TIA1/parser holds for Drawboard.Core 1.2.7, which is not on nuget.org");
        card.Detail.ShouldNotBeNull().ShouldContain("Drawboard.Core's publish run CI/CD #4711");
        card.Detail.ShouldContain("concluded failure");
        card.Detail.ShouldContain("Drawboard.Core 1.2.7 or a later version of 1.2");
        card.Detail.ShouldContain("latest published: 1.2.5");
        L("parser").Hold.ShouldNotBeNull().Escalated.ShouldBeTrue();

        // It comes in the end: the card closes with the resume.
        _feed.Publish("Drawboard.Core", "1.2.9");
        _time.Advance(TimeSpan.FromMinutes(11));
        await _watch.LookAsync(Ct);
        L("parser").State.ShouldBe(LegState.Flying);
        _state.Cards(openOnly: true).ShouldNotContain(c => c.Kind == PackageWatch.LateKind);
    }

    [Fact]
    public async Task TheHoldIsInTheStatusTheBriefsAndTheLegLine()
    {
        MakeSiblingARelease();
        await FlyingParser();
        await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "failed", Nu1102);

        FlightWaiting.LegState(L("parser")).ShouldBe("holding for Drawboard.Core 1.2.7");
        FlightWaiting.LegState(L("render")).ShouldBe("planned");
        Briefs.Watchtower("wt", _state.Flights(), _state.Legs(), _state.Cards(), "pilot").ShouldContain("1. parser: holding for Drawboard.Core 1.2.7");

        var snapshot = new SnapshotEvent { Seq = 1, At = DateTimeOffset.UnixEpoch, Stream = "s", Notes = [], Sessions = [], Flights = _state.Flights(), Legs = _state.Legs() };
        var status = StatusMarkdown.Build(snapshot);
        status.ShouldContain("holding for Drawboard.Core 1.2.7");
    }

    [Fact]
    public async Task AWorkersBriefSaysAMissingSiblingPackageIsReportedVerbatimAndNotWorkedAround()
    {
        MakeSiblingARelease();
        await FlyingParser();

        var brief = _workers.Started.Single().Brief;

        brief.ShouldContain("NU1101 or NU1102");
        brief.ShouldContain("the NU line, verbatim");
        brief.ShouldContain("no local");
        brief.ShouldContain("feed, no pin changed back");
    }

    [Theory]
    [InlineData("error NU1102: Unable to find package Foo.Bar with version (>= 1.2.3)", "Foo.Bar", "1.2.3")]
    [InlineData("x.csproj : error NU1102: Unable to find package Foo.Bar with version (= 1.2.3-beta.4) [x.csproj]", "Foo.Bar", "1.2.3")]
    [InlineData("error NU1101: Unable to find package Foo.Bar. No packages exist with this id in source(s): nuget.org", "Foo.Bar", null)]
    public void TheNuLinesAreReadForTheirPackageAndVersion(string text, string id, string? version)
    {
        var miss = RestoreMisses.In($"Restore failed\n{text}\nBuild FAILED").ShouldHaveSingleItem();
        (miss.Id, miss.Version).ShouldBe((id, version));
        miss.Line.ShouldBe(text);
    }

    [Fact]
    public void ARepeatedLineIsOneMissAndOtherErrorsAreNone()
    {
        RestoreMisses.In($"{Nu1102}\n{Nu1102}").Count.ShouldBe(1);
        RestoreMisses.In("error NU1605: Detected package downgrade: Foo").ShouldBeEmpty();
        RestoreMisses.In(null).ShouldBeEmpty();
    }
}
