using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class LegCommander
{
    /// <summary>The Handled card of a flight whose PR is rebase-only while a leg flies (pilot #1299).</summary>
    public const string RebaseOnlyKind = "rebase-only";

    /// <summary>What the flight and its card say while a leg flies on a rebase-only PR.</summary>
    public const string RebaseOnlySaid = "Conflict (rebase only): known, lands squashed / laid as one commit at landing";

    /// <summary>
    /// A PR that is mergeable but not rebaseable on a base that merges by rebase (pilot #1299). While a leg flies on the plane
    /// (<paramref name="legFlying"/>) nothing is pushed: the flight records <see cref="RebaseOnlySaid"/> and one Handled card says why. With
    /// none flying, the branch is reshaped now (<see cref="ShapeForMergeAsync"/>: rebased onto the base, else laid as one commit, then
    /// lease-pushed with its review carried), and the note is cleared. A base that does not merge by rebase is left alone. A shape that fails
    /// rings once, as it does at landing (<see cref="LayFailedKind"/>), and throws.
    /// </summary>
    public async Task ReshapeRebaseOnlyAsync(string code, string head, bool legFlying, CancellationToken cancellationToken)
    {
        var call = await EnterAsync(code, "being reshaped for its base's rebase merge", cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { State: FlightState.Flying } flight || OnHoldRefusal(flight) is not null)
            {
                return;
            }
            if (legFlying)
            {
                var subject = Verbs.FlightTarget(code);
                Record(code, f => f with { RebaseOnly = RebaseOnlySaid });
                if (!state.Cards(openOnly: true).Any(c => c.Kind == RebaseOnlyKind && c.Subject == subject))
                {
                    state.RaiseCard(CardBin.Handled, RebaseOnlyKind, subject, $"{code}'s PR: {RebaseOnlySaid}",
                        $"A merge of {flight.Base} that resolved a conflict cannot be replayed by rebase, and {flight.Base} merges by rebase. A leg is flying on the plane, so nothing is pushed now: the PR lands squashed, or is laid as one commit at landing. When the leg ends the node reshapes the branch itself.");
                }
                return;
            }
            if (!Directory.Exists(flight.Worktree))
            {
                return;
            }
            var reshaped = false;
            try
            {
                var shaped = await ShapeForMergeAsync(flight, head, Models.MergeMethod, cancellationToken);
                reshaped = !string.Equals(shaped, head, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                // A shape that threw has rung for itself (LayFailedKind, or a review not carried); "known, lands squashed" is no longer what the flight says.
                if (flight.RebaseOnly is not null || reshaped)
                {
                    Record(code, f => f with { RebaseOnly = null });
                    state.CloseCardsAbout(Verbs.FlightTarget(code), $"{code} was looked at again for {flight.Base}", RebaseOnlyKind);
                }
            }
        }
        finally
        {
            call.Dispose();
            _one.Release();
        }
    }
}
