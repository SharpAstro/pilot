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
    private sealed partial class BuildContext
    {
        /// <summary>Makes repos and prs: each tracked repo and every open PR, as the node last read them.</summary>
        internal (Command, Command) RepoCommands()
        {
            // repos
            var allRepos = new Option<bool>("--all") { Description = "Repos nothing has flown in too, not only the tracked ones" };
            var freshRepos = new Option<bool>("--refresh") { Description = "Wait for a fresh read of git and gh, not the node's cache: every repo's git status is read again unless it was read within the last 5 s" };
            var reposCommand = Action(new Command("repos", "Each tracked repo: its branch, worktrees and open PRs, as the node last read them") { Options = { allRepos, freshRepos } },
                async (p, client, ct) =>
                {
                    var envelope = await client.GetRepoStatusAsync(ct, p.GetValue(freshRepos));
                    if (envelope is not { Success: true, Response: { } rows })
                    {
                        await error.WriteLineAsync($"pilot: {envelope.Error}");
                        return 1;
                    }
                    var everyRepo = p.GetValue(allRepos);
                    await WriteAsync(p.GetValue(outputFormat), () => Repos(rows, everyRepo), () => ReposMarkdown.Build(rows, everyRepo));
                    return 0;
                });

            reposCommand.Subcommands.Add(Action(new Command("sync", "git fetch -p --all in each tracked repo, then fast-forward its default branch; a dirty, off-branch or diverged checkout is left alone and said so"),
                (p, client, ct) => Send(client, Verbs.SyncRepos, Verbs.NodeTarget, [], who, ct)));

            // prs
            var needsMe = new Option<bool>("--needs-me") { Description = "Only the PRs that wait on you: ready, changes requested or conflicts" };
            var freshPrs = new Option<bool>("--refresh") { Description = "Ask the node to read GitHub now, not wait for its minute" };
            var watchPrs = new Option<bool>("--watch") { Description = "Stay on the node's event stream as a client showing the Pull requests page (the node reads every minute meanwhile), and print the PRs again each time they change; stop with Ctrl+C" };
            var prsCommand = Action(new Command("prs", "Every open PR of the repos pilot works in, grouped by repo, with GitHub's merge state, as the node last read them") { Options = { needsMe, freshPrs, watchPrs } },
                async (p, client, ct) =>
                {
                    if (p.GetValue(freshPrs))
                    {
                        var asked = (await client.SendAsync(new CommandRequest { Verb = Verbs.RefreshPullRequests, Target = Verbs.NodeTarget, Who = who }, ct)).Response;
                        if (asked is { Ok: false })
                        {
                            await error.WriteLineAsync($"pilot: {asked.Error}");
                            return 1;
                        }
                    }
                    var onlyMine = p.GetValue(needsMe);
                    async Task PrintAsync(PullRequestsRow? shown)
                    {
                        var now = DateTimeOffset.UtcNow;
                        await WriteAsync(p.GetValue(outputFormat), () => PullRequestsMarkdown.Plain(shown, onlyMine, now), () => PullRequestsMarkdown.Build(shown, onlyMine, now));
                    }
                    if (!p.GetValue(watchPrs))
                    {
                        await PrintAsync((await SnapshotAsync(client, ct)).PullRequests);
                        return 0;
                    }
                    // The stream stays open and says the page shown: the node counts this command as a viewer until it ends or the stream closes.
                    try
                    {
                        await using var events = await client.OpenEventsAsync(since: null, stream: null, ct);
                        if (await events.ReadAsync(ct) is not SnapshotEvent snapshot)
                        {
                            await error.WriteLineAsync("pilot: the node's stream did not start with a snapshot");
                            return 1;
                        }
                        await events.SayPageAsync(ClientPages.PullRequests, ct);
                        await PrintAsync(snapshot.PullRequests);
                        while (await events.ReadAsync(ct) is { } next)
                        {
                            if (next is PullRequestsChangedEvent changed)
                            {
                                await PrintAsync(changed.Row);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Ctrl+C: the stream closing with this command is how the node forgets the page.
                    }
                    return 0;
                });

            return (reposCommand, prsCommand);
        }
    }
}
