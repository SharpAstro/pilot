using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A leg held for a package: its restore failed NU1101 or NU1102 on a package a connected flight or a sibling's merged release
/// is publishing, which is the sibling's publish not done yet and no fault of the leg. It goes back to planned with its
/// <see cref="LegRow.Hold"/>, its worker stopped and nobody rung; <see cref="PackageWatch"/> looks for the package on the
/// first leg's backoff and one card says when it is late, and <see cref="ResumeHeldAsync"/> starts the leg afresh once
/// nuget.org serves it. Planned rather than a state of its own, so every reader that already knows a planned leg waits (the
/// plan, the briefs, the merge's open-legs check) is right without a change, and a hold is the one field that says why.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The note a hold leaves, and the one a resume does.</summary>
    public const string HoldKind = "leg-holding";

    /// <summary>What says whether a package a restore missed is being published; null in a test that holds nothing.</summary>
    public PackageReleases? Releases { get; set; }

    /// <summary>
    /// Holds <paramref name="leg"/> when <paramref name="text"/> (a failed report, a failed check's output) names a restore
    /// miss of a package being published: true when it did. The caller stops the worker.
    /// </summary>
    private async Task<bool> TryHoldAsync(FlightRow flight, LegRow leg, string text, CancellationToken cancellationToken)
    {
        if (Releases is null)
        {
            return false;
        }
        foreach (var miss in RestoreMisses.In(text))
        {
            if (await Releases.PublishingAsync(flight, miss, state.Flights(), cancellationToken) is not { } wait)
            {
                continue;
            }
            var say = FlightWaiting.HoldText(wait);
            var target = Verbs.LegTarget(flight.Code, leg.Name);
            state.PutLeg(leg with { State = LegState.Planned, Hold = wait, Wait = null, WorkerJob = null, FinishedAt = null, Report = $"{say}\n{miss.Line}" });
            state.CloseCardsAbout(target, $"{flight.Code}/{leg.Name} holds for a package", NoGoKind, ValidatingKind);
            state.RaiseCard(CardBin.Handled, HoldKind, target, $"{flight.Code}/{leg.Name} {say}",
                $"{miss.Line}\n{wait.Producer} is publishing it; the leg starts again, with a fresh worker, once nuget.org serves it.");
            return true;
        }
        return false;
    }

    /// <summary>
    /// The held leg's package is up: the hold is cleared and the leg starts afresh (the autopilot's start, so the 5-hour window
    /// still holds it and a flight that does not advance by itself is offered it on a card). <paramref name="version"/> is the
    /// one nuget.org serves.
    /// </summary>
    public async Task ResumeHeldAsync(string flightCode, string legName, string version, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        FlightRow flight;
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == flightCode) is not { } found
                || state.Legs(flightCode).FirstOrDefault(l => l.Name == legName) is not { Hold: { } hold } leg || leg.State != LegState.Planned)
            {
                return;
            }
            flight = found;
            var note = $"resumed: {hold.Id} {version} is up";
            var target = Verbs.LegTarget(flightCode, legName);
            state.PutLeg(leg with { Hold = null, Report = note });
            state.CloseCardsAbout(target, note, HoldKind, PackageWatch.LateKind);
            state.RaiseCard(CardBin.Handled, HoldKind, target, $"{flightCode}/{legName} {note}");
        }
        finally
        {
            _one.Release();
        }
        await AdvanceAsync(flight, cancellationToken);
    }

    /// <summary>Marks the hold of <paramref name="legName"/> as said to be late, so no second card is raised for it.</summary>
    public async Task MarkHoldLateAsync(string flightCode, string legName, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (state.Legs(flightCode).FirstOrDefault(l => l.Name == legName) is { Hold: { } hold } leg)
            {
                state.PutLeg(leg with { Hold = hold with { Escalated = true } });
            }
        }
        finally
        {
            _one.Release();
        }
    }
}
