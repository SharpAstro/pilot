using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <param name="plan">The depth the node chose (#74); null reviews at standard depth, said no more than that.</param>
    /// <param name="again">The last round and what changed since its head: a re-review reads only that range; null reads the whole diff.</param>
    public static string Review(FlightRow flight, IReadOnlyList<LegRow> legs, PlaneState plane, string run, ReviewPlan? plan = null, string? machine = null, ReReview? again = null, string? pilotMd = null)
    {
        var depth = plan?.Depth ?? ReviewDepth.Standard;
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You review flight {flight.Code} ({flight.Repo} {flight.Item.Display}{title}) before it is offered for merging.");
        text.AppendLine();
        text.AppendLine($"Work in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`, against `{flight.Base}`.");
        if (again is null)
        {
            text.AppendLine("Read the whole diff (`git diff " + flight.Base + "...HEAD`), and rerun what proves each leg's Done-when. Do not edit,");
        }
        else
        {
            text.AppendLine($"This is round {again.Last.Round + 1}: round {again.Last.Round} read {(again.Last.Head is { Length: > 0 } h ? $"`{h}`" : "an earlier head")} and said {again.Last.Verdict.ToString().ToLowerInvariant()}.");
            text.AppendLine($"Read only what changed since: `git diff {again.Last.Head}..HEAD`, not the whole PR. First check that each finding of round {again.Last.Round}");
            text.AppendLine("below was dealt with, then read the new code for bugs it brings, and rerun what proves each leg's Done-when. Do not edit,");
        }
        text.AppendLine("commit, push or merge anything: you judge the work as it stands, and a redo of the leg (a go-around) makes any change.");
        text.Append(BranchHistoryRule);
        text.Append(NamingRule(machine, flight.Code.ToLowerInvariant()));
        text.Append(LfsRule(flight.Worktree));
        text.Append(PilotMdSection(pilotMd));
        if (Trackers.Reading(flight.Tracker).Reminder(flight.Item) is { } item)
        {
            text.AppendLine(item);
        }
        // The controller's rule of 20 Oct is the standard depth: one session's reading, very light on subagents. The node
        // picks a lighter or deeper one from usage headroom, priority and the diff (#74), and says which, and why.
        if (plan is not null)
        {
            text.AppendLine();
            text.AppendLine($"Depth: {depth.ToString().ToLowerInvariant()} ({plan.Why}).");
        }
        switch (depth)
        {
            case ReviewDepth.Light:
                text.AppendLine("Review lightly: one pass, no subagents. Read the diff, rerun only the checks that prove each Done-when, and");
                text.AppendLine("go no further; the account's usage is high and this review must not spend what is left.");
                break;
            case ReviewDepth.Deep:
                text.AppendLine($"Review deeply: this diff is large and there is headroom. Read it all yourself first, then you may start up to {ReviewPlan.MaxSubagents}");
                text.AppendLine("subagents, each on one focused question (a claim a leg's report makes, or a risky part of the diff) to check");
                text.AppendLine("against the tree. Give each its question and the files to read, not the whole review; you weigh what they find");
                text.AppendLine("and give the verdict yourself.");
                break;
            default:
                text.AppendLine("Do the review yourself, in this session: no subagents, or one at most for a single narrow question. Fanning");
                text.AppendLine("out reviewers costs many times the tokens and is not what this review is for.");
                break;
        }
        text.AppendLine();
        text.AppendLine("Legs, each done:");
        foreach (var leg in legs.OrderBy(l => l.Order))
        {
            text.AppendLine($"- {leg.Name}: {leg.Goal} Done when: {leg.DoneWhen}{(leg.Check is { Length: > 0 } c ? $" (check: `{c}`)" : "")}");
        }
        if (again is not null)
        {
            text.AppendLine();
            text.AppendLine($"Findings of round {again.Last.Round} ({again.Last.Verdict.ToString().ToLowerInvariant()}):");
            text.AppendLine(again.Last.Findings.Trim());
            if (again.Range.Commits.Trim().Length > 0)
            {
                text.AppendLine();
                text.AppendLine("Commits since:");
                text.AppendLine(again.Range.Commits.Trim());
            }
            if (again.Range.DiffStat.Trim().Length > 0)
            {
                text.AppendLine();
                text.AppendLine("Files changed since:");
                text.AppendLine(again.Range.DiffStat.Trim());
            }
        }
        else
        {
            if (plane.Commits.Trim().Length > 0)
            {
                text.AppendLine();
                text.AppendLine("Commits:");
                text.AppendLine(plane.Commits.Trim());
            }
            if (plane.DiffStat.Trim().Length > 0)
            {
                text.AppendLine();
                text.AppendLine("Files:");
                text.AppendLine(plane.DiffStat.Trim());
            }
        }
        text.AppendLine();
        // TIA1814's reviewers ran tianwen's whole suite themselves (10,127 tests, 40 minutes on a busy machine) while CI had it
        // green, filled their context waiting and reading, and stopped without a verdict three times (2026-10-26).
        text.AppendLine("Do not run the whole test suite: CI runs it on the PR, and `gh pr checks` says how it went. Rerun only the legs'");
        text.AppendLine($"checks, each through `{run} run -- <check>` so the node holds the job and wakes you with its end.");
        text.AppendLine();
        text.AppendLine("Look for: a Done-when that does not hold, bugs and regressions, tests that do not test what they say, and");
        text.AppendLine("anything the issue asked for that is missing. Style alone is no reason for changes.");
        text.AppendLine();
        text.AppendLine("Sort every finding under one of two headings. `## Blocking`: what must change before the merge (a Done-when that does");
        text.AppendLine("not hold, a bug, a regression, a test that proves nothing, something the issue asked for that is missing). `## Not blocking`:");
        text.AppendLine("nits and suggestions (naming, style, a comment, a small tidy-up, an idea for later). Findings that are all not blocking are a");
        text.AppendLine("pass with notes: the notes stay on the PR-ready card and the watchtower makes the small ones in the plane; no leg goes round.");
        text.AppendLine(AttachmentRule(run));
        text.AppendLine(ReportRule(run));
        text.AppendLine();
        text.AppendLine("## Your verdict");
        text.AppendLine($"- Ready: `{run} review pass \"<what you checked, in a line or two>\"`. With only not blocking findings, still `pass`, and");
        text.AppendLine("  put them under `## Not blocking` in the report: a pass with notes.");
        text.AppendLine($"- Not ready: `{run} review changes \"<each change asked for, concretely>\"`, with at least one finding under `## Blocking`; `changes`");
        text.AppendLine("  without one is refused. A fresh worker is told the report as is.");
        // PIL383: the flight's page draws the report as Markdown, as the transcript does; PIL294's #357 report was one run of
        // "1) … 2) …" with every name bare, and read as a wall.
        text.AppendLine("Write the report (`changes`, and `pass` where it has findings) as Markdown: one list item per finding, each starting");
        text.AppendLine("with a short first line that says what is wrong, then the detail on the lines under it. Put every code name, path");
        text.AppendLine("(`path/to/File.cs:42`) and command in backticks. The page shows the first lines of each finding and the rest on");
        text.AppendLine("\"Show all\", so the first line has to stand on its own.");
        // A backtick inside double quotes is a command substitution in Bash and the escape character in PowerShell: the code names
        // the report is asked to carry would vanish, or run. A quoted here-string passes the text as it is.
        text.AppendLine("Pass the Markdown report intact, backticks and all: in Bash a quoted heredoc, `review changes \"$(cat <<'EOF'` then the");
        text.AppendLine("report and `EOF` and `)\"` on lines of their own; in PowerShell a single-quoted here-string, `@'` and `'@` on lines of");
        text.AppendLine("their own around the report. Never put it between plain double quotes, where a backtick runs or escapes.");
        text.AppendLine("Then end your turn with the same verdict.");
        return text.ToString();
    }
}
