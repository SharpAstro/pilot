using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's answer to a Stop hook: a session ending its turn in a flight's plane with files not committed there is
/// told to commit them and carries on, once per turn; a second stop is let through and rings, so a
/// session that will not commit cannot loop. Nothing is left behind starts here, before a leg finishes or a flight lands.
/// </summary>
/// <remarks>
/// Only a plane the node flies counts: Claude Code makes its own worktrees under <c>.claude/worktrees</c> too, and a
/// session in one of those is none of the node's business. Every other session, and a hook it cannot read, gets no
/// answer, which lets the stop through.
/// </remarks>
public sealed class StopGate(NodeState state, Planes planes)
{
    /// <summary>The ring raised when a session stops a second time with the files still there.</summary>
    public const string Kind = "stopped-uncommitted";

    /// <summary>
    /// The Stop decision for <paramref name="hook"/>, as Claude Code reads it on stdout
    /// (<c>{"decision":"block","reason":…}</c>), or null to let the stop through.
    /// </summary>
    public async Task<string?> DecideAsync(JsonElement hook, CancellationToken cancellationToken)
    {
        if (hook.ValueKind != JsonValueKind.Object
            || ClaudeAgentsJson.Text(hook, "hook_event_name") != "Stop"
            || ClaudeAgentsJson.Text(hook, "session_id") is not { Length: > 0 } sessionId
            || ClaudeAgentsJson.Text(hook, "cwd") is not { Length: > 0 } cwd
            // A subagent's stop is its parent's business, as in HookBook.
            || ClaudeAgentsJson.Text(hook, "agent_id") is { Length: > 0 }
            || PlaneAt(state, cwd) is not { } flight)
        {
            return null;
        }

        var uncommitted = await planes.UncommittedAsync(flight.Worktree, cancellationToken);
        var subject = Subject(flight);
        if (uncommitted is null)
        {
            state.CloseCardsAbout(subject, "committed", Kind);
            return null;
        }

        // Work is committed, never dropped: a file that is clearly not work (build output, a scratch file) goes in .gitignore or is
        // named in the commit message, and nothing is deleted without someone having looked at it.
        var reason = $"{uncommitted} in {flight.Worktree}: commit them before you stop. A file that is clearly not work (build output, a scratch file) goes in .gitignore or is named in the commit message; never delete a file silently. Nothing is left behind in a plane.";
        // Claude Code sets stop_hook_active on a stop that a Stop hook already sent back to work: told once this turn.
        if (hook.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True)
        {
            var files = await planes.UncommittedFilesAsync(flight.Worktree, cancellationToken);
            state.RaiseCard(CardBin.Ring, Kind, subject, $"{flight.Code}: a session stopped with {uncommitted}", $"it was told once to commit them and stopped again, so the node will not tell it a third time by itself",
                [new CardChoice { Label = $"Tell the session to commit its files in {flight.Code}'s plane", Verb = Verbs.Say, Target = Verbs.SessionTarget(sessionId), Args = [reason] }],
                summary: Summary(flight.Code, files));
            return null;
        }
        return Decision(reason);
    }

    /// <summary>
    /// What the ring says: which files (their names, or how many plus the first few), that the session was told once and stopped
    /// again, and that they stay in the plane until committed. It does not repeat the choice's label.
    /// </summary>
    internal static string Summary(string code, IReadOnlyList<string> porcelain)
    {
        // Names only, never a path: a path of three folders would read as a log line and the summary would be thrown away.
        // A porcelain line is two status letters, a space, then the path (a rename "old -> new": the new one).
        var names = porcelain
            .Select(l => l.Length > 3 ? l[3..] : l)
            .Select(p => p.Contains(" -> ", StringComparison.Ordinal) ? p[(p.LastIndexOf(" -> ", StringComparison.Ordinal) + 4)..] : p)
            .Select(p => Path.GetFileName(p.Trim().Trim('"').TrimEnd('/', '\\')))
            .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var count = porcelain.Count;
        var which = count == 1 && names.Count == 1
            ? $"{names[0]}"
            : $"{count} files, {string.Join(", ", names.Take(3))}{(count > 3 ? " and more" : "")}";
        return $"The worker of {code} was told once to commit {which} and stopped again with {(count == 1 ? "it" : "them")} still there. It can be told once more; the files stay in the plane until they are committed.";
    }

    /// <summary>The flight not landed whose plane holds <paramref name="cwd"/>, the plane itself or a folder in it.</summary>
    internal static FlightRow? PlaneAt(NodeState state, string cwd)
    {
        string folder;
        try
        {
            folder = Normal(cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var comparison = PathComparison;
        return state.Flights().FirstOrDefault(f => f.State != FlightState.Landed && Normal(f.Worktree) is var plane
            && (folder.Equals(plane, comparison) || folder.StartsWith(plane + Path.DirectorySeparatorChar, comparison)));
    }

    /// <summary>How paths compare on this machine: case-insensitive on Windows.</summary>
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>The flying leg, whose worker it most likely is, else the flight: its cards close when that finishes or lands.</summary>
    private string Subject(FlightRow flight) =>
        state.Legs(flight.Code).FirstOrDefault(l => l.State == LegState.Flying) is { } leg
            ? Verbs.LegTarget(flight.Code, leg.Name)
            : Verbs.FlightTarget(flight.Code);

    // Written through a Utf8JsonWriter, which needs no serializer metadata, as HookCommands does.
    private static string Decision(string reason)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("decision", "block");
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
