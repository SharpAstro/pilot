namespace Pilot.Node.Sessions;

/// <summary>
/// What a permission prompt asks, said in plain words: the one place that turns a tool call (its name and the line the transcript or
/// the PermissionRequest hook kept of its input) into <c>run: git push origin main (Bash)</c>, so the 'waits on you' card of a leg's
/// worker and of any other session say the same thing. The tool stays in brackets so the owner can match it to the prompt.
/// </summary>
public static class PermissionAsks
{
    /// <summary>The longest part of a command, file or url the phrase quotes; the tool in brackets always follows whole.</summary>
    public const int QuoteLength = 100;

    /// <summary>
    /// The phrase for a call, to follow "wants to": <c>run: dotnet test (Bash)</c>. <paramref name="plane"/> is the folder of the leg
    /// the worker flies, so an EnterWorktree into it reads as its own plane.
    /// </summary>
    public static string Describe(string tool, string? summary, string? plane = null)
    {
        var what = summary is { Length: > 0 } s ? s.Trim() : null;
        var tag = $" ({tool})";
        switch (tool)
        {
            case "EnterWorktree":
                return what is null
                    ? "switch its folder to a new worktree" + tag
                    : $"switch its folder to {(SamePlace(what, plane) ? "its own plane" : Quote(what))}{tag}";
            case "Bash" or "PowerShell" when what is not null:
                return $"run: {Quote(what)}{tag}";
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit" when what is not null:
                return $"edit {Quote(what)}{tag}";
            case "Read" or "Glob" or "Grep" when what is not null:
                return $"read {Quote(what)}{tag}";
            case "WebFetch" when what is not null:
                return $"fetch {Quote(what)}{tag}";
            case "WebSearch" when what is not null:
                return $"search the web for {Quote(what)}{tag}";
            default:
                return what is null ? $"use {tool}" : $"use {tool}: {Quote(what)}";
        }
    }

    /// <summary>
    /// The phrase for <see cref="Pilot.Contracts.SessionRow.Asking"/> (<c>Bash: git push origin main</c>, or just the tool's name);
    /// null for a missing one, and for a question (AskUserQuestion), which is no call and keeps its own words.
    /// </summary>
    public static string? FromAsking(string? asking, string? plane = null)
    {
        if (Split(asking) is not var (tool, summary))
        {
            return null;
        }
        return tool == "AskUserQuestion" ? null : Describe(tool, summary, plane);
    }

    /// <summary>The tool and the line of its input that <see cref="Pilot.Contracts.SessionRow.Asking"/> holds, or null for none.</summary>
    public static (string Tool, string? Summary)? Split(string? asking)
    {
        if (asking is not { Length: > 0 })
        {
            return null;
        }
        var split = asking.IndexOf(": ", StringComparison.Ordinal);
        return (split > 0 ? asking[..split] : asking, split > 0 ? asking[(split + 2)..] : null);
    }

    /// <summary>The one line a hook or a pending call keeps of a call, as <see cref="Pilot.Contracts.SessionRow.Asking"/> says it.</summary>
    public static string? Asking(string? tool, string? summary) =>
        tool is not { Length: > 0 } or "AskUserQuestion" ? null : summary is { Length: > 0 } ? $"{tool}: {summary}" : tool;

    private static string Quote(string text) => text.Length <= QuoteLength ? text : text[..(QuoteLength - 3)].TrimEnd() + "...";

    private static bool SamePlace(string path, string? plane)
    {
        if (plane is not { Length: > 0 })
        {
            return false;
        }
        static string Normal(string p) => p.Trim().Replace('\\', '/').TrimEnd('/');
        return string.Equals(Normal(path), Normal(plane), StringComparison.OrdinalIgnoreCase);
    }
}
