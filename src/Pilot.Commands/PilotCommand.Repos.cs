using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>
    /// Each tracked repo (every repo with <paramref name="all"/>) a line, its worktrees and open PRs indented under it:
    /// <c>name  branch  clean|dirty  +ahead/-behind (the default branch)  flights</c>.
    /// </summary>
    internal static string Repos(IReadOnlyList<RepoStatus> rows, bool all)
    {
        var text = new System.Text.StringBuilder();
        var shown = rows.Where(r => all || r.Tracked).ToList();
        foreach (var repo in shown)
        {
            var sync = repo.Ahead is { } ahead && repo.Behind is { } behind
                ? $"  ahead {ahead}, behind {behind} of {repo.DefaultBranch}"
                : "";
            text.AppendLine($"{repo.Name}  {repo.Branch ?? "(detached)"}  {(repo.Dirty ? "dirty" : "clean")}{sync}  {repo.Flights} {(repo.Flights == 1 ? "flight" : "flights")}{(repo.Tracked ? "" : "  untracked")}");
            foreach (var tree in repo.Worktrees)
            {
                text.AppendLine($"    worktree {tree.Branch ?? "(detached)"}{(tree.Flight is { } f ? $"  {f}" : "")}  {(tree.Dirty ? "dirty" : "clean")}  {tree.Files} {(tree.Files == 1 ? "file" : "files")} +{tree.Added} -{tree.Removed}  {tree.Path}");
            }
            foreach (var pr in repo.PullRequests)
            {
                text.AppendLine($"    PR #{pr.Number} {pr.Title}  {pr.Head}{(pr.Flight is { } f ? $"  {f}" : "")}  {(pr.Draft ? "draft" : "ready")}{(pr.Checks is { } checks ? $"  checks {checks}" : "")}{(pr.Labels.Length > 0 ? $"  labels {string.Join(", ", pr.Labels)}" : "")}");
            }
            foreach (var problem in repo.Errors)
            {
                text.AppendLine($"    ! {problem}");
            }
        }
        return shown.Count == 0 ? (all ? "No repos.\n" : "No tracked repos; --all lists every repo.\n") : text.ToString();
    }
}
