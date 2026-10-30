using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// One session row as the GUI shows it: every field already in the words it is drawn in, so the window, the CPU
/// capture and its <c>.rows.txt</c> dump all read the same strings, and a test asserts what a person sees.
/// </summary>
/// <remarks>
/// Pure: built from a <see cref="SessionRow"/> and a moment, with no surface. The TUI will paint the same lines.
/// </remarks>
internal sealed partial record SessionLine
{
    /// <summary>The row it shows, for the fields a line does not spell out (its id, its Remote Control link).</summary>
    public required SessionRow Row { get; init; }

    /// <summary>What the session is for: <c>watchtower</c>, <c>pilot · ORB204</c>, <c>worker · ORB204 / parser</c>, <c>ad hoc · stargazer</c>.</summary>
    public required string Role { get; init; }

    /// <summary>The session's name, the second line under <see cref="Role"/>.</summary>
    public required string Name { get; init; }

    /// <summary>The machine code of the node it runs on (<c>SE1</c>), where known: shown before a name that has no machine prefix of its own.</summary>
    public string? NodeCode { get; init; }

    /// <summary>The name as drawn: <see cref="Name"/>, with <see cref="NodeCode"/> before it when the name carries no machine prefix. Commands still go by <see cref="Name"/>.</summary>
    public string ShownName => MachineName.Shown(Name, NodeCode);

    /// <summary>Model and effort of its last response, <c>Opus 5.5 · xhigh</c>; <c>no response yet</c> before one.</summary>
    public required string Model { get; init; }

    /// <summary>Context against its window, <c>84.2k / 1M · 8%</c>.</summary>
    public required string Context { get; init; }

    /// <summary>The share of its window the context fills, 0 to 1, for the bar.</summary>
    public required float ContextFraction { get; init; }

    /// <summary>Since its last response, <c>3m ago</c>; <c>none yet</c> before one.</summary>
    public required string LastResponse { get; init; }

    /// <summary>Where it is: working, waiting on you, idle, ended, failed.</summary>
    public required string State { get; init; }

    /// <summary>What it waits on, while it waits on you; null otherwise.</summary>
    public string? WaitingFor { get; init; }

    /// <summary>Why it ended, or the note that it was renamed while it is live (<see cref="SessionRow.EndedBecause"/>); null when none.</summary>
    public string? EndedBecause { get; init; }

    /// <summary>Since when it waits on you, while it does; null otherwise (<see cref="SessionRow.WaitingSince"/>).</summary>
    public DateTimeOffset? WaitingSince { get; init; }

    /// <summary>The tool and command it asks to run, while it waits on you: <c>Bash: git push origin main</c>; null otherwise.</summary>
    public string? Asking { get; init; }

    /// <summary>Its Remote Control link, where it can be answered in the browser; null while Remote Control is off.</summary>
    public string? RemoteControlUrl { get; init; }

    /// <summary>Whether it still says waiting but its process is gone: the wait is not live and nothing can answer it.</summary>
    public bool Gone { get; init; }

    /// <summary>
    /// Whether it waits on you with a process to answer it: its state is waiting and its process is there. A stopped
    /// session (an API error) is shown as stopped, not as waiting.
    /// </summary>
    public bool IsWaiting => Row.State == SessionState.Waiting && !Gone && !IsStopped(Row);

    /// <summary>Its Remote Control link, only while it waits on you and can be answered there: what the Answer in the browser links lead to.</summary>
    public string? AnswerUrl => IsWaiting ? RemoteControlUrl : null;

    /// <summary>Its spend in weighted tokens, against its budget when it has one: <c>2.31M / 4M · 58%</c>, or <c>1.83M</c>.</summary>
    public required string Spend { get; init; }

    /// <summary>The share of its budget it has spent, 0 and up (past 1 when over); null without a budget.</summary>
    public float? SpendFraction { get; init; }

    /// <summary>The node it runs on, when the page merges several; null on a page of one node, this window's.</summary>
    public NodeMark? Node { get; init; }

