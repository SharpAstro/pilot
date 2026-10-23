using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Keeps each open PR-ready card's Merge choice saying where its PR's checks stand (green, amber, red on every client),
/// read from GitHub every <see cref="Interval"/>. The merge stays the controller's gate: this says how ready it is, it
/// decides nothing. A PR merged on GitHub itself (a person, another tool) is noticed too: its card closes and its flight
/// lands, as a merge through the card would; one closed without a merge closes its card.
/// </summary>
public sealed class PrChecks(NodeState state, IGitHub github, TimeProvider time, FlightCommander? flights = null) : BackgroundService
{
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
            if (await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken) is { } pr && pr.Number == number
                && pr.State is "MERGED" or "CLOSED")
            {
                var merged = pr.State == "MERGED";
                state.CloseCard(card.Id, merged ? $"merged on GitHub as #{number}" : $"#{number} closed on GitHub without a merge", "github");
                if (merged && flights is not null)
                {
                    await flights.LandAfterMergeAsync(flight.Code, cancellationToken);
                }
                continue;
            }
            var status = await github.ChecksAsync(flight.RepoPath, number, cancellationToken);
            var choices = card.Choices.Select(c => c.Verb == Verbs.Merge ? c with { Status = status } : c).ToList();
            state.UpdateChoices(card.Id, choices);
        }
    }
}
