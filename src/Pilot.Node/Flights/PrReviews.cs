using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Reviews of any PR, not only a flight's (#113): <c>review-pr</c> finds a clone of the PR's repo under the repo roots, or
/// makes one in the node's clone cache, pins a worktree of its own at the PR's head, and starts a fresh reviewer there as
/// a job of the node; <c>pr-verdict</c> is the reviewer's verdict, which a ring card carries to the controller. The
/// worktree goes once that card closes, by whatever way it closed, so a review leaves nothing behind in anyone's clone.
/// <para>
/// A verdict on a PR still open is posted by the node itself, whatever it is: an approve goes out under gh's login unseen,
/// as a changes or a comment does. That is wanted (a review the controller must press Post on sat unread all night), not an
/// oversight. The Handled <see cref="PostedKind"/> card is raised and closes at once with the review's card, in
/// <see cref="EndAsync"/>'s <c>CloseCardsAbout</c>, on purpose: it is a record, not a request, so it shows under
/// <c>--all</c> and in the digest rather than waiting open for anyone.
/// </para>
/// <para>
/// The verdict's call is answered before the review ends: the reviewer's job is stopped and its worktree removed only after
/// <see cref="SelfFinishGrace"/>, in the one delayed task the verdict starts.
/// </para>
/// </summary>
public sealed partial class PrReviews(
    NodeState state, IWorkerLauncher workers, IGitHub github, IGit git, Planes planes, RepoCatalog repos, NodeOptions options, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>
    /// Gives a flight a PR review's approve of its PR: code, the head reviewed, the verdict, the report; null when taken, else why
    /// not. Set where the node is built, so this needs nothing of the leg commander's.
    /// </summary>
    public Func<string, string, string, string, CancellationToken, Task<string?>>? TakeFlightVerdict { get; set; }

    /// <summary>
    /// Sends a flight's last leg round with a design question's answer: code, why (the question and the answer), who asked, the
    /// person's word if the ask carried one; null when it went round, else why not. Set where the node is built.
    /// </summary>
    public Func<string, string, string?, string?, CancellationToken, Task<string?>>? SendFlightRound { get; set; }

    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The PR targets of reviews the node posted whose reviewer is still in its <see cref="SelfFinishGrace"/>: a sweep leaves them, the delayed task of their verdict ends them.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _graced = new(StringComparer.Ordinal);

    /// <summary>The card a verdict raises: the review is ready for the controller.</summary>
    public const string ReadyKind = "pr-review-ready";

    /// <summary>The Handled line a review or fixer that waits for admission leaves, naming why; it closes when the start goes.</summary>
    public const string WaitingKind = "pr-review-waiting";

    /// <summary>Whether a start may go: the Opus cap and priority are asked here, as a flight's starts are (one gate, shared with the legs).</summary>
    public AdmissionGate Slots { get => field ??= new(state, () => Models, options.Machine); set; }

    /// <summary>The failures looked into: a failing test the PR does not touch is filed as an investigation. Null for one built without.</summary>
    public FailureInvestigations? Investigations { get; set; }

    /// <summary>What a failure under investigation does to a hand PR: a first one is run again once, a repeat names the PR on the tower's alert. Null for one built without.</summary>
    public FailureAlerts? Alerts { get; set; }

    // The failed heads already looked into: once a head, not every sweep.
    private readonly HashSet<string> _investigated = new(StringComparer.Ordinal);

    // The reviews and fixers admission holds, by PR target: the sweep asks again and starts them in turn.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (PrRef Pr, bool Fix)> _queued = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _queuedSaid = new(StringComparer.Ordinal);

    // True when the start waits: said once per reason by a Handled card (no ring), kept for the sweep. False clears any earlier wait.
    private bool WaitsForSlot(PrRef pr, bool fix, string? model, string? replacing = null)
    {
        var target = Verbs.PrTarget(pr.Repo, pr.Number);
        if (Slots.Ask(fix ? AdmissionRole.Fixer : AdmissionRole.Reviewer, model, null, replacing is null ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { replacing }, subject: target) is not AdmissionDecision.Wait wait)
        {
            _queued.TryRemove(target, out _);
            if (_queuedSaid.TryRemove(target, out _))
            {
                state.CloseCardsAbout(target, "its start was admitted", WaitingKind);
            }
            return false;
        }
        _queued[target] = (pr, fix);
        if (!_queuedSaid.TryGetValue(target, out var said) || said != wait.Why)
        {
            _queuedSaid[target] = wait.Why;
            state.CloseCardsAbout(target, "its wait changed", WaitingKind);
            state.RaiseCard(CardBin.Handled, WaitingKind, target, $"{pr}: the {(fix ? "fixer" : "review")} waits to start", wait.Why);
        }
        return true;
    }

    /// <summary>The Handled line a start leaves.</summary>
    public const string StartedKind = "pr-review-started";

    /// <summary>The card said when a review's worktree could not be removed after its card closed.</summary>
    public const string LeftKind = "pr-review-left";

    /// <summary>The Handled line a review started afresh after a node restart leaves.</summary>
    public const string RestartedKind = "pr-review-restarted";

    /// <summary>The For-you card said when a review lost in a node restart could not be started afresh.</summary>
    public const string LostKind = "pr-review-lost";

    /// <summary>The verdicts a PR reviewer gives, as GitHub's review events will take them: approve, request changes, comment.</summary>
    public static readonly string[] Verdicts = ["approve", "changes", "comment"];

    /// <summary>How often it looks for reviews whose card has closed.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a reviewer that gave its verdict has to end its turn before it is stopped.</summary>
    public TimeSpan SelfFinishGrace { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Whether a verdict on a PR still open is posted by the node at once; off, it waits on its card's Post, as a test of that path has it.</summary>
    public bool PostsItself { get; init; } = true;

    /// <summary>The model and effort the reviewer starts on; a test passes none.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>The CLI the reviewer is told to run, found once.</summary>
    public string Cli { get; init; } = Briefs.PilotCli();

    /// <summary>The node's socket when it is not the default one, so the reviewer's CLI reaches this node; null otherwise.</summary>
    public string? Socket => string.Equals(Path.GetFullPath(options.SocketPath), Path.GetFullPath(NodeSocket.DefaultPath), StringComparison.OrdinalIgnoreCase)
        ? null
        : options.SocketPath;

    /// <summary>
    /// Where the node clones a repo with no clone under the repo roots: <c>&lt;data&gt;/clones/&lt;owner&gt;/&lt;repo&gt;</c>. Claude
    /// Code starts no background session in a folder never trusted, so this folder is trusted once, which covers every
    /// clone in it.
    /// </summary>
    public string CloneCache => Path.Combine(options.DataFolder, "clones");

    /// <summary>The card's choice that posts the review.</summary>
    public const string PostLabel = "Post";

    /// <summary>
    /// Where a review's payload is written, <c>&lt;data&gt;/reviews/&lt;owner&gt;/&lt;repo&gt;-&lt;n&gt;.json</c>: in the node's own
    /// folder, so it outlives the worktree, and a full path, as gh's <c>--input</c> takes it.
    /// </summary>
    public string DraftOf(PrRef pr) => Path.Combine(Path.GetFullPath(options.DataFolder), "reviews", pr.Owner, $"{pr.Name}-{pr.Number}.json");

    public static bool Handles(string verb) => verb is Verbs.ReviewPr or Verbs.PrVerdict or Verbs.PostReview or Verbs.FixPr;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        string? error = null;
        // What a review-pr or fix-pr made, for the one line it leaves in node.log; null for the other verbs.
        string? made = null;
        try
        {
            var pr = PrRef.Parse(command.Target) is { } parsed && command.Target.StartsWith(Verbs.PrTargetPrefix, StringComparison.Ordinal)
                ? parsed
                : throw new FlightException($"this verb takes a PR target, {Verbs.PrTargetPrefix}<owner>/<repo>#<n>, not {command.Target}");
            switch (command.Verb)
            {
                case Verbs.ReviewPr:
                    made = await StartAsync(pr, cancellationToken);
                    break;
                case Verbs.PrVerdict:
                    if (await VerdictAsync(pr, command, cancellationToken))
                    {
                        await FixAfterPostAsync(pr, cancellationToken);
                    }
                    break;
                case Verbs.PostReview:
                    await PostAsync(pr, command, cancellationToken);
                    await FixAfterPostAsync(pr, cancellationToken);
                    break;
                case Verbs.FixPr:
                    made = await FixAsync(pr, command, cancellationToken);
                    break;
                case Verbs.Merge:
                    await MergeAsync(pr, command, cancellationToken);
                    break;
            }
        }
        catch (FlightException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // gh, git or the disk failing in a way no FlightException names (a gh that is not on PATH, a draft that cannot be
            // written) is a refusal that says so, not an exception out of the route: that answered 500 with an empty body.
            error = $"{command.Verb} failed: {ex.GetType().Name}: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            // Cut off (the caller went away, the node stops): the line still says so, in the finally, and the cancellation goes on.
            error = $"{command.Verb} was cancelled";
            throw;
        }
        finally
        {
            // One line per review-pr and fix-pr, written whatever came of it, so a start that left no trace is not a mystery
            // (SE1's review of tianwen#1930, 2026-10-30, left nothing in node.log to say it had been asked for).
            if (command.Verb is Verbs.ReviewPr or Verbs.FixPr)
            {
                try
                {
                    await Error.WriteLineAsync($"{command.Verb} {command.Target}, asked by {command.Who}: {made ?? $"refused: {error ?? "without a reason"}"}");
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // A log that cannot be written is not a reason to fail the command.
                }
            }
            _one.Release();
        }
        return state.Complete(command, error);
    }
}
