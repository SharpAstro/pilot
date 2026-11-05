using System.Text;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Human verification (#751): the checks a reviewer names under <c>## Human verification</c> for a flight whose issue carries the
/// <c>pilot: human verification</c> label are filed as issues in one milestone, so the controller has one place to see what waits
/// for their eyes and closes each issue once checked.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The milestone every filed human check goes in; made in the repo when it has none.</summary>
    public const string HumanVerificationMilestone = "Human verification: UI that needs eyes";

    private const string HumanVerificationMilestoneWhy = "Checks only a person can make (hardware, a live window, how it feels), filed by pilot from a reviewer's report. Check each by hand, then close its issue.";

    /// <summary>The For-you card kind that names the issues filed for a flight's human checks; one per flight.</summary>
    public const string HumanVerificationKind = "human-verification";

    /// <summary>The Handled card kind of human checks the node could not file (gh refused); the verdict stands.</summary>
    public const string HumanVerificationRefusedKind = "human-verification-refused";

    /// <summary>One human check as the reviewer wrote it: its title and its Do and Done-when lines.</summary>
    internal sealed record HumanItem(string Title, string Do, string DoneWhen);

    /// <summary>Reads a kept item (<see cref="ReviewFindings.HumanVerification"/>): the first line is the title, then a <c>Do:</c> and a <c>Done when:</c> line.</summary>
    internal static HumanItem ReadHumanItem(string item)
    {
        var lines = item.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var title = lines.Length > 0 ? BulletMarker().Replace(lines[0], "").Trim() : "";
        string? doText = null, doneWhen = null;
        var into = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Done when:", StringComparison.OrdinalIgnoreCase))
            {
                doneWhen = line["Done when:".Length..].Trim();
                into = 2;
            }
            else if (line.StartsWith("Do:", StringComparison.OrdinalIgnoreCase))
            {
                doText = line["Do:".Length..].Trim();
                into = 1;
            }
            else if (into == 1)
            {
                doText += " " + line;
            }
            else if (into == 2)
            {
                doneWhen += " " + line;
            }
        }
        return new HumanItem(title, doText ?? "", doneWhen ?? "");
    }

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex BulletMarker();

    private static string CheckKey(string title) => string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>The title of the issue filed for a check: <c>Verify by hand: &lt;item title&gt; (&lt;code&gt; note)</c>.</summary>
    internal static string HumanIssueTitle(string code, string title) => $"Verify by hand: {title} ({code} note)";

    internal static string HumanIssueBody(FlightRow flight, HumanItem item)
    {
        var text = new StringBuilder();
        text.AppendLine($"A check only a person can make, noted in the review of flight {flight.Code} ({flight.Repo}) and filed by pilot.");
        text.AppendLine();
        text.AppendLine(flight.PullRequest is { } pr ? $"Flight: {flight.Code}, PR: #{pr}{(flight.PullRequestUrl is { Length: > 0 } url ? $" ({url})" : "")}" : $"Flight: {flight.Code}, no PR yet");
        text.AppendLine();
        text.AppendLine($"**Do:** {(item.Do.Length > 0 ? item.Do : "the reviewer gave no steps; see the flight's review")}");
        text.AppendLine();
        text.AppendLine($"**Done when:** {(item.DoneWhen.Length > 0 ? item.DoneWhen : "the reviewer gave no criterion; see the flight's review")}");
        text.AppendLine();
        text.Append("Check it by hand, then close this issue.");
        return text.ToString();
    }

    /// <summary>
    /// Files each new check of a reviewer's report for a flagged flight as an issue in <see cref="HumanVerificationMilestone"/> (made when
    /// the repo has none), records them on the row, and raises or refreshes the flight's one For-you card. A check whose title was filed
    /// before (in any earlier round) is skipped. Called with the lock let go, after the verdict is kept: a gh that refuses leaves a Handled
    /// card, never a lost or delayed verdict.
    /// </summary>
    private async Task FileHumanChecksAsync(FlightRow flight, IReadOnlyList<string> items, CancellationToken cancellationToken)
    {
        var subject = Verbs.FlightTarget(flight.Code);
        var known = new HashSet<string>((flight.HumanChecks ?? []).Select(c => CheckKey(c.Title)), StringComparer.Ordinal);
        var fresh = new List<HumanItem>();
        foreach (var item in items.Select(ReadHumanItem).Where(i => i.Title.Length > 0))
        {
            if (known.Add(CheckKey(item.Title)))
            {
                fresh.Add(item);
            }
        }
        if (fresh.Count == 0)
        {
            return;
        }
        var filed = new List<string>();
        try
        {
            if (!(await github.MilestonesAsync(flight.RepoPath, cancellationToken)).Any(m => string.Equals(m.Title, HumanVerificationMilestone, StringComparison.OrdinalIgnoreCase)))
            {
                await github.CreateMilestoneAsync(flight.RepoPath, HumanVerificationMilestone, HumanVerificationMilestoneWhy, cancellationToken);
            }
            foreach (var item in fresh)
            {
                var made = await github.CreateIssueInMilestoneAsync(flight.RepoPath, HumanIssueTitle(flight.Code, item.Title), HumanIssueBody(flight, item), HumanVerificationMilestone, cancellationToken);
                filed.Add(made.Url);
                // Recorded as each is filed, so a refusal later in the list never files the first again.
                Record(flight.Code, f => f with { HumanChecks = [.. f.HumanChecks ?? [], new HumanCheck(made.Number, item.Title)] });
            }
        }
        catch (Exception ex) when (ex is FlightException or InvalidOperationException or IOException)
        {
            var left = fresh.Count - filed.Count;
            state.RaiseCard(CardBin.Handled, HumanVerificationRefusedKind, subject,
                $"{flight.Code}'s human checks were not all filed: {OneLine(ex.Message)}",
                $"{left} of {fresh.Count} checks could not be filed in the milestone '{HumanVerificationMilestone}' of {flight.Repo}. The verdict stands. Checks not filed: "
                    + string.Join("; ", fresh.Skip(filed.Count).Select(i => i.Title)),
                summary: CardSummaries.HumanVerificationRefused(flight.Code, left));
        }
        if (filed.Count > 0)
        {
            RaiseHumanVerification(FlightOf(subject), filed.FirstOrDefault(u => u.Length > 0));
        }
    }

    /// <summary>The one For-you card of a flight's human checks, replaced as more are filed: every issue filed so far, with its link, and the milestone.</summary>
    private void RaiseHumanVerification(FlightRow flight, string? sampleUrl)
    {
        var checks = flight.HumanChecks ?? [];
        // A filed issue's page is its repo's issues path plus its number: the sample (one just filed) gives the repo.
        var cut = sampleUrl?.LastIndexOf('/') ?? -1;
        var issuesPath = cut > 0 ? sampleUrl![..(cut + 1)] : null;
        var milestones = issuesPath is not null && issuesPath.EndsWith("/issues/", StringComparison.Ordinal) ? issuesPath[..^"issues/".Length] + "milestones" : null;
        var lines = checks.Select(c => issuesPath is not null ? $"{issuesPath}{c.Number} {c.Title}" : $"#{c.Number} {c.Title}");
        var subject = Verbs.FlightTarget(flight.Code);
        state.CloseCardsAbout(subject, "more human checks were filed", HumanVerificationKind);
        state.RaiseCard(CardBin.ForYou, HumanVerificationKind, subject,
            $"{flight.Code}: {checks.Length} {(checks.Length == 1 ? "check" : "checks")} only you can make, filed in '{HumanVerificationMilestone}'",
            $"Filed from the review of {flight.Code}{(flight.PullRequest is { } pr ? $" (PR #{pr})" : "")}: {string.Join("; ", lines)}",
            link: milestones,
            summary: CardSummaries.HumanVerification(flight.Code, checks.Length));
    }
}
