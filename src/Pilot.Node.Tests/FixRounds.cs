using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;

namespace Pilot.Node.Tests;

/// <summary>
/// The fix round the node sends itself when a review asks for changes below the round limit (pilot #1271): the leg flies again with a
/// fresh worker, who commits in the plane and finishes, which pushes the new head and starts the next review by itself. A test that
/// started that review by hand after a changes verdict was the person pressing the card's button, in the old default the switch kept.
/// </summary>
internal static class FixRounds
{
    /// <summary>
    /// The leg's worker commits <paramref name="files"/> in <paramref name="code"/>'s plane and finishes the round; returns once the
    /// next review's reviewer is up. A round's finish is refused when its commits change none of the files the Blocking findings name
    /// (the untouched-findings gate, pilot #1024), so a test whose findings name a file passes it here. <paramref name="git"/> is the
    /// test base's own git (the in-memory model, or real git).
    /// </summary>
    public static async Task FinishAsync(LegCommander legs, NodeState state, string code, IReadOnlyList<string> files, Func<string, string[], string> git, CancellationToken ct, string leg = "parser")
    {
        // The round's worker is on the leg once its launch is done, which a loaded machine does later.
        await legs.WhenNotStartingAsync(code, TimeSpan.FromMinutes(5), ct);
        var flying = state.Legs(code).Single(l => l.Name == leg);
        flying.State.ShouldBe(LegState.Flying);
        var worktree = state.Flights().Single(f => f.Code == code).Worktree;
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(worktree, file), "more\n");
        }
        git(worktree, ["add", "."]);
        git(worktree, ["-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", $"fix {string.Join(", ", files)}"]);
        var finished = await legs.RunAsync(new CommandRequest { Verb = Verbs.FinishLeg, Target = Verbs.LegTarget(code, leg), Args = ["done", "fixed the findings"], Who = flying.Worker! }, ct);
        finished.Error.ShouldBeNull();
        await legs.WhenNotStartingAsync(code, TimeSpan.FromMinutes(5), ct);
    }
}
