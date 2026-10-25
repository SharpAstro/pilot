using System.Text;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// <c>pilot status</c> as Markdown, for <c>--output-format markdown</c> and, rendered by Console.Lib's
/// <c>MarkdownOutput</c>, for <c>rich</c>: the same facts as <see cref="PilotCommand.Status"/>, with the issue and PR as
/// links, each flight's legs in a table, and a leg's screenshot shown where the terminal can show it.
/// </summary>
internal static class StatusMarkdown
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    internal static string Build(SnapshotEvent snapshot, bool maintenance = false, bool? autopilot = null, bool all = false)
    {
        var text = new StringBuilder();
        text.AppendLine("# pilot status").AppendLine();
        var facts = new List<string>();
        if (maintenance)
        {
            facts.Add("**maintenance** on: this node takes no new flights (`pilot settings set maintenance off`)");
        }
        if (autopilot is { } piloting)
        {
            facts.Add(piloting
                ? "**autopilot** on (only the person changes it: `pilot settings set autopilot off`)"
                : "**autopilot** off (only the person changes it: `pilot settings set autopilot on`)");
        }
        if (snapshot.Usage?.Account is { } account)
        {
            facts.Add($"**account** {Text(account.Line())}");
        }
        if (snapshot.Usage is { HasFigures: true } usage)
        {
            facts.Add($"**usage** {Text(usage.Line(DateTimeOffset.UtcNow, TimeZoneInfo.Local))}");
        }
        if (facts.Count > 0)
        {
            foreach (var fact in facts)
            {
                text.AppendLine($"- {fact}");
            }
            text.AppendLine();
        }

        text.AppendLine("## Flights").AppendLine();
        var flights = snapshot.Flights.OrderBy(f => f.State == FlightState.Landed).ThenByDescending(f => f.CreatedAt).ToList();
        var landed = 0;
        if (!all)
        {
            (flights, landed) = PilotCommand.InTheAir(flights);
        }
        if (flights.Count == 0)
        {
            text.AppendLine(landed > 0 ? "No flights in the air." : "No flights.").AppendLine();
        }
        if (landed > 0)
        {
            text.AppendLine($"_{PilotCommand.LandedHidden(landed, "`pilot status --all`")}_").AppendLine();
        }
        foreach (var flight in flights)
        {
            var state = flight.State switch
            {
                FlightState.Held => $"held, waiting for {FlightWaiting.For(flight)}",
                FlightState.Landed => "landed",
                _ => "flying",
            };
            text.AppendLine($"### {flight.Code} · {Text(flight.Title ?? $"issue #{flight.Issue}")}").AppendLine();
            var issue = $"{flight.Repo} #{flight.Issue}";
            var line = new List<string>
            {
                $"**{Text(state)}**",
                IssueUrl(flight) is { } issueUrl ? $"[{Text(issue)}]({issueUrl})" : Text(issue),
            };
            if (flight.PullRequestUrl is { } url)
            {
                line.Add($"[PR {Text(PullNumber(url) is { } n ? $"#{n}" : url)}]({url})");
            }
            line.Add($"plane `{flight.Worktree}` on `{flight.Branch}`");
            text.AppendLine(string.Join(" · ", line)).AppendLine();

            var legs = snapshot.Legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order).ToList();
            if (legs.Count > 0)
            {
                text.AppendLine("| # | Leg | State | Steps | Worker |");
                text.AppendLine("|--:|-----|-------|------:|--------|");
                foreach (var leg in legs)
                {
                    var legState = leg.State.ToString().ToLowerInvariant() + (leg.GoArounds > 0 ? $", go-around {leg.GoArounds}" : "");
                    var steps = leg.Checklist.Length == 0 ? "" : $"{leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length}";
                    var worker = leg.Worker is { } w && leg.State == LegState.Flying ? Text(w) : "";
                    text.AppendLine($"| {leg.Order} | {Text(leg.Name)} | {Text(legState)} | {steps} | {worker} |");
                }
                text.AppendLine();
            }
            foreach (var leg in legs.Where(l => l.Artifacts.Length > 0))
            {
                foreach (var artifact in leg.Artifacts)
                {
                    text.AppendLine(IsImage(artifact.Location)
                        ? $"![{Text(artifact.Title)}]({Destination(artifact.Location)})"
                        : $"- {Text(leg.Name)}: [{Text(artifact.Title)}]({Destination(artifact.Location)})");
                    text.AppendLine();
                }
            }
        }

        var reviews = snapshot.PrReviews.Where(r => r.State != PrReviewState.Closed).OrderBy(r => r.StartedAt).ToList();
        if (reviews.Count > 0)
        {
            text.AppendLine("## PR reviews").AppendLine();
            foreach (var review in reviews)
            {
                var how = review.State == PrReviewState.Reviewing ? $"reviewing, {review.Reviewer}" : $"{review.Verdict}, on card {review.Card}";
                text.AppendLine($"- [{Text($"{review.Repo}#{review.Number}")}]({review.Url}) {Text(review.Title ?? "")} ({Text(how)}, at `{review.Head[..Math.Min(8, review.Head.Length)]}`)");
            }
            text.AppendLine();
        }

        var open = snapshot.Cards.Where(c => c.State == CardState.Open).ToList();
        if (open.Count > 0)
        {
            text.AppendLine("## Cards").AppendLine();
            text.AppendLine($"{open.Count} open {(open.Count == 1 ? "card" : "cards")} ({open.Count(c => c.Bin == CardBin.Ring)} ringing): `pilot cards`").AppendLine();
        }

        var live = snapshot.Sessions.Where(s => s.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ToList();
        text.AppendLine("## Sessions").AppendLine();
        if (live.Count == 0)
        {
            text.AppendLine("No live sessions.");
        }
        foreach (var session in live)
        {
            text.AppendLine($"- **{Text(session.Name)}** {session.State.ToString().ToLowerInvariant()}");
        }
        return text.ToString();
    }

    private static string Text(string value) => Markdown.Text(value);

    /// <summary>The issue's page, from the PR's URL on the same repo; null with no PR yet, as the flight knows only the repo's folder.</summary>
    private static string? IssueUrl(FlightRow flight) =>
        flight.PullRequestUrl is { } url && url.LastIndexOf("/pull/", StringComparison.Ordinal) is var at and > 0
            ? $"{url[..at]}/issues/{flight.Issue}"
            : null;

    private static string? PullNumber(string url) =>
        url.LastIndexOf("/pull/", StringComparison.Ordinal) is var at and > 0 ? url[(at + "/pull/".Length)..].TrimEnd('/') : null;

    private static bool IsImage(string location) =>
        !location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        && !location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && ImageExtensions.Contains(Path.GetExtension(location), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A link's or image's destination: a Windows path with forward slashes (a backslash before punctuation is an escape in
    /// Markdown), and in angle brackets when it holds a space.
    /// </summary>
    private static string Destination(string location)
    {
        var target = location.Contains("://", StringComparison.Ordinal) ? location : location.Replace('\\', '/');
        return target.Contains(' ') ? $"<{target}>" : target;
    }
}
