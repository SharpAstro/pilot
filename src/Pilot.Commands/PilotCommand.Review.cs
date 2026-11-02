using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>
    /// The PR whose review worktree holds <paramref name="cwd"/>, and who is asking: its reviewer while the review is on, as
    /// a flight's reviewer is in <see cref="FlightHere"/>. Null outside every PR review's worktree.
    /// </summary>
    internal static (string Target, string Who)? PrHere(SnapshotEvent snapshot, string cwd)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        return snapshot.PrReviews.FirstOrDefault(r => r.State != PrReviewState.Closed
                && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Worktree)) + Path.DirectorySeparatorChar, comparison)) is { } review
            ? (Verbs.PrTarget(review.Repo, review.Number), review.State == PrReviewState.Reviewing ? review.Reviewer : "cli")
            : null;
    }

    /// <summary>A PR as the CLI was given it, or a usage error naming the two ways to give one.</summary>
    private static PrRef Pr(string text) =>
        PrRef.Parse(text) ?? throw new UsageException($"a PR is owner/repo#n or its URL (https://github.com/owner/repo/pull/n), not {text}");
}
