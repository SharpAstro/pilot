using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>The flights not landed, and how many landed ones that leaves out.</summary>
    internal static (List<FlightRow> Flights, int Landed) InTheAir(List<FlightRow> flights) =>
        ([.. flights.Where(f => f.State != FlightState.Landed)], flights.Count(f => f.State == FlightState.Landed));

    internal static string LandedHidden(int landed, string command = "pilot status --all") =>
        $"{landed} landed {(landed == 1 ? "flight" : "flights")} not shown: {command}";

    /// <summary>The <c>Opus n/cap</c> line (with <c>peers not counted</c> when so) and the flights waiting for admission with why; nothing from a node that has not said.</summary>
    internal static IEnumerable<string> AdmissionLines(SnapshotEvent snapshot)
    {
        if (snapshot.Admission is { } admission)
        {
            yield return $"{admission.Line()}{(admission.Counted.Length > 0 ? ": " + string.Join(", ", admission.Counted) : "")}{(admission.OneFlightPerRepo ? "; one flight per repo" : "")}";
            if (admission.WaitingOnPerson is { Length: > 0 } waiting)
            {
                yield return $"not counted: {waiting.Length} idle, waiting on a person or a job: {string.Join(", ", waiting)}";
            }
        }
        foreach (var queued in QueuedFlight.Of(snapshot.Flights, snapshot.Legs, snapshot.Admission))
        {
            yield return $"queued  {queued.Code}{(queued is { FromField: true, Level: { } level } ? $" [{IssuePriorities.Name(level).ToLowerInvariant()}]" : queued.Priority ? " [priority]" : "")}  {queued.Title}: {queued.Why}";
        }
    }

    /// <summary>
    /// The flights and their legs, a line each, and the live sessions. Landed flights are left out unless <paramref name="all"/>,
    /// with a line saying how many; a flight asked for by <paramref name="code"/>, or the newest, shows whatever its state.
    /// </summary>
    internal static string Status(SnapshotEvent snapshot, bool newestOnly = false, string? code = null, bool maintenance = false, bool? autopilot = null, bool all = false, IReadOnlyCollection<SendTimingRow>? sendTimings = null)
    {
        var text = new System.Text.StringBuilder();
        if (!newestOnly && code is null && maintenance)
        {
            text.AppendLine("maintenance  on: this node takes no new flights (pilot settings set maintenance off)");
        }
        if (!newestOnly && code is null && autopilot is { } piloting)
        {
            text.AppendLine(piloting
                ? "autopilot  on (only the person changes it: pilot settings set autopilot off)"
                : "autopilot  off (only the person changes it: pilot settings set autopilot on)");
        }
        if (!newestOnly && code is null && sendTimings is not null)
        {
            text.AppendLine($"sends  {SendTimingRow.Summary(sendTimings)}");
        }
        if (!newestOnly && code is null && snapshot.Usage?.Account is { } account)
        {
            text.AppendLine($"account  {account.Line()}");
        }
        if (!newestOnly && code is null && snapshot.Usage is { HasFigures: true } usage)
        {
            text.AppendLine($"usage  {usage.Line(DateTimeOffset.UtcNow, TimeZoneInfo.Local)}");
        }
        if (!newestOnly && code is null && snapshot.Machine is { } machine)
        {
            text.AppendLine($"machine  {machine.Line()}");
        }
        if (!newestOnly && code is null)
        {
            foreach (var line in AdmissionLines(snapshot))
            {
                text.AppendLine(line);
            }
        }
        if (!newestOnly && code is null && snapshot.Ideas.Count(i => i.State == IdeaState.Noted) is > 0 and var noted)
        {
            text.AppendLine($"ideas  {noted} noted (pilot ideas)");
        }
        var flights = snapshot.Flights.OrderBy(f => f.State == FlightState.Landed).ThenByDescending(f => f.CreatedAt).ToList();
        if (newestOnly)
        {
            flights = [.. flights.OrderByDescending(f => f.CreatedAt).Take(1)];
        }
        if (code is not null)
        {
            flights = [.. flights.Where(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))];
        }
        var landed = 0;
        if (!newestOnly && code is null && !all)
        {
            (flights, landed) = InTheAir(flights);
        }
        foreach (var flight in flights)
        {
            var state = flight.State switch
            {
                FlightState.Held => $"held, waiting for {FlightWaiting.For(flight)}",
                FlightState.Landed => flight.MovedTo?.Text ?? "landed",
                _ => FlightWaiting.HoldingText(flight) is { } holding ? $"flying, {holding}" : "flying",
            };
            var level = flight.HasPriorityField && flight.IssuePriority is { } set ? $", priority {IssuePriorities.Name(set).ToLowerInvariant()}" : "";
            text.AppendLine($"{flight.Code}  {flight.Title ?? flight.Item.Noun}  ({flight.Repo} {flight.Item.Display}, {state}{level})");
            text.AppendLine($"  plane {flight.Worktree} on {flight.Branch}{(flight.PullRequestUrl is { } url ? $", PR {url}" : "")}");
            foreach (var leg in snapshot.Legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
            {
                var steps = leg.Checklist.Length == 0 ? "" : $", {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
                var goArounds = (leg.GoArounds > 0 ? $", go-around {leg.GoArounds}" : "") + (Verbs.AttemptOf(leg) is { } attempt ? $", {attempt}" : "");
                text.AppendLine($"  {leg.Order}. {leg.Name}: {FlightWaiting.LegState(leg)}{steps}{goArounds}{(leg.Worker is { } w && leg.State == LegState.Flying ? $", worker {w}" : "")}");
                foreach (var artifact in leg.Artifacts)
                {
                    text.AppendLine($"     artifact {artifact.Title}: {artifact.Location}");
                }
            }
        }
        if (!newestOnly && code is null)
        {
            if (flights.Count == 0)
            {
                text.AppendLine(landed > 0 ? "No flights in the air." : "No flights.");
            }
            if (landed > 0)
            {
                text.AppendLine(LandedHidden(landed));
            }
            foreach (var review in snapshot.PrReviews.Where(r => r.State != PrReviewState.Closed || r.Fixer is not null).OrderBy(r => r.StartedAt))
            {
                var how = review.State switch
                {
                    PrReviewState.Reviewing => $"reviewing, {review.Reviewer}",
                    PrReviewState.Closed => $"{review.Verdict}, closed",
                    _ => $"{review.Verdict}, on card {review.Card}",
                };
                text.AppendLine($"PR {review.Repo}#{review.Number}  {review.Title ?? review.Url}  ({how}, at {review.Head[..Math.Min(9, review.Head.Length)]})");
                if (review.Fixer is { } fixer)
                {
                    text.AppendLine($"  fixing, {fixer}, round {review.FixRound ?? 1}");
                }
            }
            var open = snapshot.Cards.Where(c => c.State == CardState.Open).ToList();
            if (open.Count > 0)
            {
                text.AppendLine($"{open.Count} open {(open.Count == 1 ? "card" : "cards")} ({open.Count(c => c.Bin == CardBin.Ring)} ringing): pilot cards");
            }
            var live = snapshot.Sessions.Where(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();
            text.AppendLine($"{live.Count} live {(live.Count == 1 ? "session" : "sessions")}{(live.Count > 0 ? ": " + string.Join(", ", live.Select(s => $"{s.Name} ({s.State.ToString().ToLowerInvariant()})")) : "")}");
        }
        return text.ToString();
    }

    /// <summary>The state as the listing writes it: a session that reported itself blocked is <c>blocked: message</c>, any other its state.</summary>
    private static string StateWord(SessionRow row) =>
        row.State == SessionState.Waiting && row.Reported == "blocked"
            ? (row.WaitingFor is { Length: > 0 } why ? $"blocked: {why}" : "blocked")
            : row.State.ToString().ToLowerInvariant();

    /// <summary>Every session, a line each: name, role and flight, state, model, context and spend.</summary>
    internal static string Sessions(IReadOnlyList<SessionRow> rows, string? machine = null)
    {
        var text = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            var flight = row.Flight is null ? "" : $" {row.Flight}{(row.Leg is { } leg ? "/" + leg : "")}";
            var budget = row.Budget is { } b ? $" of {b.ToString("N0", CultureInfo.InvariantCulture)}" : "";
            text.AppendLine($"{row.Id}  {MachineName.Shown(row.Name, machine)}  {row.Role.ToString().ToLowerInvariant()}{flight}  {StateWord(row)}  {row.Model ?? "-"}  {row.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)}/{row.ContextWindow.ToString("N0", CultureInfo.InvariantCulture)}  {row.SpendWeighted.ToString("N0", CultureInfo.InvariantCulture)}{budget}  start: {row.StartReason ?? "-"}  end: {row.EndReason ?? "-"}  ended: {(row.EndedAt is { } endedAt ? endedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : "-")}{(row.WallSeconds is { } wall ? $"  wall: {(int)TimeSpan.FromSeconds(wall).TotalHours}h{TimeSpan.FromSeconds(wall).Minutes:00}m" : "")}{(row.EndedBecause is { } why ? $"  ({why})" : "")}");
        }
        return text.Length == 0 ? "No sessions.\n" : text.ToString();
    }
    private sealed partial class BuildContext
    {
        /// <summary>Makes the status, attachments and sessions: what the node holds, read.</summary>
        internal (Command, Command, Command) StatusCommands()
        {
            // status, sessions
            var allFlights = new Option<bool>("--all") { Description = "Landed flights too, not only the ones in the air" };
            var status = Action(new Command("status", "Flights in the air, their legs and artifacts, open cards, and live sessions") { Options = { allFlights } }, async (parseResult, client, ct) =>
            {
                var snapshot = await SnapshotAsync(client, ct);
                var maintenance = false;
                bool? autopilot = null;
                string? dataFolder = null;
                SendTimingRow[]? timings = null;
                try
                {
                    var info = await client.GetNodeAsync(ct);
                    timings = info.SendTimings ?? [];
                    maintenance = info.Maintenance;
                    autopilot = info.Autopilot;
                    dataFolder = info.DataFolder;
                }
                catch (HttpRequestException)
                {
                    // The node's own word on itself is a line more: the flights and sessions are what status is for.
                }
                var everyFlight = parseResult.GetValue(allFlights);
                var format = parseResult.GetValue(outputFormat);
                var copies = format is "plain" or "text" ? new Dictionary<string, string>() : await AttachmentCopies.ResolveAsync(AttachmentCopies.Of(snapshot), client, dataFolder, ct);
                await WriteAsync(format, () => Status(snapshot, maintenance: maintenance, autopilot: autopilot, all: everyFlight, sendTimings: timings),
                    () => StatusMarkdown.Build(snapshot, maintenance, autopilot, everyFlight, copies, timings));
                return 0;
            });
            var attachmentsOwner = new Argument<string?>("flight-or-session") { Description = "A flight's code or a session's name; none lists every one", Arity = ArgumentArity.ZeroOrOne };
            var attachments = Action(new Command("attachments", "The images the node keeps copies of, with where each is") { Arguments = { attachmentsOwner } }, async (p, client, ct) =>
            {
                var snapshot = await SnapshotAsync(client, ct);
                var owner = p.GetValue(attachmentsOwner);
                string? dataFolder = null;
                try
                {
                    dataFolder = (await client.GetNodeAsync(ct)).DataFolder;
                }
                catch (HttpRequestException)
                {
                    // The paths then fall back to the default data folder's layout; a copy not found there is shown missing.
                }
                var copies = AttachmentCopies.Of(snapshot, owner);
                await output.WriteAsync(AttachmentCopies.List(copies, await AttachmentCopies.ResolveAsync(copies, client, dataFolder, ct), client, dataFolder, owner));
                return 0;
            });
            var sessions = Action(new Command("sessions", "Every Claude session on the machine"), async (_, client, ct) =>
            {
                await output.WriteAsync(Sessions(await client.GetSessionsAsync(ct), await MachineAsync(client, ct)));
                return 0;
            });

            return (status, attachments, sessions);
        }

        /// <summary>Makes the summarise, review-cost and summary: the kept summary and what reviews cost.</summary>
        internal (Command, Command, Command) SummaryCommands()
        {
            // summarise: one Haiku call over what the node holds, kept on the node for every client.
            var summariseFlight = new Argument<string?>("flight") { Description = "The flight's code; leave it out for every flight in play", Arity = ArgumentArity.ZeroOrOne };
            var summarise = Action(new Command("summarise", "Summarise the flights in play (or one flight) with one Haiku call: what needs you, where it is") { Arguments = { summariseFlight } },
                (p, client, ct) => Send(client, Verbs.Summarise, p.GetValue(summariseFlight) is { Length: > 0 } code ? Verbs.FlightTarget(code) : Verbs.NodeTarget, [], who, ct));
            // review-cost: what reviews cost per landed flight, from the reviewer transcripts; a read, nothing is changed.
            var costSince = new Option<string?>("--since") { Description = "Flights landed on or after this date (2026-10-22)" };
            var costUntil = new Option<string?>("--until") { Description = "Flights landed on or before this date" };
            var costRepo = new Option<string?>("--repo") { Description = "Only this repo's flights (its name, as in pilot status)" };
            var reviewCost = Action(new Command("review-cost", "What reviews cost per landed flight: its reviewer sessions (round, model, head) and their tokens, with totals and a per-flight average")
                { Options = { costSince, costUntil, costRepo } },
                (p, client, ct) =>
                {
                    List<string> args = [];
                    foreach (var (flag, option) in new[] { ("--since", costSince), ("--until", costUntil), ("--repo", costRepo) })
                    {
                        if (p.GetValue(option) is { Length: > 0 } value)
                        {
                            args.AddRange([flag, value]);
                        }
                    }
                    return Send(client, Verbs.ReviewCost, Verbs.NodeTarget, [.. args], who, ct);
                });
            // summary: the kept summary, or one made when there is none or --fresh asks; the fleet's records are the node's to gather.
            var summaryOf = new Argument<string?>("flight") { Description = "The flight's code; leave it out for every flight in play on this node", Arity = ArgumentArity.ZeroOrOne };
            var summaryFresh = new Option<bool>("--fresh") { Description = "Make a new one now (one Haiku call) instead of showing the kept one" };
            var summaryFleet = new Option<bool>("--fleet") { Description = "Every node this machine holds a grant for, not only this node: their records are fetched from each node" };
            var summary = Action(new Command("summary", "The kept summary (needs you, where it is) with its age, tokens and cost; made now when there is none or with --fresh. Use it to answer 'where are we'")
                { Arguments = { summaryOf }, Options = { summaryFresh, summaryFleet } },
                async (p, client, ct) =>
                {
                    var snapshot = await SnapshotAsync(client, ct);
                    string scope;
                    string target;
                    string[] args = [];
                    if (p.GetValue(summaryOf) is { Length: > 0 } named)
                    {
                        if (p.GetValue(summaryFleet))
                        {
                            throw new UsageException("--fleet is about every node's flights; leave the flight out, or leave --fleet out");
                        }
                        if (snapshot.Flights.FirstOrDefault(f => string.Equals(f.Code, named, StringComparison.OrdinalIgnoreCase)) is not { } flight)
                        {
                            await output.WriteLineAsync($"refused: summary {named}: there is no flight {named}");
                            return 1;
                        }
                        (scope, target) = (flight.Code, Verbs.FlightTarget(flight.Code));
                    }
                    else if (p.GetValue(summaryFleet))
                    {
                        (scope, target, args) = (SummaryRow.AllNodesScope, Verbs.NodeTarget, [SummaryRow.FleetArgument]);
                    }
                    else
                    {
                        (scope, target) = (SummaryRow.FleetScope, Verbs.NodeTarget);
                    }

                    var kept = snapshot.Summaries.FirstOrDefault(s => s.Scope == scope);
                    string? made = null;
                    if (kept is null || p.GetValue(summaryFresh))
                    {
                        var sender = await SenderAsync(client, hereSocket, who, bySession, byAgent, cwd, sessionId, ct);
                        var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Summarise, Target = target, Args = args, Who = sender, Agent = byAgent }, ct);
                        if (envelope is not { Success: true, Response: { Ok: true } result })
                        {
                            await output.WriteLineAsync($"refused: summarise {target}: {envelope.Response?.Error ?? envelope.Error}");
                            return 1;
                        }
                        made = result.Made;
                        kept = (await SnapshotAsync(client, ct)).Summaries.FirstOrDefault(s => s.Scope == scope);
                    }

                    if (kept is null)
                    {
                        // The node kept none to read back (it was restarted between the two reads): the answer it gave is still the answer.
                        await output.WriteLineAsync(made is { Length: > 0 } ? made : $"pilot: the node holds no summary of {SummaryMarkdown.About(scope)}");
                        return made is { Length: > 0 } ? 0 : 1;
                    }
                    var now = DateTimeOffset.UtcNow;
                    await WriteAsync(p.GetValue(outputFormat), () => SummaryMarkdown.Plain(kept, now), () => SummaryMarkdown.Build(kept, now));
                    return 0;
                });

            return (summarise, reviewCost, summary);
        }
    }
}
