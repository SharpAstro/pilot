using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Keeps the fleet summary current through the watchtower while a client is attached to the node's feed (PIL903). When what the summary
/// reads has changed since the kept row was made, it waits for <see cref="Quiet"/> without a further change, and asks at most once per
/// <see cref="MinGap"/>. It asks the watchtower alone: never the paid one-off call, and never with no client attached. While the watchtower
/// cannot be asked, the kept row says so (<see cref="SummaryRow.NotFollowing"/>). <c>pilot summarise</c> and the GUI's Refresh are not
/// held to any of this: they ask at once.
/// </summary>
public sealed class SummaryFollower(NodeState state, Summaries summaries, TimeProvider time) : BackgroundService
{
    /// <summary>How long what the summary reads must hold still before it is asked again.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    /// <summary>The least time between two asks.</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(3);

    /// <summary>How often it looks at the node for a change: a timer on the node's clock, never a spin.</summary>
    public static readonly TimeSpan Look = TimeSpan.FromSeconds(5);

    /// <summary>The bounds past which work is stale (#736); the defaults unless the node wires its settings.</summary>
    public Func<StaleBounds> Bounds { get; set; } = () => new StaleBounds();

    /// <summary>Where this says what it would have said on stderr: the node's log; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    private long _seenSeq = -1;
    private string? _seenFingerprint;
    private DateTimeOffset _changedAt;
    private DateTimeOffset? _lastAsk;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Look, time, stopping);
                await LookAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One look that outlives anything the step throws (a card check, the model, a state call): the follower runs for the node's
    /// whole lifetime, and a throw that ended it would stop the host, or silently stop the following while the card still says it
    /// follows. The failure goes to the log and the next look goes on.
    /// </summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StepAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await Error.WriteLineAsync($"summary follower: one look failed, looking again: {ex.Message}");
        }
    }

    /// <summary>One look at the node: notes a change, and asks the watchtower when the change has been quiet long enough and the last ask is far enough back.</summary>
    public async Task StepAsync(CancellationToken cancellationToken)
    {
        // Nobody is looking: nothing is asked, and a change made now starts its quiet spell when someone attaches.
        if (state.ClientsAttached == 0)
        {
            _seenSeq = -1;
            return;
        }

        // The row says why it does not follow exactly while the watchtower is down, on every look whether or not a change is waiting:
        // the Refresh button names the old call's price from it, so it must hold while nothing has changed, and clear once it is up.
        var down = summaries.WatchtowerDown;
        summaries.SetNotFollowing(down is null ? null : $"the watchtower is down ({down})");

        var now = time.GetUtcNow();
        // The node's number moves with every change it makes; only then is the fingerprint worth taking.
        if (state.Seq != _seenSeq)
        {
            _seenSeq = state.Seq;
            var fingerprint = SummaryRecords.Fingerprint(state, Bounds());
            if (fingerprint != _seenFingerprint)
            {
                _seenFingerprint = fingerprint;
                _changedAt = now;
            }
        }

        var kept = state.Summaries().Any(s => s.Scope == SummaryRow.FleetScope);
        // Nothing in play and nothing kept: there is no summary to keep current, and none is made for the empty node.
        if (_seenFingerprint is null || _seenFingerprint == summaries.FleetMadeFrom || _seenFingerprint.Length == 0 && !kept)
        {
            return;
        }
        // The watchtower is down: nothing is asked (never the paid call), and the row already says so.
        if (now - _changedAt < Quiet || down is not null)
        {
            return;
        }
        if (_lastAsk is { } last && now - last < MinGap || summaries.IsMaking(SummaryRow.FleetScope))
        {
            return;
        }

        _lastAsk = now;
        // A failed ask with the watchtower up (busy, no answer) is not "not following": the next ask tries again after the gap, and the
        // button's price stays tied to the watchtower being down, which the look above sets.
        var (_, error) = await summaries.MakeAsync(SummaryRow.FleetScope, cancellationToken, watchtowerOnly: true);
        if (error is not null)
        {
            await Error.WriteLineAsync($"summary follower: the watchtower was asked and did not make one: {error}");
        }
    }
}
