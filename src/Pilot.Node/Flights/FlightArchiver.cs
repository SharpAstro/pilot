using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The archive verbs (PIL1083): <c>archive-flight flight/&lt;code&gt; [undo]</c>, and the two reads of the archive, <c>archived-flights</c> (a page of
/// its index) and <c>archived-flight</c> (one flight whole). <see cref="WhyNotAsync"/> is the one answer to why a landed flight cannot be
/// archived yet, used by the verb and by the sweep of a later leg, so the two cannot drift apart.
/// </summary>
/// <param name="git">Answers whether the flight's branch is still there; null where there is no git (the branch is then not asked about).</param>
/// <param name="removals">The folders waiting for Windows to let them go; a flight whose plane is in it is not archived yet. Null where there is none.</param>
public sealed class FlightArchiver(NodeState state, IGit? git = null, RemovalQueue? removals = null)
{
    /// <summary>How many rows <c>archived-flights</c> gives when it is not asked for a count.</summary>
    public const int DefaultPage = 50;

    /// <summary>Whether <paramref name="verb"/> is one of these.</summary>
    public static bool Handles(string verb) => verb is Verbs.ArchiveFlight or Verbs.ArchivedFlights or Verbs.ArchivedFlight;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        try
        {
            return command.Verb switch
            {
                Verbs.ArchiveFlight => await ArchiveAsync(command, cancellationToken),
                Verbs.ArchivedFlights => ReadPage(command),
                _ => ReadOne(command),
            };
        }
        catch (FlightException ex)
        {
            return state.Complete(command, ex.Message);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return state.Complete(command, $"{command.Verb} failed: {ex.Message}");
        }
    }

    private static string CodeOf(CommandRequest command) =>
        Verbs.FlightCodeOf(command.Target) is { Length: > 0 } code && !code.Contains('/')
            ? code
            : throw new FlightException($"{command.Verb} takes a flight target, {Verbs.FlightTargetPrefix}<code>, not {command.Target}");

    private async Task<CommandResult> ArchiveAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var code = CodeOf(command);
        if ((command.Args ?? []).Contains("undo", StringComparer.Ordinal))
        {
            var back = state.RestoreFlight(code);
            return state.Complete(command, null, way: $"{back.Code} is back in the flights");
        }
        if (state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) is not { } flight)
        {
            return state.Complete(command, state.ArchivedRow(code) is not null ? $"{code} is archived already" : $"There is no flight {code}");
        }
        if (await WhyNotAsync(flight, cancellationToken) is { } why)
        {
            return state.Complete(command, $"{flight.Code} is not archived: {why}");
        }
        var row = state.ArchiveFlight(flight.Code);
        return state.Complete(command, null, way: $"{row.Code} archived");
    }

    private CommandResult ReadPage(CommandRequest command)
    {
        var args = command.Args ?? [];
        var offset = args.Length > 0 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var count = args.Length > 1 && int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var c) && c > 0 ? c : DefaultPage;
        var rows = state.ArchivedFlightRows();
        return state.Complete(command, null) with { ArchivedFlights = new ArchivedFlightsPage { Rows = [.. rows.Skip(offset).Take(count)], Total = rows.Length } };
    }

    private CommandResult ReadOne(CommandRequest command)
    {
        var code = CodeOf(command);
        var whole = state.ArchivedRow(code) is { } row ? state.Archive?.Read(row.Code) : null;
        return whole is null
            ? state.Complete(command, $"{code} is not archived")
            : state.Complete(command, null) with { ArchivedFlight = whole };
    }

    /// <summary>
    /// Why <paramref name="flight"/> cannot be archived yet, or null when it can: it has not landed; its PR is not merged; a card about it is
    /// open; a session of it is live; its plane's folder is still on disk or waits in the removal queue; or its branch is still there.
    /// </summary>
    public async Task<string?> WhyNotAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.State != FlightState.Landed)
        {
            return $"it has not landed ({flight.State.ToString().ToLowerInvariant()})";
        }
        // Folded into another flight or moved to another node, its PR is the other's; otherwise the node's merge entry says whether it merged.
        if (flight.PullRequest is { } pr && flight.FoldedInto is null && flight.MovedTo is null)
        {
            if (state.Merges().FirstOrDefault(m => string.Equals(m.Flight, flight.Code, StringComparison.Ordinal)) is { State: not MergeEntryState.Merged } entry)
            {
                return $"its PR #{pr.ToString(CultureInfo.InvariantCulture)} is not merged (its merge entry is {entry.State.ToString().ToLowerInvariant()})";
            }
            if (flight.MergeQueue is { Length: > 0 } queued)
            {
                return $"its PR #{pr.ToString(CultureInfo.InvariantCulture)} is not merged ({queued})";
            }
        }
        if (state.Cards(openOnly: true).Where(c => FlightMove.IsAbout(c, flight.Code)).ToList() is { Count: > 0 } open)
        {
            return $"{(open.Count == 1 ? "a card is" : $"{open.Count} cards are")} open about it ({string.Join(", ", open.Select(c => c.Id))})";
        }
        if (state.Sessions().Where(s => string.Equals(s.Flight, flight.Code, StringComparison.OrdinalIgnoreCase) && s.State is not (SessionState.Ended or SessionState.Failed)).ToList() is { Count: > 0 } live)
        {
            return $"{(live.Count == 1 ? "a session is" : $"{live.Count} sessions are")} live ({string.Join(", ", live.Select(s => s.Name))})";
        }
        if (Directory.Exists(flight.Worktree))
        {
            return $"its plane folder {flight.Worktree} is still on disk";
        }
        if (removals?.Contains(flight.Worktree) == true)
        {
            return $"its plane folder {flight.Worktree} waits in the removal queue";
        }
        // A repo folder that is gone has no branch to wait for (and git cannot start in it).
        if (git is not null && Directory.Exists(flight.RepoPath))
        {
            foreach (var reference in new[] { $"refs/heads/{flight.Branch}", $"refs/remotes/origin/{flight.Branch}" })
            {
                if ((await git.RunAsync(["show-ref", "--verify", "--quiet", reference], flight.RepoPath, cancellationToken)).Ok)
                {
                    return $"its branch {flight.Branch} is not removed yet";
                }
            }
        }
        return null;
    }
}
