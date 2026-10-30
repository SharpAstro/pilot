using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The node's repo status as a row on the event stream (pilot #496): kept in its journal and snapshot, never absent while it knows repos.</summary>
public sealed class ReposRowTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Noon = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private string Clone(string name)
    {
        var git = Directory.CreateDirectory(Path.Combine(_root, name, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "config"), $"[remote \"origin\"]\n\turl = https://github.com/SharpAstro/{name}.git\n");
        return Path.Combine(_root, name);
    }

    private RepoWatch Watch(NodeState state) =>
        new(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), new Planes(new GitCli()), new FakeGitHub(), TimeProvider.System)
        {
            Parallelism = 1,
            Interval = TimeSpan.FromHours(1),
        };

    private static RepoStatus Status(string name, string? branch = "main", DateTimeOffset? updated = null) =>
        new() { Name = name, Path = "/r/" + name, Branch = branch, UpdatedAt = updated ?? Noon };

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false" }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', args)}");
        return output;
    }

    private sealed class Collect : INodeEventSink
    {
        public List<NodeEvent> Events { get; } = [];

        public int Capacity => 1024;

        public bool TryWrite(byte[] message)
        {
            Events.Add(JsonSerializer.Deserialize(message, PilotJsonContext.Default.NodeEvent)!);
            return true;
        }

        public void Overflowed()
        {
        }
    }

    [Fact]
    public async Task AReadReachesASubscriberAsAReposChangedEventAndTheMirrorHoldsIt()
    {
        Clone("alpha");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var sink = new Collect();
        using var subscription = state.Subscribe(null, null, sink);

        await Watch(state).RefreshAsync(Ct);

        var changed = sink.Events.OfType<ReposChangedEvent>().Last();
        changed.Row.ReadAt.ShouldNotBeNull();
        changed.Row.Since.ShouldBeNull();
        changed.Row.Repos.Single().ShouldSatisfyAllConditions(r => r.Name.ShouldBe("alpha"), r => r.Unread.ShouldBeFalse());
        var mirror = new NodeMirror();
        foreach (var e in sink.Events)
        {
            mirror.Apply(e);
        }
        mirror.Repos.ShouldBe(changed.Row);
    }

    [Fact]
    public async Task AResumedStreamsSnapshotHoldsTheRow()
    {
        Clone("alpha");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        await Watch(state).RefreshAsync(Ct);

        var sink = new Collect();
        using var subscription = state.Subscribe(null, null, sink);

        sink.Events.OfType<SnapshotEvent>().Single().Repos!.Repos.Single().Name.ShouldBe("alpha");
    }

    [Fact]
    public void ANodeRestoredFromAJournalSnapshotCarriesTheLastRow()
    {
        var journal = new InMemoryJournal(maxDeltas: 1);
        var first = new NodeState(journal, TimeProvider.System);
        first.PutRepos(new ReposRow { Repos = [Status("alpha")], ReadAt = Noon });
        // The second delta compacts the journal into a snapshot, which the restart restores from.
        first.PutRepos(new ReposRow { Repos = [Status("alpha", "feat")], ReadAt = Noon.AddMinutes(1) });
        journal.DeltaCount.ShouldBe(0);

        var restored = new NodeState(journal, TimeProvider.System);

        restored.Repos()!.Repos.Single().Branch.ShouldBe("feat");
        restored.Snapshot().Repos!.ReadAt.ShouldBe(Noon.AddMinutes(1));
    }

    [Fact]
    public async Task AFreshNodeSnapshotsTheCatalogsReposMarkedUnreadAtBootAndARestoredOneKeepsItsRow()
    {
        Clone("alpha");
        Clone("beta");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var watch = Watch(state);
        var sink = new Collect();
        using var subscription = state.Subscribe(null, null, sink);

        await ((IHostedService)watch).StartAsync(Ct);
        await ((IHostedService)watch).StopAsync(CancellationToken.None);

        // The first row put is the seed: the repos known, not read yet.
        var seed = sink.Events.OfType<ReposChangedEvent>().First().Row;
        seed.ReadAt.ShouldBeNull();
        seed.Since.ShouldNotBeNull();
        seed.Repos.Select(r => r.Name).OrderBy(n => n).ShouldBe(["alpha", "beta"]);
        seed.Repos.ShouldAllBe(r => r.Unread && r.GitHub != null);

        // A node that holds a row already puts no seed over it.
        var held = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var kept = new ReposRow { Repos = [Status("kept")], ReadAt = Noon };
        held.PutRepos(kept);
        var other = Watch(held);
        await ((IHostedService)other).StartAsync(Ct);
        await ((IHostedService)other).StopAsync(CancellationToken.None);
        held.Repos()!.Repos.ShouldNotContain(r => r.Unread);
    }

    [Fact]
    public void AnUnchangedReadWithinTwoMinutesPublishesNothingButAChangedOrOldOneDoes()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutRepos(new ReposRow { Repos = [Status("alpha")], ReadAt = Noon });
        var seq = state.Seq;

        state.PutRepos(new ReposRow { Repos = [Status("alpha", updated: Noon.AddMinutes(1))], ReadAt = Noon.AddMinutes(1) });
        state.Seq.ShouldBe(seq);

        state.PutRepos(new ReposRow { Repos = [Status("alpha", updated: Noon.AddMinutes(2))], ReadAt = Noon.AddMinutes(2) });
        state.Seq.ShouldBe(seq + 1);

        state.PutRepos(new ReposRow { Repos = [Status("alpha", "feat")], ReadAt = Noon.AddMinutes(2) });
        state.Seq.ShouldBe(seq + 2);
    }

    [Fact]
    public async Task ARefreshAfterTheMainCheckoutSwitchedBranchShowsTheNewBranch()
    {
        var repo = Path.Combine(_root, "gamma");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "commit", "-q", "--allow-empty", "-m", "first");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var watch = Watch(state);

        await watch.RefreshAsync(Ct);
        state.Repos()!.Repos.Single(r => r.Name == "gamma").Branch.ShouldBe("main");

        Git(repo, "checkout", "-q", "-b", "other");
        await watch.RefreshAsync(Ct);

        state.Repos()!.Repos.Single(r => r.Name == "gamma").Branch.ShouldBe("other");
        (await watch.StatusAsync(refresh: false, Ct)).Single(r => r.Name == "gamma").Branch.ShouldBe("other");
    }
}
