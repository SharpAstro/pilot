using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <param name="socket">The node's socket when it is not the default one: the CLI is told it, or it would ask another node.</param>
    public static string For(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, string? why = null, PlaneState? plane = null, string? cli = null, string? socket = null, string? resumed = null, string? machine = null, string? update = null, string? pilotMd = null)
    {
        var run = Run(cli, socket);
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You are the worker for leg `{leg.Name}` of flight {flight.Code} ({flight.Repo} {flight.Item.Display}{title}).");
        text.AppendLine();
        text.AppendLine($"Work only in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`. Every leg of the");
        text.AppendLine("flight flies on this branch in order, so the work of the legs before yours is already here. Commit as you go,");
        text.AppendLine("with messages that say what changed and why. Do not push, open a PR or merge: the controller lands the flight.");
        text.AppendLine("Leave nothing behind in the plane: every change is committed before you finish (the node refuses a done leg");
        text.AppendLine("whose plane holds files not committed), and scratch output goes outside the plane, never into it. Once your");
        text.AppendLine("leg is done, commit nothing more here: a late commit would miss the PR; a change after it is a redo of the leg (a go-around).");

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
        text.Append(PilotMdSection(pilotMd));
        text.Append(LfsRule(flight.Worktree));
        text.AppendLine();
        text.AppendLine(CiRule);
        text.AppendLine(WorkerScopeRule);
        text.AppendLine(MachineLoadRule(run));
        text.Append(NamingRule(machine, flight.Code.ToLowerInvariant()));
        if (CiShape.Of(flight.Worktree) is { } ci)
        {
            text.AppendLine($"This repo's CI ({ci.Workflow}) builds {ci.Configuration}: run your own `dotnet build` and `dotnet test` with `-c {ci.Configuration}`, never a bare or Debug one.");
        }

        if (why is not null || resumed is not null)
        {
            text.AppendLine();
            if (resumed is not null)
            {
                text.AppendLine("## Resumed after a hold");
                text.AppendLine($"An earlier worker flew this leg until its restore failed on a package that was being published, and the node held the leg: {resumed}");
            }
            else
            {
                text.AppendLine($"## Go-around {leg.GoArounds}");
                text.AppendLine($"An earlier worker flew this leg and was stopped: {why}");
            }
            text.AppendLine("You do not have its conversation, and do not need it: start from what the plane holds now.");
            if (update is not null)
            {
                // The node pushes the branch after the last go-around and refuses one behind its base: it is brought up to date here, checked, then done.
                text.AppendLine($"Merge the base once in this round, just before the final check, not on every attempt, as the node says for this repo: {update}. Rerun the check after it, and say done only when it passes. The node pushes the branch on each finish attempt, so the reviewer and CI see the work early.");
            }
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

        if (flight.HumanVerification)
        {
            text.AppendLine();
            text.AppendLine("## Human verification");
            text.AppendLine("This flight's issue carries the `pilot: human verification` label. A check only a person can make (it cannot be settled by a");
            text.AppendLine("test or a CPU-rendered capture: hardware, a live window, how it feels) is named in your report, under a `## Human verification`");
            text.AppendLine("heading, one list item each: a short title, then a `Do:` line (exactly what to do) and a `Done when:` line (what to look");
            text.AppendLine("for). The reviewer carries them over and the node files each as an issue for the controller. They never fail a leg.");
        }
        text.AppendLine();
        // A view that only passed its tests shipped wrong to the eye (pilot's tooltips, 2026-10-25): where the org keeps a UI
        // checklist beside its clones, a leg is told to work by it and to show a capture.
        if (UiChecklist(flight) is { } checklist)
        {
            text.AppendLine("## If your leg changes what a person sees");
            text.AppendLine($"Read the UI checklist, {checklist}, and work by it: contrast against the real background, no text cut");
            text.AppendLine("silently, one height and one middle per row, one source for each value and component, the right control, no");
            text.AppendLine("clutter, nothing moving under the pointer. Render a capture at a narrow and a wide size with long content,");
            text.AppendLine($"look at it yourself, and record it on the leg (`{run} artifact <path> <title>`): the person sees it on the flight's page.");
            // gh cannot upload an image the way the browser's drag and drop does, and PIL277 first failed a finished leg over it.
            // Committing the PNGs to link them keeps every capture in main's history under a rebase merge, and a raw link shows
            // nothing on a private repo: the artifact the node keeps is the capture's one home.
            text.AppendLine("The recorded artifact is the capture's whole delivery: commit no image and push no branch for it, embed none in the");
            text.AppendLine("PR, and never fail or hold a leg over showing a capture.");
            text.AppendLine();
        }
        text.AppendLine("## Keeping the node told");
        text.AppendLine("The node tracks this leg; tell it as you go, from this folder (it knows your leg by the folder):");
        if (leg.Checklist.Length > 0)
        {
            text.AppendLine($"- Tick each step as you finish it: `{run} check <step number>`.");
        }
        text.AppendLine($"- Something to look at (a screenshot, a capture, a page): `{run} artifact <path or URL> <title>`.");
        text.AppendLine($"  {AttachmentRule(run)}");
        text.AppendLine($"- When every step is done and the Done-when holds: `{run} leg finish done \"<one-line report>\"`. The node reruns the");
        text.AppendLine("  check itself and refuses while a step is open or the check fails; fix what it says and finish again.");
        text.AppendLine($"- When you cannot go on: `{run} leg finish failed \"<why>\"`. Fail only for what stops the Done-when; something");
        // A leg failed over a nicety the Done-when never asked for rings the person and costs a fresh worker (PIL277, 2026-10-26).
        text.AppendLine("  else you could not do (a permission refused, an image not attached, a nice-to-have) goes in the report of a done leg.");
        // A leg stopped on the controller's or the owner's word is not a failure: failed rings a go-around and an accept choice (#799 rule 4, c3623).
        text.AppendLine($"- When the controller or the owner told you to stop the leg: `{run} leg finish retired \"<whose word, and why>\"`, never `failed`.");
        text.AppendLine("  Retired ends the leg with no card and the flight goes on with its next leg; failed rings the person for a leg that broke.");
        // A restore that cannot find a sibling's package is the sibling's publish not done yet, not this leg's fault: the node
        // holds the leg for it and flies it again once nuget.org serves the package. A worker that works around it (a local
        // feed, a pin moved back) would ship a build that is not what CI restores.
        text.AppendLine("- A restore that fails NU1101 or NU1102 on a package a sibling repo or a connected flight is publishing is not yours to fix:");
        text.AppendLine($"  report it with `{run} leg finish failed \"<the NU line, verbatim>\"` and end your turn. The node reads the line, holds the");
        text.AppendLine("  leg until nuget.org serves that version, and flies it again with a fresh worker. Do not work around it: no local");
        text.AppendLine("  feed, no pin changed back, no package reference removed.");
        text.AppendLine("- A CS1704 or MSB3243 in a sibling clone's path is a stale restore left by another plane's build: run the restore the node");
        text.AppendLine($"  names through `{run} run -- dotnet restore <project>`, then the check again.");
        text.AppendLine($"- A full build or test run, or anything else long: `{run} run -- <command>`, then end your turn. The node");
        text.AppendLine("  runs it as a job and wakes you with how it ended and its last lines; do not wait for it or poll it. The machine");
        text.AppendLine("  is shared with other flights, so it may wait its turn first; that costs you nothing. Short commands (one test,"
            + " a quick build of one project) you run yourself, as usual."); 
        text.AppendLine("  A test suite or any other long run goes through `" + run + " run --`, and you read what the node says when it ends (the failures and the totals),"
            + " never the full log, so a suite's output cannot fill your context.");
        text.AppendLine("  A run goes on to its end unless you hand it over as `" + run + " run --fail-fast -- <command>`, which the node ends at its first failed test; use it when the first failure is all you need.");
        text.AppendLine(WaitRule(run));
        text.AppendLine(ReportRule(run));
        // Claude Code isolates a -w session in its worktree and refuses a command it cannot prove stays inside: a git in a
        // loop, a pilot run handed a shell's -Command string. The worker recovers by splitting it, a turn lost each time
        // (TIA1781's cross-structure, SE1 2026-10-25).
        text.AppendLine("- This session is held to the plane, and Claude Code refuses a command it cannot read as staying inside it.");
        text.AppendLine($"  Give `{run} run --` the command itself (`{run} run -- dotnet test ...`); the node runs it in its own shell, so");
        text.AppendLine("  never wrap it in `pwsh` or `bash -c`, which the plane's sandbox refuses: several steps are a job each, one after the other. Run");
        text.AppendLine($"  git plainly, one command or a few joined by `&&`, never inside a loop: tick steps as `{run} check 1 && {run} check 2`.");
        text.AppendLine("  " + JobShellRule);
        text.Append(BranchHistoryRule);
        text.AppendLine();
        text.AppendLine(InvariantRule);
        text.AppendLine("## When you stop");
        text.AppendLine("End your turn with a short report: what you did, how you checked it (the Done-when, rerun), and anything");
        text.AppendLine("left. Start it with DONE when every step is done and the Done-when holds, or BLOCKED: and why.");
        return text.ToString();
    }
}
