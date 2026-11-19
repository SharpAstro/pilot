using DIR.Lib.Notifications;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What a pilot event is about; the window opens that on a click.</summary>
internal enum PilotEventKind
{
    JobEnded,
    FlightLanded,
    DesignPublished,
}

/// <summary>One thing that happened on a node: a fading toast's words and what a click on it opens.</summary>
internal sealed record PilotEvent(PilotEventKind Kind, string Subject, string Title, string Text, NotificationSeverity Severity, string? Node);

/// <summary>
/// Derives pilot's events from the changes between one snapshot of the pages and the next (PIL1184): a job that ended, a flight that
/// landed, a design version published. Only pilot says which changes are events; the toast frames, timing and placement are DIR.Lib's.
/// The first look at each kind of page only learns the state (what is already there is not news), and a thing first seen already
/// finished is no event either: it ended before anyone was watching. Not thread-safe; called from the render thread.
/// </summary>
internal sealed class PilotEventWatch
{
    private readonly Dictionary<(string? Node, string Id), JobState> _jobs = [];
    private readonly Dictionary<(string? Node, string Code), FlightState> _flights = [];
    private readonly Dictionary<string, int> _designs = [];
    private bool _jobsKnown, _flightsKnown, _designsKnown;

    /// <summary>The events since the last call; a page that is null (not read yet) is skipped and keeps what was known.</summary>
    public IReadOnlyList<PilotEvent> Observe(JobsScreen? jobs, FlightsScreen? flights, DesignsScreen? designs)
    {
        var events = new List<PilotEvent>();
        if (jobs is not null)
        {
            ObserveJobs(jobs, events);
        }
        if (flights is not null)
        {
            ObserveFlights(flights, events);
        }
        if (designs is not null)
        {
            ObserveDesigns(designs, events);
        }
        return events;
    }

    private void ObserveJobs(JobsScreen jobs, List<PilotEvent> events)
    {
        foreach (var line in jobs.Lines)
        {
            var key = (line.Node?.Name, line.Row.Id);
            var now = line.Row.State;
            var known = _jobs.TryGetValue(key, out var before);
            _jobs[key] = now;
            if (!_jobsKnown || !known || before is JobState.Succeeded or JobState.Failed or JobState.Cancelled
                || now is JobState.Running or JobState.Queued)
            {
                continue;
            }
            var severity = now switch
            {
                JobState.Succeeded => NotificationSeverity.Success,
                JobState.Failed => NotificationSeverity.Error,
                _ => NotificationSeverity.Info,
            };
            events.Add(new PilotEvent(PilotEventKind.JobEnded, line.Id, $"Job {line.Id} {line.State}", line.StateDetail is { Length: > 0 } detail ? $"{line.Command}: {detail}" : line.Command, severity, line.Node?.Name));
        }
        _jobsKnown = true;
    }

    private void ObserveFlights(FlightsScreen flights, List<PilotEvent> events)
    {
        foreach (var line in flights.Lines)
        {
            var key = (line.Node?.Name, line.Code);
            var now = line.Row.State;
            var known = _flights.TryGetValue(key, out var before);
            _flights[key] = now;
            if (_flightsKnown && known && before != FlightState.Landed && now == FlightState.Landed)
            {
                events.Add(new PilotEvent(PilotEventKind.FlightLanded, line.Code, $"Flight {line.Code} landed", line.Title, NotificationSeverity.Success, line.Node?.Name));
            }
        }
        _flightsKnown = true;
    }

    private void ObserveDesigns(DesignsScreen designs, List<PilotEvent> events)
    {
        foreach (var row in designs.Rows)
        {
            var versions = row.Versions ?? 0;
            var known = _designs.TryGetValue(row.Id, out var before);
            _designs[row.Id] = versions;
            if (_designsKnown && known && versions > before)
            {
                events.Add(new PilotEvent(PilotEventKind.DesignPublished, row.Id, $"{row.Id} v{versions} published", row.Title ?? row.Why ?? "", NotificationSeverity.Info, null));
            }
        }
        _designsKnown = true;
    }
}
