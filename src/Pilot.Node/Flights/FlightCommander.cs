using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The flight verbs: <c>create-flight</c>, <c>connect</c> and <c>land</c>. Git work takes seconds, so each runs off the
/// state's lock, one at a time (two creates must not claim one code), and puts its result on the stream when done.
/// </summary>
public sealed class FlightCommander(NodeState state, Planes planes, TimeProvider time)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Whether <paramref name="verb"/> is one of these.</summary>
    public static bool Handles(string verb) => verb is Verbs.CreateFlight or Verbs.Connect or Verbs.Land;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            string? error = null;
            try
            {
                switch (command.Verb)
                {
                    case Verbs.CreateFlight:
                        await CreateAsync(command, cancellationToken);
                        break;
                    case Verbs.Connect:
                        Connect(command);
                        break;
                    case Verbs.Land:
                        await LandAsync(command, cancellationToken);
                        break;
                }
            }
            catch (FlightException ex)
            {
                error = ex.Message;
            }
            return state.Complete(command, error);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task CreateAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (!command.Target.StartsWith(Verbs.RepoTargetPrefix, StringComparison.Ordinal) || command.Target.Length == Verbs.RepoTargetPrefix.Length)
        {
            throw new FlightException($"create-flight takes a repo target, {Verbs.RepoTargetPrefix}<path to its checkout>, not {command.Target}");
        }
        var args = command.Args ?? [];
        if (args.Length == 0 || !int.TryParse(args[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var issue) || issue <= 0)
        {
            throw new FlightException("create-flight needs the issue number first, as in create-flight repo/<path> 1611 [title]");
        }

        var path = command.Target[Verbs.RepoTargetPrefix.Length..];
        var repoPath = await planes.MainCheckoutAsync(path, cancellationToken)
            ?? throw new FlightException($"{path} is no git checkout");
        var repo = Path.GetFileName(repoPath);
        var flights = state.Flights();
        var prefix = FlightCodes.PrefixFor(repo, flights);
        var code = FlightCodes.Next(prefix, issue, flights)
            ?? throw new FlightException($"{prefix}{issue} and its suffixes {FlightCodes.Suffixes} are all taken");

        var plane = await planes.MakeAsync(repoPath, code, cancellationToken);
        var title = string.Join(' ', args.Skip(1)).Trim();
        state.PutFlight(new FlightRow
        {
            Code = code,
            Repo = repo,
            RepoPath = plane.RepoPath,
            Issue = issue,
            Title = title.Length > 0 ? title : null,
            Branch = plane.Branch,
            Worktree = plane.Worktree,
            Base = plane.Base,
            State = FlightState.Flying,
            CreatedAt = time.GetUtcNow(),
        });
    }

    private void Connect(CommandRequest command)
    {
        var flight = Find(command.Target);
        var other = (command.Args ?? []) is [{ } code, ..] ? state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) : null;
        if (other is null)
        {
            throw new FlightException($"connect needs the code of a flight {flight.Code} waits for, as in connect flight/{flight.Code} TIA1611");
        }
        if (other.Code == flight.Code || WaitsFor(other, flight.Code))
        {
            throw new FlightException($"{flight.Code} cannot wait for {other.Code}: {other.Code} already waits for {flight.Code}, so neither would leave");
        }
        if (flight.Connections.Contains(other.Code, StringComparer.Ordinal))
        {
            return;
        }
        state.PutFlight(Settle(flight with { Connections = [.. flight.Connections, other.Code] }));
    }

    private async Task LandAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = Find(command.Target);
        if (flight.State == FlightState.Landed)
        {
            return;
        }
        await planes.RemoveAsync(flight.RepoPath, flight.Worktree, cancellationToken);
        state.PutFlight(flight with { State = FlightState.Landed, LandedAt = time.GetUtcNow() });

        // Flights that waited for it may leave now.
        foreach (var waiting in state.Flights().Where(f => f.State == FlightState.Held && f.Connections.Contains(flight.Code, StringComparer.Ordinal)))
        {
            state.PutFlight(Settle(waiting));
        }
    }

    /// <summary>A flight held while any flight it connects from has not landed, flying otherwise; a landed one stays landed.</summary>
    private FlightRow Settle(FlightRow flight)
    {
        if (flight.State == FlightState.Landed)
        {
            return flight;
        }
        var flights = state.Flights().ToDictionary(f => f.Code, StringComparer.Ordinal);
        var held = flight.Connections.Any(c => !flights.TryGetValue(c, out var other) || other.State != FlightState.Landed);
        return flight with { State = held ? FlightState.Held : FlightState.Flying };
    }

    /// <summary>Whether <paramref name="flight"/> waits for <paramref name="code"/>, directly or through the flights it waits for.</summary>
    private bool WaitsFor(FlightRow flight, string code)
    {
        var flights = state.Flights().ToDictionary(f => f.Code, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(flight.Connections);
        while (queue.TryDequeue(out var next))
        {
            if (next == code)
            {
                return true;
            }
            if (seen.Add(next) && flights.TryGetValue(next, out var row))
            {
                foreach (var c in row.Connections)
                {
                    queue.Enqueue(c);
                }
            }
        }
        return false;
    }

    private FlightRow Find(string target)
    {
        if (!target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            throw new FlightException($"this verb takes a flight target, {Verbs.FlightTargetPrefix}<code>, not {target}");
        }
        var code = target[Verbs.FlightTargetPrefix.Length..];
        return state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlightException($"There is no flight {code}");
    }
}
