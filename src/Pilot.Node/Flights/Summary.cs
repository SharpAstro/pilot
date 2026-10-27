using System.Globalization;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// What the summary's one Haiku call is shown: the node's flights as plain records, built only from what the node holds (each
/// flight's legs with state, checklist, go-arounds and report; its PR and review; its open cards; the sessions working on it)
/// and nothing it would have to look up or guess. Port of v1's <c>worker_records</c>. The call is told to quote hashes, test
/// names and results only from here, so a hash appears in these records only where the node holds it (a flight's PR head, its
/// review's head), shortened to <see cref="HashLength"/> characters.
/// </summary>
public static class SummaryRecords
{
    /// <summary>How many characters of a commit a record quotes.</summary>
    public const int HashLength = 8;

    /// <summary>The longest a report, a card's detail or a review's findings run in a record before they are cut.</summary>
    public const int MaxReport = 1500;

    /// <summary>The longest of what a finished leg says, in the fleet's records: a done leg is a line there, not a section.</summary>
    public const int MaxDoneReport = 240;

    /// <summary>
    /// The records of every flight in play (not landed), the open cards about none of them and the PR reviews not closed; null
    /// when there is nothing of that to say, which a summary does not spend a call on.
    /// </summary>
    public static string? Fleet(NodeState state, DateTimeOffset now)
    {
        var held = state.Snapshot();
        var inPlay = held.Flights.Where(f => f.State != FlightState.Landed).OrderBy(f => f.CreatedAt).ThenBy(f => f.Code, StringComparer.Ordinal).ToList();
        var strays = held.Cards.Where(c => c.State == CardState.Open && c.Kind != Digest.Kind && !inPlay.Any(f => About(c, f.Code)))
            .OrderBy(c => c.At).ToList();
        var reviews = held.PrReviews.Where(r => r.State != PrReviewState.Closed).OrderBy(r => r.StartedAt).ToList();
        if (inPlay.Count == 0 && strays.Count == 0 && reviews.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Records of the node as of {Stamp(now)}: {Count(inPlay.Count, "flight")} in play.");
        foreach (var flight in inPlay)
        {
            text.AppendLine();
            Flight(text, held, flight, detail: false);
        }
        if (strays.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Open cards about no flight in play:");
            foreach (var card in strays)
            {
                Card(text, card, "  ");
            }
        }
        if (reviews.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("PR reviews not closed:");
            foreach (var review in reviews)
            {
                PrReview(text, review);
            }
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The records of one flight, landed or not, as of <paramref name="now"/>; null when the node has no flight of that code.</summary>
    public static string? Flight(NodeState state, string code, DateTimeOffset now)
    {
        var held = state.Snapshot();
        if (held.Flights.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { } flight)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Records of flight {flight.Code} as of {Stamp(now)}.");
        text.AppendLine();
        Flight(text, held, flight, detail: true);
        return text.ToString().TrimEnd();
    }

    private static void Flight(StringBuilder text, SnapshotEvent held, FlightRow flight, bool detail)
    {
        var title = flight.Title is { Length: > 0 } t ? $" \"{t}\"" : "";
        text.AppendLine(CultureInfo.InvariantCulture, $"Flight {flight.Code}: {flight.Repo} {flight.Item.Noun}{title}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"  State: {flight.State.ToString().ToLowerInvariant()}{(flight.State == FlightState.Held ? $", waiting for {FlightWaiting.For(flight)}" : "")}{(flight.FoldedInto is { } into ? $" (folded into {into})" : "")}. Branch {flight.Branch} from {flight.Base}. Created {Stamp(flight.CreatedAt)}{(flight.LandedAt is { } landed ? $", landed {Stamp(landed)}" : "")}.");
        if (flight.FoldedIn.Length > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Folded in: {string.Join(", ", flight.FoldedIn.Select(f => $"#{f.Number} (from {f.From})"))}.");
        }
        foreach (var overlap in flight.Overlaps.Where(o => o.Answer == OverlapAnswer.Pending))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Overlap with {overlap.Other} awaits the person's answer: {Clip(overlap.Why, 300)}");
        }

        if (flight.PullRequest is { } pr)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  PR #{pr}{(flight.PullRequestUrl is { } url ? $" {url}" : "")}{(flight.GitHubHead is { } head ? $", head {Short(head)}" : "")}.");
            if (flight.MergeQueue is { } queue)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  Merge: {queue}.");
            }
            else if (flight.MergeWhenGreen is { Length: > 0 })
            {
                text.AppendLine("  Merge: asked for, waiting for the PR's checks to pass.");
            }
            if (flight.GitHubDisagrees is { } disagrees)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  GitHub disagrees with the node: {Clip(disagrees, 300)}");
            }
        }
        if (flight.Review is { } review)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  Review: {review.State.ToString().ToLowerInvariant()}, started {Stamp(review.StartedAt)}{(review.FinishedAt is { } done ? $", finished {Stamp(done)}" : "")}{(review.ReviewedHead is { } read ? $", read head {Short(read)}" : "")}{(review.Label is { } label ? $", label {label}" : "")}.");
            if (review.Report is { Length: > 0 } report)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  Review report: {Clip(report, MaxReport)}");
            }
        }

        var legs = held.Legs.Where(l => string.Equals(l.Flight, flight.Code, StringComparison.Ordinal)).OrderBy(l => l.Order).ToList();
        text.AppendLine(legs.Count == 0 ? "  Legs: none planned." : "  Legs:");
        foreach (var leg in legs)
        {
            Leg(text, leg, detail);
        }

        var cards = held.Cards.Where(c => c.State == CardState.Open && About(c, flight.Code)).OrderBy(c => c.At).ToList();
        if (cards.Count > 0)
        {
            text.AppendLine("  Open cards:");
            foreach (var card in cards)
            {
                Card(text, card, "    ");
            }
        }

        var sessions = held.Sessions.Where(s => string.Equals(s.Flight, flight.Code, StringComparison.OrdinalIgnoreCase) && s.State != SessionState.Ended && !s.Archived)
            .OrderBy(s => s.StartedAt).ToList();
        if (sessions.Count > 0)
        {
            text.AppendLine("  Sessions working on it:");
            foreach (var session in sessions)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"    {session.Name} ({session.Role.ToString().ToLowerInvariant()}{(session.Leg is { } leg ? $", leg {leg}" : "")}): {session.State.ToString().ToLowerInvariant()}{(session.State == SessionState.Waiting && (session.Asking ?? session.WaitingFor) is { } asks ? $" on {Clip(asks, 200)}" : "")}{(session.ApiError is { } error ? $", stopped by {error}" : "")}{(session.LastResponseAt is { } at ? $", last answered {Stamp(at)}" : "")}.");
            }
        }
    }

    private static void Leg(StringBuilder text, LegRow leg, bool detail)
    {
        var goArounds = leg.GoArounds > 0 ? $", {Count(leg.GoArounds, "go-around")}" : "";
        if (!detail && leg.State == LegState.Done)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"    {leg.Order}. {leg.Name}: done{goArounds}.{(leg.Report is { Length: > 0 } r ? $" Report: {Clip(r, MaxDoneReport)}" : "")}");
            return;
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"    {leg.Order}. {leg.Name}: {FlightWaiting.LegState(leg)}{goArounds}{(leg.Worker is { } worker ? $", worker {worker}" : "")}{(leg.Validation != LegValidation.None ? $", validation {leg.Validation.ToString().ToLowerInvariant()}" : "")}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"       Goal: {Clip(leg.Goal, 400)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"       Done when: {Clip(leg.DoneWhen, 400)}");
        if (leg.Checklist.Length > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"       Checklist, {leg.Checklist.Count(i => i.Done)} of {leg.Checklist.Length} ticked:");
            foreach (var item in leg.Checklist)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"         [{(item.Done ? 'x' : ' ')}] {Clip(item.Text, 200)}");
            }
        }
        if (leg.State == LegState.Flying && leg.StartedAt is { } started)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"       Started {Stamp(started)}.");
        }
        if (leg.ValidationReport is { Length: > 0 } validation && leg.Validation == LegValidation.NoGo)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"       Its validator's no-go: {Clip(validation, 600)}");
        }
        if (leg.Report is { Length: > 0 } report)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"       Report: {Clip(report, MaxReport)}");
        }
    }

    private static void Card(StringBuilder text, CardRow card, string indent)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"{indent}{card.Id} ({card.Bin.ToString().ToLowerInvariant()}, {card.Kind}): {card.Title}");
        if (card.Detail is { Length: > 0 } detail)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{indent}  {Clip(detail, 400)}");
        }
        if (card.Choices.Length > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{indent}  Choices: {string.Join("; ", card.Choices.Select(c => c.Status is { } status ? $"{c.Label} ({status})" : c.Label))}");
        }
    }

    private static void PrReview(StringBuilder text, PrReviewRow review)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"  {review.Repo}#{review.Number}{(review.Title is { Length: > 0 } title ? $" \"{title}\"" : "")}: {review.State.ToString().ToLowerInvariant()}{(review.Verdict is { } verdict ? $", verdict {verdict}" : "")}, head {Short(review.Head)}, started {Stamp(review.StartedAt)}.");
        if (review.Report is { Length: > 0 } report)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"    Report: {Clip(report, MaxReport)}");
        }
        foreach (var finding in review.Findings)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"    Finding: {Clip(finding.ToString(), 300)}");
        }
    }

    /// <summary>Whether <paramref name="card"/> is about flight <paramref name="code"/> or anything under it (a leg).</summary>
    private static bool About(CardRow card, string code)
    {
        var subject = Verbs.FlightTarget(code);
        return string.Equals(card.Subject, subject, StringComparison.OrdinalIgnoreCase) || card.Subject.StartsWith(subject + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string hash) => hash.Length > HashLength ? hash[..HashLength] : hash;

    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary><paramref name="text"/> on as few lines as it fits, cut at <paramref name="max"/> characters with an ellipsis.</summary>
    private static string Clip(string? text, int max)
    {
        var flat = string.Join(' ', (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + " …";
    }
}

/// <summary>The fixed prompt of the summary's call: two sections, and how long the second may run.</summary>
public static class SummaryPrompt
{
    /// <summary>The words the fleet's <see cref="Where"/> may run to.</summary>
    public const int FleetWords = 160;

    /// <summary>The words one flight's <see cref="Where"/> may run to.</summary>
    public const int FlightWords = 120;

    /// <summary>The heading of what stays stuck unless the person acts.</summary>
    public const string Needs = "## Needs you";

    /// <summary>The heading of where things stand.</summary>
    public const string Where = "## Where it is";

    /// <summary>What an answer with nothing for the person says under <see cref="Needs"/>.</summary>
    public const string Nothing = "Nothing.";

    /// <summary>The prompt about <paramref name="what"/> (<c>all 3 flights in play</c>, <c>the flight TIA1611</c>), the second section at most <paramref name="words"/> words, then the records.</summary>
    public static string Build(string what, int words, string records) => $$"""
        You summarise flights of background coding workers for the person who runs them. Below are records
        about {{what}}, exactly as the node holds them. Answer in exactly this format and nothing else:

        {{Needs}}
        - One bullet per thing that stays stuck unless the person acts: a question only they can answer, a
          permission prompt, a card in the ring, a PR waiting for their merge, a leg that failed, a worker
          that stopped or waits. If there is nothing, write the single word: Nothing.

        {{Where}}
        At most {{words.ToString(CultureInfo.InvariantCulture)}} words of plain prose: what has been done, what is happening now, what is left. Quote
        commit hashes, test names and results only from the records; never invent them.

        Records:

        {{records}}
        """;

    /// <summary>The prompt over the fleet's records.</summary>
    public static string Fleet(int flights, string records) => Build($"{flights.ToString(CultureInfo.InvariantCulture)} flight{(flights == 1 ? "" : "s")} in play and what else waits on the person", FleetWords, records);

    /// <summary>The prompt over the records of several nodes, each under its own heading: the second section names the node where it matters.</summary>
    public static string Nodes(int nodes, string records) => Build($"the flights in play on {nodes.ToString(CultureInfo.InvariantCulture)} nodes of a fleet and what else waits on the person, each node's records under its own heading; name the node where it matters", FleetWords, records);

    /// <summary>The prompt over one flight's records.</summary>
    public static string Flight(string code, string records) => Build($"the flight {code}", FlightWords, records);
}

/// <summary>What one Haiku call answered, and what it used.</summary>
/// <param name="Text">The answer, markdown.</param>
/// <param name="Tokens">Its input, cache and output tokens added up.</param>
/// <param name="Cost">US dollars, as Claude Code reported it; null when it did not.</param>
public sealed record SummaryAnswer(string Text, long Tokens, double? Cost)
{
    /// <summary>
    /// Reads <c>claude -p --output-format json</c>'s one result object. Throws <see cref="InvalidOperationException"/>, saying why, when
    /// it is not one, or when it says it failed or answered nothing.
    /// </summary>
    public static SummaryAnswer Parse(string output)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(output).RootElement;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(Clip(output) is { Length: > 0 } said ? $"claude did not answer in JSON: {said}" : "claude printed nothing");
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"claude did not answer in JSON: {Clip(output)}");
        }

        var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        if (root.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException($"claude failed: {Clip(result ?? output)}");
        }
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException("claude answered nothing");
        }

        long tokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens" })
            {
                if (usage.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var count))
                {
                    tokens += count;
                }
            }
        }
        double? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var usd) ? usd : null;
        return new SummaryAnswer(result.Trim(), tokens, cost);
    }

    private static string Clip(string text) => text.Trim() is { Length: > 300 } long300 ? long300[..300] + " …" : text.Trim();
}