    /// <summary>
    /// What the selection follows: its row id, which another node's session may share, so prefixed with that node's
    /// key (where it is reached) for a row of any node but this window's.
    /// </summary>
    public string Key => Node is null or { Here: true } ? Row.Id : $"{Node.Key}/{Row.Id}";

    /// <summary>Whether the session still runs: working, waiting or idle.</summary>
    public bool IsLive => Row.State is SessionState.Working or SessionState.Waiting or SessionState.Idle;

    /// <summary>The line as the <c>.rows.txt</c> dump writes it: the fields in the order the row shows them.</summary>
    public string ToDumpLine()
    {
        var detail = WaitingFor ?? EndedBecause;
        var state = detail is null ? State : $"{State} ({detail})";
        var line = string.Join(" | ", Role, ShownName, Model, Context, $"last response {LastResponse}", state, $"spend {Spend}");
        return Node is { } node ? $"{node.Name} | {line}" : line;
    }

    /// <summary>
    /// The rows in the order they are drawn: live ones first, and among them the ones waiting on you first, then
    /// working, then idle; after them the failed, then the ended. Within a state by role (watchtower, pilot, worker,
    /// reviewer, ad hoc), then flight, leg and name, so a flight's sessions sit together.
    /// </summary>
    public static IReadOnlyList<SessionLine> Build(IEnumerable<SessionRow> rows, DateTimeOffset now) => Order(rows.Select(r => From(r, now)));

    /// <summary>Lines in drawing order (<see cref="Build"/>'s), several nodes' merged; a tie between nodes goes by node.</summary>
    public static IReadOnlyList<SessionLine> Order(IEnumerable<SessionLine> lines) =>
        lines.OrderBy(l => StateOrder(l.Row.State))
            .ThenBy(l => (int)l.Row.Role)
            .ThenBy(l => l.Row.Flight ?? "", StringComparer.Ordinal)
            .ThenBy(l => l.Row.Leg ?? "", StringComparer.Ordinal)
            .ThenBy(l => l.Row.Name, StringComparer.Ordinal)
            .ThenBy(l => l.Node?.Name ?? "", StringComparer.Ordinal)
            .ToList();

    public static SessionLine From(SessionRow row, DateTimeOffset now)
    {
        var contextFraction = row.ContextWindow > 0 ? (float)row.ContextTokens / row.ContextWindow : 0f;
        float? spendFraction = row.Budget is > 0 ? (float)row.SpendWeighted / row.Budget.Value : null;
        return new SessionLine
        {
            Row = row,
            Role = RoleText(row),
            Name = row.Name,
            Model = ModelText(row.Model, row.Effort),
            Context = $"{Tokens(row.ContextTokens)} / {Tokens(row.ContextWindow)} · {Percent(contextFraction)}",
            ContextFraction = Math.Clamp(contextFraction, 0f, 1f),
            LastResponse = row.LastResponseAt is { } at ? Ago(now - at) : "none yet",
            State = IsStopped(row) ? "stopped" : StateText(row.State),
            WaitingFor = IsStopped(row) ? StopText(row, now) : row.State == SessionState.Waiting ? row.WaitingFor : null,
            EndedBecause = row.EndedBecause,
            WaitingSince = row.State == SessionState.Waiting ? row.WaitingSince : null,
            Asking = row.State == SessionState.Waiting ? row.Asking : null,
            RemoteControlUrl = row.RemoteControlUrl,
            Gone = row.State == SessionState.Waiting && row.Gone,
            Spend = row.Budget is { } budget
                ? $"{Tokens(row.SpendWeighted)} / {Tokens(budget)} · {Percent(spendFraction ?? 0f)}"
                : Tokens(row.SpendWeighted),
            SpendFraction = spendFraction,
        };
    }

    internal static int StateOrder(SessionState state) => state switch
    {
        SessionState.Waiting => 0,
        SessionState.Working => 1,
        SessionState.Idle => 2,
        SessionState.Failed => 3,
        _ => 4,
    };

