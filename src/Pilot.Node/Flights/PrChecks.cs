using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Keeps each open PR-ready card's Merge choice saying where its PR's checks stand (green, amber, red on every client),
/// read from GitHub every <see cref="Interval"/>. The merge stays the controller's gate: this says how ready it is, it
/// decides nothing. A PR merged on GitHub itself (a person, another tool) is noticed too: its card closes and its flight
/// lands, as a merge through the card would; one closed without a merge closes its card.
/// </summary>
public sealed class PrChecks(NodeState state, IGitHub github, TimeProvider time, FlightCommander? flights = null, LegCommander? legs = null) : BackgroundService
{
    // A PR head the node has tried to rebase: tried once, not every look.
    private readonly HashSet<string> _rebased = new(StringComparer.Ordinal);

    // A PR head whose failed checks were sent back as changes: once, not every look.
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await LookAsync(stopping);
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"pr checks: one look failed, looking again: {ex.Message}");
                await Task.Delay(Interval, time, stopping);
            }
        }
    }

    /// <summary>One look at every open PR-ready card's PR.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        foreach (var card in state.Cards(openOnly: true).Where(c => c.Kind == LegCommander.PrReady))
        {
            var code = card.Subject.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) ? card.Subject[Verbs.FlightTargetPrefix.Length..] : null;
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { PullRequest: { } number } flight)
            {
                continue;
            }
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            if (pr is not null && pr.Number == number && pr.State is "MERGED" or "CLOSED")
            {
                var merged = pr.State == "MERGED";
                state.CloseCard(card.Id, merged ? $"merged on GitHub as #{number}" : $"#{number} closed on GitHub without a merge", "github");
                if (merged && flights is not null)
                {
                    try
                    {
                        await flights.LandAfterMergeAsync(flight.Code, pr.Head, cancellationToken);
                    }
                    catch (FlightException ex)
                    {
                        // Merged, but something of the flight's would be left behind: said on a ring, never left in a log.
                        state.RaiseCard(CardBin.Ring, "not-landed", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} merged on GitHub, but the flight did not land", ex.Message);
                    }
                }
                continue;
            }
            // Reviewed and passed, but no longer merging into its base: main moved. Rebased by itself, once a head, and
            // reviewed afresh; a rebase that conflicts rings with what to do.
            if (legs is not null && pr is { Mergeable: "CONFLICTING" } && pr.Number == number && _rebased.Add($"{flight.Code} {pr.Head}"))
            {
                if (await legs.RebaseOntoBaseAsync(flight.Code, cancellationToken) is { } why)
                {
                    state.RaiseCard(CardBin.Ring, "pr-conflicts", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} conflicts with its base", why, link: pr.Url);
                }
                continue;
            }
            var status = await github.ChecksAsync(flight.RepoPath, number, cancellationToken);
            // Reviewed and passed, but its checks failed: sent back as changes, once a head, so the leg goes around.
            if (legs is not null && status == ChoiceStatus.Failing && pr is { } failed && failed.Number == number && _failed.Add($"{flight.Code} {failed.Head}"))
            {
                if (await legs.ChecksFailedAsync(flight.Code, failed.Head, cancellationToken) is { } why)
                {
                    state.RaiseCard(CardBin.Ring, "pr-checks-failed", Verbs.FlightTarget(flight.Code), $"{flight.Code}'s PR #{number} fails its checks", why, link: failed.Url);
                }
                continue;
            }
            var choices = card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = status } : c).ToList();
            state.UpdateChoices(card.Id, choices);
        }
    }
}
