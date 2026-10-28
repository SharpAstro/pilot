using System.Globalization;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// <c>pilot summary</c> as text: the summary the node kept (both sections, as the one Haiku call wrote them), then when it was
/// made, how many tokens it took and what it cost, so whoever reads it can tell how far to trust it. The same words for the
/// CLI and, as a tool, the MCP server.
/// </summary>
internal static class SummaryMarkdown
{
    /// <summary>What the summary is about, in words: <c>this node's flights in play</c>, <c>every node's flights in play</c>, or <c>flight TIA1611</c>.</summary>
    internal static string About(string scope) => scope switch
    {
        SummaryRow.FleetScope => "this node's flights in play",
        SummaryRow.AllNodesScope => "every node's flights in play",
        _ => $"flight {scope}",
    };

    /// <summary>The summary as Markdown, for <c>--output-format markdown</c> and rendered for <c>rich</c>.</summary>
    internal static string Build(SummaryRow row, DateTimeOffset now) =>
        $"# Summary: {Markdown.Text(About(row.Scope))}\n\n{row.Markdown.Trim()}\n\n_{Markdown.Text(Footer(row, now))}_\n";

    /// <summary>The summary as plain text: its headings without their marks.</summary>
    internal static string Plain(SummaryRow row, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.AppendLine($"summary  {About(row.Scope)}").AppendLine();
        foreach (var line in row.Markdown.Trim().Split('\n'))
        {
            text.AppendLine(line.TrimEnd().StartsWith("## ", StringComparison.Ordinal) ? line.TrimEnd()[3..] : line.TrimEnd());
        }
        return text.AppendLine().AppendLine(Footer(row, now)).ToString();
    }

    /// <summary>The line after it: <c>made 5 min ago (2026-10-28 09:00 UTC) · 1,234 tokens · $0.0021</c>.</summary>
    internal static string Footer(SummaryRow row, DateTimeOffset now)
    {
        var made = $"made {Age(now - row.MadeAt)} ({row.MadeAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)})";
        if (row.Tokens == 0 && row.Cost is null)
        {
            return $"{made} · no call was made";
        }
        var cost = row.Cost is { } usd ? $" · ${usd.ToString("0.0000", CultureInfo.InvariantCulture)}" : "";
        return $"{made} · {row.Tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens{cost}";
    }

    /// <summary>A span as people say it: <c>just now</c>, <c>5 min ago</c>, <c>3 h ago</c>, <c>2 d ago</c>.</summary>
    internal static string Age(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "just now"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
        : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} d ago";
}