    internal static string RoleText(SessionRow row)
    {
        var flight = (row.Flight, row.Leg) switch
        {
            ({ } f, { } l) => $"{f} / {l}",
            ({ } f, null) => f,
            (null, { } l) => l,
            _ => null,
        };
        return row.Role switch
        {
            SessionRole.Watchtower => "watchtower",
            SessionRole.AdHoc => row.Repo is { } repo ? $"ad hoc · {repo}" : "ad hoc",
            var role => flight is null ? RoleName(role) : $"{RoleName(role)} · {flight}",
        };
    }

    private static string RoleName(SessionRole role) => role switch
    {
        SessionRole.Pilot => "pilot",
        SessionRole.Worker => "worker",
        SessionRole.Reviewer => "reviewer",
        SessionRole.Planner => "planner",
        SessionRole.Watchtower => "watchtower",
        _ => "ad hoc",
    };

    /// <summary>
    /// Whether the API stopped its last turn and it has not run since: shown as stopped, with why and when it can go on,
    /// rather than as idle, which is how such a session otherwise looks while its work sits half done.
    /// </summary>
    internal static bool IsStopped(SessionRow row) => row.ApiError is not null && row.State != SessionState.Working;

    /// <summary>Why it stopped and when its limit resets: <c>usage limit · resets in 2h 10m</c>.</summary>
    internal static string StopText(SessionRow row, DateTimeOffset now)
    {
        var why = row.ApiError switch
        {
            "usage_limit_reached" => "usage limit",
            { } other => other.Replace('_', ' '),
            null => "an API error",
        };
        return row.ApiErrorResetsAt is not { } resets ? why
            : resets <= now ? $"{why} · reset {Ago(now - resets)}, can go on"
            : $"{why} · resets in {In(resets - now)}";
    }

    private static string In(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m"
        : span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";

    internal static string StateText(SessionState state) => state switch
    {
        SessionState.Working => "working",
        SessionState.Waiting => "waiting on you",
        SessionState.Idle => "idle",
        SessionState.Ended => "ended",
        _ => "failed",
    };

    // claude-opus-5-5 is Opus 5.5; a suffix (a date, [1m]) stays. Anything else is shown as the node sent it.
    [GeneratedRegex(@"^claude-([a-z]+)-(\d+)-(\d+)(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeModel();

    internal static string ModelText(string? model, string? effort)
    {
        if (model is null)
        {
            return "no response yet";
        }

        var match = ClaudeModel().Match(model);
        var name = match.Success
            ? $"{char.ToUpperInvariant(match.Groups[1].Value[0])}{match.Groups[1].Value[1..]} {match.Groups[2].Value}.{match.Groups[3].Value}{match.Groups[4].Value}"
            : model;
        return effort is null ? name : $"{name} · {effort}";
    }

    /// <summary>Tokens to three significant digits, trailing zeros dropped: 84.2k, 213k, 1M, 6.4M, 1.83M.</summary>
    internal static string Tokens(long tokens)
    {
        if (tokens < 1_000)
        {
            return tokens.ToString(CultureInfo.InvariantCulture);
        }

        var (value, unit) = tokens < 999_500 ? (tokens / 1e3, "k") : (tokens / 1e6, "M");
        var format = value < 10 ? "0.##" : value < 100 ? "0.#" : "0";
        return value.ToString(format, CultureInfo.InvariantCulture) + unit;
    }

    private static string Percent(float fraction) => $"{Math.Round(fraction * 100f, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%";

    /// <summary>How long ago, in the largest two units that matter: 2s, 3m, 1h 42m, 2d 5h.</summary>
    internal static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s ago"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m ago"
            : span.TotalHours < 24 ? (span.Minutes == 0 ? $"{(int)span.TotalHours}h ago" : $"{(int)span.TotalHours}h {span.Minutes}m ago")
            : span.Hours == 0 ? $"{(int)span.TotalDays}d ago" : $"{(int)span.TotalDays}d {span.Hours}h ago";
    }
}
