using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Cli;

/// <summary>
/// The <c>pilot</c> command. Each subcommand is one node command (a verb on a target) or a read of the node's snapshot;
/// the node decides and refuses, this only spells it. Inside a plane a leg command needs no leg named: the flight is the
/// one whose worktree holds the folder, and the leg its one flying, which is how a worker ticks its own checklist.
/// </summary>
public static class PilotCommand
{
    public const string Usage = """
        usage: pilot [--node-socket <path>] <command>

          status                                   flights, their legs, and live sessions
          sessions                                 every session on the machine
          flight create <issue> [title…] [--repo <path>]
          flight connect <code> <other>            <code> holds until <other> lands
          flight pr <code> [title…]                push the plane and open its PR
          flight merge <code> <head> [--squash|--rebase|--merge]
          flight land <code>
          leg plan <code> <name> --goal <text> --done-when <text> [--check <command>] [--budget <tokens>]
                   [--model <m>] [--effort <e>] [--item <step>]…
          leg start <code> <leg>
          leg finish done|failed [report…] [--leg <code>/<leg>]
          leg go-around <why…> [--leg <code>/<leg>]
          check <step number or text> [--undo] [--leg <code>/<leg>]
          artifact <path or URL> [title…] [--leg <code>/<leg>]
          say <session> <text…>
          note <text…>

        Inside a flight's plane, --leg defaults to its flying leg.
        """;

