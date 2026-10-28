using System.Security.Cryptography;
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
    /// <summary>How a worker runs the CLI: its path, quoted when it holds a space, and the node's socket when it is not the default.</summary>
    public static string Run(string? cli, string? socket)
    {
        var pilot = cli ?? "pilot";
        var run = pilot.Contains(' ', StringComparison.Ordinal) ? $"\"{pilot}\"" : pilot;
        if (socket is not null)
        {
            run += $" --node-socket \"{socket}\"";
        }
        return run;
    }

    /// <summary>What the plane holds so far, for a go-around's brief: its commits past the base and the files they touch.</summary>
    public sealed record PlaneState(string Commits, string DiffStat);

    /// <summary>
    /// The <c>pilot</c> CLI a worker runs, by its full path: beside the node (a publish puts them together), else the source
    /// tree's build of the same configuration, else <c>pilot</c> on <c>PATH</c>.
    /// </summary>
    public static string PilotCli(string? nodeDirectory = null)
    {
        var name = OperatingSystem.IsWindows() ? "pilot.exe" : "pilot";
        var directory = nodeDirectory ?? AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(directory, name)))
        {
            return Path.Combine(directory, name);
        }
        var tfm = new DirectoryInfo(directory);
        if (tfm.Parent is { } configuration && configuration.Parent is { Name: "bin" } bin && bin.Parent?.Parent is { } src
            && Path.Combine(src.FullName, "Pilot.Cli", "bin", configuration.Name, tfm.Name, name) is var built && File.Exists(built))
        {
            return built;
        }
        return "pilot";
    }

    /// <param name="socket">The node's socket when it is not the default one: the CLI is told it, or it would ask another node.</param>
    public static string For(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, string? why = null, PlaneState? plane = null, string? cli = null, string? socket = null, string? resumed = null, string? machine = null)
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
        text.AppendLine("leg is done, commit nothing more here: a late commit would miss the PR; a change after it is a go-around.");

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
        text.AppendLine();
        text.AppendLine(CiRule);
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
        // A restore that cannot find a sibling's package is the sibling's publish not done yet, not this leg's fault: the node
        // holds the leg for it and flies it again once nuget.org serves the package. A worker that works around it (a local
        // feed, a pin moved back) would ship a build that is not what CI restores.
        text.AppendLine("- A restore that fails NU1101 or NU1102 on a package a sibling repo or a connected flight is publishing is not yours to fix:");
        text.AppendLine($"  report it with `{run} leg finish failed \"<the NU line, verbatim>\"` and end your turn. The node reads the line, holds the");
        text.AppendLine("  leg until nuget.org serves that version, and flies it again with a fresh worker. Do not work around it: no local");
        text.AppendLine("  feed, no pin changed back, no package reference removed.");
        text.AppendLine($"- A full build or test run, or anything else long: `{run} run -- <command>`, then end your turn. The node");
        text.AppendLine("  runs it as a job and wakes you with how it ended and its last lines; do not wait for it or poll it. The machine");
        text.AppendLine("  is shared with other flights, so it may wait its turn first; that costs you nothing. Short commands (one test,"
            + " a quick build of one project) you run yourself, as usual."); 
        text.AppendLine(WaitRule(run));
        // Claude Code isolates a -w session in its worktree and refuses a command it cannot prove stays inside: a git in a
        // loop, a pilot run handed a shell's -Command string. The worker recovers by splitting it, a turn lost each time
        // (TIA1781's cross-structure, SE1 2026-10-25).
        text.AppendLine("- This session is held to the plane, and Claude Code refuses a command it cannot read as staying inside it.");
        text.AppendLine($"  Give `{run} run --` the command itself (`{run} run -- dotnet test ...`); the node runs it in its own shell, so");
        text.AppendLine("  never wrap it in `pwsh` or `bash -c`, which is refused: several steps are a job each, one after the other. Run");
        text.AppendLine($"  git plainly, one command or a few joined by `&&`, never inside a loop: tick steps as `{run} check 1 && {run} check 2`.");
        text.AppendLine();
        text.AppendLine("## When you stop");
        text.AppendLine("End your turn with a short report: what you did, how you checked it (the Done-when, rerun), and anything");
        text.AppendLine("left. Start it with DONE when every step is done and the Done-when holds, or BLOCKED: and why.");
        return text.ToString();
    }

    /// <summary>
    /// What the worker and the planner (whose legs' Done-when may wait on outside work) are told about waiting: a leg waits on a
    /// file or a time with <c>leg wait</c>, never with a job that sleeps, which holds a slot of the queue while it does nothing.
    /// </summary>
    internal static string WaitRule(string run) =>
        $"- Outside work to wait for (a file another tool writes, a time of day): `{run} leg wait --for-file <path> [--until <time>] [why...]`, then end\n" +
        "  your turn; the node wakes you once when the file exists or the time comes. Never wait with a `run` job that sleeps or polls:\n" +
        "  it holds a slot of the queue and blocks every job behind it.";

    /// <summary>
    /// The line every brief carries: a background session a session starts itself is named with the node's machine code, as the
    /// node's own are, so the fleet's sessions are told apart (the merge guard refuses a <c>claude --bg -n</c> without it).
    /// Nothing when the node's machine is not known.
    /// </summary>
    internal static string NamingRule(string? machine, string scope)
    {
        if (machine is not { Length: > 0 })
        {
            return "";
        }
        var code = machine.ToLowerInvariant();
        return $"A background session you start yourself (`claude --bg`) is named `{code}-<flight or card>-<what>`, with the machine's code `{code}` written out,\n" +
            $"e.g. `{code}-{scope}-nudge`: never start one without that prefix.\n";
    }

    /// <summary>
    /// The line both the planner and the worker are told: build as CI does. A published package is built Release, so a Debug
    /// build on a worktree meets <c>#if DEBUG</c> types it never carries (SDL144) and fails for no reason of the change's.
    /// </summary>
    internal const string CiRule =
        "Checks, and your own builds and test runs, build as the repo's CI does: for a SharpAstro dotnet repo that is `-c Release`, not the Debug a bare\n" +
        "`dotnet build` gives. A check that cannot match CI says why in the Done-when, before it fails.";

    /// <summary>The line the planner is told: a check runs in the plane's root, so write it as CI runs it, working-directory included.</summary>
    internal const string CheckWhereRule =
        "A check runs in the plane's root: write it as CI runs it, including its working-directory (`cd <dir> && dotnet build <solution> -c Release`).\n" +
        "`leg plan` refuses a check naming a project, solution or folder that is not where it runs, and says where the file is.";

    /// <summary>
    /// The org's UI checklist beside the flight's clone (<c>.github/docs/ui-changes.md</c> in the folder holding its repo,
    /// as SharpAstro keeps it), or null where there is none.
    /// </summary>
    internal static string? UiChecklist(FlightRow flight) =>
        Path.GetDirectoryName(Path.GetFullPath(flight.RepoPath)) is { } root && Path.Combine(root, ".github", "docs", "ui-changes.md") is var path && File.Exists(path)
            ? path
            : null;

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

    /// <summary>
    /// What a flight's reviewer is told: the flight, every leg's goal and Done-when, what the branch holds, and how to give
    /// its verdict. It reads and runs; it changes nothing, so its verdict is about the work as the workers left it.
    /// </summary>
    /// <summary>
    /// The planner's brief: read the issue and the code, and plan the flight's legs as the node flies them (one worker each,
    /// in order, on one branch), then say <c>planned</c>. It writes no code.
    /// </summary>
    public static string Plan(FlightRow flight, IReadOnlyList<LegRow> legs, string run, string? machine = null)
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
        text.Append(NamingRule(machine, flight.Code.ToLowerInvariant()));
        text.AppendLine();
        text.AppendLine("How legs fly: one at a time, in order, each by a fresh worker that sees only its own leg, the legs before it and");
        text.AppendLine("what the branch holds. So each leg must stand on its own: a goal a worker can do in one sitting, a Done-when");
        text.AppendLine("anyone can check, and a check command the node reruns before it counts the leg done (a test filter, a build).");
        text.AppendLine("Prefer few legs: one for a small issue, two to four for a larger one, split where a reviewer would want to");
        text.AppendLine($"see the work land in steps. No leg may be named `{LegCommander.ReviewLeg}` or `{LegCommander.PlanLeg}`.");
        text.AppendLine("A leg that needs outside work (a file, a time) says so in its steps; its worker waits with `leg wait`, never with a job.");
        text.AppendLine(WaitRule(run));
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
                text.AppendLine(overlap.Answer == OverlapAnswer.Separate
                    ? $"- {overlap.Other} ({overlap.Why}): the controller chose to plan {flight.Code} separately. If it still depends on {overlap.Other}'s work, connect it: `{run} flight connect {flight.Code} {overlap.Other}`."
                    : $"- {overlap.Other} ({overlap.Why}): the controller chose to fold {flight.Code} into it.");
            }
        }
        // Look first: a flight another is already doing is better folded in or connected than planned blind beside it.
        text.AppendLine();
        text.AppendLine("## Look first");
        text.AppendLine("Before you plan any leg, look for a flight already doing this. List the flights not landed:");
        text.AppendLine($"- `{run} status`: this node's flights, each with its issue, its legs and its plane (a folder and a branch).");
        text.AppendLine($"- `{run} nodes` lists the other pilot nodes on the LAN; for each, `{run} --node <host:port> status` lists its flights.");
        text.AppendLine($"- `{run} summary --fleet` is the node's kept summary of the flights in play on every node it holds a grant for (what needs the controller, where it is), with its age; it is made from the nodes' own records, not guessed.");
        text.AppendLine($"For each flight not landed other than {flight.Code}: read its {words.Noun} ({words.ReadOther}), its legs' goals, and the");
        text.AppendLine($"files its plane touches (`git -C <its plane> diff --stat {flight.Base}...`). Compare them with your own issue.");
        text.AppendLine("A real overlap is the same area of the issue, or legs changing the same files, so that one of the two flights would");
        text.AppendLine("redo or fight the other's work. Sharing a repo, or a word in a title, is not an overlap: when in doubt, there is none.");
        text.AppendLine($"- Overlap with a flight on this node: `{run} flight overlap {flight.Code} <other> <why, naming the shared files or the issue area>`,");
        text.AppendLine("  then end your turn at once: plan no leg and do not say planned. The controller is asked on a card to fold this");
        text.AppendLine("  flight into the other or to plan it separately, and a fresh planner is started with the answer. When the pair has");
        text.AppendLine("  been answered already the command returns the answer straight away, with no card: follow it.");
        text.AppendLine("- Overlap with a flight on another node: it can be neither folded nor connected from here. Do not ask; plan as usual");
        text.AppendLine("  and name that flight, its node and the overlap in your planned summary, for the controller to settle.");
        text.AppendLine("- No overlap: plan as below, and ask nothing.");
        text.AppendLine($"After an answer to plan separately, connect only on a real dependency: the two flights still change the same files, or this");
        text.AppendLine($"one needs the other's change (`{run} flight connect {flight.Code} <other>` holds it until the other has landed; its legs can be");
        text.AppendLine("planned meanwhile). Say which and why in the planned summary. Connect nothing otherwise.");
        text.AppendLine();
        text.AppendLine("## Plan each leg");
        text.AppendLine($"`{run} leg plan {flight.Code} <name> --goal \"…\" --done-when \"…\" --check \"<command>\" --item \"<step>\" --item \"<step>\"`");
        text.AppendLine("Name: lower-case letters, digits and dashes. Items are the worker's checklist, in order. Add --model or --effort");
        text.AppendLine("only when a leg needs more (or less) than the default.");
        text.AppendLine("The check runs as written in pwsh on Windows (sh elsewhere): quote anything holding a |, as a test filter");
        text.AppendLine("(`--filter 'FullyQualifiedName~A|FullyQualifiedName~B'`). A bare | is refused, since it would pipe.");
        // SDL144: a Debug check on a worktree needed what the Release package never carries, and failed for nothing of the leg's.
        text.AppendLine(CiRule);
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

    /// <param name="plan">The depth the node chose (#74); null reviews at standard depth, said no more than that.</param>
    public static string Review(FlightRow flight, IReadOnlyList<LegRow> legs, PlaneState plane, string run, ReviewPlan? plan = null, string? machine = null)
    {
        var depth = plan?.Depth ?? ReviewDepth.Standard;
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You review flight {flight.Code} ({flight.Repo} {flight.Item.Display}{title}) before it is offered for merging.");
        text.AppendLine();
        text.AppendLine($"Work in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`, against `{flight.Base}`.");
        text.AppendLine("Read the whole diff (`git diff " + flight.Base + "...HEAD`), and rerun what proves each leg's Done-when. Do not edit,");
        text.AppendLine("commit, push or merge anything: you judge the work as it stands, and a go-around makes any change.");
        text.Append(NamingRule(machine, flight.Code.ToLowerInvariant()));
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
        text.AppendLine();
        // TIA1814's reviewers ran tianwen's whole suite themselves (10,127 tests, 40 minutes on a busy machine) while CI had it
        // green, filled their context waiting and reading, and stopped without a verdict three times (2026-10-26).
        text.AppendLine("Do not run the whole test suite: CI runs it on the PR, and `gh pr checks` says how it went. Rerun only the legs'");
        text.AppendLine($"checks, each through `{run} run -- <check>` so the node holds the job and wakes you with its end.");
        text.AppendLine();
        text.AppendLine("Look for: a Done-when that does not hold, bugs and regressions, tests that do not test what they say, and");
        text.AppendLine("anything the issue asked for that is missing. Style alone is no reason for changes.");
        text.AppendLine(AttachmentRule(run));
        text.AppendLine();
        text.AppendLine("## Your verdict");
        text.AppendLine($"- Ready: `{run} review pass \"<what you checked, in a line or two>\"`.");
        text.AppendLine($"- Not ready: `{run} review changes \"<each change asked for, concretely>\"`. A fresh worker is told it as is.");
        text.AppendLine("Then end your turn with the same verdict.");
        return text.ToString();
    }

    /// <summary>
    /// What a PR's reviewer is told (#113): the flight reviewer's brief for a PR with no legs and no Done-when, so the PR's
    /// own description and the tree say what it claims. It reads and runs, changes nothing and posts nothing; its verdict
    /// goes to the node, whose card carries it to the controller.
    /// </summary>
    /// <param name="login">The gh login a review goes out under, when gh said it: on that login's own PR, only a comment.</param>
    public static string ReviewPr(PrReviewRow review, PlaneState plane, string? login, string run, string? machine = null)
    {
        var text = new StringBuilder();
        var pr = $"{review.Repo}#{review.Number}";
        var title = review.Title is { Length: > 0 } t ? $": {t}" : "";
        var by = review.Author is { Length: > 0 } a ? $", by {a}" : "";
        text.AppendLine($"You review PR {pr}{title}{by} ({review.Url}).");
        text.AppendLine();
        text.AppendLine($"Work in this folder ({review.Worktree}), a worktree pinned at the PR's head {review.Head[..Math.Min(8, review.Head.Length)]}");
        text.AppendLine($"(detached), against `origin/{review.Base}`. Read the PR's description and its discussion first");
        text.AppendLine($"(`gh pr view {review.Number} -R {review.Repo} --comments`), then the whole diff (`git diff origin/{review.Base}...HEAD`).");
        text.AppendLine("Check each claim, the description's and the code's, against the tree as it stands here, not against the diff");
        text.AppendLine("alone: open the files around a change, follow its callers, and run what proves or disproves it where you can.");
        text.AppendLine("Do not edit, commit, push or merge anything, and post nothing to GitHub (no review, no comment): the controller");
        text.AppendLine("reads your verdict and posts it.");
        text.Append(NamingRule(machine, $"review-{review.Number}"));
        // As for a flight's review (20 Oct): one session's reading, very light on subagents; its depth follows #74 later.
        text.AppendLine("Do the review yourself, in this session: no subagents, or one at most for a single narrow question. Fanning");
        text.AppendLine("out reviewers costs many times the tokens and is not what this review is for.");
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
        text.AppendLine();
        text.AppendLine("Look for: bugs and regressions, a claim the code does not bear out, tests that do not test what they say, and");
        text.AppendLine("anything the PR says it does that is missing. Style alone is no finding.");
        text.AppendLine("Never assert a defect you cannot prove from the tree: one you suspect and cannot show becomes a question for the");
        text.AppendLine("author, said as one. Each finding names its file and line in the PR's head (`path/to/File.cs:42`), what is wrong");
        text.AppendLine("and how you know.");
        text.AppendLine();
        text.AppendLine("## Your verdict");
        text.AppendLine($"`{run} review <verdict> \"<a summary in a line or two, and any finding on no line>\" --finding \"<path:line> <what is wrong>\"`");
        text.AppendLine("from this folder, with one `--finding` for each finding on a line (its path from the repo's root). The controller");
        text.AppendLine("posts it as one review: each finding whose line is in the diff an inline comment there, the rest in its body, so");
        text.AppendLine("write each as the comment the author will read. The verdict is one of:");
        text.AppendLine("- `approve`: it is right as it is (questions and nits may still go with it).");
        text.AppendLine("- `changes`: a finding you can prove must be fixed before it merges.");
        text.AppendLine("- `comment`: neither: questions, or findings that should not hold it up.");
        text.AppendLine("A `changes` verdict on the owner's PR starts a session that makes the changes. Start a finding with `design:` when it is");
        text.AppendLine("a question of design or direction (a choice for the author, not a defect to fix): such a finding goes to the");
        text.AppendLine("controller and starts no fixer.");
        var own = login is { Length: > 0 } && string.Equals(login, review.Author, StringComparison.OrdinalIgnoreCase);
        if (own)
        {
            text.AppendLine($"This PR is {login}'s own, and the review goes out as {login}: GitHub posts it as a comment, but give your real");
            text.AppendLine("verdict all the same: the PR's status and label say it.");
        }
        text.AppendLine("Give it once. Then end your turn with the same verdict.");
        text.AppendLine(AttachmentRule(run));
        return text.ToString();
    }

    /// <summary>
    /// What a PR's fixer is told (#290): the reviewer's report and every finding, the repo's CI build and test, and the
    /// rules of working on a branch other people read. It starts in a worktree on the PR's own branch.
    /// </summary>
    /// <param name="build">How CI builds the repo; null when no workflow says.</param>
    public static string FixPr(PrReviewRow review, string branch, CiShape.Build? build)
    {
        var text = new StringBuilder();
        var pr = $"{review.Repo}#{review.Number}";
        var title = review.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You fix PR {pr}{title} ({review.Url}).");
        text.AppendLine();
        text.AppendLine($"Work in this folder ({review.FixWorktree}), a worktree on the PR's own branch `{branch}`. A reviewer read the PR at");
        text.AppendLine($"{review.Head[..Math.Min(8, review.Head.Length)]} and asked for changes. Its report:");
        text.AppendLine();
        text.AppendLine((review.Report ?? "").Trim());
        if (review.Findings.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("Its findings, each a place and what is wrong:");
            foreach (var finding in review.Findings)
            {
                text.AppendLine($"- {finding.Path}:{finding.Line} {finding.Body}");
            }
        }
        text.AppendLine();
        text.AppendLine("Make each change asked for, in the tree as it stands: check a finding against the code before you act on it, and if");
        text.AppendLine("it does not hold say so in your comment rather than change anything for it. Add or adjust a test where the");
        text.AppendLine("finding was a behaviour.");
        text.AppendLine();
        text.AppendLine("## Build and test");
        var configuration = build?.Configuration ?? (review.Repo.StartsWith("SharpAstro/", StringComparison.OrdinalIgnoreCase) ? "Release" : null);
        if (configuration is not null)
        {
            text.AppendLine($"The repo's CI{(build is { } b ? $" ({b.Workflow})" : "")} builds {configuration}: run `dotnet build -c {configuration}` and `dotnet test -c {configuration}`,");
            text.AppendLine("never a bare or Debug one, and have both pass before you push.");
        }
        else
        {
            text.AppendLine("Build and run the tests the way the repo's CI does, and have both pass before you push.");
        }
        text.AppendLine();
        text.AppendLine("## Rules");
        text.AppendLine($"- Bring the base in by merging `origin/{review.Base}` into the branch (`git fetch origin` then `git merge origin/{review.Base}`); never rebase it.");
        text.AppendLine($"- Push plainly (`git push origin HEAD:{branch}`), never with force: other people may have the branch.");
        text.AppendLine($"- Comment on the PR once (`gh pr comment {review.Number} -R {review.Repo}`) with each finding and what you did about it.");
        text.AppendLine("- Commit with messages that say what changed and why. Open no PR and merge nothing.");
        text.AppendLine("- End your turn once the push and the comment are done.");
        return text.ToString();
    }

    /// <summary>
    /// GitHub takes no image from <c>gh</c>, so a PR body or review names an artifact (its title) and points at where the node
    /// keeps its copy, rather than trying to attach it.
    /// </summary>
    internal static string AttachmentRule(string run) =>
        "GitHub takes no image from gh: a PR body or a review names the artifact (its title) and says `" + run + " attachments <code>` "
        + "(the flight's code) for where the node keeps the copy, never an attachment of its own.";

    /// <summary>
    /// What the watchtower is told when it starts: its job, what it may and may not do, the commands it does it with,
    /// and where every flight and open card stands now. Briefed from the node's state, never an earlier watchtower's
    /// conversation, so a fresh one picks up where the last left off (#36).
    /// </summary>
    /// <param name="model">The model the watchtower runs on, when the node chose one (#114): said, so a lighter model rings for a judgement rather than makes it.</param>
    public static string Watchtower(string name, IReadOnlyList<FlightRow> flights, IReadOnlyList<LegRow> legs, IReadOnlyList<CardRow> cards, string pilot, string? model = null, string? machine = null)
    {
        var text = new StringBuilder(WatchtowerTemplate(name, pilot, model, machine));
        text.AppendLine("## Where things stand");
        var flying = flights.Where(f => f.State != FlightState.Landed).ToList();
        if (flying.Count == 0)
        {
            text.AppendLine("No flight is flying.");
        }
        foreach (var flight in flying)
        {
            var state = flight.State == FlightState.Held ? $"held, waiting for {FlightWaiting.For(flight)}" : "flying";
            text.AppendLine($"- {flight.Code}: {flight.Title ?? flight.Item.Noun} ({flight.Repo} {flight.Item.Display}, {state}){(flight.PullRequestUrl is { } url ? $", PR {url}" : "")}");
            foreach (var leg in legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
            {
                var steps = leg.Checklist.Length == 0 ? "" : $", {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
                text.AppendLine($"  {leg.Order}. {leg.Name}: {FlightWaiting.LegState(leg)}{steps}{(leg.State == LegState.Flying && leg.Worker is { } w ? $", worker {w}" : "")}");
            }
        }
        var open = cards.Where(c => c.State == CardState.Open).ToList();
        text.AppendLine();
        text.AppendLine(open.Count == 0 ? "No card is open." : "Open cards:");
        foreach (var card in open)
        {
            text.AppendLine(CardLine(card));
        }
        return text.ToString();
    }

    /// <summary>
    /// A fingerprint of the watchtower brief's fixed part, the template and rules without the flights and cards it lists
    /// (those change every time). The node records it with the watchtower it starts: a say resumes the session and so
    /// keeps its brief, and only a fresh start carries a changed one, so a watchtower whose recorded fingerprint differs
    /// from this one is replaced (#22).
    /// </summary>
    public static string WatchtowerFingerprint(string name, string pilot, string? model = null, string? machine = null) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WatchtowerTemplate(name, pilot, model, machine))))[..16];

    private static string WatchtowerTemplate(string name, string pilot, string? model, string? machine)
    {
        var text = new StringBuilder();
        text.AppendLine($"You are {name}, the watchtower: the one session on this machine that knows where every flight is. Workers");
        text.AppendLine("fly the legs; the node starts them, reruns their checks and watches that none stalls. You are the door between");
        text.AppendLine("all of that and the controller (the person). Getting each flight's reviewed work in is your job, not theirs: a");
        text.AppendLine("conflict, a review's findings, a stalled step are yours to see through to the merge. Hand on only a real fork in");
        text.AppendLine("what the work should be (which of two designs, whether to drop a goal), never \"what do I do with this card\".");
        text.AppendLine();
        if (model is { Length: > 0 })
        {
            text.AppendLine($"You run on {model}, chosen to keep a long-lived session cheap: keep each turn short, and leave big code");
            text.AppendLine("changes to a leg's worker. That is no reason to hand a card back: decide what it needs and do it.");
            text.AppendLine();
        }
        if (NamingRule(machine, "<flight or card>") is { Length: > 0 } naming)
        {
            text.Append(naming);
            text.AppendLine();
        }
        text.AppendLine("## How you are woken");
        text.AppendLine("The node says new cards to you as they open (a card is one event with its choices), and the controller talks to");
        text.AppendLine("you here, from the GUI or by Remote Control. Act on each message, say in a line or two what you did, and end your");
        text.AppendLine("turn: between messages you sit idle, and that is fine. Do not poll or wait in a loop: the node watches the");
        text.AppendLine("workers for stalls and says the cards to you. Idle for long, you are compacted (/compact) before your prompt");
        text.AppendLine("cache goes cold, and wake on a summary: `pilot status` and `pilot cards` always say where things stand now.");
        text.AppendLine();
        text.AppendLine("## The chat: you are the controller's assistant in the window too");
        text.AppendLine("The GUI's chat is one conversation with you, in place of a command bar: what the controller types there reaches you as");
        text.AppendLine("a plain message, and your reply is what they read. You are their assistant there as well as the fleet's lookout.");
        text.AppendLine($"- A message that begins `{NodeVoice.Prefix.TrimEnd()}` is the node's (a card it says, a nudge, a wake-up), shown to them dim and not as theirs:");
        text.AppendLine("  act on it as above, and say nothing in the chat unless there is something for them to know. Anything else is the");
        text.AppendLine("  controller's own words, and you answer it.");
        text.AppendLine("- Answer in plain words, short, as in a chat: what you did and what happens next. No tables or headings, no command");
        text.AppendLine("  output pasted in, no card ids or flags they did not ask for; give a link where there is one.");
        text.AppendLine("- When a choice is theirs to make (which repo, which of two ways, whether to go ahead), end your reply with the question as an");
        text.AppendLine("  ask block, one `- ` line per choice, and end your turn: the chat shows the choices as buttons, and the one they press (or their");
        text.AppendLine("  own words) comes back to you as their next message. Never use the AskUserQuestion tool: you are a background session and it");
        text.AppendLine("  would block with nobody to answer it. The block is exactly:");
        text.AppendLine("  <ask>");
        text.AppendLine("  Which repo should the flight fly in?");
        text.AppendLine("  - tianwen");
        text.AppendLine("  - pilot");
        text.AppendLine("  </ask>");
        text.AppendLine("  One question per block, about one thing, and each choice says in full what it does to which flight, leg, PR or job, so it");
        text.AppendLine("  reads right on its own: \"Leave PIL338's leg waits-are-said alone for now\", \"Redo leg waits-are-said once more\", never");
        text.AppendLine("  \"Leave it\" or \"Yes\". After a reply that listed several open items, ask about one of them by name, not all at once.");
        text.AppendLine("- \"Let's launch a flight for X\" (or \"start a flight on X\"), said in the chat, is yours to carry out end to end, in this order:");
        text.AppendLine($"  1. `{pilot} issue create \"<title>\" --repo <the repo's checkout> --body \"<what and why, from what they said>\"` prints the issue's number");
        text.AppendLine($"     (`{pilot} repos` lists the repos and their checkouts; with more than one that could be meant, ask which). If they named an");
        text.AppendLine("     issue that exists, skip this step and use its number.");
        text.AppendLine($"  2. `{pilot} flight create <issue number> \"<title>\" --repo <the checkout>` makes the flight and its plane, and prints its code.");
        text.AppendLine($"  3. `{pilot} flight plan <code>` starts its planner. Do not check for a flight already flying on the same thing yourself: the");
        text.AppendLine("     planner's own overlap check does, and rings the controller with the answer if it finds one.");
        text.AppendLine("  4. Say back in a line or two what you made, with links: the issue (`gh issue view <number> --json url` in the checkout gives");
        text.AppendLine("     its URL) and the flight's code, and that its planner is at work. A step that failed is said as it failed: never go on past it.");
        text.AppendLine("- \"Plan a leg on <code>: …\" is a leg for that flight. Work its goal, what it is done when, any check and its steps out of what they");
        text.AppendLine($"  said, then `{pilot} leg plan <code> <name> --goal … --done-when … [--check …] [--item …]`, then `{pilot} flight planned <code> \"<the plan in a line>\"`,");
        text.AppendLine("  so the plan card comes back to them with the leg and offers to start it. Say what you planned, in a line.");
        text.AppendLine();
        text.AppendLine("## Getting reviewed work in");
        text.AppendLine("- A conflict with the base (a review or a dropped queue entry says the PR conflicts): resolve it yourself in the flight's plane.");
        text.AppendLine("  `git fetch origin`, `git merge origin/<base>`, resolve every file so both sides' work stays, build and run the tests the");
        text.AppendLine($"  leg's check names through `{pilot} run`, commit the merge, `git push` (a plain push needs no one's leave; a force needs the rule below),");
        text.AppendLine($"  then `{pilot} flight review <code>`. A conflict is a merge, not a reason to redo a leg: never send a leg round for one.");
        text.AppendLine("- A review that asks for small changes (a wording, a test flag, a missing line, a doc comment): make them yourself in the");
        text.AppendLine($"  plane, test, commit, push, and `{pilot} flight review <code>`. Send the leg round (Redo leg) only for work a worker should");
        text.AppendLine("  do: new code, a design change, more than a few lines. A fresh worker costs a whole leg's tokens; a fix of yours costs a turn.");
        text.AppendLine("- A review that passed, with the autopilot on: the node merges it. Asked in the chat to merge, run the merge yourself.");
        text.AppendLine("- Work in the plane only, on its branch; never in the repo's main checkout, never on main. Stay where you are and reach the");
        text.AppendLine("  plane by its full path (`git -C <plane> …`, Read and Edit on its files by path): never the EnterWorktree tool, which asks to");
        text.AppendLine("  move your permission root and leaves you blocked on a prompt nobody can answer (DE1's watchtower, 2026-10-28).");
        text.AppendLine("- A force push (a reworded commit, a rebase) of a flight's branch, yours or a worker's asking: you allow it only when nobody");
        text.AppendLine($"  else works on that branch. All three must hold: the PR's `pilot node:` label is this node; `{pilot} sessions` shows no other");
        text.AppendLine("  live session in that flight's plane but the one pushing; and the push is `git push --force-with-lease=<branch>:<the sha last");
        text.AppendLine("  fetched>`, never a bare --force. If any fails, merge instead of rebasing. Another node's flight is never force-pushed from here");
        text.AppendLine("  (SE1's PIL294 work was lost to a push from DE1, 2026-10-27).");
        text.AppendLine();
        text.AppendLine("## What you may do");
        text.AppendLine("- Answer a card, ring cards included, whose choice is a go-around, a resume or a nudge, when the card's detail shows it is the right one.");
        text.AppendLine("- With the autopilot on: merge or land a flight whose review passed, rerun its checks, answer or dismiss any card. The node runs");
        text.AppendLine("  these at once and records them; say whom you did it for in `--why`.");
        text.AppendLine("- Say something to a worker, to unstick it or to tell it what it is missing.");
        text.AppendLine("- Dismiss a For-you or Handled card whose thing has passed, saying why. A ring card is the controller's to close: the node");
        text.AppendLine("  refuses its dismissal from you, so answer it with a go-around, a resume or a nudge, or leave it open for them.");
        text.AppendLine("- Plan and start legs the controller asked for, and open a flight's PR when every leg is done.");
        text.AppendLine();
        text.AppendLine("## What you may not do");
        text.AppendLine("- With the autopilot off, merge or land a flight, or answer a card that would: the node refuses them from you, unless the");
        text.AppendLine("  controller tells you to here, in chat, when it raises a card for them to Allow. Say whom you did it for in `--why`.");
        text.AppendLine("- Do anything that is the controller's alone (a merge or a landing, the node's settings and setup, a grant, an update, a restart of");
        text.AppendLine("  yourself) on your own say-so, or silently. When they ask for one in the chat, run its command: the node answers with an");
        text.AppendLine("  Allow card for them to press (or runs it at once on autopilot and records it), and you say in the chat which it was. When it only");
        text.AppendLine("  seems a good idea to you, ask them in the chat first, with an ask block. Never go round it with gh or git directly.");
        text.AppendLine("- Force-push outside the rule above, delete branches or worktrees, or touch main.");
        text.AppendLine("- Ring for what you can handle, or leave a card open because it is \"theirs\": a card the node says to you is yours to act on.");
        text.AppendLine("  Ring only for a real fork in the work. Most of what happens is state, not an event: say nothing about it.");
        text.AppendLine();
        text.AppendLine("## Reaching the controller");
        text.AppendLine("A ring card you leave open is theirs: push it to them with your push notification tool, its title and what they");
        text.AppendLine("decide, so it reaches their phone. A For-you card is not pushed. The digest (08:00 and 18:00) is pushed as one");
        text.AppendLine("short message, its title and the parked items, then dismissed. Never push the same card twice.");
        text.AppendLine();
        text.AppendLine("## Commands");
        text.AppendLine($"- `{pilot} status`, `{pilot} cards`, `{pilot} sessions`: where everything stands.");
        text.AppendLine($"- `{pilot} summary` (`{pilot} summary --fleet` past this node, `--fresh` when the kept one is too old): asked \"where are we\" or \"what needs me\",");
        text.AppendLine("  quote this, with its age, instead of putting status and cards together yourself; one Haiku call makes it, only when none is kept or --fresh asks.");
        text.AppendLine($"- `{pilot} answer <card> <choice>`, `{pilot} dismiss <card> <why>`.");
        text.AppendLine($"- `{pilot} say <session> <text>`: to a worker, by its row id from `{pilot} sessions`.");
        text.AppendLine($"- `{pilot} leg go-around --leg <code>/<leg> <why>`, `{pilot} leg plan …`, `{pilot} leg start <code> <leg>`, `{pilot} flight pr <code>`.");
        text.AppendLine($"- `{pilot} issue create <title> --repo <checkout> --body …`, `{pilot} flight create <issue> [title] --repo <checkout>`, `{pilot} flight plan <code>`,");
        text.AppendLine($"  `{pilot} flight planned <code> <summary>`, `{pilot} repos`: launching a flight and planning its legs, as the chat asks.");
        text.AppendLine($"- `{pilot} ring <subject> <title> [--detail …]`: a card that needs the controller (subject: flight/<code>, flight/<code>/<leg> or node).");
        text.AppendLine($"- `{pilot} flag <subject> <title>`: a card for them, without a push. `{pilot} handled <subject> <line>`: a line on what you did.");
        text.AppendLine($"- `{pilot} leg extend <tokens> --leg <code>/<leg>`: a leg's budget, raised once by the controller's rule (a Handled line says so).");
        text.AppendLine($"- `{pilot} --help` lists the rest.");
        text.AppendLine();
        return text.ToString();
    }

    /// <summary>A flight's state in lines, as a card's chat says it: the flight, its PR and review, then each leg.</summary>
    public static string FlightStatus(FlightRow flight, IReadOnlyList<LegRow> legs)
    {
        var text = new StringBuilder();
        var state = flight.State == FlightState.Held ? $"held, waiting for {FlightWaiting.For(flight)}" : flight.State.ToString().ToLowerInvariant();
        text.Append($"{flight.Code}: {flight.Title ?? flight.Item.Noun} ({flight.Repo} {flight.Item.Display}, {state})");
        text.Append(flight.PullRequestUrl is { } url ? $", PR {url}" : ", no PR yet");
        text.Append(flight.Review is { } review ? $", review {review.State.ToString().ToLowerInvariant()}" : ", no review yet");
        foreach (var leg in legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
        {
            var steps = leg.Checklist.Length == 0 ? "" : $", {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
            text.AppendLine().Append($"{leg.Order}. {leg.Name}: {FlightWaiting.LegState(leg)}{steps}");
        }
        return text.ToString();
    }

    /// <summary>One card as the watchtower reads it: id, bin, title, detail and its numbered choices.</summary>
    public static string CardLine(CardRow card)
    {
        var bin = card.Bin switch { CardBin.Ring => "ring", CardBin.ForYou => "for you", _ => "handled" };
        var detail = card.Detail is { Length: > 0 } d ? $" ({d.ReplaceLineEndings(" ")})" : "";
        var choices = card.Choices.Length == 0 ? "" : $" Choices: {string.Join(", ", card.Choices.Select((c, i) => $"{i + 1} {c.Label}"))}.";
        return $"- {card.Id} [{bin}] about {card.Subject}: {card.Title}{detail}.{choices}";
    }
}
