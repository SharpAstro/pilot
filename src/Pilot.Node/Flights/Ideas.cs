using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>A milestone of a repo on GitHub: its number, its title and when it is due, when it is, and how many of its issues are open and closed.</summary>
public sealed record Milestone(int Number, string Title, DateTimeOffset? DueOn = null, string Description = "", int OpenIssues = 0, int ClosedIssues = 0);

/// <summary>An issue as GitHub has it: what an idea is read from, and what planning it checks first.</summary>
/// <param name="Open">Open; false for a closed one.</param>
/// <param name="Milestone">The title of the milestone it is in; null for none.</param>
/// <param name="Author">The login that opened it.</param>
public sealed record MilestoneIssue(int Number, string Title, string Body, bool Open, string? Milestone, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Author, string Url);

/// <summary>An idea's row, from the issue it is.</summary>
public static class IdeaRows
{
    /// <summary>
    /// The rows of <paramref name="repo"/>'s Ideas milestone: an open issue is noted, or planned while a flight that came from it
    /// still flies, and a closed one is dropped.
    /// </summary>
    public static IdeaRow[] Of(string repo, IEnumerable<MilestoneIssue> issues, IReadOnlyCollection<FlightRow> flights, DateTimeOffset now)
    {
        var listed = issues.ToArray();
        var rows = listed.Select(issue =>
        {
            var id = IdeaIds.Id(issue.Number);
            var planned = issue.Open && flights.Any(f => FliesIdea(f, repo, id));
            return new IdeaRow
            {
                Id = id,
                Repo = repo,
                Title = issue.Title,
                Body = issue.Body,
                State = !issue.Open ? IdeaState.Dropped : planned ? IdeaState.Planned : IdeaState.Noted,
                UpdatedAt = issue.UpdatedAt,
                Age = IdeaIds.Age(issue.CreatedAt, now),
                Origin = issue.Author,
                Url = issue.Url,
            };
        }).ToList();

        // Plan moves the issue out of the Ideas milestone, so it is no longer listed there: an idea whose flight has not landed
        // stays as a Planned row, made from the flight, until the flight lands.
        foreach (var flight in flights.Where(f => f.FromIdea is { Length: > 0 } && FliesIdea(f, repo, f.FromIdea!)))
        {
            if (!IdeaIds.TryNumber(flight.FromIdea!, out var number) || listed.Any(i => i.Number == number))
            {
                continue;
            }
            rows.Add(new IdeaRow
            {
                Id = IdeaIds.Id(number),
                Repo = repo,
                Title = flight.Title ?? flight.Code,
                State = IdeaState.Planned,
                UpdatedAt = flight.CreatedAt,
                Age = IdeaIds.Age(flight.CreatedAt, now),
                Url = IssueUrl(number, listed, flight),
            });
        }
        return [.. rows];
    }

    private static bool FliesIdea(FlightRow flight, string repo, string id) =>
        flight.State != FlightState.Landed && string.Equals(flight.Repo, repo, StringComparison.OrdinalIgnoreCase) && flight.FromIdea == id;

    /// <summary>The issue's page, from a sibling issue's or the flight's PR's, which share its repo; empty when neither is known.</summary>
    private static string IssueUrl(int number, MilestoneIssue[] siblings, FlightRow flight)
    {
        var sibling = siblings.FirstOrDefault(i => i.Url.LastIndexOf("/issues/", StringComparison.Ordinal) >= 0)?.Url;
        if (sibling is not null)
        {
            return sibling[..(sibling.LastIndexOf("/issues/", StringComparison.Ordinal) + "/issues/".Length)] + number.ToString(CultureInfo.InvariantCulture);
        }
        var pull = flight.PullRequestUrl;
        return pull is not null && pull.LastIndexOf("/pull/", StringComparison.Ordinal) is var at and >= 0
            ? pull[..at] + "/issues/" + number.ToString(CultureInfo.InvariantCulture)
            : "";
    }
}

