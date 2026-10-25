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
public sealed class PackageWaitTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private const string MergeCommit = "feedbeef00000000feedbeef00000000feedbeef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(Start);
    private readonly FakeGitHub _github = new();
    private readonly FakeNuGetFeed _feed = new();
    private readonly List<IDisposable> _journals = [];

    public PackageWaitTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
    }

    private string Repo => Path.Combine(_root, "tianwen");

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

    /// <summary>The repo's releases: a version line in its Directory.Build.props and one packable project, as the org's repos have them.</summary>
    private void MakeRepoAReleaser(string line = "1.2")
    {
        File.WriteAllText(Path.Combine(Repo, "Directory.Build.props"), $"<Project><PropertyGroup><VersionMajorMinor>{line}</VersionMajorMinor></PropertyGroup></Project>\n");
        Directory.CreateDirectory(Path.Combine(Repo, "src", "Core"));
        File.WriteAllText(Path.Combine(Repo, "src", "Core", "Core.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Drawboard.Core</PackageId></PropertyGroup></Project>\n");
        Directory.CreateDirectory(Path.Combine(Repo, "src", "Core.Tests"));
        File.WriteAllText(Path.Combine(Repo, "src", "Core.Tests", "Core.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>\n");
        Commit(Repo, "releases");
    }

    private sealed record Rig(NodeState State, FlightCommander Flights, PackageWatch Watch);

    private Rig NewRig(NodeState? state = null)
    {
        state ??= new NodeState(new InMemoryJournal(), _time);
        var git = new GitCli();
        var releases = new PackageReleases(git, _feed, _time);
        var flights = new FlightCommander(state, new Planes(git), _time, github: _github, packages: releases);
        // The tests look when they say; the delays are the defaults, and the clock is theirs.
        return new Rig(state, flights, new PackageWatch(state, flights, _feed, _github, _time));
    }

    private async Task<CommandResult> Drive(Rig node, string verb, string target, params string[] args) =>
        await node.Flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    /// <summary>TIA1, the producer, and TIA2, connected to it.</summary>
    private async Task TwoConnectedFlights(Rig node)
    {
        foreach (var issue in new[] { "1", "2" })
        {
            (await Drive(node, Verbs.CreateFlight, Verbs.RepoTarget(Repo), issue, $"Issue {issue}")).Ok.ShouldBeTrue();
        }
        (await Drive(node, Verbs.Connect, "flight/TIA2", "TIA1")).Ok.ShouldBeTrue();
        node.State.Flights().Single(f => f.Code == "TIA2").State.ShouldBe(FlightState.Held);
    }

    /// <summary>Lands TIA1 as its PR's merge does: at the head of its plane, GitHub saying which commit it merged as.</summary>
    private async Task LandProducer(Rig node, bool merged = true)
    {
        var flight = node.State.Flights().Single(f => f.Code == "TIA1");
        var head = Git(flight.Worktree, "rev-parse", "HEAD");
        if (merged)
        {
            _github.Pr = new PullRequest(7, "MERGED", head, "https://github.com/x/tianwen/pull/7", MergeCommit: MergeCommit);
        }
        await node.Flights.LandAfterMergeAsync("TIA1", head, Ct);
        node.State.Flights().Single(f => f.Code == "TIA1").State.ShouldBe(FlightState.Landed);
    }

    private FlightRow Flight(Rig node, string code) => node.State.Flights().Single(f => f.Code == code);

    private static WorkflowRun RunOf(string conclusion, DateTimeOffset? completedAt, string status = "completed") =>
        new(4711, "CI/CD", status, status == "completed" ? conclusion : null, "https://github.com/x/tianwen/actions/runs/4711", "push", completedAt);

    [Fact]
    public async Task AFlightConnectedToAReleasingFlightStaysHeldAfterTheLandAndFliesWhenTheVersionAndItsNupkgAreOnNuGet()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        _feed.Publish("Drawboard.Core", "1.1.9");
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node);
        _github.Runs[MergeCommit] = [RunOf("success", Start.AddMinutes(1))];

        var waiting = Flight(node, "TIA2");
        waiting.State.ShouldBe(FlightState.Held);
        var wait = waiting.PackageWaits.ShouldHaveSingleItem();
        (wait.Id, wait.Line, wait.Above, wait.Producer, wait.Commit).ShouldBe(("Drawboard.Core", "1.2", "1.2.5", "TIA1", MergeCommit));
        // The test project is no package, and the status says what it waits for.
        FlightWaiting.For(waiting).ShouldBe("Drawboard.Core 1.2 on nuget.org");
        new Digest(node.State, _time).Raise(_time.GetUtcNow()).Detail.ShouldNotBeNull().ShouldContain("- TIA2 held, waiting for Drawboard.Core 1.2 on nuget.org");
        Briefs.Watchtower("wt", node.State.Flights(), node.State.Legs(), node.State.Cards(), "pilot").ShouldContain("TIA2: Issue 2 (tianwen #2, held, waiting for Drawboard.Core 1.2 on nuget.org)");

        // Nothing newer than what there was at the land: still held, and the look backs off.
        await node.Watch.LookAsync(Ct);
        Flight(node, "TIA2").State.ShouldBe(FlightState.Held);
        var reads = _feed.IndexReads;
        _time.Advance(TimeSpan.FromSeconds(30));
        await node.Watch.LookAsync(Ct);
        _feed.IndexReads.ShouldBe(reads);

        // Listed, but its .nupkg cannot be fetched yet.
        _feed.Publish("Drawboard.Core", "1.2.6", nupkg: false);
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        Flight(node, "TIA2").State.ShouldBe(FlightState.Held);

        _feed.Publish("Drawboard.Core", "1.2.7");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        var flying = Flight(node, "TIA2");
        flying.State.ShouldBe(FlightState.Flying);
        flying.PackageWaits.ShouldBeEmpty();
        node.State.Cards().ShouldNotContain(c => c.Kind == PackageWatch.LateKind);
    }

    [Fact]
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

        var wait = Flight(node, "TIA2").PackageWaits.ShouldHaveSingleItem();
        (wait.Line, wait.Above).ShouldBe(("1.3", null));
        await node.Watch.LookAsync(Ct);
        Flight(node, "TIA2").State.ShouldBe(FlightState.Held);

        _feed.Publish("Drawboard.Core", "1.3.1");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        Flight(node, "TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task AFlightConnectedToAFlightThatReleasesNothingFliesOnTheLandAsBefore()
    {
        // No version line and no package in this repo.
        var node = NewRig();
        await TwoConnectedFlights(node);

        await LandProducer(node);

        var flying = Flight(node, "TIA2");
        flying.State.ShouldBe(FlightState.Flying);
        flying.PackageWaits.ShouldBeEmpty();
        _feed.IndexReads.ShouldBe(0);
    }

    [Fact]
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
        (card.Bin, card.Subject, card.State).ShouldBe((CardBin.Ring, "flight/TIA2", CardState.Open));
        card.Title.ShouldContain("Drawboard.Core 1.2");
        card.Detail.ShouldNotBeNull().ShouldContain("CI/CD #4711");
        card.Detail.ShouldContain("concluded success");
        card.Detail.ShouldContain("a version of 1.2 newer than 1.2.5");
        card.Detail.ShouldContain("latest published: 1.2.5");
        Flight(node, "TIA2").PackageWaits.ShouldHaveSingleItem().Escalated.ShouldBeTrue();

        // Never a second one, however long it takes.
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(11));
            await node.Watch.LookAsync(Ct);
        }
        node.State.Cards().Count(c => c.Kind == PackageWatch.LateKind).ShouldBe(1);
        Flight(node, "TIA2").State.ShouldBe(FlightState.Held);

        // It comes in the end: the flight flies and its card closes.
        _feed.Publish("Drawboard.Core", "1.2.9");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        Flight(node, "TIA2").State.ShouldBe(FlightState.Flying);
        node.State.Cards().Single(c => c.Kind == PackageWatch.LateKind).State.ShouldBe(CardState.Closed);
    }

    [Fact]
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

        _time.Advance(TimeSpan.FromMinutes(10));
        await node.Watch.LookAsync(Ct);
        var card = node.State.Cards().Where(c => c.Kind == PackageWatch.LateKind).ShouldHaveSingleItem();
        card.Detail.ShouldNotBeNull().ShouldContain("never ran");
        card.Detail.ShouldContain("newer than 1.2.5");
        card.Detail.ShouldContain("latest published: 1.2.5");
    }

    [Fact]
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
        var waiting = Flight(restarted, "TIA2");
        waiting.State.ShouldBe(FlightState.Held);
        var wait = waiting.PackageWaits.ShouldHaveSingleItem();
        (wait.Id, wait.Line, wait.Above, wait.Producer, wait.Commit, wait.Escalated).ShouldBe(("Drawboard.Core", "1.2", "1.2.5", "TIA1", MergeCommit, true));

        // The new node looks at once, says nothing twice, and lets the flight go when the package is up.
        await restarted.Watch.LookAsync(Ct);
        restarted.State.Cards().Count(c => c.Kind == PackageWatch.LateKind).ShouldBe(1);
        Flight(restarted, "TIA2").State.ShouldBe(FlightState.Held);
        _feed.Publish("Drawboard.Core", "1.2.6");
        _time.Advance(TimeSpan.FromMinutes(11));
        await restarted.Watch.LookAsync(Ct);
        Flight(restarted, "TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
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
        Flight(node, "TIA2").PackageWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task ANugetOutageDuringAWaitIsNoNewsAndTheNextLookAsksAgain()
    {
        MakeRepoAReleaser();
        _feed.Publish("Drawboard.Core", "1.2.5");
        var node = NewRig();
        await TwoConnectedFlights(node);
        await LandProducer(node);
        _feed.Offline = true;

        await node.Watch.LookAsync(Ct);
        Flight(node, "TIA2").State.ShouldBe(FlightState.Held);

        _feed.Offline = false;
        _feed.Publish("Drawboard.Core", "1.2.6");
        _time.Advance(TimeSpan.FromMinutes(11));
        await node.Watch.LookAsync(Ct);
        Flight(node, "TIA2").State.ShouldBe(FlightState.Flying);
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
