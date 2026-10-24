using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The flight verbs: <c>create-flight</c>, <c>connect</c> and <c>land</c>. Git work takes seconds, so each runs off the
/// state's lock, one at a time (two creates must not claim one code), and puts its result on the stream when done.
/// </summary>
/// <param name="workers">Stops the flight's sessions before its plane goes; null in a test that starts none.</param>
/// <param name="github">Gives a flight made without a title its issue's; null in a test that names its titles.</param>
public sealed class FlightCommander(NodeState state, Planes planes, TimeProvider time, IWorkerLauncher? workers = null, IGitHub? github = null)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The settings in force: whether the node is in maintenance. A test's commander takes every flight.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>The machine's prefix, as a refusal names it.</summary>
    public string Machine { get; set; } = "this node";

    /// <summary>The quiet card a flight folded into another leaves in place of a landed one.</summary>
    public const string FoldedKind = "folded";

    /// <summary>Whether <paramref name="verb"/> is one of these.</summary>
    public static bool Handles(string verb) => verb is Verbs.CreateFlight or Verbs.Connect or Verbs.Land;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            string? error = null;
            string? made = null;
            try
            {
                switch (command.Verb)
                {
                    case Verbs.CreateFlight:
                        made = await CreateAsync(command, cancellationToken);
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
            return state.Complete(command, error, made: made);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Makes the flight; returns its code, which the result carries as what it made.</summary>
    private async Task<string> CreateAsync(CommandRequest command, CancellationToken cancellationToken)
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

        // Only a new flight is refused: flying and held flights, legs, reviews and landings never come through here. The next
        // leg of this flight routes a flight to a node that takes it, instead of refusing, at this seam.
        if (Models.Maintenance)
        {
            throw new FlightException($"{Machine} is in maintenance: it takes no new flights");
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
        if (title.Length == 0 && github is not null)
        {
            // No title given: the issue's own, so the flight reads as what it is about.
            title = (await github.IssueTitleAsync(plane.RepoPath, issue, cancellationToken))?.Trim() ?? "";
        }
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
        return code;
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

    /// <summary>
    /// Lands <paramref name="code"/> as <c>land</c> does, for a merge that has just put its work in: one at a time with
    /// the flight verbs. Throws <see cref="FlightException"/> with why it could not.
    /// </summary>
    /// <param name="mergedHead">The head the PR merged at: the plane may hold nothing past it.</param>
    public async Task LandAfterMergeAsync(string code, string mergedHead, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            await LandFlightAsync(Find(Verbs.FlightTarget(code)), cancellationToken, mergedHead);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Retires <paramref name="flight"/> as folded into <paramref name="other"/>: landed with nothing of its own, as <c>land</c>
    /// does (its sessions stopped, its clean plane removed, its code and branch kept), marked with the flight it went into, and a
    /// quiet card saying so instead of a landed one. One at a time with the flight verbs. Throws <see cref="FlightException"/>
    /// with why it could not, and then changes nothing.
    /// </summary>
    public async Task RetireFoldedAsync(FlightRow flight, string other, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            await LandFlightAsync(flight, cancellationToken, foldedInto: other);
        }
        finally
        {
            _one.Release();
        }
    }

    private Task LandAsync(CommandRequest command, CancellationToken cancellationToken) => LandFlightAsync(Find(command.Target), cancellationToken);

    private async Task LandFlightAsync(FlightRow flight, CancellationToken cancellationToken, string? mergedHead = null, string? foldedInto = null)
    {
        if (flight.State == FlightState.Landed)
        {
            return;
        }
        // Its sessions first: a worker inside its stop grace, or a reviewer, still has the plane open, and a folder held
        // open is not removed. Seen live: a flight landed a minute after its worker finished kept its plane.
        if (workers is not null)
        {
            var jobs = state.Legs(flight.Code).Select(l => l.WorkerJob).Append(flight.Review?.ReviewerJob).OfType<string>().Distinct();
            foreach (var job in jobs)
            {
                try
                {
                    await workers.StopAsync(job, cancellationToken);
                }
                catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Gone already.
                }
            }
        }
        // Nothing of the flight's is left where nobody looks: no commit that is on no remote and not in main (TIA1658's
        // last read was one, until it was landed by hand). Such a plane is not landed; it says which commits.
        // After a merge, everything up to the merged head is in: only commits past it would be left behind.
        var mainRef = flight.Base.Length > 0 ? flight.Base : "origin/main";
        if (mergedHead is not null)
        {
            if (await planes.PastAsync(flight.Worktree, mergedHead, cancellationToken) is { Count: > 0 } past)
            {
                throw new FlightException($"{flight.Code} merged at {mergedHead[..Math.Min(8, mergedHead.Length)]}, but its plane holds {past.Count} commit{(past.Count == 1 ? "" : "s")} past it: {string.Join("; ", past)}. Land {(past.Count == 1 ? "it" : "them")} in a PR of {(past.Count == 1 ? "its" : "their")} own, then land the flight");
            }
        }
        else if (await planes.UnlandedAsync(flight.Worktree, mainRef, cancellationToken) is { Count: > 0 } loose)
        {
            throw new FlightException($"{flight.Code}'s plane holds {loose.Count} commit{(loose.Count == 1 ? "" : "s")} on no remote and not in {mainRef}: {string.Join("; ", loose)}. Push {(loose.Count == 1 ? "it" : "them")} (flight pr) or drop {(loose.Count == 1 ? "it" : "them")}, then land");
        }
        var leftover = await planes.RemoveAsync(flight.RepoPath, flight.Worktree, cancellationToken);
        if (leftover is not null)
        {
            state.RaiseCard(CardBin.ForYou, "plane-left", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed, but its plane folder is still there",
                $"{leftover}; nothing of git's is in it, so delete it by hand once whatever holds it lets go");
        }
        state.PutFlight(flight with { State = FlightState.Landed, LandedAt = time.GetUtcNow(), FoldedInto = foldedInto ?? flight.FoldedInto });

        // Its cards leave with it, and a quiet one says it is in; a folded one says where its work went.
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), foldedInto is null ? $"{flight.Code} landed" : $"{flight.Code} folded into {foldedInto}");
        if (foldedInto is null)
        {
            state.RaiseCard(CardBin.ForYou, "landed", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed",
                $"{flight.Title ?? $"issue #{flight.Issue}"}{(flight.PullRequest is { } number ? $", PR #{number}" : "")}", link: flight.PullRequestUrl);
        }
        else
        {
            state.RaiseCard(CardBin.Handled, FoldedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} folded into {foldedInto}",
                $"{flight.Title ?? $"issue #{flight.Issue}"} (#{flight.Issue}) is now {foldedInto}'s: its PR closes it, and {foldedInto}'s planner plans its points");
        }

        // Flights that waited for it may leave now. A folded flight's issue is the other flight's work and has not landed, so
        // those that waited for it wait for the other flight instead (not for themselves, not twice, not in a cycle).
        foreach (var waitingRow in state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Contains(flight.Code, StringComparer.Ordinal)).ToList())
        {
            var waiting = waitingRow;
            if (foldedInto is not null)
            {
                var target = state.Flights().FirstOrDefault(f => string.Equals(f.Code, foldedInto, StringComparison.OrdinalIgnoreCase));
                var connections = waiting.Connections.Where(c => c != flight.Code).ToList();
                if (target is not null && target.Code != waiting.Code && !connections.Contains(target.Code, StringComparer.Ordinal) && !WaitsFor(target, waiting.Code))
                {
                    connections.Add(target.Code);
                }
                waiting = waiting with { Connections = [.. connections] };
            }
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
