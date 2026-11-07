using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>
    /// The flight whose plane holds <paramref name="cwd"/>, and who is asking: its reviewer while a review is on, as a
    /// worker is its leg's in <see cref="LegHere"/>.
    /// </summary>
    internal static (string Target, string Who) FlightHere(SnapshotEvent snapshot, string cwd, string fallback = "cli")
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the flight with --flight <code>");
        return (Verbs.FlightTarget(flight.Code), flight.Review is { State: ReviewState.Reviewing } review ? review.Reviewer : fallback);
    }
    private sealed partial class BuildContext
    {
        /// <summary>Makes the flight: create, plan, overlap, connect, open a PR, review, merge, land.</summary>
        internal Command FlightCommands()
        {
            // flight
            var issue = Arg("issue", "The issue the flight flies");
            var title = Words("title", "What it is for, in a line", required: false);
            var repo = new Option<string?>("--repo") { Description = RepoOptionDescription };
            var anyway = new Option<bool>(Verbs.AnywayFlag) { Description = "Create it although the issue carries another node's pilot node: label; says so on the issue" };
            var create = Action(new Command("create", "A flight on an issue of this repo, and its plane") { Arguments = { issue, title }, Options = { repo, anyway } },
                (p, client, ct) => Send(client, Verbs.CreateFlight, Verbs.RepoTarget(RepoPath(client, p.GetValue(repo), cwd)),
                    [p.GetValue(issue)!, .. p.GetValue(title) ?? [], .. p.GetValue(anyway) ? new[] { Verbs.AnywayFlag } : []], who, ct));

            var other = Arg("other", "The flight it waits for");
            var connect = Action(new Command("connect", "The flight holds until the other, a flight of this node or of a reachable one, has landed") { Arguments = { code, other } },
                (p, client, ct) => Send(client, Verbs.Connect, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(other)!], who, ct));

            var disconnectAnyway = new Option<bool>(Verbs.AnywayFlag) { Description = "Drop a connection the controller chose as the answer to an overlap (the person's to give)" };
            var disconnect = Action(new Command("disconnect", "Undo a connect: the flight stops waiting for the other (on this node or another) and flies when nothing else holds it") { Arguments = { code, other }, Options = { disconnectAnyway } },
                (p, client, ct) => Send(client, Verbs.Disconnect, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(other)!, .. p.GetValue(disconnectAnyway) ? new[] { Verbs.AnywayFlag } : []], who, ct));

            var fly = Action(new Command("fly", "Stop the flight's wait for a package nuget.org does not serve: it flies now") { Arguments = { code } },
                (p, client, ct) => Send(client, Verbs.FlyAnyway, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct));

            var level = Arg("level", "urgent, high, medium, low, or none to clear it");
            var priority = Action(new Command("priority", "Set the Priority field of the flight's issue (none clears it)") { Arguments = { code, level } },
                (p, client, ct) => Send(client, Verbs.FlightPriority, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(level)!], who, ct));

            var move = Action(new Command("move", "Hand the flight to the best willing node of the fleet: its plane is pushed and checked, and this node stops flying it") { Arguments = { code }, Options = { actingOn } },
                (p, client, ct) => Send(client, Verbs.MoveFlight, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct, p.GetValue(actingOn)));

            var prTitle = Words("title", "The PR's title after the code; else the flight's", required: false);
            var pr = Action(new Command("pr", "Push the plane and open its PR") { Arguments = { code, prTitle } },
                (p, client, ct) => Send(client, Verbs.OpenPr, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(prTitle) ?? [], who, ct));

            var head = Arg("head", "The head commit you reviewed; the merge is refused if the PR moved past it");
            var method = new Option<string?>("--method") { Description = "squash (the default), rebase or merge" };
            method.AcceptOnlyFromAmong("squash", "rebase", "merge");
            var squash = new Option<bool>("--squash") { Description = "Same as --method squash" };
            var rebase = new Option<bool>("--rebase") { Description = "Same as --method rebase" };
            var mergeCommit = new Option<bool>("--merge") { Description = "Same as --method merge" };
            var merge = Action(new Command("merge", "Merge the flight's PR, guarded on the head you reviewed, then land it")
                {
                    Arguments = { code, head },
                    Options = { method, squash, rebase, mergeCommit, actingOn },
                },
                (p, client, ct) =>
                {
                    var how = p.GetValue(method) ?? (p.GetValue(rebase) ? "rebase" : p.GetValue(mergeCommit) ? "merge" : p.GetValue(squash) ? "squash" : null);
                    string[] args = how is null ? [p.GetValue(head)!] : [p.GetValue(head)!, how];
                    return Send(client, Verbs.Merge, Verbs.FlightTarget(p.GetValue(code)!), args, who, ct, p.GetValue(actingOn));
                });

            var land = Action(new Command("land", "The flight's work is in: its plane goes, its branch and code stay") { Arguments = { code }, Options = { actingOn } },
                (p, client, ct) => Send(client, Verbs.Land, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct, p.GetValue(actingOn)));

            var reviewStart = Action(new Command("review", "Start a fresh reviewer on the flight's branch (flight pr starts one by itself)") { Arguments = { code } },
                (p, client, ct) => Send(client, Verbs.StartReview, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct));

            var allowWhy = Words("why", "Why one more round is worth it", required: false);
            var allowRound = Action(new Command("allow-round", "Allow one more review round on a flight whose review loop was held at its third round with changes") { Arguments = { code, allowWhy } },
                (p, client, ct) => Send(client, Verbs.AllowRound, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(allowWhy) ?? [], who, ct));

            var clearWhy = Words("why", "Why the flight is cleared to land", required: false);
            var clear = Action(new Command("clear", "Clear a flight held in a holding pattern (its review passed with notes) to land: its merge is no longer held") { Arguments = { code, clearWhy } },
                (p, client, ct) => Send(client, Verbs.Clear, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(clearWhy) ?? [], who, ct));

            var holdWhy = Words("why", "Why the flight is put on hold");
            var hold = Action(new Command("hold", "Put a flight on hold (the owner's hold): the node starts nothing for it and takes no outward step, an armed merge is disarmed, until flight unhold") { Arguments = { code, holdWhy } },
                (p, client, ct) => Send(client, Verbs.Hold, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(holdWhy) ?? [], who, ct));

            var unholdWhy = Words("why", "Why the hold is released", required: false);
            var unhold = Action(new Command("unhold", "Release a flight's hold: it takes its next due step") { Arguments = { code, unholdWhy } },
                (p, client, ct) => Send(client, Verbs.Unhold, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(unholdWhy) ?? [], who, ct));

            // plan: a planner worker plans the legs from the issue; planned: what it says when it is done.
            var planFlight = Action(new Command("plan", "Start a planner in the plane: it reads the issue and the code and plans the legs") { Arguments = { code } },
                (p, client, ct) => Send(client, Verbs.PlanFlight, Verbs.FlightTarget(p.GetValue(code)!), [], who, ct));
            var plannedSummary = Words("summary", "The plan in a line or two", required: false);
            var planned = Action(new Command("planned", "The planner is done: a card shows the legs and starts the first") { Arguments = { code, plannedSummary } },
                (p, client, ct) => Send(client, Verbs.Planned, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(plannedSummary) ?? [], who, ct));
            var overlapWhy = Words("why", "Why it looks like part of the other");
            var overlapFiles = new Option<string[]>("--file") { Description = "A file both flights change (repo-relative, as flight touches names it); give it once per file", AllowMultipleArgumentsPerToken = false };
            var overlapPick = new Option<string?>("--pick") { Description = "What flight touches picked for this flight: fold (this node's flight only) or connect (the default); the card's title says it" };
            overlapPick.AcceptOnlyFromAmong(Verbs.OverlapPickFold, Verbs.OverlapPickConnect);
            var overlap = Action(new Command("overlap", "The planner found the flight to overlap another, on this node or another: asks the controller to fold it in (this node only), connect it or plan in parallel") { Arguments = { code, other, overlapWhy }, Options = { overlapFiles, overlapPick } },
                (p, client, ct) => Send(client, Verbs.Overlap, Verbs.FlightTarget(p.GetValue(code)!),
                    [p.GetValue(other)!, .. (p.GetValue(overlapPick) is { } pick ? new[] { Verbs.OverlapPickArg + pick } : []), .. (p.GetValue(overlapFiles) ?? []).Select(f => Verbs.OverlapFileArg + f), .. p.GetValue(overlapWhy) ?? []], who, ct));
            var touchedFiles = Words("file", "A file the flight's legs will change, repo-relative, as many as the legs have");
            var touches = Action(new Command("touches", "The planner's overlap check: compares the files its legs will change with every other flight in the air on every node and every open PR, and says fold, connect or parallel for each") { Arguments = { code, touchedFiles } },
                (p, client, ct) => Send(client, Verbs.Touches, Verbs.FlightTarget(p.GetValue(code)!), p.GetValue(touchedFiles) ?? [], who, ct));
            var separate = Action(new Command("separate", "Plan the flight in parallel with the other despite the overlap: a fresh planner is started, told the answer") { Arguments = { code, other } },
                (p, client, ct) => Send(client, Verbs.Separate, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(other)!], who, ct));
            var connectOverlap = Action(new Command("connect-overlap", "Answer an overlap by connecting the flight to the other (on this node or another): it holds until the other has landed, and a fresh planner is started, told the answer") { Arguments = { code, other } },
                (p, client, ct) => Send(client, Verbs.ConnectOverlap, Verbs.FlightTarget(p.GetValue(code)!), [p.GetValue(other)!], who, ct));
            var flight = new Command("flight", "Flights: create, plan, check what they touch, overlap, connect, disconnect, fly past a package wait, open a PR, review, merge, land")
                { Subcommands = { create, planFlight, planned, touches, overlap, separate, connectOverlap, connect, disconnect, fly, priority, move, pr, reviewStart, allowRound, clear, hold, unhold, merge, land } };

            return flight;
        }
    }
}
