using System.Text;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// <c>pilot repos</c> as Markdown, for <c>--output-format markdown</c> and <c>rich</c>: the same repos as
/// <see cref="PilotCommand.Repos"/>, each a heading with its branch, state and flights, its worktrees and open PRs as
/// tables and what could not be read as a list.
/// </summary>
internal static class ReposMarkdown
{
    internal static string Build(IReadOnlyList<RepoStatus> rows, bool all)
    {
        var shown = rows.Where(r => all || r.Tracked).ToList();
        if (shown.Count == 0)
        {
            return all ? "No repos.\n" : "No tracked repos; --all lists every repo.\n";
        }
        var text = new StringBuilder();
        text.AppendLine("# pilot repos").AppendLine();
        foreach (var repo in shown)
        {
            var sync = repo.Ahead is { } ahead && repo.Behind is { } behind
                ? $", ahead {ahead}, behind {behind} of {Markdown.Text(repo.DefaultBranch ?? "")}"
                : "";
            var untracked = repo.Tracked ? "" : ", untracked";
            text.AppendLine($"## {Markdown.Text(repo.Name)}").AppendLine();
            text.AppendLine($"{Markdown.Text(repo.Branch ?? "(detached)")}, {(repo.Dirty ? "dirty" : "clean")}{sync}, {repo.Flights} {(repo.Flights == 1 ? "flight" : "flights")}{untracked}").AppendLine();

            if (repo.Worktrees.Length > 0)
            {
                text.AppendLine("| Worktree | Flight | State | Files | Diff | Path |");
                text.AppendLine("| --- | --- | --- | --- | --- | --- |");
                foreach (var tree in repo.Worktrees)
                {
                    text.AppendLine($"| {Markdown.Text(tree.Branch ?? "(detached)")} | {Markdown.Text(tree.Flight ?? "")} | {(tree.Dirty ? "dirty" : "clean")} | {tree.Files} | +{tree.Added} -{tree.Removed} | {Markdown.Text(tree.Path)} |");
                }
                text.AppendLine();
            }

            if (repo.PullRequests.Length > 0)
            {
                text.AppendLine("| PR | Branch | Flight | State | Checks |");
                text.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (var pr in repo.PullRequests)
                {
                    var label = Markdown.Text($"#{pr.Number} {pr.Title}");
                    var link = string.IsNullOrEmpty(pr.Url) ? label : $"[{label}]({(pr.Url.Contains(' ') ? $"<{pr.Url}>" : pr.Url)})";
                    text.AppendLine($"| {link} | {Markdown.Text(pr.Head)} | {Markdown.Text(pr.Flight ?? "")} | {(pr.Draft ? "draft" : "ready")} | {Markdown.Text(pr.Checks ?? "")} |");
                }
                text.AppendLine();
            }

            if (repo.Errors.Length > 0)
            {
                foreach (var problem in repo.Errors)
                {
                    text.AppendLine($"- {Markdown.Text(problem.ReplaceLineEndings(" "))}");
                }
                text.AppendLine();
            }
        }
        return text.ToString();
    }
}
