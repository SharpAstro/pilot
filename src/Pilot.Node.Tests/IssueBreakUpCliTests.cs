using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>pilot issue break-up against a node on a temporary socket, with the fake splitter: the verb reaches the node, the parts print, --save writes.</summary>
public sealed class IssueBreakUpCliTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2b-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SeededRepos _repos;

    public IssueBreakUpCliTests() => _repos = new SeededRepos(_root);

    public void Dispose() => _repos.Dispose();

    private static async Task<(int Exit, string Output, string Error)> Pilot(NodeHarness node, string cwd, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", node.SocketPath, .. args], cwd, output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    // The repo is laid on the in-memory git (#952), which the node runs on too: no git process starts.
    private string MakeRepo() => _repos.Seed("tianwen");

    [Fact]
    public async Task IssueBreakUpShowsTheProposedPartsAndWritesNothingUntilSave()
    {
        var repo = MakeRepo();
        await using var node = await NodeHarness.StartAsync(Ct, git: _repos.Git);
        node.GitHub.AddMilestone(repo, "v1");
        node.GitHub.AddIssue(repo, "Rings and shadows", "v1", body: "do both");

        var shown = await Pilot(node, _root, "issue", "break-up", "--repo", repo, "1", "--into", "2");

        shown.Exit.ShouldBe(0, shown.Output + shown.Error);
        node.Splitter.Asked.Single().Into.ShouldBe(2);
        shown.Output.ShouldContain("1. Draw the rings");
        shown.Output.ShouldContain("2. Shadow the planet");
        shown.Output.ShouldContain("? Soft or hard edge?");
        shown.Output.ShouldContain("nothing is written");
        node.GitHub.IssuesCreated.ShouldBeEmpty();

        var saved = await Pilot(node, _root, "issue", "break-up", "--repo", repo, "1", "--save");

        saved.Exit.ShouldBe(0, saved.Output + saved.Error);
        saved.Output.ShouldContain("broken up #1");
        saved.Output.ShouldContain("new issues #");
        node.GitHub.IssuesCreated.Select(c => c.Title).ShouldBe(["Shadow the planet"]);
    }

    [Fact]
    public async Task DiscardForgetsTheProposalAndSaveWithoutOneIsRefused()
    {
        var repo = MakeRepo();
        await using var node = await NodeHarness.StartAsync(Ct, git: _repos.Git);
        node.GitHub.AddMilestone(repo, "v1");
        node.GitHub.AddIssue(repo, "Rings and shadows", "v1");
        await Pilot(node, _root, "issue", "break-up", "--repo", repo, "1");

        var discarded = await Pilot(node, _root, "issue", "break-up", "--repo", repo, "1", "--discard");
        var save = await Pilot(node, _root, "issue", "break-up", "--repo", repo, "1", "--save");

        discarded.Output.ShouldContain("discarded the break-up of #1");
        save.Exit.ShouldNotBe(0);
        save.Error.ShouldContain("no break-up of #1 is waiting");
    }
}
