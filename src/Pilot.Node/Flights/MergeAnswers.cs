using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>What GitHub's answer to a merge, an enqueue or an auto-merge arm comes to.</summary>
public enum MergeAnswerKind
{
    /// <summary>The state wanted: it worked, or the PR is already queued, merged or armed.</summary>
    Done,
    /// <summary>"This branch can't be rebased": the rebase method needs the branch's shape changed first.</summary>
    NotRebaseable,
    /// <summary>The branch or its base moved behind the merge ("Base branch was modified").</summary>
    Behind,
    /// <summary>The branch conflicts with its base.</summary>
    Conflicts,
    /// <summary>The PR's head is not the commit the merge named ("Head branch was modified").</summary>
    HeadMoved,
    /// <summary>GitHub 5xx or a timeout: worth asking again.</summary>
    Transient,
    /// <summary>Anything else; the full message is kept.</summary>
    Refused,
}

/// <summary>One sorted answer: its kind and GitHub's whole message (every line, never cut).</summary>
public sealed record MergeAnswer(MergeAnswerKind Kind, string Message);

/// <summary>A merge GitHub refused: the kind of refusal and GitHub's whole message.</summary>
public sealed class MergeRefusedException(MergeAnswerKind kind, string said, string message) : FlightException(message)
{
    public MergeAnswerKind Kind { get; } = kind;

    /// <summary>GitHub's own text, every line.</summary>
    public string Said { get; } = said;
}

/// <summary>The merge methods a repo allows (<c>allow_squash_merge</c>, <c>allow_rebase_merge</c>, <c>allow_merge_commit</c>).</summary>
public sealed record AllowedMergeMethods(bool Squash, bool Rebase, bool Merge)
{
    public bool Allows(string method) => method.ToLowerInvariant() switch
    {
        "squash" => Squash,
        "rebase" => Rebase,
        "merge" => Merge,
        _ => false,
    };

    /// <summary><c>gh api repos/{owner}/{repo}</c>'s output; null when it is no JSON. A flag GitHub leaves out counts as allowed (the default).</summary>
    public static AllowedMergeMethods? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            bool Flag(string name) => !doc.RootElement.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.False;
            return new AllowedMergeMethods(Flag("allow_squash_merge"), Flag("allow_rebase_merge"), Flag("allow_merge_commit"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The one table of GitHub's merge texts: the only place that matches them. The first row that matches decides.</summary>
public static class MergeAnswers
{
    private static bool Has(string said, string text) => said.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static readonly Regex Http5xx = new(@"\bHTTP 50[234]\b|\b50[234] (Bad Gateway|Service Unavailable|Gateway Time-?out)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"already in the queue" (gh, or GraphQL's enqueuePullRequest): the state wanted.</summary>
    public static bool AlreadyQueued(string said) => Has(said, "already in the queue") || Has(said, "already queued");

    /// <summary>Auto-merge is armed already: the state wanted. Narrow, so no other "already" answer passes.</summary>
    public static bool AlreadyArmed(string said) => (Has(said, "auto-merge") || Has(said, "auto merge")) && Has(said, "already enabled");

    private static readonly (MergeAnswerKind Kind, Func<string, bool> Matches)[] Table =
    [
        (MergeAnswerKind.Done, AlreadyQueued),
        (MergeAnswerKind.Done, AlreadyArmed),
        (MergeAnswerKind.Done, said => Has(said, "already merged")),
        // Leaving the queue when not in it (dequeuePullRequest).
        (MergeAnswerKind.Done, said => Has(said, "not in the merge queue") || Has(said, "not queued") || Has(said, "not currently")),
        (MergeAnswerKind.NotRebaseable, said => Has(said, "can't be rebased") || Has(said, "cannot be rebased") || Has(said, "can not be rebased")),
        (MergeAnswerKind.HeadMoved, said => Has(said, "Head branch was modified") || Has(said, "Head sha didn't match")),
        (MergeAnswerKind.Behind, said => Has(said, "Base branch was modified") || Has(said, "branch is behind")),
        (MergeAnswerKind.Conflicts, said => Has(said, "merge conflict") || Has(said, "has conflicts")),
        (MergeAnswerKind.Transient, said => Http5xx.IsMatch(said) || Has(said, "timed out") || Has(said, "timeout") || Has(said, "time-out")),
    ];

    /// <summary>The text of an answer: stderr when it has any, else stdout, trimmed, every line kept.</summary>
    public static string Said(string stdout, string stderr) => (stderr.Trim().Length > 0 ? stderr : stdout).Trim();

    /// <summary>Sorts gh's exit code and streams. Exit 0 with no GraphQL <c>errors</c> is done.</summary>
    public static MergeAnswer Classify(int exit, string stdout, string stderr)
    {
        var said = Said(stdout, stderr);
        var failed = exit != 0 || stdout.Contains("\"errors\"", StringComparison.Ordinal);
        if (!failed)
        {
            return new MergeAnswer(MergeAnswerKind.Done, said);
        }
        var all = stderr + "\n" + stdout;
        foreach (var (kind, matches) in Table)
        {
            if (matches(all))
            {
                return new MergeAnswer(kind, said);
            }
        }
        return new MergeAnswer(MergeAnswerKind.Refused, said);
    }
}