    public static async Task<int> RunAsync(string[] args, string cwd, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var rest = args.ToList();
        var socket = Take(rest, "--node-socket");
        if (rest.Count == 0 || rest[0] is "-h" or "--help" or "help")
        {
            await output.WriteLineAsync(Usage);
            return rest.Count == 0 ? 2 : 0;
        }

        using var client = new NodeClient(NodeSocket.Resolve(socket));
        try
        {
            return await DispatchAsync(rest, client, cwd, output, cancellationToken);
        }
        catch (UsageException ex)
        {
            await error.WriteLineAsync($"pilot: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or IOException or System.Net.WebSockets.WebSocketException)
        {
            await error.WriteLineAsync($"pilot: no node answers on {client.SocketPath} ({ex.Message}); start it with pilot-gui or pilot-node");
            return 3;
        }
    }

    private static async Task<int> DispatchAsync(List<string> args, NodeClient client, string cwd, TextWriter output, CancellationToken cancellationToken)
    {
        var command = args[0];
        args.RemoveAt(0);
        switch (command)
        {
            case "status":
                await output.WriteAsync(Status(await SnapshotAsync(client, cancellationToken)));
                return 0;
            case "sessions":
                await output.WriteAsync(Sessions(await client.GetSessionsAsync(cancellationToken)));
                return 0;
            case "say" when args.Count >= 2:
                return await SendAsync(client, Verbs.Say, Verbs.SessionTarget(args[0]), [string.Join(' ', args.Skip(1))], "cli", output, cancellationToken);
            case "note" when args.Count >= 1:
                return await SendAsync(client, Verbs.Note, Verbs.NodeTarget, [string.Join(' ', args)], "cli", output, cancellationToken);
            case "flight" when args.Count >= 1:
                return await FlightAsync(args, client, cwd, output, cancellationToken);
            case "leg" when args.Count >= 1:
                return await LegAsync(args, client, cwd, output, cancellationToken);
            case "check" or "artifact":
                return await OnLegAsync(command, args, client, cwd, output, cancellationToken);
            default:
                throw new UsageException($"unknown or incomplete command '{command}'. pilot --help lists them");
        }
    }

    private static async Task<int> FlightAsync(List<string> args, NodeClient client, string cwd, TextWriter output, CancellationToken cancellationToken)
    {
        var sub = args[0];
        args.RemoveAt(0);
        switch (sub)
        {
            case "create" when args.Count >= 1:
                var repo = Take(args, "--repo") ?? cwd;
                return await SendAsync(client, Verbs.CreateFlight, Verbs.RepoTarget(Path.GetFullPath(repo, cwd)), [.. args], "cli", output, cancellationToken);
            case "connect" when args.Count == 2:
                return await SendAsync(client, Verbs.Connect, Verbs.FlightTarget(args[0]), [args[1]], "cli", output, cancellationToken);
            case "pr" when args.Count >= 1:
                return await SendAsync(client, Verbs.OpenPr, Verbs.FlightTarget(args[0]), [.. args.Skip(1)], "cli", output, cancellationToken);
            case "merge" when args.Count >= 2:
                var method = Flag(args, "--rebase") ? "rebase" : Flag(args, "--merge") ? "merge" : Flag(args, "--squash") ? "squash" : null;
                string[] mergeArgs = method is null ? [args[1]] : [args[1], method];
                return await SendAsync(client, Verbs.Merge, Verbs.FlightTarget(args[0]), mergeArgs, "cli", output, cancellationToken);
            case "land" when args.Count == 1:
                return await SendAsync(client, Verbs.Land, Verbs.FlightTarget(args[0]), [], "cli", output, cancellationToken);
            default:
                throw new UsageException($"unknown or incomplete 'flight {sub}'. pilot --help lists them");
        }
    }

    private static async Task<int> LegAsync(List<string> args, NodeClient client, string cwd, TextWriter output, CancellationToken cancellationToken)
    {
        var sub = args[0];
        args.RemoveAt(0);
        switch (sub)
        {
            case "plan" when args.Count >= 2:
                var plan = new List<string> { $"name={args[1]}" };
                foreach (var (option, key) in (ReadOnlySpan<(string, string)>)[("--goal", "goal"), ("--done-when", "done-when"), ("--check", "check"),
                             ("--budget", "budget"), ("--model", "model"), ("--effort", "effort")])
                {
                    if (Take(args, option) is { } value)
                    {
                        plan.Add($"{key}={value}");
                    }
                }
                while (Take(args, "--item") is { } item)
                {
                    plan.Add($"item={item}");
                }
                return await SendAsync(client, Verbs.PlanLeg, Verbs.FlightTarget(args[0]), [.. plan], "cli", output, cancellationToken);
            case "start" when args.Count == 2:
                return await SendAsync(client, Verbs.StartLeg, Verbs.LegTarget(args[0], args[1]), [], "cli", output, cancellationToken);
            case "finish" or "go-around":
                return await OnLegAsync(sub, args, client, cwd, output, cancellationToken);
            default:
                throw new UsageException($"unknown or incomplete 'leg {sub}'. pilot --help lists them");
        }
    }

    /// <summary>A command on a leg: the one <c>--leg</c> names, else the flying leg of the plane the folder is in.</summary>
    private static async Task<int> OnLegAsync(string command, List<string> args, NodeClient client, string cwd, TextWriter output, CancellationToken cancellationToken)
    {
        var named = Take(args, "--leg");
        var undo = Flag(args, "--undo");
        var snapshot = await SnapshotAsync(client, cancellationToken);
        var (target, who) = named is not null ? (Verbs.FlightTargetPrefix + named, "cli") : LegHere(snapshot, cwd);
        switch (command)
        {
            case "check" when args.Count >= 1:
                return await SendAsync(client, Verbs.Check, target, undo ? [string.Join(' ', args), "undo"] : [string.Join(' ', args)], who, output, cancellationToken);
            case "artifact" when args.Count >= 1:
                return await SendAsync(client, Verbs.AddArtifact, target, [Path.IsPathRooted(args[0]) || args[0].Contains("://", StringComparison.Ordinal) ? args[0] : Path.GetFullPath(args[0], cwd), .. args.Skip(1)], who, output, cancellationToken);
            case "finish" when args.Count >= 1 && args[0] is "done" or "failed":
                return await SendAsync(client, Verbs.FinishLeg, target, [.. args], who, output, cancellationToken);
            case "go-around" when args.Count >= 1:
                return await SendAsync(client, Verbs.GoAround, target, [.. args], who, output, cancellationToken);
            default:
                throw new UsageException($"incomplete '{command}'. pilot --help lists it");
        }
    }

    /// <summary>
    /// The leg the folder <paramref name="cwd"/> works on: the flying leg of the flight whose plane holds it, and who is
    /// asking, its worker by name, so the black box says which worker ticked what.
    /// </summary>
    internal static (string Target, string Who) LegHere(SnapshotEvent snapshot, string cwd)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the leg with --leg <code>/<leg>");
        var leg = snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Flying)
            ?? throw new UsageException($"{flight.Code} has no leg flying; name it with --leg {flight.Code}/<leg>");
        return (Verbs.LegTarget(flight.Code, leg.Name), leg.Worker ?? "cli");
    }

