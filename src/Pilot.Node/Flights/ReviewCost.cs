using System.Globalization;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>One reviewer session of a flight: which round it was, what it ran on, the head it read, and what it spent.</summary>
/// <param name="Round">1 for the flight's first reviewer session, then one more for each, by when they started.</param>
/// <param name="Head">The commit the round read, from the flight's kept rounds; null when the node did not keep one.</param>
/// <param name="Usage">The four counts summed over the session's transcript and its subagents' files, each message once.</param>
/// <param name="Weighted">The weighted spend, as <see cref="SessionRow.SpendWeighted"/> weighs it.</param>
/// <param name="FromTranscript">False when no transcript was found: <paramref name="Weighted"/> is then the session row's own, and the counts are not known.</param>
public sealed record ReviewCostSession(int Round, string Session, string? Model, string? Head, DateTimeOffset StartedAt, MessageUsage Usage, long Weighted, bool FromTranscript);

/// <summary>A landed flight and what its reviewer sessions spent.</summary>
public sealed record ReviewCostFlight(string Code, string Repo, int? PullRequest, DateTimeOffset LandedAt, IReadOnlyList<ReviewCostSession> Sessions)
{
    public long Input => Sessions.Sum(s => s.Usage.Input);

    public long CacheCreation => Sessions.Sum(s => s.Usage.CacheCreation);

    public long CacheRead => Sessions.Sum(s => s.Usage.CacheRead);

    public long Output => Sessions.Sum(s => s.Usage.Output);

    public long Weighted => Sessions.Sum(s => s.Weighted);
}

/// <summary>What reviews cost over the landed flights of a window: <see cref="ReviewCost"/>'s answer.</summary>
public sealed record ReviewCostReport(IReadOnlyList<ReviewCostFlight> Flights, DateTimeOffset? Since, DateTimeOffset? Until, string? Repo)
{
    public int Sessions => Flights.Sum(f => f.Sessions.Count);

    /// <summary>The flights that had a reviewer session at all: the average is over these.</summary>
    public int Reviewed => Flights.Count(f => f.Sessions.Count > 0);

    public long Input => Flights.Sum(f => f.Input);

    public long CacheCreation => Flights.Sum(f => f.CacheCreation);

    public long CacheRead => Flights.Sum(f => f.CacheRead);

    public long Output => Flights.Sum(f => f.Output);

    public long Weighted => Flights.Sum(f => f.Weighted);

