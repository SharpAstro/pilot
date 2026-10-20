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
        text.AppendLine();
        text.AppendLine("## When you stop");
        text.AppendLine("End your turn with a short report: what you did, how you checked it (the Done-when, rerun), and anything");
        text.AppendLine("left. Start it with DONE when every step is done and the Done-when holds, or BLOCKED: and why.");
        return text.ToString();
    }

    /// <summary>
    /// What the watchtower is told when it starts: its job, what it may and may not do, the commands it does it with,
    /// and where every flight and open card stands now. Briefed from the node's state, never an earlier watchtower's
    /// conversation, so a fresh one picks up where the last left off (#36).
    /// </summary>
    public static string Watchtower(string name, IReadOnlyList<FlightRow> flights, IReadOnlyList<LegRow> legs, IReadOnlyList<CardRow> cards, string pilot)
    {
        var text = new StringBuilder();
        text.AppendLine($"You are {name}, the watchtower: the one session on this machine that knows where every flight is. Workers");
        text.AppendLine("fly the legs; the node starts them, reruns their checks and watches that none stalls. You are the door between");
        text.AppendLine("all of that and the controller (the person): handle what is yours, and hand on only what needs them.");
        text.AppendLine();
        text.AppendLine("## How you are woken");
        text.AppendLine("The node says new cards to you as they open (a card is one event with its choices), and the controller talks to");
        text.AppendLine("you here, from the GUI or by Remote Control. Act on each message, say in a line or two what you did, and end your");
        text.AppendLine("turn: between messages you sit idle, and that is fine. Do not poll or wait in a loop: when you have been idle");
        text.AppendLine("for 50 minutes the node sends you on a round (look at status and cards, act on what stalled), which also keeps");
        text.AppendLine("your prompt cache warm. Keep a quiet round to one word.");
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
        text.AppendLine("## Commands");
        text.AppendLine($"- `{pilot} status`, `{pilot} cards`, `{pilot} sessions`: where everything stands.");
        text.AppendLine($"- `{pilot} answer <card> <choice>`, `{pilot} dismiss <card> <why>`.");
        text.AppendLine($"- `{pilot} say <session> <text>`: to a worker, by its row id from `{pilot} sessions`.");
        text.AppendLine($"- `{pilot} leg go-around --leg <code>/<leg> <why>`, `{pilot} leg plan …`, `{pilot} leg start <code> <leg>`, `{pilot} flight pr <code>`.");
        text.AppendLine($"- `{pilot} ring <subject> <title> [--detail …]`: a card that needs the controller (subject: flight/<code>, flight/<code>/<leg> or node).");
        text.AppendLine($"- `{pilot} flag <subject> <title>`: a card for them, without a push. `{pilot} handled <subject> <line>`: a line on what you did.");
        text.AppendLine($"- `{pilot} --help` lists the rest.");
        text.AppendLine();
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

    /// <summary>One card as the watchtower reads it: id, bin, title, detail and its numbered choices.</summary>
    public static string CardLine(CardRow card)
    {
        var bin = card.Bin switch { CardBin.Ring => "ring", CardBin.ForYou => "for you", _ => "handled" };
        var detail = card.Detail is { Length: > 0 } d ? $" ({d.ReplaceLineEndings(" ")})" : "";
        var choices = card.Choices.Length == 0 ? "" : $" Choices: {string.Join(", ", card.Choices.Select((c, i) => $"{i + 1} {c.Label}"))}.";
        return $"- {card.Id} [{bin}] about {card.Subject}: {card.Title}{detail}.{choices}";
    }
}
