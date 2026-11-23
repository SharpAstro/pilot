using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>
    /// An investigator's brief (PIL1285): a short Opus session the node starts on one subject with the evidence in front of it. It finds the
    /// cause and ends with exactly one of three outcomes, each with the exact command: an issue plus a flight, an action recorded with its
    /// reason, or one ring to the person saying what was tried and what is needed. The node records the outcome and ends the session.
    /// </summary>
    /// <param name="name">The session's name, <c>&lt;machine&gt;-investigate-&lt;subject&gt;</c>.</param>
    /// <param name="key">The subject's key: a flight code, a card id or <c>digest</c>.</param>
    /// <param name="target">The subject as a target (<c>flight/PIL12</c>, <c>card/c2737</c>, <c>node</c>).</param>
    /// <param name="triggers">What started the run, and any trigger that joined it before it started.</param>
    /// <param name="evidence">The cards whole, the repeat task's occurrences, the failure ledger's records, the flight's status lines and job ids.</param>
    /// <param name="run">The pilot command the commands are written with.</param>
    public static string Investigator(string name, string key, string target, IReadOnlyList<InvestigationTrigger> triggers, IReadOnlyList<string> evidence, string run)
    {
        var text = new StringBuilder();
        text.AppendLine($"You are `{name}`, the node's investigator on {key} ({target}). The node runs with a standing goal: flights keep flying,");
        text.AppendLine("pilot's own faults are found and fixed, cards are settled with evidence, and the person is rung only as a last resort.");
        text.AppendLine("The watchtower keeps the routine presses; you are started when it takes thinking: find the cause, then decide.");
        text.AppendLine();
        text.AppendLine($"Started on: {string.Join(", ", triggers.Distinct().Select(Describe))}.");
        text.AppendLine("A short run: read the evidence, look at what it names (the jobs with `" + run + " job <id>`, the cards with `" + run + " cards`,");
        text.AppendLine($"the flight with `{run} status`, the code and git log in this folder), and decide. Change no file and commit nothing here,");
        text.AppendLine("do it yourself with no subagents, and keep to about forty tool calls. A card that waits on you stays quiet until you end.");
        text.AppendLine();
        text.AppendLine("## Evidence");
        foreach (var line in evidence)
        {
            text.AppendLine($"- {line.ReplaceLineEndings("\n  ")}");
        }
        text.AppendLine();
        text.AppendLine("## End with exactly one of these");
        text.AppendLine("Each `--why` and each reason names the evidence it rests on (a job id such as j962, a card id, a commit, a run, an issue number).");
        text.AppendLine();
        text.AppendLine("1. **An issue and a flight**: the cause is a fault to fix (pilot's own, or the repo's). File it, start a flight on it, then report both:");
        text.AppendLine($"   `{run} issue create \"<what is wrong>\" --body \"<the cause and the evidence>\" --repo <repo> --why \"<evidence>\"`");
        text.AppendLine($"   `{run} flight create <issue number> --repo <repo> --why \"<evidence>\"`");
        text.AppendLine($"   `{run} {Verbs.Investigated} {key} {Pilot.Node.Flights.Investigator.IssueOutcome} \"<issue #N and flight CODE: the cause, in a line>\"`");
        text.AppendLine("2. **An action**: one thing the node lets you do settles it (a go-around, a rerun of checks, each with `--why`).");
        text.AppendLine("   Do it, then report it with its reason:");
        text.AppendLine($"   `{run} {Verbs.Investigated} {key} {Pilot.Node.Flights.Investigator.ActionOutcome} \"<what you did and why, naming the evidence>\"`");
        text.AppendLine("3. **One ring to the person**: only they can settle it (a merge, a setting, a decision of theirs, money, an outside account).");
        text.AppendLine("   The ring's words are yours: what was tried, and the one thing they need to do.");
        text.AppendLine($"   `{run} {Verbs.Investigated} {key} {Pilot.Node.Flights.Investigator.RingOutcome} \"Tried: <what>. Needed: <the one action>.\"`");
        text.AppendLine();
        text.AppendLine($"Report once; the node records it as a Handled line and a decision, and ends this session itself. Do not stop it yourself.");
        text.AppendLine("You may raise Handled lines and For-you notes (`" + run + " handled`, `" + run + " flag`); a ring goes only through outcome 3.");
        text.AppendLine("Merging, landing, the node's settings, overrides and answering a session's question are the person's: never yours.");
        text.AppendLine("If nothing needs doing, that is outcome 2 with the evidence that it settles by itself.");
        return text.ToString();
    }

    private static string Describe(InvestigationTrigger trigger) => trigger switch
    {
        InvestigationTrigger.Repeat => "a find-the-cause task (the same card or failure came a second time)",
        InvestigationTrigger.Stale => "a flight past its stale bound",
        InvestigationTrigger.Ring => "a card about to ring the person, which waits for your outcome",
        _ => "the digest, to read and act on or file from",
    };
}
