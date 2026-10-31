using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A flight connected to a flight that lands with a release waits for the package, not just the land: held until the
/// release's package is fetchable on nuget.org, then flying; one ring card when it never comes. Real git repos, the real
/// commander and watch, fakes for GitHub and nuget.org.
/// </summary>
public abstract class PackageWaitTestsBase : IDisposable
{
    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    private protected static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private protected const string MergeCommit = "feedbeef00000000feedbeef00000000feedbeef";

    private protected readonly string _root = Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8]);
    private protected readonly ManualTime _time = new(Start);
    private protected readonly FakeGitHub _github = new();
    private protected readonly FakeNuGetFeed _feed = new();
    private protected readonly List<IDisposable> _journals = [];

    protected PackageWaitTestsBase()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");

        Directory.CreateDirectory(Path.Combine(Consumer, "src", "App"));
        Git(Consumer, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Consumer, "src", "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Drawboard.Core\" Version=\"1.*\" /></ItemGroup></Project>\n");
        Git(Consumer, "add", ".");
        Git(Consumer, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
    }

    private protected string Repo => Path.Combine(_root, "tianwen");

    /// <summary>The repo of the flight that consumes the package: it names Drawboard.Core in a project, and releases nothing itself.</summary>
    private protected string Consumer => Path.Combine(_root, "sdlapp");

    /// <summary>A workflow as the org's repos have it: a build on every push and PR, and a job that pushes the packages on a push to main.</summary>
    private protected const string PublishWorkflow = """
        name: CI/CD
        on:
          push:
            branches: [ main ]
          pull_request:
            branches: [ main ]
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
            - run: dotnet build -c Release
          publish:
            needs: build
            if: github.ref == 'refs/heads/main' && github.event_name == 'push'
            runs-on: ubuntu-latest
            steps:
            - run: dotnet nuget push **/*.nupkg -s nuget.org -k ${{ secrets.NUGET_API_KEY }}
        """;

    private protected static void WriteWorkflow(string repo, string text, string file = "dotnet.yml")
    {
        Directory.CreateDirectory(Path.Combine(repo, ".github", "workflows"));
        File.WriteAllText(Path.Combine(repo, ".github", "workflows", file), text + "\n");
    }

    public void Dispose()
    {
        foreach (var journal in _journals)
        {
            journal.Dispose();
        }
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

    private protected static string Git(string cwd, params string[] args)
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

    private protected static void Commit(string repo, string message)
    {
        Git(repo, "add", ".");
        Git(repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", message);
    }

    /// <summary>The repo's releases: a version line in its Directory.Build.props and one packable project, as the org's repos have them.</summary>
    private protected void MakeRepoAReleaser(string line = "1.2")
    {
        File.WriteAllText(Path.Combine(Repo, "Directory.Build.props"), $"<Project><PropertyGroup><VersionMajorMinor>{line}</VersionMajorMinor></PropertyGroup></Project>\n");
        Directory.CreateDirectory(Path.Combine(Repo, "src", "Core"));
        File.WriteAllText(Path.Combine(Repo, "src", "Core", "Core.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Drawboard.Core</PackageId></PropertyGroup></Project>\n");
        WriteWorkflow(Repo, PublishWorkflow);
        Directory.CreateDirectory(Path.Combine(Repo, "src", "Core.Tests"));
        File.WriteAllText(Path.Combine(Repo, "src", "Core.Tests", "Core.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>\n");
        Commit(Repo, "releases");
    }

    private protected sealed record Rig(NodeState State, FlightCommander Flights, PackageWatch Watch);

    private protected Rig NewRig(NodeState? state = null)
    {
        state ??= new NodeState(new InMemoryJournal(), _time);
        var git = new GitCli();
        var releases = new PackageReleases(git, _feed, _time);
        var flights = new FlightCommander(state, new Planes(git), _time, github: _github, packages: releases);
        // The tests look when they say; the delays are the defaults, and the clock is theirs.
        return new Rig(state, flights, new PackageWatch(state, flights, _feed, _github, _time));
    }

    private protected async Task<CommandResult> Drive(Rig node, string verb, string target, params string[] args) =>
        await node.Flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    /// <summary>TIA1, the producer, and SDL2, a flight of the repo that uses its package, connected to it.</summary>
    private protected async Task TwoConnectedFlights(Rig node, string? consumerRepo = null)
    {
        (await Drive(node, Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1", "Issue 1")).Ok.ShouldBeTrue();
        var second = consumerRepo ?? Consumer;
        (await Drive(node, Verbs.CreateFlight, Verbs.RepoTarget(second), "2", "Issue 2")).Ok.ShouldBeTrue();
        var code = node.State.Flights().Single(f => f.Issue == 2).Code;
        (await Drive(node, Verbs.Connect, $"flight/{code}", "TIA1")).Ok.ShouldBeTrue();
        node.State.Flights().Single(f => f.Issue == 2).State.ShouldBe(FlightState.Held);
    }

    /// <summary>Lands TIA1 as its PR's merge does: at the head of its plane, GitHub saying which commit it merged as.</summary>
    private protected async Task LandProducer(Rig node, bool merged = true, DateTimeOffset? mergedAt = null)
    {
        var flight = node.State.Flights().Single(f => f.Code == "TIA1");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        if (merged)
        {
            _github.Pr = new PullRequest(7, "MERGED", head, "https://github.com/x/tianwen/pull/7", MergeCommit: MergeCommit, MergedAt: mergedAt);
        }
        await node.Flights.LandAfterMergeAsync("TIA1", head, Ct);
        node.State.Flights().Single(f => f.Code == "TIA1").State.ShouldBe(FlightState.Landed);
    }

    private protected FlightRow Flight(Rig node, string code) => node.State.Flights().Single(f => f.Code == code);

    private protected static WorkflowRun RunOf(string conclusion, DateTimeOffset? completedAt, string status = "completed") =>
        new(4711, "CI/CD", status, status == "completed" ? conclusion : null, "https://github.com/x/tianwen/actions/runs/4711", "push", completedAt);
}

/// <summary>Part 1 of PackageWaitTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class PackageWaitTests1 : PackageWaitTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightConnectedToAReleasingFlightStaysHeldAfterTheLandAndFliesWhenTheVersionAndItsNupkgAreOnNuGet()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        _feed.Publish("Drawboard.Core", "1.1.9");
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node);
        _github.Runs[MergeCommit] = [RunOf("success", Start.AddMinutes(1))];

        var waiting = Flight(node, "SDL2");
        waiting.State.ShouldBe(FlightState.Held);
        var wait = waiting.PackageWaits.ShouldHaveSingleItem();
        (wait.Id, wait.Line, wait.Above, wait.Producer, wait.Commit).ShouldBe(("Drawboard.Core", "1.2", "1.2.5", "TIA1", MergeCommit));
        // The test project is no package, and the status says what it waits for.
        FlightWaiting.For(waiting).ShouldBe("Drawboard.Core 1.2 on nuget.org");
        new Digest(node.State, _time).Raise(_time.GetUtcNow()).Detail.ShouldNotBeNull().ShouldContain("- SDL2 held, waiting for Drawboard.Core 1.2 on nuget.org");
        Briefs.Watchtower("wt", node.State.Flights(), node.State.Legs(), node.State.Cards(), "pilot").ShouldContain("SDL2: Issue 2 (sdlapp #2, held, waiting for Drawboard.Core 1.2 on nuget.org)");

        // Nothing newer than what there was at the land: still held, and the look backs off.
        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Held);
        var reads = _feed.IndexReads;
        _time.Advance(TimeSpan.FromSeconds(30));
        await node.Watch.LookAsync(Ct);
        _feed.IndexReads.ShouldBe(reads);

        // Listed, but its .nupkg cannot be fetched yet.
        _feed.Publish("Drawboard.Core", "1.2.6", nupkg: false);
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Held);

        _feed.Publish("Drawboard.Core", "1.2.7");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        var flying = Flight(node, "SDL2");
        flying.State.ShouldBe(FlightState.Flying);
        flying.PackageWaits.ShouldBeEmpty();
        node.State.Cards().ShouldNotContain(c => c.Kind == PackageWatch.LateKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABumpedLineWaitsForAnyVersionOfItAndAnOlderLineDoesNotCount()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node);
        var producer = Flight(node, "TIA1");
        // The flight moves the line to 1.3, as a breaking-free minor does.
        File.WriteAllText(Path.Combine(producer.Worktree, "Directory.Build.props"), "<Project><PropertyGroup><VersionMajorMinor>1.3</VersionMajorMinor></PropertyGroup></Project>\n");
        Commit(producer.Worktree, "bump to 1.3");

        await LandProducer(node);

        var wait = Flight(node, "SDL2").PackageWaits.ShouldHaveSingleItem();
        (wait.Line, wait.Above).ShouldBe(("1.3", null));
        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Held);

        _feed.Publish("Drawboard.Core", "1.3.1");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightConnectedToAFlightThatReleasesNothingFliesOnTheLandAsBefore()
    {
        // No version line and no package in this repo.
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node);

        var flying = Flight(node, "SDL2");
        flying.State.ShouldBe(FlightState.Flying);
        flying.PackageWaits.ShouldBeEmpty();
        _feed.IndexReads.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APackageNotUpThirtyMinutesAfterThePublishRunSucceededRaisesOneCardNamingTheRunAndTheVersions()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node);
        await LandProducer(node);
        _github.Runs[MergeCommit] = [RunOf("success", Start.AddMinutes(4))];

        // 29 minutes after the run succeeded: not late yet.
        _time.Advance(TimeSpan.FromMinutes(33));
        await node.Watch.LookAsync(Ct);
        node.State.Cards().ShouldNotContain(c => c.Kind == PackageWatch.LateKind);

        _time.Advance(TimeSpan.FromMinutes(2));
        await node.Watch.LookAsync(Ct);
        var card = node.State.Cards().Where(c => c.Kind == PackageWatch.LateKind).ShouldHaveSingleItem();
        (card.Bin, card.Subject, card.State).ShouldBe((CardBin.Ring, "flight/SDL2", CardState.Open));
        card.Title.ShouldContain("Drawboard.Core 1.2");
        card.Detail.ShouldNotBeNull().ShouldContain("CI/CD #4711");
        card.Detail.ShouldContain("concluded success");
        card.Detail.ShouldContain("a version of 1.2 newer than 1.2.5");
        card.Detail.ShouldContain("latest published: 1.2.5");
        Flight(node, "SDL2").PackageWaits.ShouldHaveSingleItem().Escalated.ShouldBeTrue();

        // Never a second one, however long it takes.
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(11));
            await node.Watch.LookAsync(Ct);
        }
        node.State.Cards().Count(c => c.Kind == PackageWatch.LateKind).ShouldBe(1);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Held);

        // It comes in the end: the flight flies and its card closes.
        _feed.Publish("Drawboard.Core", "1.2.9");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Flying);
        node.State.Cards().Single(c => c.Kind == PackageWatch.LateKind).State.ShouldBe(CardState.Closed);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APublishRunThatFailedRaisesTheCardAtOnce()
    {
        MakeRepoAReleaser();
        var node = NewRig();
        await TwoConnectedFlights(node);
        await LandProducer(node);
        _github.Runs[MergeCommit] = [RunOf("failure", Start.AddMinutes(2))];

        _time.Advance(TimeSpan.FromMinutes(3));
        await node.Watch.LookAsync(Ct);

        var card = node.State.Cards().Where(c => c.Kind == PackageWatch.LateKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain("CI/CD #4711");
        card.Detail.ShouldContain("concluded failure");
        card.Detail.ShouldContain("expected a version of 1.2");
        card.Detail.ShouldContain("latest published: none of 1.2");

        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        node.State.Cards().Count(c => c.Kind == PackageWatch.LateKind).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APublishRunThatNeverRanRaisesTheCardOnceTheStartGraceIsOver()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node);
        await LandProducer(node);

        // A push starts its run within seconds, but not before the land is read.
        _time.Advance(TimeSpan.FromMinutes(1));
        await node.Watch.LookAsync(Ct);
        node.State.Cards().ShouldNotContain(c => c.Kind == PackageWatch.LateKind);

        // GitHub has created a push run 12 minutes after its commit: still no card then.
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        node.State.Cards().ShouldNotContain(c => c.Kind == PackageWatch.LateKind);

        _time.Advance(TimeSpan.FromMinutes(20));
        await node.Watch.LookAsync(Ct);
        var card = node.State.Cards().Where(c => c.Kind == PackageWatch.LateKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain("never ran");
        card.Detail.ShouldContain("newer than 1.2.5");
        card.Detail.ShouldContain("latest published: 1.2.5");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AWaitSurvivesARestartOfTheNodeAndSoDoesTheCardAlreadyRaised()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var folder = Path.Combine(_root, "journal");
        var first = new FileJournal(folder);
        _journals.Add(first);
        var node = NewRig(new NodeState(first, _time));
        await TwoConnectedFlights(node);
        await LandProducer(node);
        _github.Runs[MergeCommit] = [RunOf("failure", Start.AddMinutes(2))];
        _time.Advance(TimeSpan.FromMinutes(3));
        await node.Watch.LookAsync(Ct);
        node.State.Cards().Count(c => c.Kind == PackageWatch.LateKind).ShouldBe(1);
        first.Dispose();

        var second = new FileJournal(folder);
        _journals.Add(second);
        var restarted = NewRig(new NodeState(second, _time));
        var waiting = Flight(restarted, "SDL2");
        waiting.State.ShouldBe(FlightState.Held);
        var wait = waiting.PackageWaits.ShouldHaveSingleItem();
        (wait.Id, wait.Line, wait.Above, wait.Producer, wait.Commit, wait.Escalated).ShouldBe(("Drawboard.Core", "1.2", "1.2.5", "TIA1", MergeCommit, true));

        // The new node looks at once, says nothing twice, and lets the flight go when the package is up.
        await restarted.Watch.LookAsync(Ct);
        restarted.State.Cards().Count(c => c.Kind == PackageWatch.LateKind).ShouldBe(1);
        Flight(restarted, "SDL2").State.ShouldBe(FlightState.Held);
        _feed.Publish("Drawboard.Core", "1.2.6");
        _time.Advance(TimeSpan.FromMinutes(11));
        await restarted.Watch.LookAsync(Ct);
        Flight(restarted, "SDL2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALandThatCannotReadNuGetIsRefusedAndLeavesTheFlightFlyingWithItsPlane()
    {
        MakeRepoAReleaser();
        _feed.Offline = true;
        var node = NewRig();
        await TwoConnectedFlights(node);
        var producer = Flight(node, "TIA1");
        var head = Git(producer.Worktree, "rev-parse", "HEAD");

        var ex = await Should.ThrowAsync<FlightException>(() => node.Flights.LandAfterMergeAsync("TIA1", head, Ct));

        ex.Message.ShouldContain("nuget.org could not say");
        Flight(node, "TIA1").State.ShouldBe(FlightState.Flying);
        Directory.Exists(producer.Worktree).ShouldBeTrue();
        Flight(node, "SDL2").PackageWaits.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ANugetOutageDuringAWaitIsNoNewsAndTheNextLookAsksAgain()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node);
        await LandProducer(node);
        _feed.Offline = true;

        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Held);

        _feed.Offline = false;
        _feed.Publish("Drawboard.Core", "1.2.6");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightOfTheProducersOwnRepoFliesOnTheLandBecauseItBuildsFromSource()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node, consumerRepo: Repo);

        await LandProducer(node);

        var flying = Flight(node, "TIA2");
        flying.State.ShouldBe(FlightState.Flying);
        flying.PackageWaits.ShouldBeEmpty();
        _feed.IndexReads.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightOfAnotherRepoThatNamesNoPackageOfTheReleaseFliesOnTheLand()
    {
        MakeRepoAReleaser();
        File.WriteAllText(Path.Combine(Consumer, "src", "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Some.Other\" Version=\"1.*\" /></ItemGroup></Project>\n");
        Commit(Consumer, "uses something else");
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node);

        Flight(node, "SDL2").State.ShouldBe(FlightState.Flying);
        Flight(node, "SDL2").PackageWaits.ShouldBeEmpty();
        _feed.IndexReads.ShouldBe(0);
    }
}

