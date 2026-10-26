using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>One step of a leg's checklist, ticked or not.</summary>
internal sealed record StepLine(string Text, bool Done);

/// <summary>
/// One artifact a leg's sessions recorded, in words, with what opening it means: a URL opens the browser, a path opens the
/// file (its viewer) and its folder.
/// </summary>
internal sealed record ArtifactLine(string Title, string Location, string Leg, string When, bool IsUrl, string? Folder);

/// <summary>One leg as the flight's page shows it, every field already in words.</summary>
internal sealed record LegDetail
{
    public required LegRow Row { get; init; }

    public required string Name { get; init; }

    public required int Order { get; init; }

    /// <summary>planned, holding (for a package, said in full by the report), flying, done, failed.</summary>
    public required string State { get; init; }

    /// <summary>Its go-arounds when it had any (<c>go-around 2</c>); null otherwise.</summary>
    public string? GoArounds { get; init; }

    public required string Goal { get; init; }

    public required string DoneWhen { get; init; }

    /// <summary>The command the node reruns to take the leg as done; null when the Done-when is judged by reading.</summary>
    public string? Check { get; init; }

    public required IReadOnlyList<StepLine> Steps { get; init; }

    /// <summary><c>2/3 steps</c>, or null for a leg with no checklist.</summary>
    public string? StepsText { get; init; }

    /// <summary>What the last finish or check said; null before there is one.</summary>
    public string? Report { get; init; }

    /// <summary>Each artifact at its latest, newest first.</summary>
    public required IReadOnlyList<ArtifactLine> Artifacts { get; init; }

    public static LegDetail From(LegRow leg, DateTimeOffset now) => new()
    {
        Row = leg,
        Name = leg.Name,
        Order = leg.Order,
        State = leg.Hold is not null ? "holding" : leg.Wait is { } wait && leg.State == LegState.Flying ? FlightWaiting.WaitText(wait) : leg.State.ToString().ToLowerInvariant(),
        GoArounds = leg.GoArounds > 0 ? $"go-around {leg.GoArounds}" : null,
        Goal = leg.Goal,
        DoneWhen = leg.DoneWhen,
        Check = string.IsNullOrWhiteSpace(leg.Check) ? null : leg.Check,
        Steps = [.. leg.Checklist.Select(i => new StepLine(i.Text, i.Done))],
        StepsText = leg.Checklist.Length == 0 ? null : $"{leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps",
        Report = string.IsNullOrWhiteSpace(leg.Report) ? null : leg.Report.Trim(),
        Artifacts = Latest(leg, now),
    };

    /// <summary>
    /// A title recorded again replaces the earlier one on the node, so each is already at its latest; the newest of any
    /// that arrive twice is kept all the same, so a page never lists a title's old version beside its new one.
    /// </summary>
    private static IReadOnlyList<ArtifactLine> Latest(LegRow leg, DateTimeOffset now) =>
    [
        .. leg.Artifacts
            .GroupBy(a => a.Title, StringComparer.Ordinal)
            .Select(g => g.MaxBy(a => a.At)!)
            .OrderByDescending(a => a.At)
            .Select(a =>
            {
                var isUrl = Uri.TryCreate(a.Location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                return new ArtifactLine(a.Title, a.Location, leg.Name, SessionLine.Ago(now - a.At), isUrl, isUrl ? null : FolderOf(a.Location));
            }),
    ];

    private static string? FolderOf(string path)
    {
        // Split on both slash kinds by hand: the path comes from the node's machine, and Path.GetDirectoryName only
        // knows this OS's separators, so a Windows path would have no folder on Linux.
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut <= 0 ? null : trimmed[..cut];
    }
}

/// <summary>
/// A flight as its own page shows it: every leg in its order with its state, Done-when, checklist, go-arounds and the
/// artifacts its sessions recorded (each at its latest), what the flight's row has no room for in the Flights table.
/// </summary>
internal sealed record FlightDetail
{
    public required FlightLine Line { get; init; }

    public required IReadOnlyList<LegDetail> Legs { get; init; }

    public static FlightDetail From(FlightLine line, DateTimeOffset now) => new()
    {
        Line = line,
        Legs = [.. line.LegRows.OrderBy(l => l.Order).Select(l => LegDetail.From(l, now))],
    };

    /// <summary>The page as a dump writes it, one line per leg, step and artifact.</summary>
    public string ToDump()
    {
        var lines = new List<string> { Line.ToDumpLine() };
        foreach (var leg in Legs)
        {
            lines.Add($"{leg.Order}. {leg.Name} | {string.Join(" · ", new[] { leg.State, leg.StepsText, leg.GoArounds }.OfType<string>())}");
            lines.Add($"   goal: {leg.Goal}");
            lines.Add($"   done when: {leg.DoneWhen}");
            lines.AddRange(leg.Steps.Select(s => $"   [{(s.Done ? "x" : " ")}] {s.Text}"));
            lines.AddRange(leg.Artifacts.Select(a => $"   artifact {a.Title}: {a.Location}"));
        }
        return string.Join('\n', lines);
    }
}
