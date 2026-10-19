using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a leg's worker is told: everything it needs from the flight and the leg, and nothing of an earlier worker's
/// conversation. A go-around's worker is told why the last one stopped and what the plane holds now (#35: a long
/// go-around in one session drags its whole conversation along; a fresh one briefed from the work does not).
/// </summary>
public static class Briefs
{
    /// <summary>What the plane holds so far, for a go-around's brief: its commits past the base and the files they touch.</summary>
    public sealed record PlaneState(string Commits, string DiffStat);

    public static string For(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, string? why = null, PlaneState? plane = null)
    {
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You are the worker for leg `{leg.Name}` of flight {flight.Code} ({flight.Repo} #{flight.Issue}{title}).");
        text.AppendLine();
        text.AppendLine($"Work only in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`. Every leg of the");
        text.AppendLine("flight flies on this branch in order, so the work of the legs before yours is already here. Commit as you go,");
        text.AppendLine("with messages that say what changed and why. Do not push, open a PR or merge: the controller lands the flight.");

        var before = legs.Where(l => l.Order < leg.Order).ToList();
        if (before.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Legs before yours, done:");
            foreach (var done in before)
            {
                text.AppendLine($"- {done.Name}: {done.Goal}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Goal");
        text.AppendLine(leg.Goal);
        if (leg.Checklist.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## Checklist");
            for (var i = 0; i < leg.Checklist.Length; i++)
            {
                text.AppendLine($"{i + 1}. [{(leg.Checklist[i].Done ? "x" : " ")}] {leg.Checklist[i].Text}");
            }
        }
        text.AppendLine();
        text.AppendLine("## Done when");
        text.AppendLine(leg.DoneWhen);
        if (leg.Check is { Length: > 0 } check)
        {
            text.AppendLine();
            text.AppendLine($"The node reruns `{check}` in this folder before it takes the leg as done; it must exit 0. Run it yourself first.");
        }

        if (why is not null)
        {
            text.AppendLine();
            text.AppendLine($"## Go-around {leg.GoArounds}");
            text.AppendLine($"An earlier worker flew this leg and was stopped: {why}");
            text.AppendLine("You do not have its conversation, and do not need it: start from what the plane holds now.");
            if (plane is not null)
            {
                text.AppendLine();
                text.AppendLine("Commits on the plane past its base:");
                text.AppendLine(plane.Commits.Trim().Length > 0 ? plane.Commits.Trim() : "(none yet)");
                if (plane.DiffStat.Trim().Length > 0)
                {
                    text.AppendLine();
                    text.AppendLine("Files they change:");
                    text.AppendLine(plane.DiffStat.Trim());
                }
            }
        }

        text.AppendLine();
        text.AppendLine("## When you stop");
        text.AppendLine("End your turn with a short report: what you did, how you checked it (the Done-when, rerun), and anything");
        text.AppendLine("left. Start it with DONE when every step is done and the Done-when holds, or BLOCKED: and why.");
        return text.ToString();
    }
}
