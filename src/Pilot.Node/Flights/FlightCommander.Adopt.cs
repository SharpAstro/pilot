using System.Globalization;
using System.Net.WebSockets;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    /// <summary>The Handled card that says this node adopted a flight from a node that was down (#894).</summary>
    public const string AdoptedKind = "adopted";

    /// <summary>The For-you card that says what the old node had not pushed is not in an adopted flight, so it is at risk.</summary>
    public const string AdoptAtRiskKind = "adopt-at-risk";

    /// <summary>The Handled line that says the adoption is done but the issue or the PR was not told.</summary>
    public const string AdoptCommentRefusedKind = "adopt-comment-refused";

    // An adoption claims a code as a create and a receive do, so it takes their turn on the one lock; the flight goes on by the
    // normal advance off the reply, as a flight handed over does.
    private async Task<CommandResult> AdoptOneAtATimeAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        CommandResult result;
        await _creating.WaitAsync(cancellationToken);
        try
        {
            string? error = null;
            string? made = null;
            string? way = null;
            try
            {
                (made, way) = await AdoptAsync(command, cancellationToken);
            }
            catch (FlightException ex)
            {
                error = ex.Message;
            }
            result = state.Complete(command, error, way: way, made: made);
        }
        finally
        {
            _creating.Release();
        }
        if (result.Ok && result.Made is { } adopted && Advance is { } advance)
        {
            AdvanceOffReply(adopted, advance, cancellationToken);
        }
        return result;
    }

    /// <summary>
    /// Takes over a flight whose node has gone down (<see cref="Verbs.AdoptFlight"/>, #894), from this node's last copy of that node's flights
    /// and legs (<see cref="FleetCopy"/>). Refused while the old node answers now or was heard within <c>adopt-after</c> (saying when it was
    /// last heard), when the copy has no such flight, when the flight is landed, when this node holds the code already, and when it has no
    /// checkout of the repo. Otherwise the plane is made from the branch the old node pushed, the flight is put as adopted from it, a leg that
    /// was flying there is planned again so it restarts here, the pilot node label moves to this node, the issue and the PR say who adopted
    /// it from whom, and a card says that whatever the old node had not pushed is at risk. A refused comment or label is a card, not a failed adoption.
    /// </summary>
    private async Task<(string Code, string? Way)> AdoptAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (Models.Maintenance)
        {
            throw new FlightException($"{Machine} is in maintenance: it takes no new flights");
        }
        var from = (command.Args ?? []).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(from) || !command.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) || command.Target.Length == Verbs.FlightTargetPrefix.Length)
        {
            throw new FlightException($"{Verbs.AdoptFlight} takes the flight and the node it flies on, as in {Verbs.AdoptFlight} flight/<code> <node>");
        }
        var code = command.Target[Verbs.FlightTargetPrefix.Length..];
        if (string.Equals(from, Machine, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{code} is asked for from {from}, which is this node: there is nothing to adopt");
        }

        // The node must be down: one that answers now flies its flights itself, and one heard from lately may only be out of reach for a moment.
        if (await AnswersNowAsync(from, cancellationToken))
        {
            throw new FlightException($"{from} answers now, so it still flies {code}: let it hand the flight over (pilot flight move {code} there), or adopt it once {from} has been silent for {DurationWords(Models.AdoptAfter)}");
        }
        if (Copy?.Of(from) is not { } copy)
        {
            throw new FlightException($"{Machine} has no copy of {from}'s flights (it never heard from it), so {code} cannot be adopted from {from}");
        }
        var silence = time.GetUtcNow() - copy.LastHeard;
        if (silence < Models.AdoptAfter)
        {
            throw new FlightException($"{from} was last heard from at {WhenWords(copy.LastHeard)}, {DurationWords(silence)} ago: {code} is adopted from a node silent for {DurationWords(Models.AdoptAfter)} or more (adopt-after)");
        }
        if (copy.Flights.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { } sent)
        {
            throw new FlightException($"{Machine}'s copy of {from}'s flights, last heard at {WhenWords(copy.LastHeard)}, has no flight {code}");
        }
        code = sent.Code;
        if (sent.State == FlightState.Landed)
        {
            throw new FlightException($"{code} is landed on {from}{(sent.MovedTo is { } to ? $" ({to.Text})" : "")}: only a flight in the air is adopted");
        }
        if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is { } here)
        {
            throw new FlightException($"{code} is already here ({here.State.ToString().ToLowerInvariant()} on {Machine}): a code is one flight on a node for good");
        }
        var checkout = Repos?.Repos().FirstOrDefault(r => string.Equals(r.Name, sent.Repo, StringComparison.OrdinalIgnoreCase))?.Path;
        var repoPath = checkout is null ? null : await planes.MainCheckoutAsync(checkout, cancellationToken);
        if (repoPath is null)
        {
            throw new FlightException($"{Machine} has no checkout of {sent.Repo}, {code}'s repo on {from}");
        }

        var plane = await planes.MakeFromBranchAsync(repoPath, code, sent.Branch, sent.Base, cancellationToken);
        var legs = copy.Legs.Where(l => string.Equals(l.Flight, code, StringComparison.Ordinal)).ToArray();
        var adopted = new FlightMoved
        {
            From = from,
            AfterLeg = legs.Where(l => l.State == LegState.Done).OrderByDescending(l => l.Order).FirstOrDefault()?.Name,
            At = time.GetUtcNow(),
            Adopted = true,
        };
        var taken = sent with
        {
            Repo = Path.GetFileName(repoPath),
            RepoPath = repoPath,
            Branch = plane.Branch,
            Worktree = plane.Worktree,
            Base = plane.Base,
            MovedFrom = adopted,
            MovedTo = null,
            MoveFailure = null,
            MoveUnknown = null,
        };
        state.PutFlight(sent.State == FlightState.Held ? Settle(WaitsHere(taken)) : taken);
        foreach (var leg in legs)
        {
            // A leg that was flying there has no worker here: it is planned again, and restarts by the normal advance.
            state.PutLeg(leg.State == LegState.Flying ? Replanned(leg) : leg);
        }

        // The item says it flies here, not there. The adoption comment below says why; LabelAsync's own "created anyway" comment would not be true.
        await LabelAsync(repoPath, sent.Item, code, null, cancellationToken);
        await TakeOffLabelAsync(repoPath, sent, from, cancellationToken);
        await CommentAdoptionAsync(repoPath, taken, from, copy.LastHeard, cancellationToken);

        var way = $"{code} {adopted.Text}";
        state.RaiseCard(CardBin.Handled, AdoptedKind, Verbs.FlightTarget(code), way,
            $"{Machine} adopted {code} from {from}, last heard from at {WhenWords(copy.LastHeard)}, and made its plane at {plane.Worktree} from {plane.Branch} on origin");
        // The title says what happened, the first line what to do and why; the one button holds the flight here while the person looks.
        state.RaiseCard(CardBin.ForYou, AdoptAtRiskKind, Verbs.FlightTarget(code),
            $"{code} was adopted; what {from} had not pushed is not here",
            $"{Machine} made {code}'s plane from {plane.Branch} on origin, as {from} last pushed it. Commits not pushed, files not committed and a leg's work not yet pushed on {from} are not here, and a leg that was flying there starts again here from what is pushed. Look at {from}'s plane when it is back, before it flies {code} again.",
            [new CardChoice { Label = AdoptHoldLabel, Verb = Verbs.Ground, Target = Verbs.FlightTarget(code), Args = [$"held to bring in what {from} had not pushed before it flies on here"] }],
            summary: $"Look at {from}'s plane when it is back and push what it holds, or hold {code} here until you have: its plane was made from what {from} last pushed, so its unpushed work is not in it.");
        return (code, way);
    }

    /// <summary>The adopt-at-risk card's button: the owner's hold on the adopted flight, while what the old node had not pushed is brought in.</summary>
    public const string AdoptHoldLabel = "Hold the flight here";

    /// <summary>The leg as a node that has no worker for it holds it: planned again, with nothing of the attempt that was flying elsewhere.</summary>
    private static LegRow Replanned(LegRow leg) => leg with
    {
        State = LegState.Planned,
        Worker = null,
        WorkerJob = null,
        AttemptModel = null,
        AttemptEffort = null,
        Validator = null,
        ValidatorJob = null,
        TickerJob = null,
        Queued = null,
        Wait = null,
        Recheck = null,
        StartedAt = null,
        StartHead = null,
    };

    /// <summary>Whether a peer this node holds a grant for answers now, naming itself <paramref name="node"/>. One that cannot be asked is not answering.</summary>
    private async Task<bool> AnswersNowAsync(string node, CancellationToken cancellationToken)
    {
        var peers = (Fleet?.Peers() ?? []).Where(p => p.HasGrant).ToList();
        if (peers.Count == 0)
        {
            return false;
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PeerBudget);
        var answers = await Task.WhenAll(peers.Select(async peer =>
        {
            try
            {
                return string.Equals(Learn(peer, await peer.NodeAsync(budget.Token)), node, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or WebSocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }));
        return answers.Any(a => a);
    }

    /// <summary>Takes the old node's label off the item; a refusal is a card, as every other label's is.</summary>
    private async Task TakeOffLabelAsync(string repoPath, FlightRow flight, string from, CancellationToken cancellationToken)
    {
        if (Trackers?.Of(flight.Item.Tracker) is not { } tracker)
        {
            return;
        }
        try
        {
            await tracker.RemoveLabelAsync(repoPath, flight.Issue, PilotMarks.NodeLabel(from), cancellationToken);
        }
        catch (FlightException ex)
        {
            // A line, as every refused node label is (#797): the flight flies on, and the detail says what GitHub answered.
            state.RaiseCard(CardBin.Handled, "node-label-refused", Verbs.FlightTarget(flight.Code),
                $"{PilotMarks.NodeLabel(from)} is still on {flight.Item.Noun} {flight.Item.Display}", ex.Message);
        }
    }

    /// <summary>Says on the issue and on the PR who adopted the flight from whom; a refusal is a card, not a failed adoption.</summary>
    private async Task CommentAdoptionAsync(string repoPath, FlightRow flight, string from, DateTimeOffset lastHeard, CancellationToken cancellationToken)
    {
        var body = $"{Machine} adopted flight {flight.Code} from {from}, which had been silent since {WhenWords(lastHeard)}. Its plane is remade from {flight.Branch} on origin: anything {from} had not pushed is not in it.";
        var failures = new List<string>();
        if (Trackers?.Of(flight.Item.Tracker) is { } tracker)
        {
            try
            {
                await tracker.CommentAsync(repoPath, flight.Issue, body, cancellationToken);
            }
            catch (FlightException ex)
            {
                failures.Add($"{flight.Item.Noun}: {LegCommander.OneLine(ex.Message)}");
            }
        }
        if (flight.PullRequest is { } pr && github is not null)
        {
            try
            {
                await github.CommentIssueAsync(repoPath, pr, body, cancellationToken);
            }
            catch (FlightException ex)
            {
                failures.Add($"PR #{pr}: {LegCommander.OneLine(ex.Message)}");
            }
        }
        if (failures.Count > 0)
        {
            // A line, not a ring: the adoption stands, and the detail holds the words to post by hand if they are wanted there.
            state.RaiseCard(CardBin.Handled, AdoptCommentRefusedKind, Verbs.FlightTarget(flight.Code),
                $"{flight.Code} was adopted, but {string.Join(" and ", failures.Select(f => f.Split(':')[0]))} was not told",
                $"{string.Join("\n", failures)}\nThe comment, to post by hand: {body}");
        }
    }

    private static string WhenWords(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string DurationWords(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{Math.Max(0, (int)span.TotalMinutes)} min";
}