/// <summary>
/// The idea verbs: <c>note-idea</c>, <c>edit-idea</c>, <c>plan-idea</c> and <c>drop-idea</c>. An idea is an issue in a repo's Ideas milestone
/// (made on the first one), id <c>I&lt;issue number&gt;</c>. Each verb checks the issue as GitHub has it now, never as the node
/// last read it, and leaves the node's rows as GitHub then has them.
/// </summary>
public sealed class IdeaCommander(NodeState state, Planes planes, RepoCatalog repos, IGitHub github, IIdeaMerger merger, IIdeaTidier tidier, IIdeaRepoChooser chooser, FlightCommander flights, TimeProvider time, IIdeaMilestoner? milestoner = null)
{
    // Two plans of one idea must not both make a flight.
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Where this says how long each step of noting an idea took: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    public static bool Handles(string verb) => verb is Verbs.NoteIdea or Verbs.PlanIdea or Verbs.DropIdea or Verbs.EditIdea or Verbs.TidyIdea or Verbs.SaveTidyIdea or Verbs.MoveIdea or Verbs.FoldIdea or Verbs.ProposeMilestone or Verbs.SetMilestone;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        // Proposing writes nothing, so it must not queue behind (or hold up) the verbs that do: it runs Claude for minutes.
        if (command.Verb == Verbs.ProposeMilestone)
        {
            try
            {
                return await ProposeAsync(command, cancellationToken);
            }
            catch (FlightException ex)
            {
                return state.Complete(command, ex.Message);
            }
        }
        await _one.WaitAsync(cancellationToken);
        try
        {
            return command.Verb switch
            {
                Verbs.NoteIdea => await NoteAsync(command, cancellationToken),
                Verbs.PlanIdea => await PlanAsync(command, cancellationToken),
                Verbs.EditIdea or Verbs.SaveTidyIdea => await EditAsync(command, cancellationToken),
                Verbs.TidyIdea => await TidyAsync(command, cancellationToken),
                Verbs.MoveIdea => await MoveAsync(command, cancellationToken),
                Verbs.FoldIdea => await FoldAsync(command, cancellationToken),
                Verbs.SetMilestone => await SetMilestoneAsync(command, cancellationToken),
                _ => await DropAsync(command, cancellationToken),
            };
        }
        catch (FlightException ex)
        {
            return state.Complete(command, ex.Message);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The repo a verb names, by its folder name (a repo the node lists) or by any path in its checkout.</summary>
    private async Task<(string Name, string Path)> RepoOfAsync(string given, CancellationToken cancellationToken)
    {
        given = given.Trim();
        if (repos.Repos().FirstOrDefault(r => string.Equals(r.Name, given, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return (named.Name, named.Path);
        }
        var main = await planes.MainCheckoutAsync(given, cancellationToken)
            ?? throw new FlightException($"{given} is no repo this node lists and no git checkout");
        return (System.IO.Path.GetFileName(main.TrimEnd('/', '\\')), main);
    }

    /// <summary>Reads the repo's Ideas milestone afresh into the node's rows; a repo gh cannot be asked leaves its rows as they were.</summary>
    private async Task RefreshAsync(string name, string path, CancellationToken cancellationToken)
    {
        try
        {
            state.PutIdeas(name, IdeaRows.Of(name, await github.MilestoneIssuesAsync(path, IdeaIds.Milestone, cancellationToken), state.Flights(), time.GetUtcNow()));
        }
        catch (FlightException)
        {
            // What was done on GitHub stands; the next look at the repo reads the rest.
        }
    }

    /// <summary><c>note-idea repo/&lt;repo or path&gt; &lt;text…&gt;</c>: the first line is the title, the rest the body.</summary>
    private async Task<CommandResult> NoteAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (!command.Target.StartsWith(Verbs.RepoTargetPrefix, StringComparison.Ordinal) || command.Target.Length == Verbs.RepoTargetPrefix.Length)
        {
            throw new FlightException($"note-idea takes a repo target, {Verbs.RepoTargetPrefix}<repo name or path to its checkout>, not {command.Target}");
        }
        var given = command.Target[Verbs.RepoTargetPrefix.Length..];
        var chosen = given == Verbs.AnyRepo;
        var args = command.Args ?? [];
        var text = (chosen ? args.FirstOrDefault() ?? "" : string.Join(' ', args)).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var (title, body) = text.IndexOf('\n') is var cut and >= 0 ? (text[..cut].Trim(), text[(cut + 1)..].Trim()) : (text, "");
        if (title.Length == 0)
        {
            throw new FlightException("note-idea needs the idea's text: its first line is the title, the rest the body");
        }
        // Each step's time in the log: noting one took most of a minute and nothing said where it went (the owner, 2026-10-29).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<string>();
        void Step(string what)
        {
            steps.Add($"{what} {clock.ElapsedMilliseconds} ms");
            clock.Restart();
        }
        string name, path;
        if (chosen)
        {
            var picked = await ChooseAsync(new IdeaText(title, body), string.Join(' ', args.Skip(1)), cancellationToken);
            Step("repo chosen");
            if (picked.Length > 1)
            {
                // Two fit equally: nothing is created until the person answers.
                return state.Complete(command, null, way: IdeaEditWays.Asked, made: AskWhere(text, title, picked).Id);
            }
            (name, path) = (picked[0].Name, picked[0].Path);
        }
        else
        {
            (name, path) = await RepoOfAsync(given, cancellationToken);
        }
        var milestone = await github.IdeasMilestoneAsync(path, cancellationToken);
        Step("milestone found");
        var issue = await github.CreateIssueInMilestoneAsync(path, title, body.Length > 0 ? body : null, milestone.Title, cancellationToken);
        Step("issue made");
        state.CloseCardsAbout(RepoCardSubject(text), "filed", CardKinds.IdeaRepo);
        await RefreshAsync(name, path, cancellationToken);
        Step("ideas read again");
        await Error.WriteLineAsync($"idea: noted {name} {IdeaIds.Id(issue.Number)}: {string.Join(", ", steps)}");
        return state.Complete(command, null, made: $"{name} {IdeaIds.Id(issue.Number)}");
    }

    /// <summary>
    /// The repo Claude chooses for an idea among the ones the node tracks that are on GitHub: one, or the few that fit equally. Refused when
    /// none is tracked or Claude names none, so nothing is ever filed under a guess.
    /// </summary>
    private async Task<RepoOption[]> ChooseAsync(IdeaText idea, string context, CancellationToken cancellationToken)
    {
        var options = repos.Repos().Where(r => r.GitHub is not null).Select(r => new RepoOption(r.Name, RepoTypes.Of(r.Path), r.Path)).ToArray();
        if (options.Length == 0)
        {
            throw new FlightException("the node tracks no repo on GitHub to file the idea in: name one with --repo");
        }
        var names = await chooser.ChooseAsync(idea, options, context.Length > 0 ? context : null, cancellationToken);
        // In the order Claude named them, the likeliest first.
        var picked = names.Select(n => options.FirstOrDefault(o => string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase))).OfType<RepoOption>().Distinct().ToArray();
        return picked.Length > 0
            ? picked
            : throw new FlightException($"Claude found no repo among {string.Join(", ", options.Select(o => o.Name))} that the idea belongs in, so nothing is filed: name one with --repo");
    }

    private static string RepoCardSubject(string text) =>
        "idea-new/" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary>The card that asks which of the repos that fit equally the idea goes in; each choice files it there through <see cref="Verbs.NoteIdea"/>, with no Claude call.</summary>
    private CardRow AskWhere(string text, string title, RepoOption[] fits) =>
        state.RaiseCard(CardBin.ForYou, CardKinds.IdeaRepo, RepoCardSubject(text),
            $"Which repo does this idea belong in? {title}",
            $"{text}\n\nIt fits {string.Join(" and ", fits.Select(f => f.Name))} equally: nothing is filed until you choose.",
            [.. fits.Select(f => new CardChoice { Label = $"File in {f.Name}", Verb = Verbs.NoteIdea, Target = Verbs.RepoTarget(f.Name), Args = [text] })]);

    /// <summary>
    /// <c>move-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;repo&gt;</c>: the issue transferred to the other repo through <see cref="SaveAsync"/>, put in that repo's Ideas
    /// milestone, and remembered, so the old id resolves to the new one.
    /// </summary>
    private async Task<CommandResult> MoveAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (name, path, issue) = await IdeaOfAsync(command, cancellationToken);
        var to = string.Join(' ', command.Args ?? []).Trim();
        if (to.Length == 0)
        {
            throw new FlightException("move-idea needs the repo to move the idea to");
        }
        var (toName, toPath) = await RepoOfAsync(to, cancellationToken);
        if (string.Equals(toName, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{IdeaIds.Id(issue.Number)} is in {name} already");
        }
        var toGitHub = repos.Repos().FirstOrDefault(r => string.Equals(r.Name, toName, StringComparison.OrdinalIgnoreCase))?.GitHub
            ?? throw new FlightException($"{toName} has no GitHub repo to move {IdeaIds.Id(issue.Number)} to");
        MilestoneIssue? moved = null;
        await SaveAsync(name, path, issue.Number, null, issue.UpdatedAt,
            async (now, _) => (moved = await github.TransferIssueAsync(path, issue.Number, toGitHub, now.UpdatedAt, cancellationToken)) is not null, cancellationToken);
        if (moved is null)
        {
            throw new FlightException($"{IdeaIds.Id(issue.Number)} was not moved");
        }
        var there = moved.Number;
        // The old id points to the new one from here on, whatever else goes wrong.
        state.PutIdeaMove(new IdeaMoveRow(name, IdeaIds.Id(issue.Number), toName, IdeaIds.Id(there)));
        // A transfer drops the milestone: the idea is put in the new repo's Ideas (made when it has none), guarded as every write is.
        var milestone = await github.IdeasMilestoneAsync(toPath, cancellationToken);
        var inIdeas = false;
        for (var attempt = 0; attempt < Attempts && !inIdeas; attempt++)
        {
            var now = await github.IssueNowAsync(toPath, there, cancellationToken)
                ?? throw new FlightException($"{IdeaIds.Id(issue.Number)} was moved to {toName} as {IdeaIds.Id(there)}, which GitHub then did not find to put in {IdeaIds.Milestone}");
            inIdeas = await github.MoveIssueAsync(toPath, there, milestone.Title, now.UpdatedAt, cancellationToken);
        }
        await RefreshAsync(name, path, cancellationToken);
        await RefreshAsync(toName, toPath, cancellationToken);
        return inIdeas
            ? state.Complete(command, null, made: $"{toName} {IdeaIds.Id(there)}")
            : throw new FlightException($"{IdeaIds.Id(issue.Number)} was moved to {toName} as {IdeaIds.Id(there)}, but it kept changing on GitHub and is not yet in {IdeaIds.Milestone} there");
    }

    /// <summary>The idea behind an idea target, as GitHub has it now, with the repo; refused when it is no idea of the repo or is closed.</summary>
    private async Task<(string Name, string Path, MilestoneIssue Issue)> IdeaOfAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (given, number) = IdeaIds.ReadTarget(command.Target)
            ?? throw new FlightException($"{command.Verb} takes an idea target, {Verbs.IdeaTargetPrefix}<repo>/I<issue number>, not {command.Target}");
        var (name, path, now) = await ResolveAsync(given, number, cancellationToken);
        return (name, path, await IdeaNowAsync(name, path, now, cancellationToken));
    }

