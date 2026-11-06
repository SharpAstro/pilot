using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>
    /// What a PR's reviewer is told (#113): the flight reviewer's brief for a PR with no legs and no Done-when, so the PR's
    /// own description and the tree say what it claims. It reads and runs, changes nothing and posts nothing; its verdict
    /// goes to the node, whose card carries it to the controller.
    /// </summary>
    /// <param name="login">The gh login a review goes out under, when gh said it: on that login's own PR, only a comment.</param>
    public static string ReviewPr(PrReviewRow review, PlaneState plane, string? login, string run, string? machine = null)
    {
        var text = new StringBuilder();
        var pr = $"{review.Repo}#{review.Number}";
        var title = review.Title is { Length: > 0 } t ? $": {t}" : "";
        var by = review.Author is { Length: > 0 } a ? $", by {a}" : "";
        text.AppendLine($"You review PR {pr}{title}{by} ({review.Url}).");
        text.AppendLine();
        text.AppendLine($"Work in this folder ({review.Worktree}), a worktree pinned at the PR's head {CommitRefs.Short(review.Head)}");
        text.AppendLine($"(detached), against `origin/{review.Base}`. Read the PR's description and its discussion first");
        text.AppendLine($"(`gh pr view {review.Number} -R {review.Repo} --comments`), then the whole diff (`git diff origin/{review.Base}...HEAD`).");
        text.AppendLine("Check each claim, the description's and the code's, against the tree as it stands here, not against the diff");
        text.AppendLine("alone: open the files around a change, follow its callers, and run what proves or disproves it where you can.");
        text.AppendLine("Do not edit, commit, push or merge anything, and post nothing to GitHub (no review, no comment): the controller");
        text.AppendLine("reads your verdict and posts it.");
        text.Append(BranchHistoryRule);
        text.Append(NamingRule(machine, $"review-{review.Number}"));
        text.Append(LfsRule(review.Worktree, "worktree"));
        // As for a flight's review (20 Oct): one session's reading, very light on subagents; its depth follows #74 later.
        text.AppendLine("Do the review yourself, in this session: no subagents, or one at most for a single narrow question. Fanning");
        text.AppendLine("out reviewers costs many times the tokens and is not what this review is for.");
        if (plane.Commits.Trim().Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Commits:");
            text.AppendLine(plane.Commits.Trim());
        }
        if (plane.DiffStat.Trim().Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Files:");
            text.AppendLine(plane.DiffStat.Trim());
        }
        text.AppendLine();
        text.AppendLine("Look for: bugs and regressions, a claim the code does not bear out, tests that do not test what they say, and");
        text.AppendLine("anything the PR says it does that is missing. Style alone is no finding.");
        text.AppendLine("Never assert a defect you cannot prove from the tree: one you suspect and cannot show becomes a question for the");
        text.AppendLine("author, said as one. Each finding names its file and line in the PR's head (`path/to/File.cs:42`), what is wrong");
        text.AppendLine("and how you know.");
        text.AppendLine();
        text.AppendLine("## Your verdict");
        text.AppendLine($"`{run} review <verdict> \"<a summary in a line or two, and any finding on no line>\" --finding \"<path:line> <what is wrong>\"`");
        text.AppendLine("from this folder, with one `--finding` for each finding on a line (its path from the repo's root). The controller");
        text.AppendLine("posts it as one review: each finding whose line is in the diff an inline comment there, the rest in its body, so");
        text.AppendLine("write each as the comment the author will read. The verdict is one of:");
        text.AppendLine("- `approve`: it is right as it is (questions and nits may still go with it).");
        text.AppendLine("- `changes`: a finding you can prove must be fixed before it merges.");
        text.AppendLine("- `comment`: neither: questions, or findings that should not hold it up.");
        text.AppendLine("A `changes` verdict on the owner's PR starts a session that makes the changes. Start a finding with `design:` when it is");
        text.AppendLine("a question of design or direction (a choice for the author, not a defect to fix): such a finding goes to the");
        text.AppendLine("controller and starts no fixer. Write it on one line as `design: <the question> Option: <choice> => <what follows>`,");
        text.AppendLine("with 2 to 4 concrete options (each `Option: <choice> => <consequence in a line>`) and `Option*:` for the one you recommend.");
        var own = login is { Length: > 0 } && string.Equals(login, review.Author, StringComparison.OrdinalIgnoreCase);
        if (own)
        {
            text.AppendLine($"This PR is {login}'s own, and the review goes out as {login}: GitHub posts it as a comment, but give your real");
            text.AppendLine("verdict all the same: the PR's status and label say it.");
        }
        text.AppendLine("Give it once. Then end your turn with the same verdict.");
        text.AppendLine(AttachmentRule(run));
        return text.ToString();
    }
}
