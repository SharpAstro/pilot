using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>
    /// What a PR's fixer is told (#290): the reviewer's report and every finding, the repo's CI build and test, and the
    /// rules of working on a branch other people read. It starts in a worktree on the PR's own branch.
    /// </summary>
    /// <param name="build">How CI builds the repo; null when no workflow says.</param>
    public static string FixPr(PrReviewRow review, string branch, CiShape.Build? build)
    {
        var text = new StringBuilder();
        var pr = $"{review.Repo}#{review.Number}";
        var title = review.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You fix PR {pr}{title} ({review.Url}).");
        text.AppendLine();
        var shortHead = review.Head[..Math.Min(8, review.Head.Length)];
        if (review.FixDetached == true)
        {
            text.AppendLine($"Work in this folder ({review.FixWorktree}), a worktree detached at the head of the PR's branch `{branch}` (the branch is checked out in another worktree, so it cannot be checked out here).");
            text.AppendLine($"Commit on the detached HEAD and push with `git push origin HEAD:refs/heads/{branch}`, plainly and never with force: it is a fast-forward, refused if the branch moved. A reviewer read the PR at");
        }
        else if (review.FixWorktreeBorrowed == true)
        {
            text.AppendLine($"Work in this folder ({review.FixWorktree}), a worktree on the PR's own branch `{branch}`. The folder belongs to someone else: keep it on `{branch}`, switch no branch and remove nothing. A reviewer read the PR at");
        }
        else
        {
            text.AppendLine($"Work in this folder ({review.FixWorktree}), a worktree on the PR's own branch `{branch}`. A reviewer read the PR at");
        }
        text.AppendLine($"{shortHead} and asked for changes. Its report:");
        text.AppendLine();
        text.AppendLine((review.Report ?? "").Trim());
        if (review.Findings.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Its findings, each a place and what is wrong:");
            foreach (var finding in review.Findings)
            {
                text.AppendLine($"- {finding.Path}:{finding.Line} {finding.Body}");
            }
        }
        var questions = review.Findings.Where(f => f.Body.StartsWith(PrReviews.DesignPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (questions.Length > 0 && review.DesignAnswer is { Length: > 0 } answer)
        {
            text.AppendLine();
            text.AppendLine("The review asked these questions of design. The answer below is the author's decision: carry it out, do not reopen it.");
            foreach (var question in questions)
            {
                text.AppendLine();
                text.AppendLine($"### Question: {question.Path}:{question.Line}");
                text.AppendLine(question.Body);
            }
            text.AppendLine();
            text.AppendLine("### Answer");
            text.AppendLine(answer);
        }
        text.AppendLine();
        text.AppendLine("Make each change asked for, in the tree as it stands: check a finding against the code before you act on it, and if");
        text.AppendLine("it does not hold say so in your comment rather than change anything for it. Add or adjust a test where the");
        text.AppendLine("finding was a behaviour.");
        text.AppendLine(InvariantRule);
        text.Append(LfsRule(review.FixWorktree, "worktree"));
        text.AppendLine();
        text.AppendLine("## Build and test");
        var configuration = build?.Configuration ?? (review.Repo.StartsWith("SharpAstro/", StringComparison.OrdinalIgnoreCase) ? "Release" : null);
        if (configuration is not null)
        {
            text.AppendLine($"The repo's CI{(build is { } b ? $" ({b.Workflow})" : "")} builds {configuration}: run `dotnet build -c {configuration}` and `dotnet test -c {configuration}`,");
            text.AppendLine("never a bare or Debug one, and have both pass before you push.");
        }
        else
        {
            text.AppendLine("Build and run the tests the way the repo's CI does, and have both pass before you push.");
        }
        text.AppendLine("A check run (a leg's check, run through `pilot run`) stops at its first failed test: the node ends the job and names that test, so the queue is free for the next job.");
        text.AppendLine();
        text.AppendLine("## Rules");
        text.AppendLine($"- Bring the base in by merging `origin/{review.Base}` into the branch (`git fetch origin` then `git merge origin/{review.Base}`); never rebase it.");
        text.AppendLine(review.FixDetached == true
            ? $"- Push plainly (`git push origin HEAD:refs/heads/{branch}`), never with force: other people may have the branch."
            : $"- Push plainly (`git push origin HEAD:{branch}`), never with force: other people may have the branch.");
        text.AppendLine($"- Comment on the PR once (`gh pr comment {review.Number} -R {review.Repo}`) with each finding and what you did about it.");
        text.AppendLine("- Commit with messages that say what changed and why. Open no PR and merge nothing.");
        text.AppendLine("- If GitHub refuses a push because the PR has merged (or is in a merge queue and is merging), the node follows up a merged PR's commits: commit, say so, and end your turn. Do not dequeue the PR or open a new one yourself.");
        text.AppendLine("- End your turn once the push and the comment are done.");
        return text.ToString();
    }
}
