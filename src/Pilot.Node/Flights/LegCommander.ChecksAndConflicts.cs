using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// A PR whose checks failed or that conflicts with its base: the cards and the legs that answer them.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>
    /// A reviewed PR whose checks failed: taken as a review asking for changes, so its PR goes back to draft, its merge card
    /// closes, and the ring offers the go-around a done leg otherwise refuses. PIL53's review passed while the Linux runner
    /// failed two of its tests, and no verb could send it round (2026-10-23). Null when done; else why not.
    /// </summary>
    public Task<string?> ChecksFailedAsync(string code, string head, CancellationToken cancellationToken) =>
        SendBackAsync(code, number => $"the PR's checks failed on {CommitRefs.Short(head)}: read them (gh pr checks {number}, then gh run view <run> --log-failed), make them pass on every runner, and push", "its checks failed", cancellationToken, SendBackReason.ChecksFailed);

    /// <summary>One line of a reason, cut at 80 characters: what a card title carries of a longer detail.</summary>
    public static string OneLine(string text)
    {
        var line = text.Split('\n')[0].Trim().TrimEnd('.', ':', ';');
        return line.Length > 80 ? line[..77].TrimEnd() + "..." : line;
    }

    /// <summary>The title of the card for a PR whose failed checks the node could not send back: the reason is in it.</summary>
    public static string ChecksFailedTitle(string code, int number, string? failedName, string why) =>
        $"{code}'s PR #{number} fails {(failedName is { Length: > 0 } ? $"check {failedName}" : "its checks")} and the leg could not be sent back: {OneLine(why)}";

    /// <summary>What the node tried for a PR whose failed checks it could not send back, and what each choice does.</summary>
    public string ChecksFailedSummary(string code, int number, string why, IReadOnlyList<CardChoice> choices) =>
        $"The PR #{number} of {code} fails its checks; the node tried to send the last leg back and could not ({OneLine(why)})." +
        (choices.Count > 0 ? $" {choices[0].Label} starts a fresh worker told what failed." : " Fix them by hand, then dismiss this.");

    /// <summary>The choice that sends the flight's last leg round on a PR's failed checks, as the card's one action.</summary>
    public IReadOnlyList<CardChoice> ChecksFailedChoices(string code, string head, int number) =>
        state.Legs(code).OrderBy(l => l.Order).LastOrDefault() is { } last
            ? [new CardChoice { Label = Models.RedoLabel(last, "left the PR's checks failing"), Verb = Verbs.GoAround, Target = Verbs.LegTarget(code, last.Name),
                Args = [$"the PR's checks failed on {CommitRefs.Short(head)}: read them (gh pr checks {number}, then gh run view <run> --log-failed), make them pass on every runner, and push"] }]
            : [];

    /// <summary>
    /// A flight whose base merge conflicts goes to the watchtower, not to a fresh worker: a ring card names the conflict and the plane,
    /// and the watchtower merges the base in there, resolves it, reruns the check and pushes plain. Its Resolve the conflict choice is for the
    /// watchtower to take only when the conflict is real work (two designs, a goal to drop). One card a conflict, not one a look.
    /// Null when the card is up; else why not.
    /// </summary>
    public Task<string?> ConflictsAsync(string code, string why, CancellationToken cancellationToken)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { } flight)
        {
            return Task.FromResult<string?>($"{code} is not a flight");
        }
        var subject = Verbs.FlightTarget(code);
        if (state.Cards(openOnly: true).Any(c => c.Kind == PrConflictsKind && c.Subject == subject))
        {
            return Task.FromResult<string?>(null);
        }
        var legs = state.Legs(code);
        var choices = new List<CardChoice>();
        if (legs.LastOrDefault() is { } last)
        {
            // Not "Redo leg": a worker sent round resolves it on the leg's work as it stands; nothing is thrown away (the owner, 2026-10-29).
            choices.Add(new CardChoice { Label = Models.SendBackLabel(last, ResolveConflict), Verb = Verbs.GoAround, Target = Verbs.LegTarget(code, last.Name), Args = [ConflictBrief(why)] });
        }
        var number = flight.PullRequest is { } n ? $"PR #{n}" : "branch";
        state.RaiseCard(CardBin.Ring, PrConflictsKind, subject, $"{code}'s {number} conflicts with its base",
            $"{why}. A worker already tried (the node sends one first, and rings only when it stopped or the same conflict came back). The plane is {flight.Worktree}: {ConflictBrief(why)}, then {(flight.PullRequest is null ? $"`pilot flight pr {code}` (or dismiss this card, and the node opens it)" : $"`pilot flight review {code}`")}. {ResolveConflict} starts a fresh worker on the last leg.",
            choices, link: flight.PullRequestUrl,
            summary: $"The {number} of {code} conflicts with its base and a worker already tried. The watchtower merges the base into the plane and resolves it; {ResolveConflict} starts a fresh worker.",
            // The title is the same for every conflict of the PR: a new head or a further go-around is a new cause (#853).
            cause: $"conflicts:{flight.PullRequest}:{flight.GitHubHead}:{legs.Sum(l => l.GoArounds)}");
        return Task.FromResult<string?>(null);
    }

    /// <summary>The conflict card's go-around button: the last leg's worker resolves the conflict on its work as it stands.</summary>
    public const string ResolveConflict = "Resolve the conflict";

    /// <summary>The card a base merge that conflicts raises, for the watchtower to resolve in the plane.</summary>
    public const string PrConflictsKind = "pr-conflicts";

    /// <summary>What the watchtower or a worker is told when its flight's PR conflicts with its base.</summary>
    public static string ConflictBrief(string why) => $"the PR conflicts with its base ({why}): {UpdateInstruction}";

    /// <summary>What the node tells the worker it sends round on a conflict that held a flight's PR from opening.</summary>
    public static string OpenConflictBrief(string why) =>
        $"{ConflictBrief(why)}. Keep both sides' work; on a version pin line (a PackageVersion in Directory.Packages.props, say) take the higher of the two versions";

    /// <summary>The base head each flight's open conflict was last sent round for: one send a conflict, never a loop.</summary>
    private readonly ConcurrentDictionary<string, string> _conflictSent = new(StringComparer.Ordinal);

    /// <summary>
    /// A flight's PR open that conflicted with its base: the last leg is sent round by the node with the conflict brief, once for
    /// each head of the base (a second look at the same head finds it sent). Null when sent; else why not, and the caller raises the card.
    /// </summary>
    private async Task<string?> SendConflictRoundAsync(FlightRow flight, string why, CancellationToken cancellationToken)
    {
        var head = await git.RunAsync(["rev-parse", flight.Base], flight.Worktree, cancellationToken);
        var key = head.Ok ? head.Output.Trim() : "";
        if (key.Length == 0)
        {
            return "the base head could not be read";
        }
        if (_conflictSent.TryGetValue(flight.Code, out var sent) && sent == key)
        {
            return $"already sent round for {CommitRefs.Short(key)}";
        }
        if (state.Legs(flight.Code).OrderBy(l => l.Order).LastOrDefault() is not { State: LegState.Done } last)
        {
            return "the last leg is not done";
        }
        try
        {
            await GoAroundAsync(new CommandRequest { Verb = Verbs.GoAround, Target = Verbs.LegTarget(flight.Code, last.Name), Args = [OpenConflictBrief(why)], Who = LegWatch.Who }, cancellationToken, openConflict: true);
        }
        catch (FlightException ex)
        {
            return ex.Message;
        }
        _conflictSent[flight.Code] = key;
        return null;
    }

    /// <summary>What to do to bring the plane up to date with its base: merge it in, never rebase, never force push.</summary>
    public const string UpdateInstruction = "fetch, then merge origin/<base> into the plane (git merge --no-edit, never a rebase), resolving every conflict so both sides' work stays, rerun the check, and push plain (never --force or --force-with-lease)";

    /// <summary>
    /// Sets on GitHub what an earlier refusal left: the review comment, the status, the label. The node's look at the PRs calls it.
    /// Never under the commander's lock: the marks are one flight at a time among themselves, and each write to a flight's row is one
    /// step (PIL338).
    /// </summary>
    public Task RetryMarksAsync(CancellationToken cancellationToken) => _marks.RetryAsync(cancellationToken);

    /// <summary>
    /// Keeps a flight's PR marks to what the node's look at the PR found: its conflicts and merge queue as labels, and its head
    /// and <c>pilot:</c> label as GitHub has them on the flight's row. Never under the commander's lock, as <see cref="RetryMarksAsync"/>.
    /// </summary>
    public Task NoteGitHubAsync(string code, PullRequest pr, bool queued, CancellationToken cancellationToken) => _marks.NoteAsync(code, pr, queued, cancellationToken);
}