/// <summary>The one call behind a summary: tests give it a fake, so no test runs <c>claude</c>.</summary>
public interface ISummaryModel
{
    /// <summary>Asks <paramref name="prompt"/> once. Throws, saying why in its message, when it could not be had.</summary>
    Task<SummaryAnswer> AskAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>
/// One <c>claude -p</c> on Haiku, as v1's <c>haiku()</c> ran it: no tools, no MCP servers, no saved session, none of the user's
/// settings, its prompt on standard input and its answer as JSON; in a folder of its own, so no project's <c>CLAUDE.md</c> is read.
/// </summary>
public sealed class ClaudeSummaryModel(string folder) : ISummaryModel
{
    /// <summary>How long the call may take before it is given up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(150);

    /// <summary>The arguments of the call.</summary>
    public static readonly IReadOnlyList<string> Arguments =
    [
        "-p", "--model", "haiku", "--tools", "", "--strict-mcp-config", "--no-session-persistence", "--restricted", "--output-format", "json",
    ];

    public async Task<SummaryAnswer> AskAsync(string prompt, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var run = await ClaudeCli.RunAsync(Arguments, prompt, folder, Timeout, cancellationToken);
        try
        {
            return SummaryAnswer.Parse(run.Output);
        }
        catch (InvalidOperationException) when (run.ExitCode != 0 && run.Error.Trim() is { Length: > 0 } error)
        {
            throw new InvalidOperationException($"claude exited {run.ExitCode}: {error}");
        }
    }
}

/// <summary>
/// The summary the node makes and keeps: <c>summarise node</c> for the flights in play, <c>summarise flight/&lt;code&gt;</c> for one.
/// It builds the records (<see cref="SummaryRecords"/>) from what the node holds, asks the model once under the fixed
/// <see cref="SummaryPrompt"/>, and keeps the answer per scope with its time, tokens and cost as a <see cref="SummaryRow"/>, on
/// the feed for every client. It never runs by itself, and never twice at once for one scope.
/// </summary>
public sealed class Summaries(NodeState state, ISummaryModel model, TimeProvider time)
{
    /// <summary>The nodes this one holds a grant for, whose records <see cref="SummaryRow.FleetArgument"/> gathers; none when unset.</summary>
    public IFleet? Fleet { get; set; }

