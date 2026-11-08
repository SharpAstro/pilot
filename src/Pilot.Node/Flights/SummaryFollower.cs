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
                await StepAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
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
            summaries.SetNotFollowing(null);
            return;
        }
        if (now - _changedAt < Quiet)
        {
            return;
        }
        if (summaries.WatchtowerDown is { } down)
        {
            summaries.SetNotFollowing($"the watchtower is down ({down})");
            return;
        }
        if (_lastAsk is { } last && now - last < MinGap || summaries.IsMaking(SummaryRow.FleetScope))
        {
            return;
        }

        _lastAsk = now;
        var (_, error) = await summaries.MakeAsync(SummaryRow.FleetScope, cancellationToken, watchtowerOnly: true);
        if (error is not null)
        {
            summaries.SetNotFollowing(error);
        }
    }
}
