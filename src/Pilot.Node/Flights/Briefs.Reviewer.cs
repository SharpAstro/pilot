using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <param name="plan">The depth the node chose (#74); null reviews at standard depth, said no more than that.</param>
    /// <param name="again">The last round and what changed since its head: a re-review reads only that range; null reads the whole diff.</param>
    public static string Review(FlightRow flight, IReadOnlyList<LegRow> legs, PlaneState plane, string run, ReviewPlan? plan = null, string? machine = null, ReReview? again = null, string? pilotMd = null, DesignRef? design = null)
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
            text.AppendLine($"Read only what changed since: `git diff {again.Last.Head}..HEAD`, not the whole PR. The first review was the thorough one; this round");
            text.AppendLine("answers its list. First settle every earlier point below (see \"Earlier points\"), then read the changed lines for bugs they bring,");
            text.AppendLine("and rerun what proves each leg's Done-when. Do not edit,");
        }
        text.AppendLine("commit, push or merge anything: you judge the work as it stands, and a redo of the leg (a go-around) makes any change.");
        text.Append(BranchHistoryRule);
        text.Append(NoSpinRule);
        text.Append(CommandShapeRule);
        text.Append(NamingRule(machine, flight.Code.ToLowerInvariant()));
        text.Append(LfsRule(flight.Worktree));
        text.Append(PilotMdSection(pilotMd));
        text.Append(DesignVersion(flight, DesignRole.Reviewer, design));
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
            AppendEarlierPoints(text, again.Rounds);
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
        text.AppendLine("Do not run the whole test suite: the full suite is CI's, and `gh pr checks` says how it went (pilot #752). Your own run is");
        text.AppendLine("the Release build, the tests the flight added or changed, and `Category=Invariant`; rerun the legs' checks only where they");
        text.AppendLine($"say more than that. Each goes through `{run} run -- <command>` so the node holds the job and wakes you with its end.");
        text.AppendLine("A check run stops at its first failed test (the node ends the job and names that test), so the queue is free for the next job.");
        text.AppendLine($"The node knows it for a check only when it is handed over whole, as the leg's check reads; `{run} run --fail-fast -- <command>`");
        text.AppendLine("stops any run that way, a check split into its commands too.");
        text.AppendLine(JobShellRule);
        text.AppendLine();
        text.AppendLine("Look for: a Done-when that does not hold, bugs and regressions, tests that do not test what they say, and");
        text.AppendLine("anything the issue asked for that is missing. Style alone is no reason for changes.");
        text.AppendLine();
        text.AppendLine("Sort every finding under one of two headings. `## Blocking`: what must change before the merge (a Done-when that does");
        text.AppendLine("not hold, a bug, a regression, a test that proves nothing, something the issue asked for that is missing). `## Not blocking`:");
        text.AppendLine("nits and suggestions (naming, style, a comment, a small tidy-up), which a review-notes round makes in this PR. Findings that are all not blocking are a");
        text.AppendLine("pass with notes: the node plans a review-notes leg that makes the small ones in this PR.");
        if (again is null)
        {
            text.AppendLine();
            text.AppendLine("This is the first review, and the thorough one: read the whole diff and raise everything you find now. Later rounds only answer");
            text.AppendLine("your list point by point and read the lines the fix round changed, so what you leave out here is not looked for again.");
        }
        else
        {
            AppendAnswerTable(text, again);
        }
        text.AppendLine(SelfTriggeringCiRule);
        if (flight.HumanVerification)
        {
            text.AppendLine();
            text.AppendLine("This flight's issue carries the `pilot: human verification` label. A check only a person can make (it cannot be settled by a");
            text.AppendLine("test or a CPU-rendered capture: hardware, a live window, how it feels) goes under a third heading, `## Human verification`,");
            text.AppendLine("one list item each. The item's first line is a short title; under it, a `Do:` line (exactly what to do) and a `Done when:`");
            text.AppendLine("line (what to look for). Carry over such checks the legs' reports name. The node files each item as an issue for the");
            text.AppendLine("controller. They never block: do not put them under `## Blocking`, and they do not change your verdict.");
        }
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

    /// <summary>Every point of every earlier round, numbered, as a flight's and a PR's re-review brief both list them.</summary>
    internal static void AppendEarlierPoints(StringBuilder text, IReadOnlyList<ReviewRound> rounds)
    {
        var points = ReviewPoints.Number(rounds);
        text.AppendLine($"Earlier points, every one of every round ({rounds.Count}), blocking and not blocking:");
        foreach (var round in rounds.OrderBy(r => r.Round))
        {
            var mine = points.Where(p => p.Round == round.Round).ToList();
            text.AppendLine($"Round {round.Round} ({round.Verdict.ToString().ToLowerInvariant()}){(mine.Count == 0 ? ", no points:" : ":")}");
            if (mine.Count == 0)
            {
                text.AppendLine(round.Findings.Trim());
            }
            foreach (var p in mine)
            {
                text.AppendLine($"- {p.Id} ({(p.Blocking ? "blocking" : "not blocking")}): {p.Text}");
            }
        }
    }

    internal static void AppendAnswerTable(StringBuilder text, ReReview again, string what = "flight")
    {
        var first = ReviewPoints.Number(again.Rounds).FirstOrDefault()?.Id ?? "R1.1";
        // A PR no flight owns files no follow-up issue (its points are made by a fixer or go on the PR); a flight's re-review keeps the filing.
        var ownsPr = what == "PR";
        text.AppendLine();
        text.AppendLine($"This is a re-review, and its one job is the list above. The report opens with `## {ReviewPoints.AnswerHeading}`: a table with one row for every");
        text.AppendLine("point, none left out, columns `Point | Answer | Where or reason`, e.g. `| " + first + " | addressed | src/Foo.cs:42 |`. Every point ends one of four ways (pilot #1226), each answer being one of:");
        text.AppendLine("- `addressed`: fixed, with where (`file:line`, or the commit). Check it by reading the code or running it; the fixer's word is not a check.");
        text.AppendLine("- `not work: <why>`: the point asks for nothing to be done (a question, praise, a restatement, text that is fine as it is). It is settled and files nothing.");
        text.AppendLine($"- `node's own`: the {what}'s PR body, title or label. The node sets those, not a fixer; it is settled and files nothing.");
        text.AppendLine($"- `deferred: another repo`, `deferred: owner decision` or `deferred: larger than the {what}`: the only three reasons a point may wait.");
        text.AppendLine(ownsPr
            ? $"  A point deferred to another repo gets an issue there and an owner decision becomes a card; one deferred as larger than the {what} is put on the PR as one comment, no issue is filed for it."
            : $"  Every deferred point goes into one follow-up issue for the {what}, which the node files from your table.");
        text.AppendLine("  A bare `left` (and any other free-text answer) is no end and is refused.");
        text.AppendLine($"  `deferred: larger than the {what}` holds only for a point that needs a design or a plan of its own. A fixer that deferred a test, a rename, a comment,");
        text.AppendLine("  a doc line, a guard or a refactor of the files it touched as larger has not addressed it: answer it `not addressed`, so the next round makes it.");
        text.AppendLine("- `not addressed`: no fix and no reason. This blocks the merge.");
        text.AppendLine("\"Still open\", \"unchanged\", \"for later\" and \"to do\" with no reason are refused: a point is settled or it blocks. A point you do not");
        text.AppendLine("mention is refused too, by name.");
        text.AppendLine($"Then the new findings, under `## Blocking` and `## Not blocking`. A fault in the lines changed since `{again.Last.Head}` blocks as always. A new finding");
        text.AppendLine($"in text the fix round did not touch blocks only when it must: start it with `{ReviewPoints.MustBlockMarker} <kind>`, the kind being one of");
        text.AppendLine($"{string.Join(", ", ReviewPoints.MustBlockKinds)}. Any other finding in untouched text goes under `## Not blocking`: it is a note, made by a");
        text.AppendLine(ownsPr
            ? "fixer's round in this PR, not a follow-up issue. Put it under `## Blocking` without the prefix only when it needs a design or a plan of its own:"
            : "review-notes round in this PR, not a follow-up issue. Put it under `## Blocking` without the prefix only when it needs a design or a plan of its own:");
        text.AppendLine(ownsPr
            ? "then give `changes`, and the node takes the round as a pass and puts the point on the PR as a comment (no issue is filed for a PR no flight owns)."
            : "then give `changes`, and the node takes the round as a pass and files it as the follow-up issue.");
    }
}