    private static async Task<int> SendAsync(NodeClient client, string verb, string target, string[] args, string who, TextWriter output, CancellationToken cancellationToken)
    {
        var envelope = await client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, cancellationToken);
        if (envelope.Success && envelope.Response is { Ok: true } result)
        {
            await output.WriteLineAsync($"ok: {verb} {target}{(result.Way is { } way ? $" ({way})" : "")}");
            if (verb is Verbs.CreateFlight or Verbs.OpenPr)
            {
                await output.WriteAsync(Status(await SnapshotAsync(client, cancellationToken), newestOnly: verb == Verbs.CreateFlight, code: verb == Verbs.OpenPr ? target[Verbs.FlightTargetPrefix.Length..] : null));
            }
            return 0;
        }
        await output.WriteLineAsync($"refused: {verb} {target}: {envelope.Response?.Error ?? envelope.Error}");
        return 1;
    }

    private static async Task<SnapshotEvent> SnapshotAsync(NodeClient client, CancellationToken cancellationToken)
    {
        await using var events = await client.OpenEventsAsync(since: null, stream: null, cancellationToken);
        return await events.ReadAsync(cancellationToken) as SnapshotEvent
            ?? throw new IOException("the node's stream did not start with a snapshot");
    }

    /// <summary>The flights and their legs, a line each, and the live sessions.</summary>
    internal static string Status(SnapshotEvent snapshot, bool newestOnly = false, string? code = null)
    {
        var text = new System.Text.StringBuilder();
        var flights = snapshot.Flights.OrderBy(f => f.State == FlightState.Landed).ThenByDescending(f => f.CreatedAt).ToList();
        if (newestOnly)
        {
            flights = [.. flights.OrderByDescending(f => f.CreatedAt).Take(1)];
        }
        if (code is not null)
        {
            flights = [.. flights.Where(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))];
        }
        foreach (var flight in flights)
        {
            var state = flight.State switch
            {
                FlightState.Held => $"held, waiting for {string.Join(", ", flight.Connections)}",
                FlightState.Landed => "landed",
                _ => "flying",
            };
            text.AppendLine($"{flight.Code}  {flight.Title ?? $"issue #{flight.Issue}"}  ({flight.Repo} #{flight.Issue}, {state})");
            text.AppendLine($"  plane {flight.Worktree} on {flight.Branch}{(flight.PullRequestUrl is { } url ? $", PR {url}" : "")}");
            foreach (var leg in snapshot.Legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
            {
                var steps = leg.Checklist.Length == 0 ? "" : $", {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
                var goArounds = leg.GoArounds > 0 ? $", go-around {leg.GoArounds}" : "";
                text.AppendLine($"  {leg.Order}. {leg.Name}: {leg.State.ToString().ToLowerInvariant()}{steps}{goArounds}{(leg.Worker is { } w && leg.State == LegState.Flying ? $", worker {w}" : "")}");
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
                text.AppendLine("No flights.");
            }
            var live = snapshot.Sessions.Where(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();
            text.AppendLine($"{live.Count} live {(live.Count == 1 ? "session" : "sessions")}{(live.Count > 0 ? ": " + string.Join(", ", live.Select(s => $"{s.Name} ({s.State.ToString().ToLowerInvariant()})")) : "")}");
        }
        return text.ToString();
    }

    /// <summary>Every session, a line each: name, role and flight, state, model, context and spend.</summary>
    internal static string Sessions(IReadOnlyList<SessionRow> rows)
    {
        var text = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            var flight = row.Flight is null ? "" : $" {row.Flight}{(row.Leg is { } leg ? "/" + leg : "")}";
            var budget = row.Budget is { } b ? $" of {b.ToString("N0", CultureInfo.InvariantCulture)}" : "";
            text.AppendLine($"{row.Id}  {row.Name}  {row.Role.ToString().ToLowerInvariant()}{flight}  {row.State.ToString().ToLowerInvariant()}  {row.Model ?? "-"}  {row.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)}/{row.ContextWindow.ToString("N0", CultureInfo.InvariantCulture)}  {row.SpendWeighted.ToString("N0", CultureInfo.InvariantCulture)}{budget}");
        }
        return text.Length == 0 ? "No sessions.\n" : text.ToString();
    }

    private static string? Take(List<string> args, string option)
    {
        var at = args.IndexOf(option);
        if (at < 0)
        {
            return null;
        }
        if (at + 1 >= args.Count)
        {
            throw new UsageException($"{option} needs a value");
        }
        var value = args[at + 1];
        args.RemoveRange(at, 2);
        return value;
    }

    private static bool Flag(List<string> args, string flag) => args.Remove(flag);

    private sealed class UsageException(string message) : Exception(message);
}
