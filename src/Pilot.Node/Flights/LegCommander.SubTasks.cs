using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A flight's legs kept in step with its work item's sub-tasks, for the repos that opt in (<c>sub-task-repos</c>): a planned leg gets a
/// sub-task, a leg done ticks it, a leg removed closes it. The node is the authority and the sync is one way; a tracker that refuses
/// never fails the verb: it is logged, and tried again at the next change.
/// </summary>
public sealed partial class LegCommander
{
    // One sync at a time, so two verbs in quick succession never both write a sub-task for the same leg.
    private readonly SemaphoreSlim _subTaskSync = new(1, 1);

    private const int SubTaskTitleLength = 200;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>A leg's sub-task title: the flight, the leg's name and its goal, on one line. The flight's code leads, so a leg's own sub-task is told from a person's.</summary>
    internal static string SubTaskTitle(string flight, LegRow leg)
    {
        var title = $"{flight}/{leg.Name}: {Whitespace().Replace(leg.Goal, " ").Trim()}";
        return title.Length <= SubTaskTitleLength ? title : title[..(SubTaskTitleLength - 3)] + "...";
    }

    /// <summary>
    /// Brings the work item's sub-tasks in step with <paramref name="code"/>'s legs, after a verb changed them: <paramref name="removed"/> (a
    /// leg taken out) is closed, a leg without a sub-task gets one (the leg <paramref name="finished"/> too, so its tick is recorded), a
    /// done leg's is marked done, and the item's sub-tasks that are no leg's are kept on the flight row. Not under the lock: the tracker takes as long as it takes.
    /// </summary>
    private async Task SyncSubTasksAsync(string code, SubTask? removed, string? finished, CancellationToken cancellationToken)
    {
        if (state.Flights().FirstOrDefault(f => f.Code == code) is not { } flight || flight.Issue <= 0 || !Models.SubTasks(flight.Repo)
            || flights.Trackers?.Of(flight.Tracker) is not { } tracker)
        {
            return;
        }
        await _subTaskSync.WaitAsync(cancellationToken);
        try
        {
            if (removed is not null)
            {
                await TryAsync(flight, $"close the sub-task {removed.Id} of a removed leg", () => tracker.RemoveSubTaskAsync(flight.RepoPath, flight.Issue, removed, cancellationToken));
            }

            foreach (var leg in state.Legs(code).Where(l => l.SubTask is null && (l.State != LegState.Done || l.Name == finished)))
            {
                SubTask? made = null;
                if (!await TryAsync(flight, $"add a sub-task for {leg.Name}", async () => made = await tracker.AddSubTaskAsync(flight.RepoPath, flight.Issue, SubTaskTitle(code, leg), cancellationToken)))
                {
                    // A tracker that refuses one refuses the rest: they are tried again at the next change.
                    break;
                }
                await _one.WaitAsync(cancellationToken);
                try
                {
                    // The leg as it is now: a worker may have ticked a step while the tracker was asked. A leg taken out meanwhile has no row to write to.
                    if (state.Legs(code).FirstOrDefault(l => l.Name == leg.Name) is { } now)
                    {
                        state.PutLeg(now with { SubTask = made });
                    }
                }
                finally
                {
                    _one.Release();
                }
            }

            SubTaskItem[] items = [];
            await TryAsync(flight, "read its sub-tasks", async () => items = await tracker.SubTasksAsync(flight.RepoPath, flight.Issue, cancellationToken));
            var legs = state.Legs(code);
            // A read that gave nothing may be one that failed, so the list is trusted only when it has something in it: then a done leg's sub-task not done yet is marked; else only the leg just finished.
            foreach (var leg in legs.Where(l => l.State == LegState.Done && l.SubTask is not null))
            {
                var listed = items.FirstOrDefault(i => i.Id == leg.SubTask!.Id);
                if (items.Length > 0 ? listed is { Done: false } : leg.Name == finished)
                {
                    await TryAsync(flight, $"mark the sub-task of {leg.Name} done", () => tracker.CompleteSubTaskAsync(flight.RepoPath, flight.Issue, leg.SubTask!, cancellationToken));
                }
            }

            if (items.Length > 0 || !legs.Any(l => l.SubTask is not null))
            {
                var ours = legs.Select(l => l.SubTask?.Id).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
                var prefix = code + "/";
                // A removed leg's sub-issue stays listed (closed), and is told from a person's by the flight's code its title leads with.
                SubTaskItem[] others = [.. items.Where(i => !ours.Contains(i.Id) && !i.Title.StartsWith(prefix, StringComparison.Ordinal))];
                state.UpdateFlight(code, f => f with { OtherSubTasks = others });
            }
        }
        finally
        {
            _subTaskSync.Release();
        }
    }

    // A tracker call whose refusal is written to node.log and goes no further; false when it was refused.
    private async Task<bool> TryAsync(FlightRow flight, string what, Func<Task> call)
    {
        try
        {
            await call();
            return true;
        }
        catch (FlightException ex)
        {
            Error.WriteLine($"sub-tasks of {flight.Code}: could not {what} in {flight.Item.Display}: {ex.Message}");
            return false;
        }
    }
}
