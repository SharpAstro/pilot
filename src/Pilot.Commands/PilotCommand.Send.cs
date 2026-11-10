using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>The --repo description of every command that reads <see cref="RepoPath"/>; it is the MCP schema's text too.</summary>
    internal const string RepoOptionDescription = "The repo's checkout (any of its worktrees); else this folder. On another node (a node argument or --node) it is required and is that machine's checkout path, as its repos list shows";

    /// <summary>
    /// The repo a flight is created on, as the node will read it. On this machine's node, the folder resolved here (this
    /// folder when none is named). On a node on the LAN it is that machine's path, sent as given and never resolved
    /// against this one (a Windows client turned a Linux node's /home/… into C:\home\…), and it must be named.
    /// </summary>
    internal static string RepoPath(NodeClient client, string? named, string cwd) => client.IsRemote
        ? named ?? throw new UsageException($"--repo is needed on a node on the LAN ({client.SocketPath}): it is that machine's checkout path, not one of this machine's. Find it with `pilot repos --node {client.SocketPath}` (from MCP, the repos tool with node set to {client.SocketPath})")
        : Path.GetFullPath(named ?? cwd, cwd);

    /// <summary>
    /// Who a command is from when nothing nearer says: the Claude session running this CLI, by its name on the node
    /// (Claude Code gives a session's tools <c>CLAUDE_CODE_SESSION_ID</c>), so the watchtower's commands are the
    /// watchtower's; else <paramref name="fallback"/>, the CLI for a person at a terminal or an MCP client by its name.
    /// </summary>
    internal static string Caller(SnapshotEvent snapshot, string? sessionId, string fallback = "cli") =>
        SessionNamed(snapshot, sessionId) ?? fallback;

    /// <summary>
    /// A session's name on the node by its Claude session id: its current one or one it had before the node resumed it into a
    /// new id (<see cref="SessionRow.EarlierSessionIds"/>), as a worker's tools may still carry the old one.
    /// </summary>
    private static string? SessionNamed(SnapshotEvent snapshot, string? sessionId) =>
        sessionId is { Length: > 0 } id
            ? snapshot.Sessions.FirstOrDefault(s => string.Equals(s.SessionId, id, StringComparison.OrdinalIgnoreCase)
                || s.EarlierSessionIds is { } earlier && earlier.Any(e => string.Equals(e, id, StringComparison.OrdinalIgnoreCase)))?.Name
            : null;

    /// <summary>
    /// Who a Claude session's command is from: its name on the node (<see cref="Caller"/>), and when the node knows no such
    /// session (not polled yet) but the command is agent-run inside a flight's plane, that plane's own session (its flying leg's
    /// worker, a validator waiting, the review's reviewer, else a planner or worker row of the flight, else the flight's planner
    /// by the name the node gives it, <c>&lt;machine&gt;-&lt;flight&gt;-plan</c>, as a new flight's planner has no row yet), since a session working
    /// in a plane is that flight's and must not be taken for an assistant. Otherwise <paramref name="fallback"/>.
    /// </summary>
    /// <param name="machine">The node's machine prefix (<c>DU1</c>), for a planner's name; null when not known.</param>
    internal static string Sender(SnapshotEvent snapshot, string? sessionId, string cwd, bool agent, string fallback = "cli", string? machine = null) =>
        SessionNamed(snapshot, sessionId) ?? (agent ? PlaneSession(snapshot, cwd, machine) : null) ?? fallback;

    /// <summary>The name of the session the plane (or PR review worktree) holding <paramref name="cwd"/> belongs to, or null outside every one or when none is named.</summary>
    internal static string? PlaneSession(SnapshotEvent snapshot, string cwd, string? machine = null)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        if (snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison)) is { } flight)
        {
            return snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Flying)?.Worker
                ?? snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Planned && l.Validation == LegValidation.Pending)?.Validator
                ?? (flight.Review is { State: ReviewState.Reviewing } review ? review.Reviewer : null)
                ?? snapshot.Sessions.FirstOrDefault(s => s.Flight == flight.Code && s.Role is SessionRole.Planner or SessionRole.Worker or SessionRole.Reviewer)?.Name
                ?? (machine is { Length: > 0 } ? $"{machine.ToLowerInvariant()}-{flight.Code.ToLowerInvariant()}-plan" : null);
        }
        return PrHere(snapshot, cwd) is { Who: not "cli" } pr ? pr.Who : null;
    }

    /// <summary>
    /// Who a command is from: the Claude session running this host by its name on the node, where there is one, else
    /// <paramref name="who"/>. Sent to another node (<c>--node</c>), the session is looked up on this machine's node
    /// (<paramref name="hereSocket"/>), which runs it: the target's snapshot does not have it, and a watchtower's tell went
    /// as <c>cli</c>, unmarked, as if the person had written it.
    /// </summary>
    private static async Task<string> SenderAsync(NodeClient client, string? hereSocket, string who, bool bySession, bool agent, string cwd, string? sessionId, CancellationToken cancellationToken)
    {
        if (!bySession || (sessionId is not { Length: > 0 } && !agent))
        {
            return who;
        }
        if (client.IsRemote && sessionId is { Length: > 0 } && await NamedHereAsync(hereSocket, sessionId, cancellationToken) is { } here)
        {
            return here;
        }
        return Sender(await SnapshotAsync(client, cancellationToken), sessionId, cwd, agent, who, agent ? await MachineAsync(client, cancellationToken) : null);
    }

    /// <summary>The name this machine's node gives the Claude session <paramref name="sessionId"/>, or null when it has none or no node answers here.</summary>
    private static async Task<string?> NamedHereAsync(string? hereSocket, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var here = new NodeClient(NodeSocket.Resolve(hereSocket));
            return SessionNamed(await SnapshotAsync(here, cancellationToken), sessionId);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException or System.Net.WebSockets.WebSocketException)
        {
            return null;
        }
    }

    /// <summary>The node's machine prefix, or null when it cannot be had: the name of a planner the node has not listed yet is built from it.</summary>
    private static async Task<string?> MachineAsync(NodeClient client, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.GetNodeAsync(cancellationToken)).MachinePrefix is { Length: > 0 } prefix ? prefix : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The node's answer to a command, or <see cref="CommandTimeoutException"/> once it has not answered in <paramref name="deadline"/>:
    /// the client itself waits without limit, as a command may run for minutes, so the CLI sets its own, and says so.
    /// </summary>
    private static async Task<ResponseEnvelope<CommandResult>> SendCommandAsync(NodeClient client, CommandRequest request, TimeSpan deadline, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(deadline);
        try
        {
            return await client.SendAsync(request, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new CommandTimeoutException(deadline);
        }
    }

    private static async Task<int> SendAsync(NodeClient client, string? hereSocket, string verb, string target, string[] args, string who, bool bySession, bool agent, string? why, string cwd, string? sessionId, TextWriter output, string requestId, TimeSpan deadline, CancellationToken cancellationToken, string? attachment = null)
    {
        who = await SenderAsync(client, hereSocket, who, bySession, agent, cwd, sessionId, cancellationToken);
        var envelope = await SendCommandAsync(client, new CommandRequest { Verb = verb, Target = target, Args = args, Who = who, Agent = agent, Why = why, RequestId = requestId, SessionId = verb == Verbs.ReportState ? sessionId : null, Attachment = attachment }, deadline, cancellationToken);
        if (envelope.Success && envelope.Response is { Ok: true, Waiting: { Length: > 0 } waiting })
        {
            // Queued behind a lock the node's other work holds: not done, and not to be sent again, as it runs once the lock frees.
            // A finish's own words say what runs and what its worker does (end its turn); the generic tail would send it to look.
            await output.WriteLineAsync(verb == Verbs.FinishLeg
                ? $"queued: {verb} {target}: {waiting}"
                : $"queued: {verb} {target}: {waiting}; it runs when that is done, and its outcome comes on the event stream and a Handled card (pilot status)");
            return ExitQueued;
        }
        if (envelope.Success && envelope.Response is { Ok: true } result)
        {
            await output.WriteLineAsync($"ok: {verb} {target}{(result.Way is { } way ? $" ({way})" : "")}");
            if (verb == Verbs.Answer && CardAnswering.IsJoined(result.Made))
            {
                // A press that joined an answer already taken (#792): said, so the caller can tell it from a fresh run, as the GUI and TUI log it.
                await output.WriteLineAsync($"joined: {result.Made}");
            }
            if (verb == Verbs.SyncRepos)
            {
                foreach (var outcome in RepoSyncOutcome.Parse(result.Made))
                {
                    await output.WriteLineAsync($"{outcome.Name}  {outcome.Status}  {outcome.Detail}");
                }
            }
            if (verb == Verbs.CreateIssue && result.Made is { Length: > 0 } issueNumber)
            {
                await output.WriteLineAsync($"created issue #{issueNumber}");
            }
            if (verb is Verbs.NoteIdea or Verbs.MoveIdea && result.Made is { Length: > 0 } ideaId)
            {
                // "<repo> I<n>" when filed or moved; when two repos fit, the card that asks which.
                await output.WriteLineAsync(result.Way == IdeaEditWays.Asked
                    ? $"asked: card {ideaId} offers the repos it fits (File in <repo>); nothing is filed until you choose"
                    : ideaId);
            }
            if (verb == Verbs.ProposeMilestone && result.Milestones is { } proposed)
            {
                await WriteMilestonesAsync(output, result.Made ?? target, proposed);
                await output.WriteLineAsync($"nothing is planned: pilot idea {result.Made ?? target} plan --milestone <name> (or --new-milestone <title> [--milestone-description <line>]) plans it");
            }
            if (verb == Verbs.CheckReady && result.Readiness is { } checkedReady)
            {
                await WriteReadinessAsync(output, result.Made ?? target, checkedReady);
            }
            if (verb == Verbs.SuggestMilestones && result.Suggestions is { } suggested)
            {
                await WriteSuggestionsAsync(output, suggested);
            }
            if (verb == Verbs.AcceptMilestones && result.Made is { Length: > 0 } accepted)
            {
                await output.WriteLineAsync($"moved: {accepted}");
            }
            if (verb == Verbs.BreakUpIssue && result.BreakUp is { } broken)
            {
                BreakUpCache.Write(target, broken);
                await output.WriteLineAsync($"{result.Made ?? target}: proposed {broken.Parts.Length} parts");
                for (var i = 0; i < broken.Parts.Length; i++)
                {
                    var part = broken.Parts[i];
                    await output.WriteLineAsync($"{i + 1}. {part.Title}");
                    if (part.Body.Trim() is { Length: > 0 } partBody)
                    {
                        await output.WriteLineAsync("   " + partBody.Replace("\n", "\n   ", StringComparison.Ordinal));
                    }
                    foreach (var question in part.OpenQuestions)
                    {
                        await output.WriteLineAsync($"   ? {question}");
                    }
                }
                await output.WriteLineAsync("nothing is written: pilot issue break-up <issue> --save accepts it (part 1 replaces the issue, the rest are new issues), --discard drops it");
            }
            if (verb == Verbs.AcceptBreakUp && result.Made is { Length: > 0 } madeParts)
            {
                await output.WriteLineAsync($"broken up {target.Split('/')[^1]}: part 1 stays in it, new issues {madeParts}");
            }
            if (verb == Verbs.TidyIdea && result.Tidy is { } tidy)
            {
                var id = result.Made ?? target;
                if (tidy.Milestone is { } tidyMilestone)
                {
                    await WriteMilestonesAsync(output, id, tidyMilestone);
                }
                IdeaTidyCache.Write(target, tidy);
                if (IdeaDiff.Unified(IdeaTidyCache.Joined(tidy.CurrentTitle, tidy.CurrentBody), IdeaTidyCache.Joined(tidy.Title, tidy.Body), $"{id} (GitHub)", $"{id} (tidied)") is { Length: > 0 } diff)
                {
                    await output.WriteAsync(diff);
                    await output.WriteLineAsync($"nothing is saved: pilot idea {id} tidy --save writes it (through the same up-to-date check as an edit), pilot idea {id} tidy --discard drops it");
                }
                else
                {
                    IdeaTidyCache.Discard(target);
                    await output.WriteLineAsync($"{id} is tidy already: nothing to change");
                }
            }
            if (verb == Verbs.SaveTidyIdea && result.Made is { Length: > 0 } tidied)
            {
                if (result.Way == IdeaEditWays.Asked)
                {
                    await output.WriteLineAsync($"asked: the tidy and GitHub's contradict, nothing is saved until card {tidied} is answered");
                }
                else
                {
                    await output.WriteLineAsync($"{tidied} {result.Way}");
                }
            }
            if (verb == Verbs.EditIdea && result.Made is { Length: > 0 } edited)
            {
                if (result.Way == IdeaEditWays.Asked)
                {
                    await output.WriteLineAsync($"asked: your edit and GitHub's contradict, nothing is saved until card {edited} is answered");
                    if ((await SnapshotAsync(client, cancellationToken)).Cards.FirstOrDefault(c => c.Id == edited) is { } asked)
                    {
                        await output.WriteLineAsync($"{asked.Id}  {asked.Title}");
                        if (asked.Detail is { Length: > 0 } detail)
                        {
                            await output.WriteLineAsync(detail);
                        }
                        await output.WriteLineAsync($"choices: {string.Join(", ", asked.Choices.Select((c, i) => $"{i + 1} {c.Label}"))}");
                    }
                }
                else
                {
                    await output.WriteLineAsync($"{edited} {result.Way}");
                }
            }
            if (verb is Verbs.Overlap or Verbs.Touches or Verbs.Separate or Verbs.ConnectOverlap or Verbs.Connect or Verbs.Disconnect or Verbs.WaitLeg or Verbs.Summarise or Verbs.ReviewCost or Verbs.Order or Verbs.OrderDone or Verbs.Orders && result.Made is { Length: > 0 } told)
            {
                await output.WriteLineAsync(told);
            }
            if (verb == Verbs.CreateFlight && result.Way is { Length: > 0 } routed)
            {
                // Made on another node of the fleet, as this one is in maintenance: its code is not in this node's flights, so there is
                // no row to show here, only where it went.
                await output.WriteLineAsync(routed);
            }
            else if (verb is Verbs.CreateFlight or Verbs.PlanIdea or Verbs.OpenPr)
            {
                await output.WriteAsync(Status(await SnapshotAsync(client, cancellationToken), newestOnly: verb != Verbs.OpenPr, code: verb == Verbs.OpenPr ? target[Verbs.FlightTargetPrefix.Length..] : null));
            }
            return 0;
        }
        await output.WriteLineAsync($"refused: {verb} {target}: {envelope.Response?.Error ?? envelope.Error}");
        return 1;
    }

    internal static async Task<SnapshotEvent> SnapshotAsync(NodeClient client, CancellationToken cancellationToken)
    {
        await using var events = await client.OpenEventsAsync(since: null, stream: null, cancellationToken);
        return await events.ReadAsync(cancellationToken) as SnapshotEvent
            ?? throw new IOException("the node's stream did not start with a snapshot");
    }
}
