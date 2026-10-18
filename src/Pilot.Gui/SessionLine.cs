using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Gui;

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

    /// <summary>Its spend in weighted tokens, against its budget when it has one: <c>2.31M / 4M · 58%</c>, or <c>1.83M</c>.</summary>
    public required string Spend { get; init; }

    /// <summary>The share of its budget it has spent, 0 and up (past 1 when over); null without a budget.</summary>
    public float? SpendFraction { get; init; }

    /// <summary>Whether the session still runs: working, waiting or idle.</summary>
    public bool IsLive => Row.State is SessionState.Working or SessionState.Waiting or SessionState.Idle;

    /// <summary>The line as the <c>.rows.txt</c> dump writes it: the fields in the order the row shows them.</summary>
    public string ToDumpLine()
    {
        var state = WaitingFor is null ? State : $"{State} ({WaitingFor})";
        return string.Join(" | ", Role, Name, Model, Context, $"last response {LastResponse}", state, $"spend {Spend}");
    }

    /// <summary>
    /// The rows in the order they are drawn: live ones first, and among them the ones waiting on you first, then
    /// working, then idle; after them the failed, then the ended. Within a state by role (watchtower, pilot, worker,
    /// reviewer, ad hoc), then flight, leg and name, so a flight's sessions sit together.
    /// </summary>
    public static IReadOnlyList<SessionLine> Build(IEnumerable<SessionRow> rows, DateTimeOffset now) =>
        rows.OrderBy(r => StateOrder(r.State))
            .ThenBy(r => (int)r.Role)
            .ThenBy(r => r.Flight ?? "", StringComparer.Ordinal)
            .ThenBy(r => r.Leg ?? "", StringComparer.Ordinal)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => From(r, now))
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
            State = StateText(row.State),
            WaitingFor = row.State == SessionState.Waiting ? row.WaitingFor : null,
            Spend = row.Budget is { } budget
                ? $"{Tokens(row.SpendWeighted)} / {Tokens(budget)} · {Percent(spendFraction ?? 0f)}"
                : Tokens(row.SpendWeighted),
            SpendFraction = spendFraction,
        };
    }

    private static int StateOrder(SessionState state) => state switch
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
        SessionRole.Watchtower => "watchtower",
        _ => "ad hoc",
    };

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