    /// <summary>The repo and number an idea id has now: an id of an idea that was moved to another repo finds the one it moved to.</summary>
    private async Task<(string Name, string Path, int Number)> ResolveAsync(string given, int number, CancellationToken cancellationToken)
    {
        var (name, path) = await RepoOfAsync(given, cancellationToken);
        var (repo, now) = IdeaIds.Follow(state.IdeaMoves(), name, number);
        if (now == number && string.Equals(repo, name, StringComparison.OrdinalIgnoreCase))
        {
            return (name, path, number);
        }
        (name, path) = await RepoOfAsync(repo, cancellationToken);
        return (name, path, now);
    }

    /// <summary>Issue <paramref name="number"/> as GitHub has it now; refused when it is no idea of the repo or is closed.</summary>
    private async Task<MilestoneIssue> IdeaNowAsync(string name, string path, int number, CancellationToken cancellationToken)
    {
        var id = IdeaIds.Id(number);
        var issue = await github.IssueNowAsync(path, number, cancellationToken)
            ?? throw new FlightException($"{id} is no idea of {name}: it has no issue {number}");
        if (!string.Equals(issue.Milestone, IdeaIds.Milestone, StringComparison.OrdinalIgnoreCase))
        {
            if (IdeaIds.FlightOf(state.Flights(), state.IdeaMoves(), name, number) is { } flight)
            {
                throw new FlightException($"{id} of {name} is planned: it is the flight {flight}, so it is neither planned, dropped nor edited as an idea any more");
            }
            throw new FlightException($"{id} is not an idea of {name}: issue #{number} {(issue.Milestone is null ? "is in no milestone" : $"is in the milestone {issue.Milestone}")}, and ideas are the issues of the {IdeaIds.Milestone} milestone");
        }
        if (!issue.Open)
        {
            throw new FlightException($"{id} of {name} is closed already, so it is neither planned, dropped nor edited any more");
        }
        return issue;
    }

