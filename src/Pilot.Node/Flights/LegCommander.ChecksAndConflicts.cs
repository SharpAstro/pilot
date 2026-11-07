using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
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

    /// <summary>Says text to a session row (a node say), true when it was delivered; null where there is none (a test that tells no one).</summary>
    public Func<string, string, CancellationToken, Task<bool>>? Say { get; set; }

    /// <summary>
    /// A flight's PR that conflicts with its base is the node's own work, said to no one: no card of any kind. This is the one entry point of
    /// every path that finds the conflict (the PR sweeps, a queue drop, the merge card, a PR recheck, the merge command, a PR open). A leg
    /// flying on the plane with a live worker is told to merge the base, with the conflict brief, and nothing else starts; otherwise the last
    /// leg, when it is done, goes round by the node with the brief. Once for each head of the base. Null when a worker is resolving it (told now,
    /// or already for this head); else why none could be. <paramref name="lockHeld"/> is true for a caller that holds the leg lock already (the
    /// merge command, a PR open).
    /// </summary>
    public async Task<string?> ResolveConflictAsync(string code, string why, CancellationToken cancellationToken, bool lockHeld = false)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { } flight)
        {
            return $"{code} is not a flight";
        }
        if (flight.State != FlightState.Flying || !Directory.Exists(flight.Worktree))
        {
            return $"{code} is not a flying flight with a plane";
        }
        var head = await git.RunAsync(["rev-parse", flight.Base], flight.Worktree, cancellationToken);
        var key = head.Ok ? head.Output.Trim() : "";
        if (key.Length == 0)
        {
            return "the base head could not be read";
        }
        if (_conflictSent.TryGetValue(code, out var sent) && sent == key)
        {
            // Told or sent round for this head already: its worker is on it, or stopped, and the stop has its own leg-failed card.
            return null;
        }
        var legs = state.Legs(code).OrderBy(l => l.Order).ToArray();
        var brief = ConflictBrief(why, await ConflictDetailAsync(flight, cancellationToken));
        // A leg flying on the plane is the one to merge the base: a second worker on the same plane would only clash with it.
        var flying = legs.FirstOrDefault(l => l.State == LegState.Flying && l.WorkerJob is { } job
            && state.Sessions().Any(s => s.Id == job && s.State is not (SessionState.Ended or SessionState.Failed)));
        if (flying?.WorkerJob is { } live)
        {
            if (Say is null)
            {
                return $"{code}/{flying.Name}'s worker cannot be told: this node has no way to say a line";
            }
            try
            {
                if (!await Say(live, $"[the node] {brief}. Merge the base into your plane now, then go on with your leg.", cancellationToken))
                {
                    return $"{code}/{flying.Name}'s worker could not be told";
                }
            }
            catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return $"{code}/{flying.Name}'s worker could not be told ({ex.Message})";
            }
            _conflictSent[code] = key;
            return null;
        }
        if (legs.LastOrDefault() is not { State: LegState.Done } last)
        {
            return legs.LastOrDefault() is { } notDone ? $"the last leg, {notDone.Name}, is {notDone.State.ToString().ToLowerInvariant()}" : "the flight has no leg";
        }
        var command = new CommandRequest { Verb = Verbs.GoAround, Target = Verbs.LegTarget(code, last.Name), Args = [brief], Who = LegWatch.Who };
        if (lockHeld)
        {
            try
            {
                await GoAroundAsync(command, cancellationToken, conflictRound: true);
            }
            catch (FlightException ex)
            {
                return ex.Message;
            }
        }
        else
        {
            // Through the command route, which takes the lock and waits for the launch; the go-around finds its why among these and is no review's round.
            _conflictRounds[code] = brief.Trim();
            var round = await RunAsync(command, cancellationToken);
            if (!round.Ok)
            {
                return round.Error ?? "the go-around was refused";
            }
        }
        _conflictSent[code] = key;
        return null;
    }

    /// <summary>What a Handled line or a refusal says once a conflict is a worker's: nothing rings.</summary>
    public const string WorkerResolves = "a worker is resolving it";

    /// <summary>
    /// The brief a worker is told when its flight's PR conflicts with its base: the conflict, what each side did in the files and the base's
    /// commits that touch them, and the rule. Keep both sides' work; on a version pin take the higher.
    /// </summary>
    public static string ConflictBrief(string why, string detail = "") =>
        $"the PR conflicts with its base ({why}).{(detail.Length > 0 ? $"\n{detail}\n" : " ")}Rule: {UpdateInstruction}. Keep both sides' work; on a version pin line (a PackageVersion in Directory.Packages.props, say) take the higher of the two versions";

    /// <summary>The most files of a conflict the brief lists one by one.</summary>
    private const int ConflictFilesShown = 12;

    /// <summary>The most commits of the base the brief names for one file.</summary>
    private const int ConflictCommitsShown = 8;

    /// <summary>
    /// What the plane says of the conflict: the files a test merge of the base conflicts in (<c>git merge-tree</c>, else the files both sides
    /// changed), the merge base, and for each file what each side added since it and the commits on the base that touch it. Empty when git
    /// cannot say.
    /// </summary>
    private async Task<string> ConflictDetailAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var cwd = flight.Worktree;
        var baseRef = flight.Base;
        var found = await git.RunAsync(["merge-base", "HEAD", baseRef], cwd, cancellationToken);
        if (!found.Ok || found.Output.Trim() is not { Length: > 0 } mergeBase)
        {
            return "";
        }
        var files = new List<string>();
        var tree = await git.RunAsync(["merge-tree", "--write-tree", "--name-only", "--no-messages", "HEAD", baseRef], cwd, cancellationToken);
        if (tree.ExitCode == 1)
        {
            files.AddRange(Lines(tree.Output).Skip(1).Distinct(StringComparer.Ordinal));
        }
        if (files.Count == 0)
        {
            // An older git (no merge-tree --write-tree) or a merge that went clean here: the files both sides changed.
            var mine = await git.RunAsync(["diff", "--name-only", mergeBase, "HEAD"], cwd, cancellationToken);
            var theirs = await git.RunAsync(["diff", "--name-only", mergeBase, baseRef], cwd, cancellationToken);
            if (mine.Ok && theirs.Ok)
            {
                var onBase = Lines(theirs.Output).ToHashSet(StringComparer.Ordinal);
                files.AddRange(Lines(mine.Output).Where(onBase.Contains));
            }
        }
        if (files.Count == 0)
        {
            return "";
        }
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Merging {baseRef} (merge base {CommitRefs.Short(mergeBase)}) conflicts in {files.Count} file{(files.Count == 1 ? "" : "s")}:");
        foreach (var file in files.Take(ConflictFilesShown))
        {
            var ours = await SideAddedAsync(cwd, mergeBase, "HEAD", file, cancellationToken);
            var onTheBase = await SideAddedAsync(cwd, mergeBase, baseRef, file, cancellationToken);
            text.Append(CultureInfo.InvariantCulture, $"\n- {file}: this branch {ours}; the base {onTheBase}");
            var log = await git.RunAsync(["log", "--oneline", $"--max-count={ConflictCommitsShown}", $"{mergeBase}..{baseRef}", "--", file], cwd, cancellationToken);
            foreach (var line in log.Ok ? Lines(log.Output) : [])
            {
                text.Append(CultureInfo.InvariantCulture, $"\n    {line}");
            }
        }
        if (files.Count > ConflictFilesShown)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n- and {files.Count - ConflictFilesShown} more: git status lists them once the merge is started");
        }
        return text.ToString();
    }

    private static IEnumerable<string> Lines(string output) => output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    /// <summary>What one side did to a file since the merge base, as lines added and removed; "left it alone" when it did not touch it.</summary>
    private async Task<string> SideAddedAsync(string cwd, string mergeBase, string side, string file, CancellationToken cancellationToken)
    {
        var numstat = await git.RunAsync(["diff", "--numstat", mergeBase, side, "--", file], cwd, cancellationToken);
        if (!numstat.Ok || numstat.Output.Split('\n')[0].Split('\t') is not { Length: >= 3 } parts)
        {
            return "left it alone";
        }
        return parts[0] == "-" ? "changed it (binary)" : $"added {parts[0]} and removed {parts[1]} lines";
    }

    /// <summary>The base head each flight's conflict was last put to a worker for: one send a conflict, never a loop.</summary>
    private readonly ConcurrentDictionary<string, string> _conflictSent = new(StringComparer.Ordinal);

    /// <summary>The plane's and the base's heads (as one string) at the PR open that last conflicted, by flight: the sweep does not open it again until one moves.</summary>
    private readonly ConcurrentDictionary<string, string> _conflictOpenTried = new(StringComparer.Ordinal);

    private async Task<string> ConflictHeadsAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        var baseHead = await git.RunAsync(["rev-parse", flight.Base], flight.Worktree, cancellationToken);
        var planeHead = await git.RunAsync(["rev-parse", "HEAD"], flight.Worktree, cancellationToken);
        return $"{(baseHead.Ok ? baseHead.Output.Trim() : "")} {(planeHead.Ok ? planeHead.Output.Trim() : "")}";
    }

    /// <summary>The brief of a conflict round the node asked for through the command route, by flight: the go-around that carries it is no review's round.</summary>
    private readonly ConcurrentDictionary<string, string> _conflictRounds = new(StringComparer.Ordinal);

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
