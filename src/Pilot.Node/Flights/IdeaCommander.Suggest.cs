using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The verbs that judge and sort the open issues of a repo, ideas among them: <c>check-ready</c>, <c>suggest-milestones</c> and
/// <c>accept-milestones</c>. The first two ask Claude and write nothing to GitHub; only accepting moves an issue, through the guarded move
/// <c>set-milestone</c> makes.
/// </summary>
public sealed partial class IdeaCommander
{
    /// <summary>How many issues of a section are suggested for at once: what Claude can weigh in one answer.</summary>
    public const int SectionLimit = 25;

    /// <summary>A suggestion kept for <see cref="Verbs.AcceptMilestones"/>: the issue's, and the goal of the new milestone it names, if any.</summary>
    private sealed record Kept(ItemSuggestion Item, string NewDescription);

    private readonly Lock _keptGate = new();

    // By repo name (lower case) and then issue number: what the last suggestion proposed, until it is accepted or suggested again.
    private readonly Dictionary<string, Dictionary<int, Kept>> _kept = [];

    /// <summary>The repo and issue number an issue or idea target names; refused for any other.</summary>
    private async Task<(string Name, string Path, int Number)> IssueTargetAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (IssueIds.ReadTarget(command.Target) is { } issue)
        {
            var (name, path) = await RepoOfAsync(issue.Repo, cancellationToken);
            return (name, path, issue.Number);
        }
        if (IdeaIds.ReadTarget(command.Target) is { } idea)
        {
            return await ResolveAsync(idea.Repo, idea.Number, cancellationToken);
        }
        throw new FlightException($"{command.Verb} takes an issue target, {Verbs.IssueTargetPrefix}<repo>/#<issue number> (or an idea's, {Verbs.IdeaTargetPrefix}<repo>/I<n>), not {command.Target}");
    }

    private async Task<MilestoneIssue> OpenIssueAsync(string name, string path, int number, CancellationToken cancellationToken)
    {
        var issue = await github.IssueNowAsync(path, number, cancellationToken)
            ?? throw new FlightException($"{name} has no issue #{number}");
        return issue.Open ? issue : throw new FlightException($"#{number} of {name} is closed, so there is nothing to do with it");
    }

    /// <summary>
    /// <c>check-ready issue/&lt;repo&gt;/#&lt;n&gt;</c>: Claude's judgement of the issue as GitHub has it now, kept on its row. Claude failing (or no one to ask)
    /// leaves the row as it was and says why in the answer's note; the command still succeeds.
    /// </summary>
    private async Task<CommandResult> CheckReadyAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (name, path, number) = await IssueTargetAsync(command, cancellationToken);
        var issue = await OpenIssueAsync(name, path, number, cancellationToken);
        var made = $"{name} #{number.ToString(CultureInfo.InvariantCulture)}";
        if (readiness is null)
        {
            return state.Complete(command, null, made: made, readiness: new ReadinessResult(null, "not checked: this node has no one to check with"));
        }
        ReadinessAnswer answer;
        try
        {
            answer = await readiness.CheckAsync(new IdeaText(issue.Title, issue.Body), path, cancellationToken);
        }
        catch (FlightException ex)
        {
            return state.Complete(command, null, made: made, readiness: new ReadinessResult(null, $"not checked: {ex.Message}"));
        }
        var kept = new IssueReadiness(answer.Ready, answer.Reasons, time.GetUtcNow());
        if (!state.PutReadiness(name, number, kept))
        {
            // The node has not read this issue yet (a repo it does not watch): read it, so the answer has a row to be kept on.
            await RefreshBoardAsync(name, path, cancellationToken);
            state.PutReadiness(name, number, kept);
        }
        return state.Complete(command, null, made: made, readiness: new ReadinessResult(kept));
    }

    /// <summary>The open milestones but Ideas, each with a few of its open issues: what Claude weighs an issue against.</summary>
    private async Task<MilestoneContext[]> ContextsAsync(string path, CancellationToken cancellationToken)
    {
        var open = await OpenMilestonesAsync(path, cancellationToken);
        return await Task.WhenAll(open.Select(async m =>
        {
            var titles = (await github.MilestoneIssuesAsync(path, m.Title, cancellationToken))
                .Where(i => i.Open).Take(ClaudeIdeaMilestoner.IssuesShown).Select(i => i.Title).ToArray();
            return new MilestoneContext(m, titles);
        }));
    }

    /// <summary>
    /// <c>suggest-milestones issue/&lt;repo&gt;/#&lt;n&gt;</c> or <c>section/&lt;repo&gt;/unsorted|none</c>: a suggestion for each open issue, an open milestone or a
    /// new one that groups the issues that would go in it, kept for <see cref="AcceptMilestonesAsync"/>. Nothing is moved and nothing is made.
    /// Claude failing gives no suggestion and the reason as a note.
    /// </summary>
    private async Task<CommandResult> SuggestMilestonesAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        string name, path;
        MilestoneIssue[] issues;
        string? note = null;
        if (IssueIds.ReadSection(command.Target) is { } section)
        {
            (name, path) = await RepoOfAsync(section.Repo, cancellationToken);
            var open = (await github.OpenMilestoneIssuesAsync(path, cancellationToken)).Where(i => i.Open)
                .Where(i => section.Section == Verbs.SectionUnsorted
                    ? string.Equals(i.Milestone, IdeaIds.Milestone, StringComparison.OrdinalIgnoreCase)
                    : i.Milestone is null)
                .OrderBy(i => i.Number).ToArray();
            if (open.Length > SectionLimit)
            {
                note = $"suggested for the {SectionLimit} oldest of {open.Length} open issues: suggest again once those are sorted";
            }
            issues = [.. open.Take(SectionLimit)];
            if (issues.Length == 0)
            {
                return state.Complete(command, null, made: name,
                    suggestions: new MilestoneSuggestions(name, [], [], $"no open issue is {(section.Section == Verbs.SectionUnsorted ? $"in {IdeaIds.Milestone}" : "in no milestone")}"));
            }
        }
        else
        {
            var (repoName, repoPath, number) = await IssueTargetAsync(command, cancellationToken);
            (name, path) = (repoName, repoPath);
            issues = [await OpenIssueAsync(name, path, number, cancellationToken)];
        }

        MilestoneContext[] contexts;
        try
        {
            contexts = await ContextsAsync(path, cancellationToken);
        }
        catch (FlightException ex)
        {
            return state.Complete(command, null, made: name, suggestions: new MilestoneSuggestions(name, [], [], $"the milestones could not be read: {ex.Message}"));
        }
        if (suggester is null)
        {
            return state.Complete(command, null, made: name, suggestions: new MilestoneSuggestions(name, [], [], "no suggestion: this node has no one to suggest with"));
        }
        SuggestAnswer answer;
        try
        {
            answer = await suggester.SuggestAsync([.. issues.Select(i => new SuggestItem(i.Number, new IdeaText(i.Title, i.Body)))], contexts, path, cancellationToken);
        }
        catch (FlightException ex)
        {
            return state.Complete(command, null, made: name, suggestions: new MilestoneSuggestions(name, [], [], $"no suggestion: {ex.Message}"));
        }

        var items = new List<ItemSuggestion>();
        var kept = new Dictionary<int, Kept>();
        foreach (var issue in issues)
        {
            var one = answer.Items.FirstOrDefault(i => i.Number == issue.Number);
            ItemSuggestion item;
            if (one is null)
            {
                item = new ItemSuggestion(issue.Number, issue.Title, null, null, "", issue.UpdatedAt, "Claude gave no suggestion for it");
            }
            else if (one.Milestone is not null && string.Equals(one.Milestone, issue.Milestone, StringComparison.OrdinalIgnoreCase))
            {
                item = new ItemSuggestion(issue.Number, issue.Title, null, null, one.Reason, issue.UpdatedAt, $"it is in {one.Milestone} already");
            }
            else if (one.Milestone is null && one.NewMilestone is null)
            {
                item = new ItemSuggestion(issue.Number, issue.Title, null, null, one.Reason, issue.UpdatedAt, "Claude named no open milestone that exists");
            }
            else
            {
                item = new ItemSuggestion(issue.Number, issue.Title, one.Milestone, one.NewMilestone, one.Reason, issue.UpdatedAt);
                kept[issue.Number] = new Kept(item, answer.New.FirstOrDefault(n => string.Equals(n.Title, one.NewMilestone, StringComparison.OrdinalIgnoreCase))?.Description ?? "");
            }
            items.Add(item);
        }
        var groups = items.Where(i => i.NewMilestone is not null).GroupBy(i => i.NewMilestone!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NewMilestoneSuggestion(g.Key, kept[g.First().Number].NewDescription, [.. g.Select(i => i.Number)])).ToArray();
        lock (_keptGate)
        {
            var forRepo = _kept.TryGetValue(name.ToLowerInvariant(), out var held) ? held : _kept[name.ToLowerInvariant()] = [];
            foreach (var issue in issues)
            {
                forRepo.Remove(issue.Number);
            }
            foreach (var (number, suggestion) in kept)
            {
                forRepo[number] = suggestion;
            }
        }
        return state.Complete(command, null, made: name, suggestions: new MilestoneSuggestions(name, [.. items], groups, note));
    }

    /// <summary>
    /// <c>accept-milestones issue/&lt;repo&gt;/#&lt;n&gt;</c>, or <c>accept-milestones repo/&lt;repo&gt; all|&lt;n&gt;…</c>: the suggestions kept are applied. A new
    /// milestone is made on GitHub first, once however many issues go in it (or the open one of that title is used); each issue is then moved the way
    /// <c>set-milestone</c> moves it, guarded on the <c>updated_at</c> the suggestion was made from. One that cannot be moved is told, stays suggested, and
    /// does not stop the others.
    /// </summary>
    private async Task<CommandResult> AcceptMilestonesAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        string name, path;
        int[]? numbers;
        var args = command.Args ?? [];
        if (command.Target.StartsWith(Verbs.RepoTargetPrefix, StringComparison.Ordinal) && command.Target.Length > Verbs.RepoTargetPrefix.Length)
        {
            (name, path) = await RepoOfAsync(command.Target[Verbs.RepoTargetPrefix.Length..], cancellationToken);
            if (args.Length == 0)
            {
                throw new FlightException($"accept-milestones on a repo needs {Verbs.AllArg}, or the numbers of the issues to accept");
            }
            numbers = args.Length == 1 && string.Equals(args[0].Trim(), Verbs.AllArg, StringComparison.OrdinalIgnoreCase)
                ? null
                : [.. args.Select(a => IssueIds.Parse(a)?.Number ?? throw new FlightException($"{a} is no issue: write #12 or 12"))];
        }
        else
        {
            var (repoName, repoPath, number) = await IssueTargetAsync(command, cancellationToken);
            (name, path, numbers) = (repoName, repoPath, new[] { number });
        }

        Kept[] chosen;
        var failed = new List<string>();
        lock (_keptGate)
        {
            var forRepo = _kept.TryGetValue(name.ToLowerInvariant(), out var held) ? held : [];
            if (numbers is null)
            {
                chosen = [.. forRepo.OrderBy(k => k.Key).Select(k => k.Value)];
            }
            else
            {
                chosen = [.. numbers.Distinct().Where(n => forRepo.ContainsKey(n)).Select(n => forRepo[n])];
                failed.AddRange(numbers.Distinct().Where(n => !forRepo.ContainsKey(n)).Select(n => $"#{n} (no suggestion is kept for it: suggest-milestones first)"));
            }
        }
        if (chosen.Length == 0)
        {
            throw new FlightException(failed.Count > 0 ? $"nothing is accepted: {string.Join(", ", failed)}" : $"no suggestion is kept for {name}: suggest-milestones first");
        }

        var open = await github.MilestonesAsync(path, cancellationToken);
        string? Open(string title) => open.FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase))?.Title;
        var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moved = new List<int>();
        var changed = false;
        foreach (var (item, description) in chosen)
        {
            var number = item.Number;
            try
            {
                string target;
                if (item.NewMilestone is { } fresh)
                {
                    if (errors.TryGetValue(fresh, out var why))
                    {
                        throw new FlightException(why);
                    }
                    if (!made.TryGetValue(fresh, out var resolved))
                    {
                        // Made once for the whole group; one an earlier look already made is used as it is.
                        try
                        {
                            resolved = Open(fresh) ?? (await github.CreateMilestoneAsync(path, fresh, description, cancellationToken)).Title;
                        }
                        catch (FlightException ex)
                        {
                            errors[fresh] = $"the milestone {fresh} could not be made: {ex.Message}";
                            throw new FlightException(errors[fresh]);
                        }
                        made[fresh] = resolved;
                        changed = true;
                    }
                    target = resolved;
                }
                else
                {
                    target = Open(item.Milestone!) ?? throw new FlightException($"{name} has no open milestone {item.Milestone} any more");
                }
                await MoveToMilestoneAsync(name, path, number, target, item.UpdatedAt, refresh: false, cancellationToken);
                moved.Add(number);
                changed = true;
                lock (_keptGate)
                {
                    if (_kept.TryGetValue(name.ToLowerInvariant(), out var held))
                    {
                        held.Remove(number);
                    }
                }
            }
            catch (FlightException ex)
            {
                failed.Add($"#{number} ({ex.Message})");
            }
        }
        if (changed)
        {
            await RefreshBoardAsync(name, path, cancellationToken);
        }
        if (failed.Count > 0)
        {
            throw new FlightException($"{(moved.Count > 0 ? $"moved {string.Join(", ", moved.Select(n => "#" + n.ToString(CultureInfo.InvariantCulture)))}; " : "")}not moved: {string.Join("; ", failed)}");
        }
        return state.Complete(command, null, made: $"{name} {string.Join(' ', moved.Select(n => "#" + n.ToString(CultureInfo.InvariantCulture)))}");
    }
}