    /// <summary>The report as the lines a terminal or a PR takes.</summary>
    public string Text()
    {
        var text = new StringBuilder();
        text.AppendLine($"Review cost of flights landed {Window()}{(Repo is null ? "" : $" in {Repo}")}: {Flights.Count} {(Flights.Count == 1 ? "flight" : "flights")}, {Sessions} reviewer {(Sessions == 1 ? "session" : "sessions")}");
        foreach (var flight in Flights)
        {
            text.AppendLine();
            text.AppendLine($"{flight.Code}  {flight.Repo}{(flight.PullRequest is { } pr ? $" #{pr}" : "")}  landed {flight.LandedAt.UtcDateTime:yyyy-MM-dd HH:mm}Z  {Plural(flight.Sessions.Count, "reviewer session")}");
            foreach (var s in flight.Sessions)
            {
                text.AppendLine($"  round {s.Round}  {s.Model ?? "?"}  head {(s.Head is { Length: > 8 } h ? h[..8] : s.Head ?? "?")}  {Tokens(s.Usage, s.Weighted)}{(s.FromTranscript ? "" : "  (no transcript: the row's spend only)")}");
            }
            if (flight.Sessions.Count > 1)
            {
                text.AppendLine($"  flight total  {Tokens(flight.Input, flight.CacheCreation, flight.CacheRead, flight.Output, flight.Weighted)}");
            }
        }
        text.AppendLine();
        text.AppendLine($"total  {Tokens(Input, CacheCreation, CacheRead, Output, Weighted)}");
        if (Reviewed > 0)
        {
            text.AppendLine($"average per reviewed flight ({Reviewed})  {Tokens(Input / Reviewed, CacheCreation / Reviewed, CacheRead / Reviewed, Output / Reviewed, Weighted / Reviewed)}  {(double)Sessions / Reviewed:0.0} sessions");
        }
        if (Flights.Count > Reviewed)
        {
            text.AppendLine($"{Flights.Count - Reviewed} landed {(Flights.Count - Reviewed == 1 ? "flight" : "flights")} with no reviewer session on this node (not in the average)");
        }
        return text.ToString();
    }

    private string Window() => (Since, Until) switch
    {
        ({ } a, { } b) => $"{a.UtcDateTime:yyyy-MM-dd} to {b.UtcDateTime.AddDays(-1):yyyy-MM-dd}",
        ({ } a, null) => $"since {a.UtcDateTime:yyyy-MM-dd}",
        (null, { } b) => $"until {b.UtcDateTime.AddDays(-1):yyyy-MM-dd}",
        _ => "ever",
    };

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Tokens(MessageUsage u, long weighted) => Tokens(u.Input, u.CacheCreation, u.CacheRead, u.Output, weighted);

    private static string Tokens(long input, long creation, long read, long output, long weighted) =>
        string.Create(CultureInfo.InvariantCulture, $"in {input:N0}  cache-write {creation:N0}  cache-read {read:N0}  out {output:N0}  weighted {weighted:N0}");
}

/// <summary>
/// What reviews cost (#433): for each landed flight, its reviewer sessions and their tokens, read from the transcripts as
/// <see cref="SessionTranscripts"/> reads them (each message once, subagents included). Works from the rows the node keeps, so the node's
/// verb calls it with its own state and <see cref="ReadStore"/> feeds it a data folder with no node running.
/// </summary>
public static class ReviewCost
{
    /// <summary>
    /// The report for the flights landed in <paramref name="since"/> (inclusive) to <paramref name="until"/> (exclusive) in
    /// <paramref name="repo"/> (a repo's name; any when null). A flight's reviewer sessions are the reviewer-role sessions on it, by
    /// the flight code (or a <c>-&lt;code&gt;-review</c> name), oldest first; the nth is round n, its head from the flight's nth kept round.
    /// </summary>
    public static ReviewCostReport Compute(IEnumerable<FlightRow> flights, IEnumerable<SessionRow> sessions, ClaudeProfile profile, DateTimeOffset? since = null, DateTimeOffset? until = null, string? repo = null)
    {
        var reviewers = sessions.Where(s => s.Role == SessionRole.Reviewer).ToArray();
        var rows = new List<ReviewCostFlight>();
        foreach (var flight in flights.Where(f => f.LandedAt is { } landed && (since is null || landed >= since) && (until is null || landed < until)
            && (repo is null || string.Equals(f.Repo, repo, StringComparison.OrdinalIgnoreCase))).OrderBy(f => f.LandedAt))
        {
            var known = reviewers
                .Where(s => string.Equals(s.Flight, flight.Code, StringComparison.OrdinalIgnoreCase)
                    || s.Flight is null && s.Name.EndsWith($"-{flight.Code}-review", StringComparison.OrdinalIgnoreCase))
                .Select(s => new Candidate(s.SessionId, s.Name, s.Model, s.StartedAt, s.SpendWeighted))
                .ToList();
            // Ended sessions leave the node's rows; their transcripts stay in the flight's worktree folder, titled by their name.
            known.AddRange(Discover(profile, flight).Where(d => !known.Any(k => k.SessionId == d.SessionId)));
            var ofFlight = known.OrderBy(c => c.StartedAt).ToArray();
            var kept = flight.ReviewRounds ?? [];
            var list = new List<ReviewCostSession>();
            for (var i = 0; i < ofFlight.Length; i++)
            {
                var candidate = ofFlight[i];
                var round = i < kept.Length ? kept[i] : null;
                var head = round?.Head ?? (i == ofFlight.Length - 1 ? flight.Review?.ReviewedHead : null);
                var transcripts = new SessionTranscripts(profile, candidate.SessionId);
                var found = transcripts.Locate();
                var model = candidate.Model;
                if (found)
                {
                    var facts = transcripts.Refresh();
                    model ??= facts.Model;
                }
                var usage = found ? transcripts.Usage : default;
                list.Add(new ReviewCostSession(i + 1, candidate.Name, model ?? round?.Model, head, candidate.StartedAt, usage, found ? usage.WeightedTwentieths / 20 : candidate.Spend, found));
            }
            rows.Add(new ReviewCostFlight(flight.Code, flight.Repo, flight.PullRequest, flight.LandedAt!.Value, list));
        }
        return new ReviewCostReport(rows, since, until, repo);
    }

    private sealed record Candidate(string SessionId, string Name, string? Model, DateTimeOffset StartedAt, long Spend);

    /// <summary>
    /// The reviewer sessions of a flight whose transcripts sit in its worktree's project folder: a transcript whose title (the
    /// <c>custom-title</c> line Claude Code writes first) is a <c>-&lt;code&gt;-review</c> name, started when its first stamped line says.
    /// </summary>
    private static IEnumerable<Candidate> Discover(ClaudeProfile profile, FlightRow flight)
    {
        var folder = string.Concat(flight.Worktree.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-'));
        string? project = null;
        try
        {
            project = Directory.EnumerateDirectories(Path.Combine(profile.Root, "projects")).FirstOrDefault(d => string.Equals(Path.GetFileName(d), folder, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (project is null)
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(project, "*.jsonl"))
        {
            string? title = null;
            DateTimeOffset? started = null;
            try
            {
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
                for (var read = 0; read < 200 && (title is null || started is null) && reader.ReadLine() is { } line; read++)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    if (title is null && ClaudeAgentsJson.Text(root, "type") == "custom-title")
                    {
                        title = ClaudeAgentsJson.Text(root, "customTitle");
                    }
                    if (started is null && ClaudeAgentsJson.Text(root, "timestamp") is { } stamp && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                    {
                        started = at;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
            if (title is not null && title.EndsWith($"-{flight.Code}-review", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Candidate(Path.GetFileNameWithoutExtension(file), title, null, started ?? new DateTimeOffset(File.GetCreationTimeUtc(file), TimeSpan.Zero), 0);
            }
        }
    }

    /// <summary>
    /// Runs <c>review-cost</c> as the node's verb takes it: <c>node</c> as the target, <c>--since</c>, <c>--until</c> (dates, the day itself counted)
    /// and <c>--repo</c> as args, the report in <c>Made</c>.
    /// </summary>
    public static (string? Report, string? Error) Run(string[] args, NodeState state, ClaudeProfile profile)
    {
        if (!TryParse(args, out var since, out var until, out var repo, out var error))
        {
            return (null, error);
        }
        return (Compute(state.Flights(), state.Sessions(), profile, since, until, repo).Text(), null);
    }

    /// <summary>The options of <c>review-cost</c> as its args carry them: <c>--since &lt;date&gt;</c>, <c>--until &lt;date&gt;</c>, <c>--repo &lt;name&gt;</c>.</summary>
    public static bool TryParse(string[] args, out DateTimeOffset? since, out DateTimeOffset? until, out string? repo, out string? error)
    {
        (since, until, repo, error) = (null, null, null, null);
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--since" or "--until" when value is not null && DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var day):
                    var at = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    if (args[i] == "--since")
                    {
                        since = at;
                    }
                    else
                    {
                        until = at.AddDays(1);
                    }
                    i++;
                    break;
                case "--repo" when value is { Length: > 0 }:
                    repo = value;
                    i++;
                    break;
                default:
                    error = args[i] is "--since" or "--until" ? $"{args[i]} takes a date (2026-10-22)" : $"review-cost takes --since <date>, --until <date> and --repo <name>, not {args[i]}";
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The flights and sessions a node's data folder holds, read and never written: its snapshot, then the journal's lines after it. A line
    /// that cannot be read ends the replay there (a build's newer kind, or a line a crash cut), with what came before kept.
    /// </summary>
    public static NodeMirror ReadStore(string folder)
    {
        var mirror = new NodeMirror();
        // The folder the node was given holds the store in its store folder; the store folder itself is as good.
        if (!File.Exists(Path.Combine(folder, FileJournal.SnapshotFile)) && Directory.Exists(Path.Combine(folder, "store")))
        {
            folder = Path.Combine(folder, "store");
        }
        var snapshotPath = Path.Combine(folder, FileJournal.SnapshotFile);
        if (File.Exists(snapshotPath) && JsonSerializer.Deserialize(File.ReadAllBytes(snapshotPath), PilotJsonContext.Default.NodeEvent) is { } snapshot)
        {
            mirror.Apply(snapshot);
        }
        var journalPath = Path.Combine(folder, FileJournal.JournalFile);
        if (File.Exists(journalPath))
        {
            using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                NodeEvent? delta;
                try
                {
                    delta = JsonSerializer.Deserialize(line, PilotJsonContext.Default.NodeEvent);
                }
                catch (JsonException)
                {
                    break;
                }
                if (delta is null or SnapshotEvent || delta.Seq <= mirror.Seq)
                {
                    continue;
                }
                mirror.Apply(delta);
            }
        }
        return mirror;
    }
}
