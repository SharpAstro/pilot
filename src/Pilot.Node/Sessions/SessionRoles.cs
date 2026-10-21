using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Sessions;

/// <summary>Where the tracker finds what it reads, and how often it looks.</summary>
/// <param name="Profile">Claude Code's profile folder.</param>
/// <param name="ActiveInterval">The poll interval while a client is connected.</param>
/// <param name="IdleInterval">The poll interval while none is.</param>
/// <param name="EndedRetention">How long an ended session stays listed.</param>
public sealed record SessionTrackerOptions(
    ClaudeProfile Profile,
    TimeSpan ActiveInterval,
    TimeSpan IdleInterval,
    TimeSpan EndedRetention)
{
    /// <summary>
    /// Folders whose sessions are the node's own plumbing and no row: the relay's, whose throwaway <c>claude -p</c>
    /// session carries one message and is gone. Listed while it ran, it showed for a day as an ad hoc session named
    /// after the sender.
    /// </summary>
    public IReadOnlyList<string> HiddenFolders { get; init; } = [];

    /// <summary>Whether a session working in <paramref name="cwd"/> is the node's own and gets no row.</summary>
    public bool IsHidden(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd) || HiddenFolders.Count == 0)
        {
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return HiddenFolders.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)), full, comparison));
    }

    public static readonly TimeSpan DefaultActiveInterval = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan DefaultIdleInterval = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan DefaultEndedRetention = TimeSpan.FromHours(24);

    public static SessionTrackerOptions FromEnvironment() => new SessionTrackerOptions(
        ClaudeProfile.Default(),
        DefaultActiveInterval,
        DefaultIdleInterval,
        DefaultEndedRetention);
}

/// <summary>What a session is for, and which flight and leg it flies: from its name until the node keeps flights.</summary>
public sealed record SessionRoleInfo(SessionRole Role, string? Flight, string? Leg, long? Budget)
{
    public static readonly SessionRoleInfo AdHoc = new SessionRoleInfo(SessionRole.AdHoc, null, null, null);
}

public static partial class SessionRoles
{
    /// <summary>A worker's session name, <c>&lt;FLIGHT&gt; &lt;leg&gt;</c> (<c>PIL5 node</c>, <c>TIA1611a node</c>): a flight code is capitals and a number.</summary>
    [GeneratedRegex(@"^([A-Z]{2,}[0-9]+[a-z]?) (\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkerName();

    /// <summary>A worker's session name as v2 gives it, <c>&lt;machine&gt;-&lt;flight&gt;-&lt;leg&gt;</c> in lower case (<c>du1-tia1611a-parser</c>).</summary>
    [GeneratedRegex(@"^[a-z]{2}[0-9]+-([a-z]{2,}[0-9]+[a-z]?)-(\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MachineWorkerName();

    /// <summary>A flight code as a name writes it in lower case, back in its own: TIA1611a of tia1611a.</summary>
    internal static string FlightCode(string lower)
    {
        var prefix = Pilot.Node.Flights.FlightCodes.PrefixOf(lower);
        var rest = lower[prefix.Length..];
        return prefix.ToUpperInvariant() + rest;
    }

    /// <summary>
    /// The role of a session, by its name. The watchtower is a background session named <c>*-watchtower</c>; a worker is
    /// one named <c>&lt;FLIGHT&gt; &lt;leg&gt;</c>. A reviewer or co-pilot is a subagent, so is no row. Everything else is
    /// ad hoc. The flights leg gives a worker its budget, and finds it by session as well as by name.
    /// </summary>
    /// <param name="background">
    /// Whether it runs in the background. The node starts its watchtower so; an interactive <c>claude</c> opened in the
    /// watchtower's folder, as trusting the folder takes, is named <c>&lt;host&gt;-watchtower</c> by Remote Control and is
    /// someone's terminal, not a second watchtower.
    /// </param>
    public static SessionRoleInfo Resolve(string? name, bool background = true)
    {
        if (background && name is { Length: > 0 } && (name.EndsWith("-watchtower", StringComparison.Ordinal) || name == "watchtower"))
        {
            return new SessionRoleInfo(SessionRole.Watchtower, null, null, null);
        }

        if (name is { Length: > 0 } && MachineWorkerName().Match(name) is { Success: true } named)
        {
            // <machine>-<flight>-review is the flight's reviewer, which flies no leg (no leg may be named review).
            if (named.Groups[2].Value == "review")
            {
                return new SessionRoleInfo(SessionRole.Reviewer, FlightCode(named.Groups[1].Value), null, null);
            }
            // <machine>-<flight>-plan plans the flight's legs, and flies none (no leg may be named plan).
            if (named.Groups[2].Value == "plan")
            {
                return new SessionRoleInfo(SessionRole.Planner, FlightCode(named.Groups[1].Value), null, null);
            }
            return new SessionRoleInfo(SessionRole.Worker, FlightCode(named.Groups[1].Value), named.Groups[2].Value, null);
        }
        var match = name is { Length: > 0 } ? WorkerName().Match(name) : Match.Empty;
        return match.Success
            ? new SessionRoleInfo(SessionRole.Worker, match.Groups[1].Value, match.Groups[2].Value, null)
            : SessionRoleInfo.AdHoc;
    }
}
