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
    public static string For(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, string? why = null, PlaneState? plane = null, string? cli = null, string? socket = null)
    {
        var run = Run(cli, socket);
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You are the worker for leg `{leg.Name}` of flight {flight.Code} ({flight.Repo} #{flight.Issue}{title}).");
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
        text.AppendLine("## Keeping the node told");
        text.AppendLine("The node tracks this leg; tell it as you go, from this folder (it knows your leg by the folder):");
        if (leg.Checklist.Length > 0)
        {
            text.AppendLine($"- Tick each step as you finish it: `{run} check <step number>`.");
        }
        text.AppendLine($"- Something to look at (a screenshot, a capture, a page): `{run} artifact <path or URL> <title>`.");
        text.AppendLine($"- When every step is done and the Done-when holds: `{run} leg finish done \"<one-line report>\"`. The node reruns the");
        text.AppendLine("  check itself and refuses while a step is open or the check fails; fix what it says and finish again.");
        text.AppendLine($"- When you cannot go on: `{run} leg finish failed \"<why>\"`.");
        text.AppendLine($"- A full build or test run, or anything else long: `{run} run -- <command>`, then end your turn. The node");
        text.AppendLine("  runs it as a job and wakes you with how it ended and its last lines; do not wait for it or poll it. The machine");
        text.AppendLine("  is shared with other flights, so it may wait its turn first; that costs you nothing. Short commands (one test,"
            + " a quick build of one project) you run yourself, as usual."); 
        text.AppendLine();
        text.AppendLine("## When you stop");
        text.AppendLine("End your turn with a short report: what you did, how you checked it (the Done-when, rerun), and anything");
        text.AppendLine("left. Start it with DONE when every step is done and the Done-when holds, or BLOCKED: and why.");
        return text.ToString();
    }

    /// <summary>
    /// A leg's validator's brief: it checks the few facts about the tree that the leg rests on (files, types, verbs and tests it
    /// names or assumes), after the legs before it, and answers GO unless one is false. It judges no route, only facts, and
    /// changes nothing. A NO-GO names each stale fact with the command that showed it.
    /// </summary>
    public static string Validate(FlightRow flight, LegRow leg, IReadOnlyList<LegRow> legs, string run)
    {
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You are the validator of leg `{leg.Name}` of flight {flight.Code} ({flight.Repo} #{flight.Issue}{title}). A few minutes, one answer:");
        text.AppendLine("is the leg's brief still true of the tree the earlier legs left? You do not do the leg.");
        text.AppendLine();
        text.AppendLine($"Work in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`. Read only: change, commit,");
        text.AppendLine("push and delete nothing, and keep scratch output outside the plane. Do it yourself, in this session, with no subagents.");
        text.AppendLine("Your ceiling is a few minutes and about thirty tool calls; past it, answer with what you have.");

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
    public static string Plan(FlightRow flight, IReadOnlyList<LegRow> legs, string run)
    {
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You plan flight {flight.Code} ({flight.Repo} #{flight.Issue}{title}): the legs that will do its issue.");
        text.AppendLine();
        text.AppendLine($"Work in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`. Read the issue");
        text.AppendLine($"(`gh issue view {flight.Issue} --comments`), the repo's CLAUDE.md, and the code it touches. Do not edit, commit or push");
        text.AppendLine("anything: you plan, and each leg's worker does the work.");
        text.AppendLine("Do it yourself, in this session: no subagents, or one at most for a single narrow question.");
        text.AppendLine();
        text.AppendLine("How legs fly: one at a time, in order, each by a fresh worker that sees only its own leg, the legs before it and");
        text.AppendLine("what the branch holds. So each leg must stand on its own: a goal a worker can do in one sitting, a Done-when");
        text.AppendLine("anyone can check, and a check command the node reruns before it counts the leg done (a test filter, a build).");
        text.AppendLine("Prefer few legs: one for a small issue, two to four for a larger one, split where a reviewer would want to");
        text.AppendLine($"see the work land in steps. No leg may be named `{LegCommander.ReviewLeg}` or `{LegCommander.PlanLeg}`.");
        if (legs.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Legs it has already (plan any more after them; do not repeat them):");
            foreach (var leg in legs.OrderBy(l => l.Order))
            {
                text.AppendLine($"- {leg.Name} ({leg.State.ToString().ToLowerInvariant()}): {leg.Goal}");
            }
        }
        text.AppendLine();
        text.AppendLine("## Plan each leg");
        text.AppendLine($"`{run} leg plan {flight.Code} <name> --goal \"…\" --done-when \"…\" --check \"<command>\" --item \"<step>\" --item \"<step>\"`");
        text.AppendLine("Name: lower-case letters, digits and dashes. Items are the worker's checklist, in order. Add --model or --effort");
        text.AppendLine("only when a leg needs more (or less) than the default.");
        text.AppendLine();
        text.AppendLine("## When the legs are planned");
        text.AppendLine($"`{run} flight planned {flight.Code} \"<the plan in a line or two: why these legs, in this order>\"`. It shows the");
        text.AppendLine("controller the legs with a button to start the first. Then end your turn with the same line.");
        return text.ToString();
    }

    /// <param name="plan">The depth the node chose (#74); null reviews at standard depth, said no more than that.</param>
    public static string Review(FlightRow flight, IReadOnlyList<LegRow> legs, PlaneState plane, string run, ReviewPlan? plan = null)
    {
        var depth = plan?.Depth ?? ReviewDepth.Standard;
        var text = new StringBuilder();
        var title = flight.Title is { Length: > 0 } t ? $": {t}" : "";
        text.AppendLine($"You review flight {flight.Code} ({flight.Repo} #{flight.Issue}{title}) before it is offered for merging.");
        text.AppendLine();
        text.AppendLine($"Work in this folder, the flight's plane ({flight.Worktree}), on its branch `{flight.Branch}`, against `{flight.Base}`.");
        text.AppendLine("Read the whole diff (`git diff " + flight.Base + "...HEAD`), and rerun what proves each leg's Done-when. Do not edit,");
        text.AppendLine("commit, push or merge anything: you judge the work as it stands, and a go-around makes any change.");
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
        text.AppendLine("Look for: a Done-when that does not hold, bugs and regressions, tests that do not test what they say, and");
        text.AppendLine("anything the issue asked for that is missing. Style alone is no reason for changes.");
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
    public static string ReviewPr(PrReviewRow review, PlaneState plane, string? login, string run)
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
        var own = login is { Length: > 0 } && string.Equals(login, review.Author, StringComparison.OrdinalIgnoreCase);
        if (own)
        {
            text.AppendLine($"This PR is {login}'s own, and the review goes out as {login}: GitHub takes only a comment on one's own PR, so give");
            text.AppendLine("`comment`, with what you would have asked for in it.");
        }
        text.AppendLine("Give it once. Then end your turn with the same verdict.");
        return text.ToString();
    }

    /// <summary>
    /// What the watchtower is told when it starts: its job, what it may and may not do, the commands it does it with,
    /// and where every flight and open card stands now. Briefed from the node's state, never an earlier watchtower's
    /// conversation, so a fresh one picks up where the last left off (#36).
    /// </summary>
    /// <param name="model">The model the watchtower runs on, when the node chose one (#114): said, so a lighter model rings for a judgement rather than makes it.</param>
    public static string Watchtower(string name, IReadOnlyList<FlightRow> flights, IReadOnlyList<LegRow> legs, IReadOnlyList<CardRow> cards, string pilot, string? model = null)
    {
        var text = new StringBuilder(WatchtowerTemplate(name, pilot, model));
        text.AppendLine("## Where things stand");
        var flying = flights.Where(f => f.State != FlightState.Landed).ToList();
        if (flying.Count == 0)
        {
            text.AppendLine("No flight is flying.");
        }
        foreach (var flight in flying)
        {
            var state = flight.State == FlightState.Held ? $"held, waiting for {string.Join(", ", flight.Connections)}" : "flying";
            text.AppendLine($"- {flight.Code}: {flight.Title ?? $"issue #{flight.Issue}"} ({flight.Repo} #{flight.Issue}, {state}){(flight.PullRequestUrl is { } url ? $", PR {url}" : "")}");
            foreach (var leg in legs.Where(l => l.Flight == flight.Code).OrderBy(l => l.Order))
            {
                var steps = leg.Checklist.Length == 0 ? "" : $", {leg.Checklist.Count(i => i.Done)}/{leg.Checklist.Length} steps";
                text.AppendLine($"  {leg.Order}. {leg.Name}: {leg.State.ToString().ToLowerInvariant()}{steps}{(leg.State == LegState.Flying && leg.Worker is { } w ? $", worker {w}" : "")}");
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
    public static string WatchtowerFingerprint(string name, string pilot, string? model = null) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WatchtowerTemplate(name, pilot, model))))[..16];

    private static string WatchtowerTemplate(string name, string pilot, string? model)
    {
        var text = new StringBuilder();
        text.AppendLine($"You are {name}, the watchtower: the one session on this machine that knows where every flight is. Workers");
        text.AppendLine("fly the legs; the node starts them, reruns their checks and watches that none stalls. You are the door between");
        text.AppendLine("all of that and the controller (the person): handle what is yours, and hand on only what needs them.");
        text.AppendLine();
        if (model is { Length: > 0 })
        {
            text.AppendLine($"You run on {model}, chosen to keep a long-lived session cheap. A judgement call (whether a review's concern");
            text.AppendLine("blocks a merge, which way a design should go, whether a result is good enough) is the controller's: ring for it");
            text.AppendLine("with what you know rather than decide it.");
            text.AppendLine();
        }
        text.AppendLine("## How you are woken");
        text.AppendLine("The node says new cards to you as they open (a card is one event with its choices), and the controller talks to");
        text.AppendLine("you here, from the GUI or by Remote Control. Act on each message, say in a line or two what you did, and end your");
        text.AppendLine("turn: between messages you sit idle, and that is fine. Do not poll or wait in a loop: the node watches the");
        text.AppendLine("workers for stalls and says the cards to you. Idle for long, you are compacted (/compact) before your prompt");
        text.AppendLine("cache goes cold, and wake on a summary: `pilot status` and `pilot cards` always say where things stand now.");
        text.AppendLine();
        text.AppendLine("## What you may do");
        text.AppendLine("- Answer a card whose choice is a go-around, a resume or a nudge, when the card's detail shows it is the right one.");
        text.AppendLine("- Say something to a worker, to unstick it or to tell it what it is missing.");
        text.AppendLine("- Dismiss a card whose thing has passed, saying why.");
        text.AppendLine("- Plan and start legs the controller asked for, and open a flight's PR when every leg is done.");
        text.AppendLine();
        text.AppendLine("## What you may not do");
        text.AppendLine("- Merge or land a flight, or answer a card that would: those are the controller's, and the node refuses them from you.");
        text.AppendLine("- Push, force anything, delete branches or worktrees, or change code yourself: workers change code, in their planes.");
        text.AppendLine("- Ring for what you can handle. Ring for a stop you may not resolve, a real fork in the work, a gate before an");
        text.AppendLine("  irreversible step. Most of what happens is state, not an event: say nothing about it.");
        text.AppendLine();
        text.AppendLine("## Reaching the controller");
        text.AppendLine("A ring card you leave open is theirs: push it to them with your push notification tool, its title and what they");
        text.AppendLine("decide, so it reaches their phone. A For-you card is not pushed. The digest (08:00 and 18:00) is pushed as one");
        text.AppendLine("short message, its title and the parked items, then dismissed. Never push the same card twice.");
        text.AppendLine();
        text.AppendLine("## Commands");
        text.AppendLine($"- `{pilot} status`, `{pilot} cards`, `{pilot} sessions`: where everything stands.");
        text.AppendLine($"- `{pilot} answer <card> <choice>`, `{pilot} dismiss <card> <why>`.");
        text.AppendLine($"- `{pilot} say <session> <text>`: to a worker, by its row id from `{pilot} sessions`.");
        text.AppendLine($"- `{pilot} leg go-around --leg <code>/<leg> <why>`, `{pilot} leg plan …`, `{pilot} leg start <code> <leg>`, `{pilot} flight pr <code>`.");
        text.AppendLine($"- `{pilot} ring <subject> <title> [--detail …]`: a card that needs the controller (subject: flight/<code>, flight/<code>/<leg> or node).");
        text.AppendLine($"- `{pilot} flag <subject> <title>`: a card for them, without a push. `{pilot} handled <subject> <line>`: a line on what you did.");
        text.AppendLine($"- `{pilot} leg extend <tokens> --leg <code>/<leg>`: a leg's budget, raised once by the controller's rule (a Handled line says so).");
        text.AppendLine($"- `{pilot} --help` lists the rest.");
        text.AppendLine();
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