    /// <summary>This node's name in a fleet's records: its machine prefix.</summary>
    public string Machine { get; set; } = "this node";

    /// <summary>What a fleet with nothing in play says, with no call: there is nothing to summarise.</summary>
    public const string EmptyFleet = $"{SummaryPrompt.Needs}\n{SummaryPrompt.Nothing}\n\n{SummaryPrompt.Where}\nNo flights in play.";

    private readonly Lock _gate = new Lock();
    private readonly HashSet<string> _making = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Runs <c>summarise</c>; the model's call is off the state's lock, which the records and the kept row take for an instant each.</summary>
    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        string scope;
        if (command.Target == Verbs.NodeTarget)
        {
            scope = (command.Args ?? []).Contains(SummaryRow.FleetArgument, StringComparer.OrdinalIgnoreCase) ? SummaryRow.AllNodesScope : SummaryRow.FleetScope;
        }
        else if (command.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) && command.Target[Verbs.FlightTargetPrefix.Length..] is { Length: > 0 } code && !code.Contains('/'))
        {
            if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { } flight)
            {
                return state.Complete(command, $"There is no flight {code}");
            }
            scope = flight.Code;
        }
        else
        {
            return state.Complete(command, $"summarise takes the target {Verbs.NodeTarget} or {Verbs.FlightTargetPrefix}<code>, not {command.Target}");
        }

        var (row, error) = await MakeAsync(scope, cancellationToken);
        return state.Complete(command, error, made: row?.Markdown);
    }

    /// <summary>
    /// Runs <c>records</c>: what the node holds of its flights in play (target <c>node</c>) or of one flight, as the plain records a
    /// summary is made from, in the result's <c>Made</c>; nothing when it holds none. No call, nothing kept.
    /// </summary>
    public CommandResult Records(CommandRequest command)
    {
        var now = time.GetUtcNow();
        if (command.Target == Verbs.NodeTarget)
        {
            return state.Complete(command, null, made: SummaryRecords.Fleet(state, now));
        }
        if (command.Target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal) && command.Target[Verbs.FlightTargetPrefix.Length..] is { Length: > 0 } code && !code.Contains('/'))
        {
            return SummaryRecords.Flight(state, code, now) is { } records
                ? state.Complete(command, null, made: records)
                : state.Complete(command, $"There is no flight {code}");
        }
        return state.Complete(command, $"records takes the target {Verbs.NodeTarget} or {Verbs.FlightTargetPrefix}<code>, not {command.Target}");
    }

    /// <summary>
    /// The records of this node and of each node it holds a grant for, each under its own heading, fetched with <c>records node</c>
    /// from the node itself; a node that cannot be reached, or refuses, is a line saying so, never a guess. Null when no node has
    /// anything in play and none was missed.
    /// </summary>
    private async Task<(string? Records, int Nodes)> GatherAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var peers = (Fleet?.Peers() ?? []).Where(p => p.HasGrant).ToList();
        var fetched = await Task.WhenAll(peers.Select(p => FetchAsync(p, cancellationToken)));
        var text = new StringBuilder();
        var nodes = 0;
        var missed = false;
        if (SummaryRecords.Fleet(state, now) is { } own)
        {
            nodes++;
            text.AppendLine(CultureInfo.InvariantCulture, $"=== Node {Machine} (this node) ===").AppendLine(own).AppendLine();
        }
        foreach (var (name, records, why) in fetched.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (why is not null)
            {
                missed = true;
                text.AppendLine(CultureInfo.InvariantCulture, $"=== Node {name} ===").AppendLine(why).AppendLine();
            }
            else if (records is { Length: > 0 })
            {
                nodes++;
                text.AppendLine(CultureInfo.InvariantCulture, $"=== Node {name} ===").AppendLine(records).AppendLine();
            }
        }
        return nodes == 0 && !missed ? (null, 0) : (text.ToString().TrimEnd(), Math.Max(nodes, 1));
    }

    private static async Task<(string Name, string? Records, string? Why)> FetchAsync(IFleetPeer peer, CancellationToken cancellationToken)
    {
        var name = peer.Endpoint;
        try
        {
            var info = await peer.NodeAsync(cancellationToken);
            name = info.MachinePrefix.Length > 0 ? info.MachinePrefix : peer.Endpoint;
            var result = await peer.SendAsync(new CommandRequest { Verb = Verbs.Records, Target = Verbs.NodeTarget, Who = "summary" }, cancellationToken);
            return result.Ok
                ? (name, result.Made, null)
                : (name, null, $"This node's records could not be had: it refused ({result.Error ?? "no reason given"}).");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException)
        {
            return (name, null, $"This node's records could not be had: it could not be reached ({e.Message}).");
        }
    }

    /// <summary>
    /// Makes the summary of <paramref name="scope"/> (<see cref="SummaryRow.FleetScope"/>, <see cref="SummaryRow.AllNodesScope"/> or a flight's code) and keeps it. A fleet
    /// with nothing in play is answered without a call, at no cost. The row, or why there is none.
    /// </summary>
    public async Task<(SummaryRow? Row, string? Error)> MakeAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_making.Add(scope))
            {
                return (null, scope is SummaryRow.FleetScope or SummaryRow.AllNodesScope ? "The fleet is being summarised already" : $"{scope} is being summarised already");
            }
        }

        try
        {
            var now = time.GetUtcNow();
            string prompt;
            if (scope == SummaryRow.FleetScope)
            {
                var records = SummaryRecords.Fleet(state, now);
                if (records is null)
                {
                    return (Keep(scope, EmptyFleet, tokens: 0, cost: null), null);
                }
                prompt = SummaryPrompt.Fleet(state.Flights().Count(f => f.State != FlightState.Landed), records);
            }
            else if (scope == SummaryRow.AllNodesScope)
            {
                var (records, nodes) = await GatherAsync(now, cancellationToken);
                if (records is null)
                {
                    return (Keep(scope, EmptyFleet, tokens: 0, cost: null), null);
                }
                prompt = SummaryPrompt.Nodes(nodes, records);
            }
            else
            {
                var records = SummaryRecords.Flight(state, scope, now);
                if (records is null)
                {
                    return (null, $"There is no flight {scope}");
                }
                prompt = SummaryPrompt.Flight(scope, records);
            }

            SummaryAnswer answer;
            try
            {
                answer = await model.AskAsync(prompt, cancellationToken);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                return (null, $"The summary could not be made: {e.Message}");
            }
            return (Keep(scope, answer.Text, answer.Tokens, answer.Cost), null);
        }
        finally
        {
            lock (_gate)
            {
                _making.Remove(scope);
            }
        }
    }

    private SummaryRow Keep(string scope, string markdown, long tokens, double? cost)
    {
        var row = new SummaryRow { Scope = scope, Markdown = markdown, MadeAt = time.GetUtcNow(), Tokens = tokens, Cost = cost };
        state.PutSummary(row);
        return row;
    }
}
