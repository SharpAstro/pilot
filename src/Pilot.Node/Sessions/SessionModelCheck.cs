namespace Pilot.Node.Sessions;

/// <summary>
/// Compares what a live session runs on (the model and effort its transcript shows) with what the node started it on (the
/// options <see cref="StartOptionsBook"/> recorded). No model change mid-session is the owner's rule, so a difference is
/// said, never fixed in place: the watchtower is replaced between turns, any other session is resumed as a copy on the
/// recorded options at its next wake.
/// </summary>
public static class SessionModelCheck
{
    private const string Window = "[1m]";

    /// <summary>The value after <paramref name="flag"/> in recorded options, or null.</summary>
    public static string? Flag(IReadOnlyList<string> arguments, string flag)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i] == flag)
            {
                return arguments[i + 1];
            }
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="ran"/> (a full id such as <c>claude-opus-5-5[1m]</c>) is another model than <paramref name="asked"/>: by
    /// <see cref="RoleModels.ModelRank"/>, and by the 1M window when it ran with one that was not asked for. Not judged (false)
    /// when either is unknown, or the model is not one of the three families.
    /// </summary>
    public static bool ModelDiffers(string? asked, string? ran)
    {
        if (string.IsNullOrEmpty(asked) || string.IsNullOrEmpty(ran))
        {
            return false;
        }
        var wanted = RoleModels.ModelRank(asked);
        var got = RoleModels.ModelRank(ran);
        if (wanted < 0 || got < 0)
        {
            return false;
        }
        // A transcript may drop the window suffix, so only a window that was not asked for counts.
        return wanted != got || (ran.Contains(Window, StringComparison.OrdinalIgnoreCase) && !asked.Contains(Window, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a row runs on that differs from what it was started on, in words, or null when it matches or cannot be judged.</summary>
    public static string? Mismatch(Pilot.Contracts.SessionRow row, SessionStartOptions start)
    {
        var parts = new List<string>();
        if (Flag(start.Arguments, "--model") is { } model && ModelDiffers(model, row.Model))
        {
            parts.Add($"model {row.Model}, started on {model}");
        }
        if (Flag(start.Arguments, "--effort") is { } effort && !string.IsNullOrEmpty(row.Effort)
            && !string.Equals(effort, row.Effort, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"effort {row.Effort}, started on {effort}");
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
