using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>What <c>pilot idea</c> was given: an idea to note (<see cref="Action"/> null, its text in <see cref="Rest"/>), or one to <c>plan</c> or <c>drop</c> (<see cref="Number"/>, with <see cref="Rest"/> the why).</summary>
    internal sealed record IdeaCall(string? Repo, int Number, string? Action, string Rest);

    /// <summary>
    /// Reads <c>pilot idea</c>'s words: <c>I381 plan</c>, <c>tianwen I1829 drop why…</c> act on an idea, any other words are the text of
    /// one to note. Only an id followed by plan or drop is an action, so a note that starts with one (<c>I381 is wrong</c>) is still a note.
    /// </summary>
    internal static IdeaCall ReadIdea(string[] words)
    {
        for (var at = 0; at <= 1 && at < words.Length; at++)
        {
            if (!IdeaIds.TryNumber(words[at], out var number))
            {
                continue;
            }
            if (at + 1 < words.Length && words[at + 1] is "plan" or "drop" or "edit" or "tidy" or "move" or "fold")
            {
                var action = words[at + 1];
                var rest = string.Join(' ', words.Skip(at + 2));
                if (action == "move" && (rest.Length == 0 || rest.Contains(' ', StringComparison.Ordinal)))
                {
                    throw new UsageException($"idea {words[at]} move takes the one repo to move it to: pilot idea {words[at]} move <repo>");
                }
                if (action == "fold" && (rest.Length == 0 || rest.Contains(' ', StringComparison.Ordinal)))
                {
                    throw new UsageException($"idea {words[at]} fold takes the one flight to fold it into: pilot idea {words[at]} fold <flight code>");
                }
                if (action == "tidy" && rest.Length > 0)
                {
                    throw new UsageException($"idea {words[at]} tidy takes no more words: pilot idea {words[at]} tidy shows the proposal, tidy --save saves it, tidy --discard drops it");
                }
                if (action == "plan" && rest.Length > 0)
                {
                    throw new UsageException($"idea {words[at]} plan takes no more words: the milestone is --milestone <name>");
                }
                if (action == "edit" && rest.Length == 0)
                {
                    throw new UsageException($"idea {words[at]} edit needs the new text: pilot idea {words[at]} edit \"<title, then the body on its lines>\"");
                }
                if (action == "drop" && rest.Length == 0)
                {
                    throw new UsageException($"idea {words[at]} drop needs why: pilot idea {words[at]} drop \"<why>\", which is the issue's closing comment");
                }
                return new IdeaCall(at == 1 ? words[0] : null, number, action, rest);
            }
            if (at == 0 && words.Length == 1)
            {
                throw new UsageException($"{words[at]} is an idea's id: say what to do with it, pilot idea {words[at]} edit <text>, pilot idea {words[at]} plan --milestone <name> (or --new-milestone <title>) or pilot idea {words[at]} drop \"<why>\"");
            }
        }
        return new IdeaCall(null, 0, null, string.Join(' ', words));
    }

    /// <summary>The repo an idea is in when its id names none: <c>--repo</c> as a path when it is one, else by name, else <see cref="IdeaIds.DefaultRepo"/>.</summary>
    internal static string IdeaRepoOf(NodeClient client, string? named, string cwd, string fallback = IdeaIds.DefaultRepo)
    {
        if (named is not { Length: > 0 })
        {
            return fallback;
        }
        return client.IsRemote || !(named.AsSpan().IndexOfAny('/', '\\') >= 0 || Directory.Exists(Path.GetFullPath(named, cwd))) ? named : Path.GetFullPath(named, cwd);
    }

    /// <summary>The proposal for an idea's milestone, its reason and the open milestones, or why there is no proposal.</summary>
    internal static async Task WriteMilestonesAsync(TextWriter output, string id, MilestoneProposal proposal)
    {
        var why = proposal.Reason is { Length: > 0 } reason ? $": {reason}" : "";
        if (proposal.Title is { Length: > 0 } title)
        {
            await output.WriteLineAsync($"{id}: proposed milestone {title}{why}");
        }
        else if (proposal.NewTitle is { Length: > 0 } newTitle)
        {
            await output.WriteLineAsync($"{id}: proposed new milestone {newTitle}{(proposal.NewDescription is { Length: > 0 } line ? $" ({line})" : "")}{why}");
        }
        else
        {
            await output.WriteLineAsync($"{id}: {proposal.Note ?? "no proposal"}");
        }
        await output.WriteLineAsync(proposal.Open.Length == 0 ? "no open milestone but Ideas" : "open milestones:");
        foreach (var m in proposal.Open)
        {
            var due = m.DueOn is { } at ? $", due {at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : "";
            await output.WriteLineAsync($"  {m.Title} ({m.OpenIssues} open{due}){(m.Description.Length > 0 ? $": {m.Description}" : "")}");
        }
    }

    /// <summary>What a readiness check found: ready or not, its reasons and when; or why nothing was checked.</summary>
    internal static async Task WriteReadinessAsync(TextWriter output, string id, ReadinessResult result)
    {
        if (result.Readiness is not { } readiness)
        {
            await output.WriteLineAsync($"{id}: {result.Note ?? "not checked"}");
            return;
        }
        await output.WriteLineAsync($"{id}: {(readiness.Ready ? "ready" : "not ready")} (checked {readiness.CheckedAt.ToString("u", CultureInfo.InvariantCulture)})");
        foreach (var reason in readiness.Reasons)
        {
            await output.WriteLineAsync($"  - {reason}");
        }
    }

    /// <summary>A milestone suggested for each issue, and the new milestones with the issues that would go in them; nothing is moved.</summary>
    internal static async Task WriteSuggestionsAsync(TextWriter output, MilestoneSuggestions suggestions)
    {
        if (suggestions.Items.Length == 0)
        {
            await output.WriteLineAsync($"{suggestions.Repo}: {suggestions.Note ?? "no suggestion"}");
            return;
        }
        foreach (var item in suggestions.Items)
        {
            var where = item.Milestone is { } open ? open : item.NewMilestone is { } fresh ? $"new milestone {fresh}" : item.Note ?? "no suggestion";
            await output.WriteLineAsync($"#{item.Number}  {item.Title}: {where}{(item.Reason.Length > 0 && (item.Milestone ?? item.NewMilestone) is not null ? $" ({item.Reason})" : "")}");
        }
        foreach (var made in suggestions.New)
        {
            await output.WriteLineAsync($"new milestone {made.Title}{(made.Description.Length > 0 ? $" ({made.Description})" : "")}: {string.Join(' ', made.Issues.Select(n => "#" + n.ToString(CultureInfo.InvariantCulture)))}");
        }
        if (suggestions.Note is { Length: > 0 } note)
        {
            await output.WriteLineAsync(note);
        }
        await output.WriteLineAsync("nothing is moved: pilot issue accept <issue> (or --all) applies the suggestions");
    }

    /// <summary>The ideas, newest first, a line each: <c>I381  pilot  noted  3d  Title</c>; the dropped ones only with <paramref name="all"/>.</summary>
    internal static string Ideas(IEnumerable<IdeaRow> ideas, bool all, string? repo)
    {
        var shown = IdeaIds.Order(ideas.Where(i => (all || i.State != IdeaState.Dropped) && (repo is null || string.Equals(i.Repo, repo, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (shown.Count == 0)
        {
            return "no ideas: pilot idea \"<text>\" notes one\n";
        }
        var text = new System.Text.StringBuilder();
        foreach (var idea in shown)
        {
            text.AppendLine($"{idea.Id}  {idea.Repo}  {idea.State.ToString().ToLowerInvariant()}  {idea.Age}  {idea.Title}");
        }
        return text.ToString();
    }
}