    /// <summary>How many times a save is tried again when GitHub changes the idea between its read and its write.</summary>
    private const int Attempts = 3;

    /// <summary>What a save came to: how (<see cref="IdeaEditWays"/>), the issue as it was last read, and the card when the person is asked.</summary>
    internal sealed record IdeaSaved(string Way, MilestoneIssue Issue, CardRow? Card);

    /// <summary>
    /// The one way an idea is written, whichever write it is (an edit, plan's move out of Ideas, drop's close; a later Tidy save as well):
    /// the idea is read now, and
    /// <list type="bullet">
    /// <item>for a write of <paramref name="mine"/>, its <c>updated_at</c> compared with <paramref name="startedFrom"/>, the version the edit
    /// began from: unchanged, it is saved as is; changed elsewhere, Claude's merge of both is saved; contradicting, a card asks and nothing is written;</item>
    /// <item>then <paramref name="write"/> is made guarded on the <c>updated_at</c> just read, and when GitHub moved in between, all of it runs again.</item>
    /// </list>
    /// A write with no text of its own (<paramref name="mine"/> null) skips the comparison and is only guarded.
    /// </summary>
    internal async Task<IdeaSaved> SaveAsync(string name, string path, int number, IdeaText? mine, DateTimeOffset startedFrom,
        Func<MilestoneIssue, IdeaText?, Task<bool>> write, CancellationToken cancellationToken)
    {
        var id = IdeaIds.Id(number);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var now = await IdeaNowAsync(name, path, number, cancellationToken);
            var text = mine;
            var way = IdeaEditWays.Saved;
            if (mine is not null && now.UpdatedAt != startedFrom)
            {
                var theirs = new IdeaText(now.Title, now.Body);
                if (Same(theirs, mine))
                {
                    return new IdeaSaved(IdeaEditWays.Saved, now, null);
                }
                // GitHub's text is what the edit started from when the node last read this very version: only something else changed, so mine stands.
                var read = state.Ideas().FirstOrDefault(i => i.Id == id && string.Equals(i.Repo, name, StringComparison.OrdinalIgnoreCase) && i.UpdatedAt == startedFrom);
                if (read is null || !Same(new IdeaText(read.Title, read.Body), theirs))
                {
                    var merge = await merger.MergeAsync(mine, theirs, cancellationToken);
                    if (merge.Contradicts)
                    {
                        return new IdeaSaved(IdeaEditWays.Asked, now, AskAbout(name, number, now, mine, theirs, merge.Merged));
                    }
                    text = merge.Merged;
                    way = IdeaEditWays.Merged;
                }
            }
            if (await write(now, text))
            {
                state.CloseCardsAbout(IdeaIds.Target(name, number), "saved", CardKinds.IdeaConflict);
                return new IdeaSaved(way, now, null);
            }
            // GitHub moved on between the read and the write: read it again, and compare again.
        }
        throw new FlightException($"{id} of {name} kept changing on GitHub while it was saved: nothing is saved, try again");
    }

    private static bool Same(IdeaText a, IdeaText b) =>
        IdeaText.Of(a.Joined).Equals(IdeaText.Of(b.Joined));

    /// <summary>The card that asks which of two contradicting versions to keep; every choice saves through <see cref="Verbs.EditIdea"/>, so the check runs again on it.</summary>
    private CardRow AskAbout(string name, int number, MilestoneIssue now, IdeaText mine, IdeaText theirs, IdeaText merged)
    {
        var subject = IdeaIds.Target(name, number);
        state.CloseCardsAbout(subject, "asked again", CardKinds.IdeaConflict);
        var seen = now.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        CardChoice Keep(string label, IdeaText text) => new() { Label = label, Verb = Verbs.EditIdea, Target = subject, Args = [seen, text.Joined] };
        return state.RaiseCard(CardBin.ForYou, CardKinds.IdeaConflict, subject,
            $"{IdeaIds.Id(number)}: {theirs.Title} (your edit and GitHub's contradict)",
            $"Yours:\n{mine.Joined}\n\nGitHub's:\n{theirs.Joined}\n\nThe merge Claude proposes:\n{merged.Joined}",
            [Keep("Keep the merge", merged), Keep("Keep mine", mine), Keep("Keep GitHub's", theirs)],
            link: now.Url.Length > 0 ? now.Url : null);
    }

    /// <summary>
    /// <c>edit-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;updated_at&gt; &lt;text…&gt;</c>: the idea's title and body saved through <see cref="SaveAsync"/>.
    /// <c>updated_at</c> is the issue's as the edit started from it.
    /// </summary>
    private async Task<CommandResult> EditAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (given, number) = IdeaIds.ReadTarget(command.Target)
            ?? throw new FlightException($"{command.Verb} takes an idea target, {Verbs.IdeaTargetPrefix}<repo>/I<issue number>, not {command.Target}");
        var args = command.Args ?? [];
        if (args.Length < 2 || !DateTimeOffset.TryParse(args[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedFrom))
        {
            throw new FlightException("edit-idea takes the updated_at the edit started from (ISO 8601) and then the idea's text");
        }
        var mine = IdeaText.Of(string.Join(' ', args.Skip(1)));
        if (mine.Title.Length == 0)
        {
            throw new FlightException("edit-idea needs the idea's text: its first line is the title, the rest the body");
        }
        (var name, var path, number) = await ResolveAsync(given, number, cancellationToken);
        var saved = await SaveAsync(name, path, number, mine, startedFrom,
            (now, text) => github.EditIssueAsync(path, number, text!.Title, text.Body, now.UpdatedAt, cancellationToken), cancellationToken);
        await RefreshAsync(name, path, cancellationToken);
        return saved.Card is { } card
            ? state.Complete(command, null, way: IdeaEditWays.Asked, made: card.Id)
            : state.Complete(command, null, way: saved.Way, made: IdeaIds.Id(number));
    }

    /// <summary>
    /// <c>tidy-idea idea/&lt;repo&gt;/I&lt;n&gt;</c>: Claude's proposal for the idea as GitHub has it now, with the <c>updated_at</c> it was made from.
    /// Nothing is written: saving it is <see cref="Verbs.SaveTidyIdea"/>, which goes through <see cref="SaveAsync"/>.
    /// </summary>
    private async Task<CommandResult> TidyAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (_, path, issue) = await IdeaOfAsync(command, cancellationToken);
        var text = new IdeaText(issue.Title, issue.Body);
        // Both are Claude runs and neither needs the other: together they take the longer one, not the sum (the CLI waits 100 s).
        var tidying = tidier.TidyAsync(text, path, cancellationToken);
        var proposing = ProposalAsync(text, path, cancellationToken);
        var proposed = await tidying;
        var milestone = await proposing;
        return state.Complete(command, null, made: IdeaIds.Id(issue.Number),
            tidy: new IdeaTidyProposal(proposed.Title, proposed.Body, issue.Title, issue.Body, issue.UpdatedAt) { Milestone = milestone });
    }

    /// <summary>The repo's open milestones but Ideas, the earliest due first.</summary>
    private async Task<Milestone[]> OpenMilestonesAsync(string path, CancellationToken cancellationToken) =>
        [.. (await github.MilestonesAsync(path, cancellationToken))
            .Where(m => !string.Equals(m.Title, IdeaIds.Milestone, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.DueOn ?? DateTimeOffset.MaxValue).ThenBy(m => m.Number)];

    /// <summary>
    /// <c>propose-milestone idea/&lt;repo&gt;/I&lt;n&gt;</c>: the open milestones and Claude's proposal. Claude failing in any way, or naming a
    /// milestone that is not open, leaves the list with no proposal and why in the note.
    /// </summary>
    private async Task<CommandResult> ProposeAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (_, path, issue) = await IdeaOfAsync(command, cancellationToken);
        return state.Complete(command, null, made: IdeaIds.Id(issue.Number), milestones: await ProposalAsync(new IdeaText(issue.Title, issue.Body), path, cancellationToken));
    }

    /// <summary>The list of open milestones with Claude's proposal; never throws for Claude (or the issue listing), which only blanks the proposal.</summary>
    private async Task<MilestoneProposal> ProposalAsync(IdeaText idea, string path, CancellationToken cancellationToken)
    {
        Milestone[] open;
        try
        {
            open = await OpenMilestonesAsync(path, cancellationToken);
        }
        catch (FlightException ex)
        {
            return new MilestoneProposal([], Note: $"the milestones could not be read: {ex.Message}");
        }
        var choices = open.Select(m => new MilestoneChoice(m.Title, m.Description, m.DueOn, m.OpenIssues)).ToArray();
        if (milestoner is null)
        {
            return new MilestoneProposal(choices, Note: "no proposal: this node has no one to propose with");
        }
        try
        {
            var contexts = await Task.WhenAll(open.Select(async m =>
            {
                var titles = (await github.MilestoneIssuesAsync(path, m.Title, cancellationToken))
                    .Where(i => i.Open).Take(ClaudeIdeaMilestoner.IssuesShown).Select(i => i.Title).ToArray();
                return new MilestoneContext(m, titles);
            }));
            var answer = await milestoner.ProposeAsync(idea, contexts, path, cancellationToken);
            return new MilestoneProposal(choices, answer.Title, answer.NewTitle, answer.NewDescription, answer.Reason.Length > 0 ? answer.Reason : null);
        }
        catch (FlightException ex)
        {
            return new MilestoneProposal(choices, Note: $"no proposal: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>plan-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;milestone…&gt;</c> or <c>--new &lt;title&gt; [description…]</c>: the open milestone named, else a new one, made
    /// on GitHub only once the flight is made (none named is refused, naming the open ones); a flight on the issue, which remembers its idea,
    /// and then the issue moved out of Ideas. Everything that can be refused is refused before the flight is made.
    /// </summary>
    private async Task<CommandResult> PlanAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (name, path, issue) = await IdeaOfAsync(command, cancellationToken);
        var id = IdeaIds.Id(issue.Number);
        var args = command.Args ?? [];
        var asksNew = args.Length > 0 && args[0] == Verbs.NewMilestoneArg;
        var wanted = asksNew ? (args.Length > 1 ? args[1] : "").Trim() : string.Join(' ', args).Trim();
        var newDescription = asksNew ? string.Join(' ', args.Skip(2)).Trim() : "";
        var open = await OpenMilestonesAsync(path, cancellationToken);
        var openNames = open.Length > 0 ? $"it has {string.Join(", ", open.Select(m => m.Title))}" : $"it has no open milestone but {IdeaIds.Milestone}";
        if (wanted.Length == 0)
        {
            throw new FlightException(asksNew
                ? "plan-idea with a new milestone needs its title"
                : $"plan-idea needs the milestone to plan {id} into, none is chosen for it ({name}: {openNames}): name one with --milestone, or a new one with --new-milestone");
        }
        if (string.Equals(wanted, IdeaIds.Milestone, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{id} is in {IdeaIds.Milestone} already: plan it into another milestone");
        }
        var existing = open.FirstOrDefault(m => string.Equals(m.Title, wanted, StringComparison.OrdinalIgnoreCase));
        if (existing is null && !asksNew)
        {
            throw new FlightException($"{name} has no open milestone {wanted} ({openNames})");
        }
        var makeNew = existing is null;
        var target = existing?.Title ?? wanted;

        var made = await flights.RunAsync(command with
        {
            Verb = Verbs.CreateFlight,
            Target = Verbs.RepoTarget(path),
            Args = [issue.Number.ToString(CultureInfo.InvariantCulture), issue.Title],
            RequestId = null,
        }, cancellationToken);
        if (made is not { Ok: true, Made: { Length: > 0 } code })
        {
            throw new FlightException(made.Error ?? $"no flight was made for {id}");
        }
        // A flight made on another node (maintenance) is not in these rows: that node remembers where it came from.
        if (state.Flights().FirstOrDefault(f => f.Code == code) is { } flight)
        {
            state.PutFlight(flight with { FromIdea = id });
        }
        try
        {
            // Every refusal is behind us: only now is a new milestone made.
            if (makeNew)
            {
                target = (await github.CreateMilestoneAsync(path, wanted, newDescription, cancellationToken)).Title;
            }
            await SaveAsync(name, path, issue.Number, null, issue.UpdatedAt,
                (now, _) => github.MoveIssueAsync(path, issue.Number, target, now.UpdatedAt, cancellationToken), cancellationToken);
        }
        catch (FlightException ex)
        {
            await RefreshAsync(name, path, cancellationToken);
            throw new FlightException($"flight {code} is made on {id}, but it stays in {IdeaIds.Milestone}: {ex.Message}");
        }
        await RefreshAsync(name, path, cancellationToken);
        return state.Complete(command, null, way: made.Way, made: code);
    }

    /// <summary>Reads the repo's open milestones and open issues afresh into the node's rows; a read gh cannot make leaves its rows as they were.</summary>
    private async Task RefreshBoardAsync(string name, string path, CancellationToken cancellationToken)
    {
        try
        {
            state.PutMilestones(name, IssueRows.OfMilestones(name, await github.MilestonesAsync(path, cancellationToken)));
        }
        catch (FlightException)
        {
            // What was done on GitHub stands; the next look at the repo reads the rest.
        }
        try
        {
            state.PutIssues(name, IssueRows.OfIssues(name, await github.OpenMilestoneIssuesAsync(path, cancellationToken), state.Flights(), time.GetUtcNow()));
        }
        catch (FlightException)
        {
        }
        await RefreshAsync(name, path, cancellationToken);
    }

    /// <summary>
    /// <c>set-milestone issue/&lt;repo&gt;/#&lt;n&gt; &lt;milestone or none&gt; [&lt;updated_at&gt;]</c>: the open issue put in an open milestone, or in none. The
    /// milestone must be open (refused otherwise, naming the open ones); the <c>updated_at</c> the caller saw, when it gives one, must still be
    /// GitHub's, and the write is guarded on the <c>updated_at</c> read just before it, read again when GitHub moves in between.
    /// </summary>
    private async Task<CommandResult> SetMilestoneAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var (given, number) = IssueIds.ReadTarget(command.Target)
            ?? throw new FlightException($"{command.Verb} takes an issue target, {Verbs.IssueTargetPrefix}<repo>/#<issue number>, not {command.Target}");
        var args = command.Args ?? [];
        var wanted = args.Length > 0 ? args[0].Trim() : "";
        if (wanted.Length == 0)
        {
            throw new FlightException($"set-milestone needs the milestone to put the issue in, or {Verbs.NoMilestone} to take it out of every milestone");
        }
        DateTimeOffset? seen = null;
        if (args.Length > 1)
        {
            seen = DateTimeOffset.TryParse(args[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : throw new FlightException("set-milestone takes the updated_at the caller saw as its second argument (ISO 8601)");
        }
        var (name, path) = await RepoOfAsync(given, cancellationToken);
        var open = await github.MilestonesAsync(path, cancellationToken);
        string? target = null;
        if (!string.Equals(wanted, Verbs.NoMilestone, StringComparison.OrdinalIgnoreCase))
        {
            target = open.FirstOrDefault(m => string.Equals(m.Title, wanted, StringComparison.OrdinalIgnoreCase))?.Title
                ?? throw new FlightException($"{name} has no open milestone {wanted} ({(open.Length > 0 ? $"it has {string.Join(", ", open.Select(m => m.Title))}" : "it has no open milestone")})");
        }
        var made = $"{name} #{number.ToString(CultureInfo.InvariantCulture)}";
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var now = await github.IssueNowAsync(path, number, cancellationToken)
                ?? throw new FlightException($"{name} has no issue #{number}");
            if (!now.Open)
            {
                throw new FlightException($"#{number} of {name} is closed, so it is not moved to a milestone");
            }
            if (seen is { } was && now.UpdatedAt != was)
            {
                await RefreshBoardAsync(name, path, cancellationToken);
                throw new FlightException($"#{number} of {name} changed on GitHub since you saw it ({was:O}, now {now.UpdatedAt:O}), so it is not moved: look at it again");
            }
            if (string.Equals(now.Milestone, target, StringComparison.OrdinalIgnoreCase))
            {
                // Already where it was asked to go: nothing to write.
                await RefreshBoardAsync(name, path, cancellationToken);
                return state.Complete(command, null, made: made);
            }
            if (await github.SetIssueMilestoneAsync(path, number, target, now.UpdatedAt, cancellationToken))
            {
                await RefreshBoardAsync(name, path, cancellationToken);
                return state.Complete(command, null, made: made);
            }
            // GitHub moved on between the read and the write: read it again, and compare again (with what the caller saw too).
        }
        await RefreshBoardAsync(name, path, cancellationToken);
        throw new FlightException($"#{number} of {name} kept changing on GitHub while it was moved: nothing is moved, try again");
    }

    /// <summary>
    /// <c>fold-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;flight code&gt;</c>: the idea's words added to the flight's issue as a comment (unchanged, never reworded),
    /// then the idea closed as folded into the flight.
    /// </summary>
    private async Task<CommandResult> FoldAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var code = string.Join(' ', command.Args ?? []).Trim();
        var flight = state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlightException(code.Length == 0 ? "fold-idea needs the flight to fold the idea into" : $"no flight {code} to fold the idea into");
        var (name, path, issue) = await IdeaOfAsync(command, cancellationToken);
        var id = IdeaIds.Id(issue.Number);
        var (_, flightPath) = await RepoOfAsync(flight.Repo, cancellationToken);
        var text = issue.Body.Length > 0 ? $"{issue.Title}\n\n{issue.Body}" : issue.Title;
        await github.CommentIssueAsync(flightPath, flight.Issue, $"Folded in from {name} {id}:\n\n{text}", cancellationToken);
        await SaveAsync(name, path, issue.Number, null, issue.UpdatedAt,
            (now, _) => github.CloseIssueNotPlannedAsync(path, issue.Number, $"Folded into {flight.Code} ({flight.Repo} #{flight.Issue}).", now.UpdatedAt, cancellationToken), cancellationToken);
        await RefreshAsync(name, path, cancellationToken);
        return state.Complete(command, null, made: id);
    }

    /// <summary><c>drop-idea idea/&lt;repo&gt;/I&lt;n&gt; &lt;why…&gt;</c>: closed as not planned, why its comment.</summary>
    private async Task<CommandResult> DropAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var why = string.Join(' ', command.Args ?? []).Trim();
        if (why.Length == 0)
        {
            throw new FlightException("drop-idea needs why: the reason is the issue's closing comment");
        }
        var (name, path, issue) = await IdeaOfAsync(command, cancellationToken);
        await SaveAsync(name, path, issue.Number, null, issue.UpdatedAt,
            (now, _) => github.CloseIssueNotPlannedAsync(path, issue.Number, why, now.UpdatedAt, cancellationToken), cancellationToken);
        await RefreshAsync(name, path, cancellationToken);
        return state.Complete(command, null, made: IdeaIds.Id(issue.Number));
    }
}
