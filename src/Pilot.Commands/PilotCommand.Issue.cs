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
    private sealed partial class BuildContext
    {
        /// <summary>Makes the issue: create, milestone, priority, ready, suggest, accept.</summary>
        internal Command IssueCommands()
        {
            // issue
            var issueTitle = Arg("title", "The issue's title");
            var issueBody = new Option<string?>("--body") { Description = "The issue's text" };
            var issueRepo = new Option<string?>("--repo") { Description = "The repo's checkout (any of its worktrees); else this folder" };
            var issueCreate = Action(new Command("create", "A new issue in the repo, through gh; prints its number") { Arguments = { issueTitle }, Options = { issueRepo, issueBody } },
                (p, client, ct) => Send(client, Verbs.CreateIssue, Verbs.RepoTarget(RepoPath(client, p.GetValue(issueRepo), cwd)),
                    p.GetValue(issueBody) is { Length: > 0 } text ? [p.GetValue(issueTitle)!, text] : [p.GetValue(issueTitle)!], who, ct));
            var issueRef = Arg("issue", "The issue: #12 or 12 in the repo, or tianwen#12 in another");
            var issueMilestone = Words("milestone", $"The open milestone to put the issue in, or {Verbs.NoMilestone} to take it out of every milestone");
            var issueSetMilestone = Action(new Command("milestone", $"Put an issue in an open milestone, or in none ({Verbs.NoMilestone}): GitHub sets it, guarded on the issue as the node last read it; a milestone that is not open is refused, naming the open ones") { Arguments = { issueRef, issueMilestone }, Options = { issueRepo } },
                async (p, client, ct) =>
                {
                    var parsed = IssueIds.Parse(p.GetValue(issueRef) ?? "")
                        ?? throw new UsageException($"{p.GetValue(issueRef)} is no issue: write #12, 12 or tianwen#12");
                    var repoGiven = parsed.Repo ?? RepoPath(client, p.GetValue(issueRepo), cwd);
                    var repoName = Path.GetFileName(repoGiven.TrimEnd('/', '\\'));
                    // The node compares the updated_at the row showed with GitHub's, so a move from a stale row is refused rather than made.
                    var seen = (await SnapshotAsync(client, ct)).Issues.FirstOrDefault(i => i.Number == parsed.Number && string.Equals(i.Repo, repoName, StringComparison.OrdinalIgnoreCase));
                    var milestoneName = Join(p, issueMilestone);
                    return await Send(client, Verbs.SetMilestone, IssueIds.Target(repoGiven, parsed.Number),
                        seen is null ? [milestoneName] : [milestoneName, seen.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)], who, ct);
                });
            // An issue as a person writes it, with the repo of the call when it names none: the repo as the node is given it, and the number.
            (string Repo, int Number) ParseIssue(NodeClient client, ParseResult p, string? text)
            {
                var parsed = IssueIds.Parse(text ?? "") ?? throw new UsageException($"{text} is no issue: write #12, 12 or tianwen#12");
                return (parsed.Repo ?? RepoPath(client, p.GetValue(issueRepo), cwd), parsed.Number);
            }
            var issueLevel = Arg("level", "urgent, high, medium, low, or none to clear it");
            var issuePriority = Action(new Command("priority", "Set the Priority issue field of an open issue (none clears it), as the Ideas board does; refused where the repo has no field") { Arguments = { issueRef, issueLevel }, Options = { issueRepo } },
                (p, client, ct) =>
                {
                    var (repoGiven, number) = ParseIssue(client, p, p.GetValue(issueRef));
                    return Send(client, Verbs.SetPriority, IssueIds.Target(repoGiven, number), [p.GetValue(issueLevel)!], who, ct);
                });
            var issueReady = Action(new Command("ready", "Ask Claude whether an issue (an idea too) is ready to fly as a flight: one leg-sized goal, tests that can be named, no open questions. The answer, with its reasons and the time, is kept on the issue's row; nothing is written to GitHub") { Arguments = { issueRef }, Options = { issueRepo } },
                (p, client, ct) =>
                {
                    var (repoGiven, number) = ParseIssue(client, p, p.GetValue(issueRef));
                    return Send(client, Verbs.CheckReady, IssueIds.Target(repoGiven, number), [], who, ct);
                });
            var issueSuggestRefs = Words("issue", "The issue to suggest a milestone for: #12 or 12 in the repo, or tianwen#12 in another; none with --section", required: false);
            var issueSection = new Option<string?>("--section") { Description = $"Suggest for every open issue of a section instead: {Verbs.SectionUnsorted} (the {IdeaIds.Milestone} milestone) or {Verbs.NoMilestone} (no milestone)" };
            var issueSuggest = Action(new Command("suggest", "Ask Claude for a milestone for an issue, or for each issue of a section: an open milestone, or a new one (title and goal) with the issues that would go in it. Nothing is moved or made until pilot issue accept") { Arguments = { issueSuggestRefs }, Options = { issueRepo, issueSection } },
                (p, client, ct) =>
                {
                    if (p.GetValue(issueSection) is { Length: > 0 } section)
                    {
                        section = section.Trim().ToLowerInvariant();
                        return section is Verbs.SectionUnsorted or Verbs.NoMilestone
                            ? Send(client, Verbs.SuggestMilestones, IssueIds.SectionTarget(RepoPath(client, p.GetValue(issueRepo), cwd), section), [], who, ct)
                            : throw new UsageException($"--section is {Verbs.SectionUnsorted} or {Verbs.NoMilestone}, not {section}");
                    }
                    var (repoGiven, number) = ParseIssue(client, p, p.GetValue(issueSuggestRefs)?.FirstOrDefault());
                    return Send(client, Verbs.SuggestMilestones, IssueIds.Target(repoGiven, number), [], who, ct);
                });
            var issueAcceptAll = new Option<bool>("--all") { Description = "Accept every suggestion kept for the repo" };
            var issueAccept = Action(new Command("accept", "Apply the suggestions the node kept from pilot issue suggest: one issue, several, or --all. A suggested new milestone is made first, once; each issue is then moved as pilot issue milestone moves it") { Arguments = { issueSuggestRefs }, Options = { issueRepo, issueAcceptAll } },
                (p, client, ct) =>
                {
                    var refs = p.GetValue(issueSuggestRefs) ?? [];
                    if (p.GetValue(issueAcceptAll))
                    {
                        return Send(client, Verbs.AcceptMilestones, Verbs.RepoTarget(RepoPath(client, p.GetValue(issueRepo), cwd)), [Verbs.AllArg], who, ct);
                    }
                    if (refs.Length == 0)
                    {
                        throw new UsageException("name the issue to accept the suggestion of, or --all");
                    }
                    var parsed = refs.Select(r => ParseIssue(client, p, r)).ToArray();
                    return parsed.Length == 1
                        ? Send(client, Verbs.AcceptMilestones, IssueIds.Target(parsed[0].Repo, parsed[0].Number), [], who, ct)
                        : Send(client, Verbs.AcceptMilestones, Verbs.RepoTarget(parsed[0].Repo), [.. parsed.Select(i => i.Number.ToString(CultureInfo.InvariantCulture))], who, ct);
                });
            var issueInto = new Option<int?>("--into") { Description = "break-up: how many parts to ask for (two or more; a wish, Claude may propose another number)" };
            var issueSave = new Option<bool>("--save") { Description = "break-up: accept the proposal the last break-up showed: the first part replaces the issue, the others are made as new issues" };
            var issueDiscard = new Option<bool>("--discard") { Description = "break-up: forget the proposal the last break-up showed; nothing is written" };
            var issueBreakUp = Action(new Command("break-up", "Ask Claude to split an open issue (or idea, I<n>) into two or more distinct parts and show them; nothing is written until --save, which puts the first part over the issue and makes the others as new issues in its milestone with its priority, each naming its siblings. --discard forgets the proposal") { Arguments = { issueRef }, Options = { issueRepo, issueInto, issueSave, issueDiscard } },
                async (p, client, ct) =>
                {
                    var text = p.GetValue(issueRef) ?? "";
                    string target;
                    string label;
                    if (IdeaIds.TryNumber(text, out var ideaNumber))
                    {
                        target = IdeaIds.Target(RepoPath(client, p.GetValue(issueRepo), cwd), ideaNumber);
                        label = IdeaIds.Id(ideaNumber);
                    }
                    else
                    {
                        var (repo, number) = ParseIssue(client, p, text);
                        target = IssueIds.Target(repo, number);
                        label = "#" + number.ToString(CultureInfo.InvariantCulture);
                    }
                    if (p.GetValue(issueDiscard))
                    {
                        await output.WriteLineAsync(BreakUpCache.Discard(target) ? $"discarded the break-up of {label}: nothing was written" : $"no break-up of {label} is waiting");
                        return 0;
                    }
                    if (p.GetValue(issueSave))
                    {
                        var kept = BreakUpCache.Read(target)
                            ?? throw new UsageException($"no break-up of {label} is waiting to save: run pilot issue break-up {text} first");
                        var saved = await Send(client, Verbs.AcceptBreakUp, target, [kept.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)], who, ct);
                        if (saved == 0)
                        {
                            BreakUpCache.Discard(target);
                        }
                        return saved;
                    }
                    if (p.GetValue(issueInto) is { } into)
                    {
                        return into < 2
                            ? throw new UsageException("--into is two or more parts")
                            : await Send(client, Verbs.BreakUpIssue, target, [into.ToString(CultureInfo.InvariantCulture)], who, ct);
                    }
                    return await Send(client, Verbs.BreakUpIssue, target, [], who, ct);
                });
            var issueCommand = new Command("issue", "Issues: create, milestone, priority, ready, suggest, accept, break-up") { Subcommands = { issueCreate, issueSetMilestone, issuePriority, issueReady, issueSuggest, issueAccept, issueBreakUp } };

            return issueCommand;
        }

        /// <summary>Makes idea and ideas: issues in a repo's Ideas milestone.</summary>
        internal (Command, Command) IdeaCommands()
        {
            // idea, ideas: issues in a repo's Ideas milestone, id I<issue number>
            var ideaWords = Words("text", "The idea (its first line the title, the rest the body); or an idea's id (I381, or tianwen I1829) and then edit and the new text, plan, or drop and why");
            var ideaRepo = new Option<string?>("--repo") { Description = $"The repo, by its folder's name or the path of its checkout; a new idea without it goes where Claude chooses among the node's repos, an idea's id without it is in {IdeaIds.DefaultRepo}" };
            var ideaContext = new Option<string?>("--context") { Description = "A new idea: the page or flight you were on, which helps Claude choose its repo" };
            var ideaMilestone = new Option<string?>("--milestone") { Description = "plan: the open milestone to move the idea to; without one (or --new-milestone) the node proposes a milestone and plans nothing" };
            var ideaNewMilestone = new Option<string?>("--new-milestone") { Description = "plan: plan into a new milestone of this title, made on GitHub" };
            var ideaMilestoneDescription = new Option<string?>("--milestone-description") { Description = "plan: with --new-milestone, the new milestone's one-line description" };
            var ideaSave = new Option<bool>("--save") { Description = "tidy: save the proposal the last tidy showed (through the same up-to-date check as an edit)" };
            var ideaDiscard = new Option<bool>("--discard") { Description = "tidy: forget the proposal the last tidy showed; nothing is written" };
            var idea = Action(new Command("idea", $"Note an idea in the repo's {IdeaIds.Milestone} milestone (prints I<n>), or edit one (I<n> edit <text>: kept up to date with GitHub), tidy one (I<n> tidy: Claude's proposal as a diff, saved only by tidy --save), plan one (I<n> plan --milestone <name> or --new-milestone <title>: moves it to that milestone and creates its flight; without either it prints the milestone the node proposes and plans nothing), drop one (I<n> drop <why>) or fold one into a flight (I<n> fold <code>: its text goes to the flight's issue as a comment, then it is dropped as folded)")
                { Arguments = { ideaWords }, Options = { ideaRepo, ideaContext, ideaMilestone, ideaNewMilestone, ideaMilestoneDescription, ideaSave, ideaDiscard } },
                async (p, client, ct) =>
                {
                    var call = ReadIdea(p.GetValue(ideaWords) ?? []);
                    var named = call.Repo ?? IdeaRepoOf(client, p.GetValue(ideaRepo), cwd, call.Action is null ? Verbs.AnyRepo : IdeaIds.DefaultRepo);
                    switch (call.Action)
                    {
                        case null:
                            // Without --repo Claude chooses the repo: the node is given the text and the context, and files nothing under a guess.
                            return await Send(client, Verbs.NoteIdea, Verbs.RepoTarget(named),
                                named == Verbs.AnyRepo && p.GetValue(ideaContext) is { Length: > 0 } context ? [call.Rest, context] : [call.Rest], who, ct);
                        case "move":
                            return await Send(client, Verbs.MoveIdea, IdeaIds.Target(named, call.Number), [call.Rest], who, ct);
                        case "edit":
                            // The edit starts from the idea as the node has read it now: its updated_at is what the node compares with GitHub's.
                            var snapshot = await SnapshotAsync(client, ct);
                            // An idea that was moved to another repo is read where it is now.
                            var (readRepo, readNumber) = IdeaIds.Follow(snapshot.IdeaMoves, Path.GetFileName(named.TrimEnd('/', '\\')), call.Number);
                            var read = snapshot.Ideas.FirstOrDefault(i => i.Number == readNumber && string.Equals(i.Repo, readRepo, StringComparison.OrdinalIgnoreCase))
                                ?? throw new UsageException(IdeaIds.FlightOf(snapshot.Flights, snapshot.IdeaMoves, Path.GetFileName(named.TrimEnd('/', '\\')), call.Number) is { } planned
                                    ? $"{IdeaIds.Id(call.Number)} of {named} is planned: it is the flight {planned}"
                                    : $"the node has not read {IdeaIds.Id(call.Number)} of {named}: pilot ideas lists the ideas it has");
                            return await Send(client, Verbs.EditIdea, IdeaIds.Target(named, call.Number), [read.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), call.Rest], who, ct);
                        case "tidy":
                            var tidyTarget = IdeaIds.Target(named, call.Number);
                            if (p.GetValue(ideaDiscard))
                            {
                                await output.WriteLineAsync(IdeaTidyCache.Discard(tidyTarget) ? $"discarded the tidy of {IdeaIds.Id(call.Number)}: nothing was written" : $"no tidy of {IdeaIds.Id(call.Number)} is waiting");
                                return 0;
                            }
                            if (p.GetValue(ideaSave))
                            {
                                var kept = IdeaTidyCache.Read(tidyTarget)
                                    ?? throw new UsageException($"no tidy of {IdeaIds.Id(call.Number)} is waiting to save: run pilot idea {IdeaIds.Id(call.Number)} tidy first");
                                var saved = await Send(client, Verbs.SaveTidyIdea, tidyTarget,
                                    [kept.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), IdeaTidyCache.Joined(kept.Title, kept.Body)], who, ct);
                                if (saved == 0)
                                {
                                    IdeaTidyCache.Discard(tidyTarget);
                                }
                                return saved;
                            }
                            return await Send(client, Verbs.TidyIdea, tidyTarget, [], who, ct);
                        case "fold":
                            return await Send(client, Verbs.FoldIdea, IdeaIds.Target(named, call.Number), [call.Rest], who, ct);
                        case "plan":
                            if (p.GetValue(ideaNewMilestone) is { Length: > 0 } newMilestone)
                            {
                                return await Send(client, Verbs.PlanIdea, IdeaIds.Target(named, call.Number),
                                    p.GetValue(ideaMilestoneDescription) is { Length: > 0 } description ? [Verbs.NewMilestoneArg, newMilestone, description] : [Verbs.NewMilestoneArg, newMilestone], who, ct);
                            }
                            return p.GetValue(ideaMilestone) is { Length: > 0 } milestone
                                ? await Send(client, Verbs.PlanIdea, IdeaIds.Target(named, call.Number), [milestone], who, ct)
                                : await Send(client, Verbs.ProposeMilestone, IdeaIds.Target(named, call.Number), [], who, ct);
                        default:
                            return await Send(client, Verbs.DropIdea, IdeaIds.Target(named, call.Number), [call.Rest], who, ct);
                    }
                });
            var ideasAll = new Option<bool>("--all") { Description = "Dropped ideas too, not only the noted and planned ones" };
            var ideasRepo = new Option<string?>("--repo") { Description = "Only this repo's, by its folder's name" };
            var ideas = Action(new Command("ideas", "The ideas the node has read, newest first, with their ids") { Options = { ideasAll, ideasRepo } },
                async (p, client, ct) =>
                {
                    await output.WriteAsync(Ideas((await SnapshotAsync(client, ct)).Ideas, p.GetValue(ideasAll), p.GetValue(ideasRepo)));
                    return 0;
                });

            return (idea, ideas);
        }
    }
}
