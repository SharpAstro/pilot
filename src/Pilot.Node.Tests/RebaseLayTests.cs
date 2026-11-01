using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The rebase probe and the merge queue method (pilot #523): a PR can merge cleanly and still not rebase onto its base, which a merge
/// queue that rebases cannot land. The probe tells so in a throwaway worktree, never touching the plane.
/// </summary>
public sealed class RebaseLayTests : LegCommanderTestsBase
{
    [Theory]
    [InlineData("[{\"type\":\"merge_queue\",\"parameters\":{\"merge_method\":\"REBASE\"}}]", "REBASE")]
    [InlineData("[{\"type\":\"deletion\"},{\"type\":\"merge_queue\",\"parameters\":{\"merge_method\":\"SQUASH\"}}]", "SQUASH")]
    [InlineData("[{\"type\":\"merge_queue\",\"parameters\":{\"merge_method\":\"MERGE\"}}]", "MERGE")]
    [InlineData("[{\"type\":\"deletion\"}]", null)]
    [InlineData("[]", null)]
    [InlineData("[{\"type\":\"merge_queue\"}]", null)]
    [InlineData("{\"message\":\"Not Found\"}", null)]
    [InlineData("", null)]
    public void GhCliReadsTheMergeQueueMethodOfABase(string rules, string? method) =>
        GhCli.ParseMergeQueueMethod(rules).ShouldBe(method);

    [Fact]
    public void TheFakeAnswersNoMergeQueueByDefault() =>
        _github.MergeQueueMethod.ShouldBeNull();

    // A branch that holds a keep-ours merge of main's side branch, then commits on the same lines: the merge with main is clean
    // (main is an ancestor), but a rebase flattens the merge away and replays the first commit onto main's version of the line.
    private string KeepOursPlane()
    {
        var plane = SeedRepo.Make(Path.Combine(_root, "keep-ours"));
        File.WriteAllText(Path.Combine(plane, "f.txt"), "x\n");
        Git(plane, "add", ".");
        Commit(plane, "f");
        Git(plane, "push", "-q", "origin", "main");
        Git(plane, "checkout", "-q", "-b", "side");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "side\n");
        Commit(plane, "side", all: true);
        Git(plane, "checkout", "-q", "main");
        Git(plane, "merge", "-q", "--ff-only", "side");
        Git(plane, "push", "-q", "origin", "main");
        Git(plane, "checkout", "-q", "-b", "flight/keep", "HEAD~1");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine1\n");
        Commit(plane, "mine 1", all: true);
        Git(plane, "-c", "user.email=t@example.com", "-c", "user.name=t", "merge", "-q", "-s", "ours", "-m", "keep ours", "main");
        File.WriteAllText(Path.Combine(plane, "f.txt"), "mine2\n");
        Commit(plane, "mine 2", all: true);
        return plane;
    }

    private void Commit(string cwd, string message, bool all) =>
        Git(cwd, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", all ? "-a" : "--allow-empty-message", "-m", message);

    [Fact]
    public async Task ABranchThatMergesCleanlyButDoesNotRebaseIsNotClean()
    {
        var plane = KeepOursPlane();
        // The premise: a merge with main is clean.
        var tree = await new GitCli().RunAsync(["merge-tree", "--write-tree", "origin/main", "HEAD"], plane, Ct);
        tree.Ok.ShouldBeTrue();
        var head = Git(plane, "rev-parse", "HEAD");
        var branch = Git(plane, "branch", "--show-current");

        var probe = await _legs.ProbeRebaseAsync(plane, "main", Ct);

        probe.Clean.ShouldBeFalse();
        probe.Error.ShouldBeNull();
        probe.Files.ShouldBe(["f.txt"]);
        Git(plane, "rev-parse", "HEAD").ShouldBe(head);
        Git(plane, "branch", "--show-current").ShouldBe(branch);
        Git(plane, "status", "--porcelain").ShouldBeEmpty();
        Git(plane, "worktree", "list", "--porcelain").Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task ALinearBranchIsClean()
    {
        var plane = SeedRepo.Make(Path.Combine(_root, "linear"));
        Git(plane, "checkout", "-q", "-b", "flight/lin");
        File.WriteAllText(Path.Combine(plane, "g.txt"), "g\n");
        Git(plane, "add", ".");
        Commit(plane, "g", all: false);
        var head = Git(plane, "rev-parse", "HEAD");

        var probe = await _legs.ProbeRebaseAsync(plane, "main", Ct);

        probe.Clean.ShouldBeTrue();
        probe.Files.ShouldBeEmpty();
        Git(plane, "rev-parse", "HEAD").ShouldBe(head);
        Git(plane, "status", "--porcelain").ShouldBeEmpty();
        Git(plane, "worktree", "list", "--porcelain").Split('\n').Count(l => l.StartsWith("worktree ", StringComparison.Ordinal)).ShouldBe(1);
    }
}
