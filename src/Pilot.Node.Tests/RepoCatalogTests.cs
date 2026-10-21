using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The new-flight wizard's repos: the clones under the roots, their GitHub names, the busiest first.</summary>
public sealed class RepoCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2r-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A folder that looks like a clone: a .git folder, with an origin when given.
    private string Clone(string name, string? origin = null)
    {
        var folder = Path.Combine(_root, name);
        var git = Directory.CreateDirectory(Path.Combine(folder, ".git")).FullName;
        File.WriteAllText(Path.Combine(git, "config"), origin is null
            ? "[core]\n\tbare = false\n"
            : $"[core]\n\tbare = false\n[remote \"origin\"]\n\turl = {origin}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        return folder;
    }

    [Fact]
    public void TheClonesUnderARootAreOfferedWithTheirGitHubNamesTheBusiestFirst()
    {
        Clone("Zeta.Lib", "https://github.com/SharpAstro/Zeta.Lib.git");
        var tianwen = Clone("tianwen", "git@github.com:SharpAstro/tianwen.git");
        Clone("local-only");
        // A worktree or submodule's .git is a file: no repo of its own.
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "a-worktree")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere\n");
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutFlight(new FlightRow
        {
            Code = "TIA1", Repo = "tianwen", RepoPath = tianwen, Issue = 1, Branch = "flight/tia1", Worktree = "/w", Base = "main",
            State = FlightState.Landed, CreatedAt = DateTimeOffset.UnixEpoch,
        });

        // The flight's repo's folder is a root by itself: no root given.
        var repos = new RepoCatalog(state, new NodeOptions("/tmp/n.sock", "/tmp/data")).Repos();

        repos.Select(r => (r.Name, r.GitHub, r.Flights)).ShouldBe([
            ("tianwen", "SharpAstro/tianwen", 1),
            ("local-only", null, 0),
            ("Zeta.Lib", "SharpAstro/Zeta.Lib", 0),
        ]);
    }

    [Fact]
    public void APlannersNameIsAPlannerOfItsFlight()
    {
        var role = SessionRoles.Resolve("se1-pil99-plan");

        (role.Role, role.Flight, role.Leg).ShouldBe((SessionRole.Planner, "PIL99", (string?)null));
    }
}
