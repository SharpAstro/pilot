using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The short brief a Home chip shows on hover (#258): a few lines on what the flight, session or job is, built from the
/// line the feed already holds, so it makes no call and asks no model. One builder per kind, so Flights, Sessions and
/// Background tasks say the same words wherever the same thing appears.
/// </summary>
/// <remarks>
/// Pure: a line in, lines out. A brief is at most <see cref="MaxLines"/> lines. A long line is left whole for
/// <see cref="TooltipLayer{TSurface}.Lines"/> to wrap and break; only a title, which has no end of its own, is cut, and ends
/// in an ellipsis.
/// </remarks>
internal static class ChipBrief
{
    /// <summary>The most lines a brief has.</summary>
    public const int MaxLines = 4;

    /// <summary>
    /// The longest a title runs before it ends in an ellipsis: two of the tooltip's wrapped lines, so a long title cannot
    /// push the rest of the brief off the tooltip.
    /// </summary>
    public const int MaxTitle = 110;

    /// <summary>A brief's lines as one text, one per line, which is how the tooltip is asked for them.</summary>
    public static string Text(IReadOnlyList<string> lines) => string.Join('\n', lines);

    /// <summary>
    /// A flight: its title (the issue when it has none), where it is (its state and what holds it), the leg flying, held or
    /// failed, and its PR and review once it has them.
    /// </summary>
    public static IReadOnlyList<string> Flight(FlightLine line)
    {
        var flight = line.Row;
        var lines = new List<string>
        {
            Title(line.Title),
            // A stale flight is its last known row, and its brief says so, as its chip's hover did before it had a brief.
            line.Stale is { } stale ? $"{line.Code} was {line.State} when {stale} last answered"
                : line.StateDetail is { Length: > 0 } detail ? $"{line.Code} · {line.State}: {detail}" : $"{line.Code} · {line.State}",
            LegLine(line),
        };
        // A failed move's reason and where a moved flight came from: what the chip's hover said before it had a brief.
        if (line.MoveFailure is { Length: > 0 } moveFailure)
        {
            lines.Add(moveFailure);
        }
        if (line.Provenance is { Length: > 0 } provenance)
        {
            lines.Add(provenance);
        }
        if (flight.PullRequest is { } pr)
        {
            var review = FlightMarks.From(flight).Line ?? (flight.Review is { } r ? $"review {r.State.ToString().ToLowerInvariant()}" : "no review yet");
            lines.Add($"PR #{pr} · {review}");
        }
        return Cap(lines);
    }

    // The leg that matters first: a failed one, the flying one, one held for a package, else the first not done.
    private static string LegLine(FlightLine line)
    {
        var legs = line.LegRows;
        // A failed, flying or held leg is already named at the end of line.Legs (FlightsView.LegsText), so it is said once, there.
        if (legs.Any(l => l.State == LegState.Failed || l.State == LegState.Flying || l.Hold is not null))
        {
            return line.Legs;
        }
        // LegsText leaves the first leg not done out: it is the only one this adds.
        return legs.FirstOrDefault(l => l.State != LegState.Done) is { } next
            ? $"leg {next.Name} {FlightWaiting.LegState(next)} · {line.Legs}"
            : line.Legs;
    }

    /// <summary>
    /// A session: its role, and the flight and leg it works for; then what it is doing: the tool it asks to run, or the
    /// question it waits on you with, or how long it has been idle or working.
    /// </summary>
    public static IReadOnlyList<string> Session(SessionLine line)
    {
        var lines = new List<string> { $"{line.Role} · {line.State}" };
        if (line.Asking is { Length: > 0 } asking)
        {
            lines.Add($"asks to run {asking}");
        }
        if (line.WaitingFor is { Length: > 0 } waitingFor)
        {
            // Line 1 already says "waiting on you" for a waiting session, so this is only what it waits on.
            lines.Add(waitingFor);
        }
        else if (line.IsWaiting && line.Asking is null)
        {
            lines.Add("waiting on you");
        }
        if (line.Gone)
        {
            lines.Add("its process is gone: nothing can answer it");
        }
        else if (line.Row.State is SessionState.Idle or SessionState.Working)
        {
            lines.Add(line.LastResponse == "none yet"
                ? $"{line.State}, no response yet"
                : $"{line.State}: last response {line.LastResponse}");
        }
        else if (line.Row.State is SessionState.Ended or SessionState.Failed)
        {
            lines.Add($"last response {line.LastResponse}");
        }
        return Cap(lines);
    }

    /// <summary>
    /// The Sessions page's row hover: the session's whole name, which its column may cut with an ellipsis (the chip has the name in
    /// its own text, so its brief does not carry it), then the brief.
    /// </summary>
    public static string SessionRow(SessionLine line) => Text([line.ShownName, .. Session(line)]);

    /// <summary>A job: what it runs, whom it is for, and how long it has been queued or running, with its step.</summary>
    public static IReadOnlyList<string> Job(JobLine line)
    {
        var lines = new List<string>
        {
            line.Command,
            $"for {line.For} · {line.Id}",
            line.Row.State switch
            {
                JobState.Queued => $"queued {line.Started}",
                JobState.Running => line.Row.Pid is { } pid
                    ? $"running for {line.Took}, started {line.Started} · pid {pid}"
                    : $"running for {line.Took}, started {line.Started}",
                _ => line.StateDetail is { } how ? $"{line.State}: {how} after {line.Took}" : $"{line.State} after {line.Took}",
            },
        };
        if (line.Live && line.StateDetail is { Length: > 0 } step)
        {
            lines.Add(line.Row.State == JobState.Queued ? $"waits on: {step}" : $"step: {step}");
        }
        return Cap(lines);
    }

    /// <summary>A title in one piece, or cut at <see cref="MaxTitle"/> to end in an ellipsis; its own words wrap in the tooltip.</summary>
    internal static string Title(string title)
    {
        var one = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (one.Length == 0)
        {
            return "(no title)";
        }
        if (one.Length <= MaxTitle)
        {
            return one;
        }
        var cut = one.LastIndexOf(' ', MaxTitle - 1);
        return one[..(cut > MaxTitle / 2 ? cut : MaxTitle - 1)].TrimEnd() + "…";
    }

    private static IReadOnlyList<string> Cap(List<string> lines) => [.. lines.Where(l => l.Length > 0).Take(MaxLines)];
}
