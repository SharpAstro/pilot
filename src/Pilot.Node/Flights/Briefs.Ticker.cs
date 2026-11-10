using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>
    /// A leg's ticker's brief: a short session in the plane, started when the leg's worker committed and ended with no step ticked.
    /// It maps the worker's commits (<see cref="LegRow.StartHead"/>..HEAD) to the leg's steps, ticks each step a commit covers,
    /// changes and commits nothing, and finishes the leg done when every step is ticked or failed when one is not covered.
    /// </summary>
    /// <param name="commits">The commits since the worker started (<c>git log --oneline StartHead..HEAD</c>), when the node has them; null tells the ticker to run it.</param>
    public static string Tick(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, string run, string? machine = null, string? commits = null)
    {
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You are the ticker of leg `{leg.Name}` of flight {flight.Code} ({flight.Repo} {flight.Item.Display}{title}). A few minutes, one job:");
        text.AppendLine("the leg's worker committed its work and ended without ticking its steps; you tick the steps its commits cover.");
        text.AppendLine("You do not do the leg.");
        text.AppendLine();
        text.AppendLine($"Work in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`. Change and commit nothing, push");
        text.AppendLine("and delete nothing, and keep scratch output outside the plane. Do it yourself, in this session, with no subagents.");
        text.AppendLine("Your ceiling is a few minutes and about thirty tool calls; past it, answer with what you have.");
        text.Append(NamingRule(machine, flight.Code.ToLowerInvariant()));
        text.Append(LfsRule(flight.Worktree));

        var before = legs.Where(l => l.Order < leg.Order).OrderBy(l => l.Order).ToList();
        if (before.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Legs before it, done:");
            foreach (var done in before)
            {
                text.AppendLine($"- {done.Name}: {done.Goal}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Its goal");
        text.AppendLine(leg.Goal);
        if (leg.Checklist.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## Its checklist");
            for (var i = 0; i < leg.Checklist.Length; i++)
            {
                text.AppendLine($"{i + 1}. [{(leg.Checklist[i].Done ? "x" : " ")}] {leg.Checklist[i].Text}");
            }
        }
        text.AppendLine();
        text.AppendLine("## Its Done-when");
        text.AppendLine(leg.DoneWhen);
        if (leg.Check is { Length: > 0 } check)
        {
            text.AppendLine();
            text.AppendLine($"Its check: `{check}`. The node reruns it when you finish the leg done; you need not run it yourself unless a step's cover depends on it.");
        }

        text.AppendLine();
        text.AppendLine("## The worker's commits");
        if (leg.StartHead is { Length: > 0 } head)
        {
            text.AppendLine($"The plane's HEAD when the worker started was `{head}`; its commits are `{head}..HEAD`.");
            if (commits is { } given)
            {
                text.AppendLine(given.Trim().Length > 0 ? given.Trim() : "(none)");
            }
            else
            {
                text.AppendLine($"Run `git log --stat {head}..HEAD` to see them, and `git show <sha>` for one.");
            }
        }
        else
        {
            text.AppendLine("The start of the worker's commits was not recorded. Run `git log --oneline` and take the commits on top of the earlier legs' work.");
        }

        text.AppendLine();
        text.AppendLine("## What to do");
        text.AppendLine("Map the commits to the steps: for each step, find the commit that does what it says (read the diff, not just the message).");
        text.AppendLine($"Tick each step a commit covers with `{run} check <step number>`. Change and commit nothing.");
        text.AppendLine();
        text.AppendLine("## Your answer");
        text.AppendLine($"- Every step is ticked: `{run} leg finish done \"ticked by the node's ticking pass: <which commits covered which steps>\"`.");
        text.AppendLine($"- A step is not covered by any commit, or `leg finish done` is refused (its check failing): `{run} leg finish failed \"<the steps not covered, or the check's failure>\"`.");
        text.AppendLine($"- `{run} leg finish done` may answer that its check is rerunning and that you should end your turn: that is its answer, and the node finishes the leg when the rerun ends. End your turn only after `leg finish` has answered, as finished or with its check rerun started, never before you have run it.");
        text.AppendLine("Then end your turn with the same answer.");
        text.AppendLine(AttachmentRule(run));
        return text.ToString();
    }
}