/// <summary>Part 2 of PackageWaitTests: the class is split so its tests run side by side, not one after another.</summary>
public sealed class PackageWaitTests2 : PackageWaitTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ACentralPackageVersionFileNamingThePackageIsAReferenceToo()
    {
        MakeRepoAReleaser();
        File.Delete(Path.Combine(Consumer, "src", "App", "App.csproj"));
        File.WriteAllText(Path.Combine(Consumer, "Directory.Packages.props"),
            "<Project><ItemGroup><PackageVersion Include=\"Drawboard.Core\" Version=\"1.2.5\" /></ItemGroup></Project>\n");
        Commit(Consumer, "central versions");
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node);

        Flight(node, "SDL2").PackageWaits.ShouldHaveSingleItem().Id.ShouldBe("Drawboard.Core");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APackableRepoWhoseCiPushesNoPackageOnAPushToMainReleasesNothingAndNobodyWaits()
    {
        MakeRepoAReleaser();
        // As pilot's own: projects packable by default, and a workflow that ships nothing.
        WriteWorkflow(Repo, "name: v2 (.NET)\non:\n  push:\n    branches: [ main ]\njobs:\n  build:\n    steps:\n    - run: dotnet build -c Release");
        Commit(Repo, "no publish job");
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node);

        Flight(node, "SDL2").State.ShouldBe(FlightState.Flying);
        Flight(node, "SDL2").PackageWaits.ShouldBeEmpty();
        _feed.IndexReads.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ARepoThatPublishesOnAVersionTagReleasesNothingOnTheLand()
    {
        MakeRepoAReleaser();
        WriteWorkflow(Repo, "name: tag\non:\n  push:\n    tags: [ 'v*' ]\njobs:\n  publish:\n    steps:\n    - run: dotnet nuget push **/*.nupkg -s nuget.org");
        Commit(Repo, "publishes on a tag");
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node);

        Flight(node, "SDL2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APersonLetsAWaitingFlightFlyAnywayFromTheLateCardAndTheWaitIsDropped()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node);
        await LandProducer(node);
        _github.Runs[MergeCommit] = [RunOf("failure", Start.AddMinutes(2))];
        _time.Advance(TimeSpan.FromMinutes(3));
        await node.Watch.LookAsync(Ct);

        var card = node.State.Cards().Where(c => c.Kind == PackageWatch.LateKind).ShouldHaveSingleItem();
        var way = card.Choices.ShouldHaveSingleItem();
        (way.Label, way.Verb, way.Target).ShouldBe(("Fly anyway", Verbs.FlyAnyway, "flight/SDL2"));
        Flight(node, "SDL2").State.ShouldBe(FlightState.Held);

        (await Drive(node, way.Verb, way.Target)).Ok.ShouldBeTrue();

        var flying = Flight(node, "SDL2");
        flying.State.ShouldBe(FlightState.Flying);
        flying.PackageWaits.ShouldBeEmpty();
        node.State.Cards().Single(c => c.Kind == PackageWatch.LateKind).State.ShouldBe(CardState.Closed);

        // The watch has nothing left to look at, and a second ask is told so.
        var reads = _feed.IndexReads;
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        _feed.IndexReads.ShouldBe(reads);
        (await Drive(node, Verbs.FlyAnyway, "flight/SDL2")).Error.ShouldNotBeNull().ShouldContain("waits for no package");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task AFlightHeldOnlyByAnotherLandStaysHeldWhenToldToFlyAnyway()
    {
        MakeRepoAReleaser();
        var node = NewRig();
        await TwoConnectedFlights(node);

        var result = await Drive(node, Verbs.FlyAnyway, "flight/SDL2");

        result.Error.ShouldNotBeNull().ShouldContain("waits for no package");
        Flight(node, "SDL2").State.ShouldBe(FlightState.Held);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALandReadAfterThePublishFinishedWaitsForNothingNewerThanTheFlightsOwnRelease()
    {
        MakeRepoAReleaser();
        var merged = Start.AddMinutes(-20);
        // 1.2.5 was there before the merge; the flight's own release, 1.2.6, is up already (the node was down across the merge).
        _feed.Publish("Drawboard.Core", "1.2.4", at: merged.AddDays(-9));
        _feed.Publish("Drawboard.Core", "1.2.5", at: merged.AddDays(-2));
        _feed.Publish("Drawboard.Core", "1.2.6", at: merged.AddMinutes(8));
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node, mergedAt: merged);

        var wait = Flight(node, "SDL2").PackageWaits.ShouldHaveSingleItem();
        wait.Above.ShouldBe("1.2.5");
        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Flying);
        node.State.Cards().ShouldNotContain(c => c.Kind == PackageWatch.LateKind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ALandReadBeforeThePublishStillWaitsForTheVersionAfterTheLatestThereWasAtTheMerge()
    {
        MakeRepoAReleaser();
        var merged = Start.AddMinutes(-1);
        _feed.Publish("Drawboard.Core", "1.2.5", at: merged.AddDays(-2));
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node, mergedAt: merged);

        Flight(node, "SDL2").PackageWaits.ShouldHaveSingleItem().Above.ShouldBe("1.2.5");
        await node.Watch.LookAsync(Ct);
        Flight(node, "SDL2").State.ShouldBe(FlightState.Held);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task TheLateCardReadsTheRunOfTheWorkflowThatPushesPackagesAndNotAnUnrelatedFastOne()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node);
        await LandProducer(node);
        Flight(node, "SDL2").PackageWaits.ShouldHaveSingleItem().Workflow.ShouldBe("CI/CD");
        // A lint workflow's push run, newer and already done, and the publish run still going.
        _github.Runs[MergeCommit] =
        [
            new WorkflowRun(4800, "Labeler", "completed", "failure", "https://github.com/x/tianwen/actions/runs/4800", "push", Start.AddMinutes(1)),
            RunOf("success", null, status: "in_progress"),
        ];

        // Past the start grace: the publish run is going, so the package is not late, whatever the other workflow said.
        _time.Advance(TimeSpan.FromMinutes(40));
        await node.Watch.LookAsync(Ct);
        node.State.Cards().ShouldNotContain(c => c.Kind == PackageWatch.LateKind);

        // The publish run concludes: the card is about its run.
        _github.Runs[MergeCommit] = [_github.Runs[MergeCommit][0], RunOf("failure", Start.AddMinutes(41))];
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        var card = node.State.Cards().Where(c => c.Kind == PackageWatch.LateKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain("CI/CD #4711");
        card.Detail.ShouldNotContain("Labeler");
        card.Detail.ShouldContain("concluded failure");
    }

    [Fact]
    public void ARepoPublishesOnAPushToMainWhenAWorkflowPushesPackagesAndIsTriggeredByABranchPush()
    {
        string? Of(string text) => PublishShape.PushPublisher([(".github/workflows/dotnet.yml", text)]);

        Of(PublishWorkflow).ShouldBe("CI/CD");
        // No name: GitHub calls its runs by the path.
        Of("on: [push, pull_request]\njobs:\n  p:\n    steps:\n    - run: nuget push x.nupkg").ShouldBe(".github/workflows/dotnet.yml");
        Of("on: push\njobs:\n  p:\n    steps:\n    - run: dotnet nuget push x.nupkg").ShouldNotBeNull();
        // Tags alone, by the trigger or by the job's own gate: the release is the tag's.
        Of("on:\n  push:\n    tags: [ 'v*' ]\njobs:\n  p:\n    steps:\n    - run: dotnet nuget push x.nupkg").ShouldBeNull();
        Of("on:\n  push:\n    branches: [ main ]\n    tags: [ 'v*' ]\njobs:\n  build:\n    steps:\n    - run: dotnet build\n  publish:\n    if: startsWith(github.ref, 'refs/tags/v')\n    steps:\n    - run: dotnet nuget push x.nupkg").ShouldBeNull();
        // A PR-only or manual workflow, a workflow that builds only, and a push that is commented out.
        Of("on:\n  pull_request:\njobs:\n  p:\n    steps:\n    - run: dotnet nuget push x.nupkg").ShouldBeNull();
        Of("on:\n  push:\n    branches: [ main ]\njobs:\n  b:\n    steps:\n    - run: dotnet build -c Release").ShouldBeNull();
        Of("on:\n  push:\n    branches: [ main ]\njobs:\n  b:\n    steps:\n    # - run: dotnet nuget push x.nupkg\n    - run: dotnet build").ShouldBeNull();
    }

    [Fact]
    public void PackageIdsFollowDotnetPackWhichProjectsAreOnesAndWhatTheyAreCalled()
    {
        const string sdk = "<Project Sdk=\"Microsoft.NET.Sdk\">{0}</Project>";
        string Project(string body) => string.Format(sdk, body);
        PackageReleases.IdOf(Project(""), "Lib", true).ShouldBe("Lib");
        PackageReleases.IdOf(Project("<PropertyGroup><AssemblyName>Lib.Core</AssemblyName></PropertyGroup>"), "Lib", true).ShouldBe("Lib.Core");
        PackageReleases.IdOf(Project("<PropertyGroup><AssemblyName>Lib.Core</AssemblyName><PackageId>Org.Lib</PackageId></PropertyGroup>"), "Lib", true).ShouldBe("Org.Lib");
        PackageReleases.IdOf(Project("<PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>"), "Lib", true).ShouldBeNull();
        PackageReleases.IdOf(Project("<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup>"), "Lib.Tests", true).ShouldBeNull();
        PackageReleases.IdOf(Project("<PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>"), "App", true).ShouldBeNull();
        PackageReleases.IdOf(Project("<PropertyGroup><OutputType>Exe</OutputType><PackAsTool>true</PackAsTool></PropertyGroup>"), "App", true).ShouldBe("App");
        // A Directory.Build.props that makes nothing packable by default: only a project that says so is.
        PackageReleases.IdOf(Project(""), "Lib", false).ShouldBeNull();
        PackageReleases.IdOf(Project("<PropertyGroup><IsPackable>true</IsPackable></PropertyGroup>"), "Lib", false).ShouldBe("Lib");
        // Not an SDK project, and not a project at all.
        PackageReleases.IdOf("<Project><PropertyGroup /></Project>", "Old", true).ShouldBeNull();
        PackageReleases.IdOf("not xml", "Old", true).ShouldBeNull();
    }

    [Fact]
    public void VersionsCompareNumericallyWithinALineAndLeavePreReleasesOut()
    {
        PackageVersions.InLine(["1.2.9", "1.2.10", "1.2.3-beta", "1.3.1", "1.20.1", "1.2.10+abc"], "1.2").ShouldBe(["1.2.10", "1.2.10+abc", "1.2.9"], ignoreOrder: false);
        PackageVersions.InLine(["2.0.1"], "1.2").ShouldBeEmpty();
    }
}
