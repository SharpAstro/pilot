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
    private sealed partial class BuildContext
    {
        /// <summary>Makes the review: a PR's or a flight's review, and its verdicts.</summary>
        internal Command ReviewCommands()
        {
            // review: the reviewer's verdict, from its plane (or a PR review's worktree); or the controller's "ready anyway" with --flight
            // Each verdict is a command of its own, not a word review takes: the words after it are only ever the report, where
            // a report saying "pr" would otherwise be read as review pr.
            var verdictReport = Words("report", "What was checked, or each change asked for");
            var reviewFlight = new Option<string?>("--flight") { Description = "The flight's code; inside its plane, that flight" };
            var reviewPrOption = new Option<string?>("--pr") { Description = "The PR, owner/repo#n or its URL; inside its review's worktree, that PR" };
            var finding = new Option<string[]>("--finding")
            {
                Description = "A PR review's finding on a line: path:line from the repo's root, then what is wrong; once per finding",
                AllowMultipleArgumentsPerToken = false,
            };
            Command Verdict(string word, string description) => Action(new Command(word, description) { Arguments = { verdictReport }, Options = { reviewFlight, reviewPrOption, finding } },
                async (p, client, ct) =>
                {
                    var report = Join(p, verdictReport);
                    var findings = p.GetValue(finding) ?? [];
                    foreach (var text in findings)
                    {
                        _ = PrFinding.Parse(text) ?? throw new UsageException($"a finding is path:line from the repo's root, then what is wrong (src/A.cs:42 the null check is gone); not '{text}'");
                    }
                    if (p.GetValue(reviewFlight) is { } named)
                    {
                        return findings.Length > 0
                            ? throw new UsageException("--finding is for a PR's review; a flight's review says its findings in the report")
                            : await Send(client, Verbs.Review, Verbs.FlightTarget(named), [word, report], who, ct);
                    }
                    var snapshot = await SnapshotAsync(client, ct);
                    var (target, sender) = p.GetValue(reviewPrOption) is { } pr
                        ? (Verbs.PrTarget(Pr(pr).Repo, Pr(pr).Number), who)
                        : PrHere(snapshot, cwd) ?? FlightHere(snapshot, cwd, who);
                    if (!target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal))
                    {
                        return findings.Length > 0
                            ? throw new UsageException("--finding is for a PR's review; a flight's review says its findings in the report")
                            : await Send(client, Verbs.Review, target, [word, report], sender, ct);
                    }
                    return await Send(client, Verbs.PrVerdict, target, [word, report, .. findings], sender, ct);
                });
            // review pr: a fresh reviewer on any PR, pinned at its head in a worktree of the node's own
            var prRef = Arg("pr", "The PR: owner/repo#n, or its URL");
            var reviewPr = Action(new Command("pr", "Start a reviewer on any PR, pinned at its head; its verdict comes back as a card. To have pilot look at a PR again later, put the 'pilot: recheck' label on it") { Arguments = { prRef } },
                (p, client, ct) =>
                {
                    var pr = Pr(p.GetValue(prRef)!);
                    return Send(client, Verbs.ReviewPr, Verbs.PrTarget(pr.Repo, pr.Number), [], who, ct);
                });
            // review fix: the fixer of a PR whose review asked for changes, as its card's Fix it starts it (#350: tianwen #1857's
            // fixer could not start, its card was gone, and nothing but a script could start it again)
            var fixAnswer = new Argument<string[]>("answer") { Description = "The answer to the review's question of design, posted on the PR first; without it the PR's author's or the controller's comment after the review is the answer", Arity = ArgumentArity.ZeroOrMore };
            var reviewFix = Action(new Command("fix", "Start a fixer on a PR whose review asked for changes, as its card's Fix it does; a review that asked a question of design needs its answer (words after the PR, or a comment on the PR)") { Arguments = { prRef, fixAnswer } },
                (p, client, ct) =>
                {
                    var pr = Pr(p.GetValue(prRef)!);
                    return Send(client, Verbs.FixPr, Verbs.PrTarget(pr.Repo, pr.Number), p.GetValue(fixAnswer) ?? [], who, ct);
                });
            var review = new Command("review", "Review any PR (review pr), or give a review's verdict: a flight's pass offers the merge and changes rings for a go-around; a PR's rings with its findings")
            {
                Subcommands =
                {
                    reviewPr,
                    reviewFix,
                    Verdict("pass", "A flight's review: ready, the merge is offered"),
                    Verdict("changes", "A flight's or a PR's review: changes asked for, each one in the report"),
                    Verdict("approve", "A PR's review: it is right as it is"),
                    Verdict("comment", "A PR's review: questions, or findings that should not hold it up"),
                },
            };

            return review;
        }
    }
}
