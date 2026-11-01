using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Per-repo settings (#254): the tracker a bare number means (<c>repo-trackers</c>) and the repos whose PRs the node never merges
/// by itself (<c>no-auto-merge-repos</c>), the Setup check for the Shortcut token, several repo roots, and a plane cut from a
/// develop default branch.
/// </summary>
public sealed class RepoPolicyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8]);

    public RepoPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
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

    /// <summary>A clone named <paramref name="name"/> under <paramref name="parent"/> of a bare origin whose default branch is <paramref name="branch"/>.</summary>
    private string Clone(string parent, string name, string branch)
    {
        var origin = Path.Combine(_root, name + ".git");
        var work = Path.Combine(parent, name);
        Directory.CreateDirectory(work);
        Git(_root, "init", "-q", "--bare", "-b", branch, origin);
        Git(work, "init", "-q", "-b", branch);
        File.WriteAllText(Path.Combine(work, "README.md"), "hello\n");
        Git(work, "add", ".");
        Git(work, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        Git(work, "remote", "add", "origin", origin);
        Git(work, "push", "-q", "-u", "origin", branch);
        Git(work, "remote", "set-head", "origin", branch);
        return work;
    }

    private static NodeSettings Saved(params (string Key, string[] Values)[] settings)
    {
        var current = new NodeSettings();
        foreach (var (key, values) in settings)
        {
            var (next, error) = NodeSettings.With(current, key, values);
            current = next ?? throw new InvalidOperationException(error);
        }
        return current;
    }

    [Fact]
    public void ARepoTrackersEntryIsTakenAsRepoEqualsTrackerAndRoundTripsThroughTheFile()
    {
        var settings = Saved(("repo-trackers", ["bullclip-backend=shortcut", "tianwen=github", "bullclip-backend=Shortcut"]));

        settings.RepoTrackers.ShouldBe(["tianwen=github", "bullclip-backend=shortcut"]);
        settings.TrackerOf("bullclip-backend").ShouldBe(TrackerKind.Shortcut);
        settings.TrackerOf("BULLCLIP-BACKEND").ShouldBe(TrackerKind.Shortcut);
        settings.TrackerOf("tianwen").ShouldBe(TrackerKind.GitHub);
        settings.TrackerOf("elsewhere").ShouldBe(TrackerKind.GitHub);
        var file = Path.Combine(_root, "settings.json");
        settings.Write(file);
        NodeSettings.Read(file).RepoTrackers.ShouldBe(settings.RepoTrackers);
        NodeSettings.With(settings, "repo-trackers", ["bullclip-backend=jira"]).Error.ShouldNotBeNull().ShouldContain("repo=tracker");
        NodeSettings.With(settings, "repo-trackers", ["default"]).Settings!.RepoTrackers.ShouldBeNull();
    }

    [Theory]
    [InlineData("101645", TrackerKind.Shortcut)]
    [InlineData("#101645", TrackerKind.GitHub)]
    [InlineData("sc-101645", TrackerKind.Shortcut)]
    public void ABareNumberTakesTheReposTrackerButAHashAndScAreExplicit(string given, TrackerKind tracker) =>
        WorkItem.Parse(given, TrackerKind.Shortcut).Item.ShouldBe(new WorkItem(tracker, 101645));

    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABareNumberOnAShortcutDefaultRepoMakesAShortcutFlightAndOnAnyOtherRepoAGitHubOne()
    {
        var parent = Path.Combine(_root, "repos");
        var backend = Clone(parent, "bullclip-backend", "main");
        var other = Clone(parent, "tianwen", "main");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var commander = new FlightCommander(state, new Planes(new GitCli()), TimeProvider.System)
        {
            Models = new RoleModels(Saved(("repo-trackers", ["bullclip-backend=shortcut"]))),
        };

        (await commander.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(backend), Args = ["101645", "Pay"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await commander.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(other), Args = ["7", "Planets"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();
        (await commander.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(backend), Args = ["#9", "An issue"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        var flights = state.Flights();
        flights.Single(f => f.Issue == 101645).ShouldSatisfyAllConditions(f => f.Tracker.ShouldBe(TrackerKind.Shortcut), f => f.Branch.ShouldBe("flight/bul101645-sc-101645"));
        flights.Single(f => f.Issue == 7).Tracker.ShouldBe(TrackerKind.GitHub);
        flights.Single(f => f.Issue == 9).Tracker.ShouldBe(TrackerKind.GitHub);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task APlaneIsCutFromADevelopDefaultBranchAndSeveralRepoRootsAreAllListed()
    {
        var first = Path.Combine(_root, "one");
        var second = Path.Combine(_root, "two");
        var api = Clone(first, "api", "develop");
        var web = Clone(second, "web", "main");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var commander = new FlightCommander(state, new Planes(new GitCli()), TimeProvider.System);

        (await commander.RunAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(api), Args = ["5", "On develop"], Who = "tests" }, Ct)).Ok.ShouldBeTrue();

        var flight = state.Flights().Single();
        flight.Base.ShouldBe("origin/develop");
        Git(flight.Worktree, "rev-parse", "HEAD").ShouldBe(Git(api, "rev-parse", "origin/develop"));
        (await new Planes(new GitCli()).DefaultRefAsync(api, Ct)).ShouldBe("origin/develop");

        // The setting takes several roots, and the catalog lists the clones under each.
        var roots = Saved(("repo-roots", [first, second])).RepoRoots.ShouldNotBeNull();
        roots.Count.ShouldBe(2);
        new RepoCatalog(new NodeState(new InMemoryJournal(), TimeProvider.System), new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: roots))
            .Repos().Select(r => r.Name).Order().ToArray().ShouldBe(["api", "web"]);
        Directory.Exists(web).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void TheSetupChecklistSaysWhetherAShortcutTokenIsPresent()
    {
        var absent = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), "pilot-node", _root) { ShortcutToken = () => null };
        var present = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), "pilot-node", _root) { ShortcutToken = () => "tok" };

        var without = absent.Checks().Single(c => c.Key == NodeSetup.ShortcutKey);
        var with = present.Checks().Single(c => c.Key == NodeSetup.ShortcutKey);

        (without.Ok, without.Optional).ShouldBe((false, true));
        without.Line.ShouldContain("shortcut-token");
        with.Ok.ShouldBeTrue();
        with.Line.ShouldStartWith("set");
    }
}
