using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>
    /// A leg's validator's brief: it checks the few facts about the tree that the leg rests on (files, types, verbs and tests it
    /// names or assumes), after the legs before it, and answers GO unless one is false. It judges no route, only facts, and
    /// changes nothing. A NO-GO names each stale fact with the command that showed it.
    /// </summary>
    public static string Validate(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, string run, string? machine = null)
    {
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You are the validator of leg `{leg.Name}` of flight {flight.Code} ({flight.Repo} {flight.Item.Display}{title}). A few minutes, one answer:");
        text.AppendLine("is the leg's brief still true of the tree the earlier legs left? You do not do the leg.");
        text.AppendLine();
        text.AppendLine($"Work in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`. Read only: change, commit,");
        text.AppendLine("push and delete nothing, and keep scratch output outside the plane. Do it yourself, in this session, with no subagents.");
        text.AppendLine("Your ceiling is a few minutes and about thirty tool calls; past it, answer with what you have.");
        text.Append(NamingRule(machine, flight.Code.ToLowerInvariant()));
        text.Append(LfsRule(flight.Worktree));
        if (Trackers.Reading(flight.Tracker).Reminder(flight.Item) is { } item)
        {
            text.AppendLine(item);
        }

        text.AppendLine();
        text.AppendLine("Legs before it, done:");
        foreach (var done in legs.Where(l => l.Order < leg.Order).OrderBy(l => l.Order))
        {
            text.AppendLine($"- {done.Name}: {done.Goal}");
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
                text.AppendLine($"{i + 1}. {leg.Checklist[i].Text}");
            }
        }
        text.AppendLine();
        text.AppendLine("## Its Done-when");
        text.AppendLine(leg.DoneWhen);
        if (leg.Check is { Length: > 0 } check)
        {
            text.AppendLine();
            text.AppendLine($"Its check: `{check}`. Do not run it: the leg is not done, so it need not pass yet.");
        }

        text.AppendLine();
        text.AppendLine("## What to check");
        text.AppendLine("Pick at most five facts about the tree that the leg rests on: files, types, members, verbs, tests or commands it");
        text.AppendLine("names or assumes to exist, or to be shaped a certain way, after the legs before it. Ask of each: if this were false,");
        text.AppendLine("would the leg be wrong rather than merely longer? Check each with a command (grep, a listing, a read), today.");
        text.AppendLine("Not your business: the leg's route, its steps' wording, whether its Done-when is the best one, or anything it is");
        text.AppendLine("meant to create. A thing the leg adds is not stale for being absent. Something a worker would fix in a paragraph");
        text.AppendLine("is no reason for NO-GO.");
        text.AppendLine();
        text.AppendLine("## Your answer");
        text.AppendLine($"- Every fact holds, or you could not find one false: `{run} leg validate go \"<the facts checked, in a line>\"`.");
        text.AppendLine($"- A fact is false: `{run} leg validate no-go \"<each stale fact, with the command that showed it and what was true instead>\"`.");
        text.AppendLine("Answer GO unless a fact is false. Then end your turn with the same answer.");
        text.AppendLine(AttachmentRule(run));
        return text.ToString();
    }
}
