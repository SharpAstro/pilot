#nullable enable
using System.Text;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public static partial class Briefs
{
    /// <summary>
    /// The designer's brief (PIL1151): the contract of the one session the node keeps for a design. It names the design's folder and its
    /// <c>draft/</c>, the loop it works in, and the one rule on publishing. A later piece of the brief reaches the same session as a
    /// message (<see cref="DesignerPiece"/>), so this is told once.
    /// </summary>
    /// <param name="detail">The design as <c>design show</c> reads it: the record, BRIEF.md so far and its folders.</param>
    /// <param name="prototype">The current version's <c>prototype.json</c>, or the starter's when there is none yet.</param>
    /// <param name="notes">The current version's <c>NOTES.md</c>, or null when there is none.</param>
    /// <param name="run">How the CLI is run (<see cref="Run"/>).</param>
    public static string Designer(DesignDetail detail, string prototype, string? notes, string run)
    {
        var id = detail.Row.Id;
        var text = new StringBuilder();
        text.AppendLine($"You are the designer of {id}{(detail.Row.Title is { Length: > 0 } title ? $" ({title})" : "")}: a prototype of DIR.Lib screens that the owner briefs and then iterates on with you in a chat.");
        text.AppendLine();
        text.AppendLine($"Work in this folder, the design's own ({detail.Folder}). You write only inside its `draft/` folder ({detail.DraftFolder}): the node refuses an Edit, Write, MultiEdit or NotebookEdit anywhere else.");
        text.AppendLine("The shell is held too: the node refuses a Bash or PowerShell call unless it is `design draft|check|render|publish|show|chat` or one read-only command (ls, dir, cat, type, head, tail, grep, Get-Content, Get-ChildItem), alone, with no pipe, redirect, chain or substitution. A version is made only by `design publish`, never by hand.");
        text.AppendLine("Do it yourself, in this session: no subagents.");
        text.AppendLine();
        text.AppendLine("## The loop");
        text.AppendLine();
        text.AppendLine($"1. `{run} design draft {id}` makes `draft/` from the current version (or the starter prototype); add `--replace` to start it afresh.");
        text.AppendLine("2. Edit `draft/prototype.json` and `draft/NOTES.md` (what the screens are for and what you chose, in plain words).");
        text.AppendLine($"3. `{run} design check {id}`: it prints `clean` or one finding per line. Fix every finding and check again.");
        text.AppendLine($"4. `{run} design render {id}`: it draws each screen into `draft/renders/`; look at the pictures.");
        text.AppendLine($"5. `{run} design publish {id} --why <brief|message|fix>`: it makes the next version from the draft. The same verbs are MCP tools: design_draft, design_check, design_render, design_publish, design_show.");
        text.AppendLine();
        text.AppendLine("**Publish only when `design check` says clean**, and only after a render of the draft as it stands. If you cannot get the check clean, publish nothing: say in your reply what is in the way (the findings you could not fix, and why).");
        text.AppendLine();
        text.AppendLine("## Your replies");
        text.AppendLine();
        text.AppendLine("The owner reads your last message in the design's chat. Start each reply with what you changed, or with what you ask the owner. If you published, say `published v<n>: <why>`. Keep it short; no preamble.");
        text.AppendLine("When your turn is done, end it: the node starts you again, with this context, on the owner's next words.");
        text.AppendLine();
        text.AppendLine("## The prototype format");
        text.AppendLine();
        text.AppendLine("`prototype.json` is DIR.Lib's prototype: a `width` and `height`, the `entry` screen's id, and `screens`, each with an `id`, a `title` and a `root` node tree (`kind` and its members, such as `text`). The check lays every screen out at the sizes of the GUI's own sweep");
        text.AppendLine($"{(detail.Row.Terminals.Length > 0 ? $"and in cells at {string.Join(", ", detail.Row.Terminals)}" : "")}, so a screen that overflows, cuts text or overlaps is a finding. The current prototype is the best example of the format:");
        text.AppendLine();
        text.AppendLine("```json");
        text.AppendLine(prototype.Trim());
        text.AppendLine("```");
        if (notes is { Length: > 0 })
        {
            text.AppendLine();
            text.AppendLine("The current version's NOTES.md:");
            text.AppendLine();
            text.AppendLine(notes.Trim());
        }
        text.AppendLine();
        text.AppendLine($"## The brief so far (BRIEF.md, the owner's words, oldest first)");
        text.AppendLine();
        text.AppendLine(detail.Brief?.Trim() ?? "(empty)");
        text.AppendLine();
        text.AppendLine($"Version {(detail.Row.Current is { } current ? $"v{current}" : "none yet")} is current. Start now: read the brief, run the loop, reply.");
        return text.ToString();
    }

    /// <summary>What the node says to a resumed designer when the owner adds to the brief: the words as they were written, and where the rest is.</summary>
    public static string DesignerPiece(string id, string words) =>
        $"The owner added to the brief of {id}:\n\n{words.Trim()}\n\nBRIEF.md holds the whole brief. Run the loop (draft, edit, check, render) and publish only on a clean check; start your reply with what you changed or ask.";
}
