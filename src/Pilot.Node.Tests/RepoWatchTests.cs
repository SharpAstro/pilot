using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A repo look sweeps this node's label off an open issue no flight of this node is on (#322).</summary>
public sealed class RepoWatchTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2w-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private string Clone()
    {
        var git = Directory.CreateDirectory(Path.Combine(_root, "tianwen", ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "config"), "[remote \"origin\"]\n\turl = https://github.com/SharpAstro/tianwen.git\n");
        return Path.Combine(_root, "tianwen");
    }

    private static FlightRow Flight(string repoPath, int issue, FlightState state) => new()
    {
        Code = $"TIA{issue}", Repo = "tianwen", RepoPath = repoPath, Issue = issue, Branch = $"flight/tia{issue}",
        Worktree = Path.Combine(repoPath, ".claude", "worktrees", $"tia{issue}"), Base = "origin/main", State = state, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private Task<RepoStatus[]> Look(string repoPath, FakeGitHub github, params FlightRow[] flights)
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        foreach (var flight in flights)
        {
            state.PutFlight(flight);
        }
        var watch = new RepoWatch(state, new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data", RepoRootsGiven: [_root])), new Planes(new GitCli()), github, TimeProvider.System)
        {
            Machine = "DE1",
            Parallelism = 1,
        };
        return watch.RefreshAsync(Ct);
    }

    [Fact]
    public async Task ThisNodesLabelOnAnOpenIssueNoFlightOfItsIsOnIsTakenOffAndNothingElseIs()
    {
        var repo = Clone();
        var github = new FakeGitHub();
        github.Issues[1] = "flying";
        github.Issues[2] = "held";
        github.Issues[3] = "stale";
        github.Issues[4] = "landed";
        github.Issues[5] = "another node's";
        github.IssueLabels[1] = ["pilot node: DE1"];
        github.IssueLabels[2] = ["pilot node: DE1"];
        github.IssueLabels[3] = ["pilot node: DE1", "bug"];
        github.IssueLabels[4] = ["pilot node: DE1"];
        github.IssueLabels[5] = ["pilot node: SE1"];

        var all = await Look(repo, github, Flight(repo, 1, FlightState.Flying), Flight(repo, 2, FlightState.Held), Flight(repo, 4, FlightState.Landed));

        all.Single().Errors.Where(e => e.Contains("node label")).ShouldBeEmpty();
        github.IssueLabels[1].ShouldBe(["pilot node: DE1"]);
        github.IssueLabels[2].ShouldBe(["pilot node: DE1"]);
        github.IssueLabels[3].ShouldBe(["bug"]);
        github.IssueLabels[4].ShouldBeEmpty();
        github.IssueLabels[5].ShouldBe(["pilot node: SE1"]);
    }

    [Fact]
    public async Task ASweepGitHubRefusesIsTheRepoStatusErrorNotAStop()
    {
        var repo = Clone();
        var github = new FakeGitHub();
        github.Issues[3] = "stale";
        github.IssueLabels[3] = ["pilot node: DE1"];
        github.IssueLabelFails = "no permission";

        var status = (await Look(repo, github)).Single();

        status.Errors.ShouldHaveSingleItem().ShouldContain("no permission");
    }
}
