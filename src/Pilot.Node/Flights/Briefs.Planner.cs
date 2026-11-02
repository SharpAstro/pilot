using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>
    /// What a flight's reviewer is told: the flight, every leg's goal and Done-when, what the branch holds, and how to give
    /// its verdict. It reads and runs; it changes nothing, so its verdict is about the work as the workers left it.
    /// </summary>
    /// <summary>
    /// The planner's brief: read the issue and the code, and plan the flight's legs as the node flies them (one worker each,
    /// in order, on one branch), then say <c>planned</c>. It writes no code.
    /// </summary>
    public static string Plan(FlightRow flight, IReadOnlyList<LegRow> legs, string run, string? machine = null, string? pilotMd = null)
    {
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You plan flight {flight.Code} ({flight.Repo} {flight.Item.Display}{title}): the legs that will do its issue.");
        text.AppendLine();
        var words = Trackers.Reading(flight.Tracker);
        text.AppendLine($"Work in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`. Read the {words.Noun}");
        text.AppendLine($"({words.Read(flight.Issue)}), the repo's CLAUDE.md, and the code it touches. Do not edit, commit or push");
        text.AppendLine("anything: you plan, and each leg's worker does the work.");
        text.AppendLine("Do it yourself, in this session: no subagents, or one at most for a single narrow question.");
        text.AppendLine("A step the plane cannot do by the repo's PILOT.md rules is not planned, or is planned with the way round it named in the leg.");
        text.Append(NamingRule(machine, flight.Code.ToLowerInvariant()));
        text.Append(PilotMdSection(pilotMd));
        text.Append(LfsRule(flight.Worktree));
        text.AppendLine();
        text.AppendLine("How legs fly: one at a time, in order, each by a fresh worker that sees only its own leg, the legs before it and");
        text.AppendLine("what the branch holds. So each leg must stand on its own: a goal a worker can do in one sitting, a Done-when");
        text.AppendLine("anyone can check, and a check command the node reruns before it counts the leg done (a test filter, a build).");
        text.AppendLine("Prefer few legs: one for a small issue, two to four for a larger one, split where a reviewer would want to");
        text.AppendLine($"see the work land in steps. No leg may be named `{LegCommander.ReviewLeg}` or `{LegCommander.PlanLeg}`.");
        text.AppendLine("A leg that needs outside work (a file, a time) says so in its steps; its worker waits with `leg wait`, never with a job.");
        text.AppendLine(WaitRule(run));
        if (flight.FromIdea is { Length: > 0 } idea)
        {
            text.AppendLine();
            text.AppendLine($"This flight was made from the idea {idea}: its issue is the idea's own, and its first words are the person's. Your plan goes in the");
            text.AppendLine("legs and the PR, not over those words. If the issue's body needs more, add a section under the idea's words (a heading of");
            text.AppendLine("your own, then the addition); never replace, shorten or reword what is there.");
        }
        if (legs.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Legs it has already (plan any more after them; do not repeat them):");
            foreach (var leg in legs.OrderBy(l => l.Order))
            {
                text.AppendLine($"- {leg.Name} ({leg.State.ToString().ToLowerInvariant()}): {leg.Goal}");
            }
        }
        if (flight.FoldedIn.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Issues folded into this flight (the controller chose to plan them here, not as flights of their own):");
            foreach (var folded in flight.FoldedIn)
            {
                text.AppendLine($"- {new WorkItem(flight.Tracker, folded.Number).Display}, from flight {folded.From}: read it ({words.Read(folded.Number)}) and plan its points as legs after the ones this flight has" +
                    (legs.Count > 0 ? "; do not repeat them." : ", after the legs for " + flight.Item.Display + "."));
            }
            text.AppendLine($"The PR closes each of them; every point of each must be done by some leg, so name the issue a leg serves in its goal.");
        }
        if (flight.Overlaps.Where(o => o.Answer != OverlapAnswer.Pending).ToList() is { Count: > 0 } answered)
        {
            text.AppendLine();
            text.AppendLine("Overlaps the controller has answered (never ask about them again):");
            foreach (var overlap in answered)
            {
                var at = overlap.Node is null ? overlap.Other : $"{overlap.Other} on {overlap.Node}";
                var files = overlap.Files is { Length: > 0 } shared ? $"; both change {string.Join(", ", shared)}" : "";
                text.AppendLine(overlap.Answer switch
                {
                    OverlapAnswer.Separate => $"- {at} ({overlap.Why}{files}): the controller chose to plan {flight.Code} in parallel with it. If it still depends on {overlap.Other}'s work, connect it: `{run} flight connect {flight.Code} {overlap.Other}`.",
                    OverlapAnswer.Connect => $"- {at} ({overlap.Why}{files}): the controller chose to connect {flight.Code} to it: {flight.Code} is held until {overlap.Other} has landed" +
                        (overlap.Node is null ? "" : $" ({overlap.Node} says so)") + ", and branches off main after it. Plan its legs for main as it will be then, building on what " + overlap.Other + " changes in the shared files; do not connect or ask again.",
                    _ => $"- {overlap.Other} ({overlap.Why}{files}): the controller chose to fold {flight.Code} into it.",
                });
            }
        }
        // Look first: a flight another is already doing is better folded in or connected than planned blind beside it.
        text.AppendLine();
        text.AppendLine("## Look first");
        text.AppendLine("Before you plan any leg, look for a flight already doing this, on any node, or a PR open on the same code.");
        text.AppendLine("First read the issue and the code and list the files your legs will change (repo-relative paths, as many as you can name).");
        text.AppendLine($"Then run `{run} flight touches {flight.Code} <file> <file> ...` with them. It fetches the repo and compares them with every other flight not");
        text.AppendLine("landed on this node and on each reachable node (the files its pushed branch changes against its base) and with every open PR, and");
        text.AppendLine("answers a line each, in the form `PIL294 (DE1): connect, both change DetachedShell.cs, JobRunner.cs`. A line says unknown when that");
        text.AppendLine("flight's branch is not pushed, its node did not answer or gh would not list the PR's files: it is not \"no overlap\", so say it in your");
        text.AppendLine($"planned summary. To confirm that a flight has the same goal, read its {words.Noun} ({words.ReadOther}) and its legs' goals (`{run} status` lists");
        text.AppendLine("this node's flights with their issues and legs).");
        text.AppendLine($"- `{run} nodes` lists the other pilot nodes on the LAN; `{run} summary --fleet` is the node's kept summary of the flights in play on every node it holds a grant for (what needs the controller, where it is), with its age.");
        text.AppendLine("What to do with each answer:");
        text.AppendLine($"- fold (a large share of your files): when it is the same goal as that flight's, `{run} flight overlap {flight.Code} <other> pick=fold file=<shared file> file=<shared file> <why>`");
        text.AppendLine("  (pick=fold so the card is titled a fold; every shared file the line names, each as file=<path>; the why in words), then end your turn at once: plan no leg and do not say planned.");
        text.AppendLine("  The controller is asked on a card naming both flights and the shared files, to fold this flight into the other (only a flight of this");
        text.AppendLine("  node), connect it to the other or plan in parallel, and a fresh planner is started with the answer. When the pair has been answered");
        text.AppendLine("  already the command returns the answer straight away, with no card: follow it. When the goal is another, treat the line as a connect.");
        text.AppendLine($"- connect (some files shared): `{run} flight connect {flight.Code} <other>` holds this flight until the other has landed, so it branches off");
        text.AppendLine("  main after it (its legs can be planned meanwhile). The other may be a flight of another node: it is held until that node says the");
        text.AppendLine("  flight landed, and when that node cannot be asked it stays held and says why. Say which and why in the planned summary.");
        text.AppendLine("  When the shared files are only touched in passing and nothing of the other's change is needed, plan beside it instead, and say so.");
        text.AppendLine("- a line for a flight on another node: it cannot be folded into from here. Connect to it as above, or ask the controller with");
        text.AppendLine("  `flight overlap` (the card then offers connect or plan in parallel) when it is not clear whether yours must wait for it.");
        text.AppendLine("- a line for an open PR: it is no flight to fold into or connect to. Do not ask; plan as usual and name that PR and the shared");
        text.AppendLine("  files in your planned summary, for the controller to settle.");
        text.AppendLine("- parallel (no file in common), or no lines at all: plan as below, and ask nothing.");
        text.AppendLine("A real overlap is the same files or the same area of the issue: sharing a repo, or a word in a title, is not one. Run touches again");
        text.AppendLine("when you change which files your legs touch.");
        text.AppendLine();
        text.AppendLine("## Plan each leg");
        text.AppendLine($"`{run} leg plan {flight.Code} <name> --goal \"…\" --done-when \"…\" --check \"<command>\" --item \"<step>\" --item \"<step>\"`");
        text.AppendLine("Name: lower-case letters, digits and dashes. Items are the worker's checklist, in order. Add --model or --effort");
        text.AppendLine("only when a leg needs more (or less) than the default.");
        text.AppendLine("Every item is the worker's to do, so none pushes, opens or edits a PR, merges, releases or publishes: the node pushes the");
        text.AppendLine("branch and opens the PR once the last leg is done, and a release follows its merge. A worker cannot tick such a step and fails");
        text.AppendLine("the leg over it with the work done (TIA1853, twice on 2026-10-28). End a leg on its last commit and its check passing.");
        text.AppendLine("The check runs as written in pwsh on Windows (sh elsewhere): quote anything holding a |, as a test filter");
        text.AppendLine("(`--filter 'FullyQualifiedName~A|FullyQualifiedName~B'`). A bare | is refused, since it would pipe.");
        // SDL144: a Debug check on a worktree needed what the Release package never carries, and failed for nothing of the leg's.
        text.AppendLine(CiRule);
        // A leg whose way to a number is loading the machine plans the ask, never the load (TIA1910).
        text.AppendLine(MachineLoadRule(run));
        // PIL319: a check naming a solution that sits in src/ could never pass from the plane's root.
        text.AppendLine(CheckWhereRule);
        if (CiShape.Of(flight.Worktree) is { } ci)
        {
            text.AppendLine($"This repo's CI ({ci.Workflow}) builds {ci.Configuration}: give every `dotnet build` and `dotnet test` in a check `-c {ci.Configuration}`. " +
                "`leg plan` refuses one that does not, unless it is given `--ci-differs \"<why>\"`.");
        }
        text.AppendLine();
        text.AppendLine("## When the legs are planned");
        text.AppendLine($"`{run} flight planned {flight.Code} \"<the plan in a line or two: why these legs, in this order>\"`. It shows the");
        text.AppendLine("controller the legs with a button to start the first. Then end your turn with the same line.");
        return text.ToString();
    }
}
