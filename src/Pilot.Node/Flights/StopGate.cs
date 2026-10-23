using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's answer to a Stop hook: a session ending its turn in a flight's plane with files not committed there is
/// told to commit them (or drop them) and carries on, once per turn; a second stop is let through and rings, so a
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
            || PlaneAt(cwd) is not { } flight)
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

        var reason = $"{uncommitted} in {flight.Worktree}: commit them, or drop them, before you stop. Nothing is left behind in a plane.";
        // Claude Code sets stop_hook_active on a stop that a Stop hook already sent back to work: told once this turn.
        if (hook.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True)
        {
            state.RaiseCard(CardBin.Ring, Kind, subject, $"{flight.Code}: a session stopped with files not committed", $"{uncommitted}, told once to commit them",
                [new CardChoice { Label = "Tell it again", Verb = Verbs.Say, Target = Verbs.SessionTarget(sessionId), Args = [reason] }]);
            return null;
        }
        return Decision(reason);
    }

    /// <summary>The flight not landed whose plane holds <paramref name="cwd"/>, the plane itself or a folder in it.</summary>
    private FlightRow? PlaneAt(string cwd)
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
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return state.Flights().FirstOrDefault(f => f.State != FlightState.Landed && Normal(f.Worktree) is var plane
            && (folder.Equals(plane, comparison) || folder.StartsWith(plane + Path.DirectorySeparatorChar, comparison)));
    }

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

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
