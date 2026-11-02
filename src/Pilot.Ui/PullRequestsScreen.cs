using DIR.Lib;
using DIR.Lib.Layout;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// What the Pull requests page shows: every open PR of the orgs the node's clones come from, as the nodes of the window
/// last read them (one row, merged), and when. Null <see cref="Row"/> is a node that has not read them yet.
/// </summary>
internal sealed record PullRequestsScreen(PullRequestsRow? Row, string Source, DateTimeOffset AsOf)
{
    /// <summary>The cards open now, whose choices a row's Merge and Go around press; the window's panel's, set when the page is drawn.</summary>
    public IReadOnlyList<CardRow> OpenCards { get; init; } = [];

    /// <summary>How old the read may be before the source line says so, in amber.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Whether the PRs were read more than <see cref="StaleAfter"/> before <see cref="AsOf"/>: a node that stopped reading, or one cut off.</summary>
    public bool Stale => Row is { } row && AsOf - row.ReadAt > StaleAfter;

    /// <summary>The counts in the top bar: <c>7 open · 3 ready · 2 checks running · 1 conflicts</c>; empty before the first read.</summary>
    public string Counts => Row?.Summary() ?? "";

    /// <summary>The orgs read, as the empty line names them: <c>SharpAstro, sebgod</c>.</summary>
    public string Orgs => Row is { Orgs.Length: > 0 } row ? string.Join(", ", row.Orgs) : "any repo pilot works in";

    /// <summary>The source line: where the rows come from and when GitHub was last read, and that it is old once it is.</summary>
    public string SourceText => Row is { } row
        ? $"{Source} · read from GitHub {PullRequestsRow.Age(row.ReadAt, AsOf)} ({row.ReadAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC){(Stale ? " · stale: over 5 minutes old, Refresh reads it now" : "")}"
        : $"{Source} · pull requests not read yet";
}
