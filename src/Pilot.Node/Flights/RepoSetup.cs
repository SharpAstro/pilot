using System.Collections.Concurrent;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The once-per-repo step that makes sure what the node reads from people exists: the org's Priority issue field (made only when the
/// org has none, never renamed or added to), the labels it reads (<see cref="PilotMarks.ReadFromPeople"/>; <c>pilot: priority</c> only
/// where there is no field), and, where the field exists, the end of the <c>pilot: priority</c> label once no open issue carries it.
/// A refusal is a Handled card and never loses the flight; the repo is set up again at the next create. One Handled card per repo per run
/// says what was made and what was there.
/// </summary>
public sealed class RepoSetup(NodeState state)
{
    internal const string ReportKind = "repo-setup";

    private readonly ConcurrentDictionary<string, bool> _done = new(StringComparer.OrdinalIgnoreCase);

    public async Task EnsureAsync(ITracker tracker, string repoPath, string code, CancellationToken cancellationToken)
    {
        if (_done.ContainsKey(repoPath))
        {
            return;
        }
        List<string> made = [];
        List<string> had = [];
        var clean = true;

        var hasField = false;
        try
        {
            switch (await tracker.EnsurePriorityFieldAsync(repoPath, cancellationToken))
            {
                case true:
                    hasField = true;
                    made.Add($"the {GhCli.PriorityFieldName} field");
                    break;
                case false:
                    hasField = true;
                    had.Add($"the {GhCli.PriorityFieldName} field");
                    break;
            }
        }
        catch (FlightException ex)
        {
            clean = false;
            state.RaiseCard(CardBin.Handled, "node-label-refused", $"flight/{code}",
                $"The {GhCli.PriorityFieldName} field could not be made for the repo of {code}", $"The {PilotMarks.Priority} label stands in: {ex.Message}");
        }

        var existing = await tracker.RepoLabelsAsync(repoPath, cancellationToken);
        var labelsFailed = false;
        foreach (var label in PilotMarks.ReadFromPeople.Where(l => !(hasField && l == PilotMarks.Priority)))
        {
            if (existing.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                had.Add($"the label {label}");
                continue;
            }
            try
            {
                await tracker.EnsureLabelAsync(repoPath, label, cancellationToken);
                made.Add($"the label {label}");
            }
            catch (FlightException ex)
            {
                clean = false;
                labelsFailed = true;
                state.RaiseCard(CardBin.Handled, "node-label-refused", $"flight/{code}", $"The label {label} could not be made in the repo of {code}", ex.Message);
                break;
            }
        }

        if (hasField && !labelsFailed && existing.Contains(PilotMarks.Priority, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var open = await tracker.OpenWithLabelAsync(repoPath, PilotMarks.Priority, cancellationToken);
                if (open == 0)
                {
                    await tracker.DeleteRepoLabelAsync(repoPath, PilotMarks.Priority, cancellationToken);
                    made.Add($"the label {PilotMarks.Priority} deleted (the field replaces it)");
                }
                else
                {
                    had.Add($"the label {PilotMarks.Priority}, kept while {open} open issue(s) carry it");
                }
            }
            catch (FlightException ex)
            {
                clean = false;
                state.RaiseCard(CardBin.Handled, "node-label-refused", $"flight/{code}", $"The label {PilotMarks.Priority} could not be deleted in the repo of {code}", ex.Message);
            }
        }

        // A clean run is the repo's for good; one that was refused is tried again at the next create.
        if (clean)
        {
            _done[repoPath] = true;
        }
        if (made.Count > 0 || had.Count > 0)
        {
            var title = $"Repo setup for {code}: made {(made.Count > 0 ? string.Join(", ", made) : "nothing")}; already there {(had.Count > 0 ? string.Join(", ", had) : "nothing")}";
            state.RaiseCard(CardBin.Handled, ReportKind, $"flight/{code}", title, title);
        }
    }
}
