#nullable enable
using System.Text;
using DIR.Lib.Prototyping;
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
        text.AppendLine("The shell is held too: the node refuses a Bash or PowerShell call unless it is `design draft|check|render|publish|show|chat` or one read-only command (ls, dir, cat, type, head, tail, grep, Get-Content, Get-ChildItem), alone, with no pipe, redirect, chain or substitution, and the program named bare (`cat`, never `draft/cat`) or, for pilot, by the path this brief writes. A version is made only by `design publish`, never by hand.");
        text.AppendLine("Do it yourself, in this session: no subagents.");
        text.AppendLine();
        text.AppendLine("## The loop");
        text.AppendLine();
        text.AppendLine($"1. `{run} design draft {id}` makes `draft/` from the current version (or the starter prototype); add `--replace` to start it afresh.");
        text.AppendLine("2. Edit `draft/prototype.json` and `draft/NOTES.md` (what the screens are for and what you chose, in plain words).");
        text.AppendLine($"3. `{run} design check {id}`: it prints `clean` or one finding per line. Fix every finding and check again.");
        text.AppendLine($"4. `{run} design render {id}`: it draws each screen into `draft/renders/`; look at the pictures.");
        text.AppendLine($"5. `{run} design publish {id} --why \"<what changed>\"`: it makes the next version from the draft. The why is a short free-text reason, one line of at most {DesignWhys.MaxLength} characters, saying what changed (the chat shows it as `published v<n>: <why>`). Publish copies only `prototype.json`, `NOTES.md` and `renders/` from `draft/`; any other file there is ignored, so nothing needs deleting. The same verbs are MCP tools: design_draft, design_check, design_render, design_publish, design_show.");
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
        text.AppendLine("`prototype.json` is DIR.Lib's prototype: a `width` and `height`, the `entry` screen's id, and `screens`, each with an `id`, a `title` and a `root` node tree (`kind`, the kind's properties, and `children`). The kinds and properties listed below are everything the loader accepts; any other is a finding. The check lays every screen out at the sizes of the GUI's own sweep");
        text.AppendLine($"{(detail.Row.Terminals.Length > 0 ? $"and in cells at {string.Join(", ", detail.Row.Terminals)}" : "")}, so a screen that overflows, cuts text or overlaps is a finding. The current prototype is the best example of the format:");
        text.AppendLine();
        text.AppendLine("```json");
        text.AppendLine(prototype.Trim());
        text.AppendLine("```");
        text.AppendLine();
        text.Append(NodeKinds());
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

    /// <summary>
    /// The node kinds section of the designer's brief (PIL1159), written from DIR.Lib's <see cref="PrototypeVocabulary"/> so a kind the loader gains
    /// reaches the designer with no edit here: every kind with its properties (type, required, enum words), child counts and bind, then the
    /// properties every kind shares and the colour roles.
    /// </summary>
    public static string NodeKinds()
    {
        static string TypeName(PrototypeProperty p) => p.Type switch
        {
            PrototypePropertyType.Enum => "one of " + string.Join(" | ", p.Values.Select(v => $"\"{v}\"")),
            PrototypePropertyType.StringList => "array of strings",
            PrototypePropertyType.Color => "colour",
            PrototypePropertyType.Size => "size: number, \"auto\", \"star\" or \"star:weight\"",
            PrototypePropertyType.Integer => "whole number",
            _ => p.Type.ToString().ToLowerInvariant(),
        };
        static string Describe(PrototypeProperty p) => $"`{p.Name}` ({TypeName(p)}{(p.Required ? ", required" : "")})";

        var text = new StringBuilder();
        text.AppendLine("### Node kinds");
        text.AppendLine();
        text.AppendLine("A node is an object with a `kind` from this list, the kind's own properties, the common properties below, and `children` (an array of nodes) where the kind takes any. A kind that takes `bind` reads and writes a named state through `\"bind\": \"<name>\"`. A node may also carry `action` and `when`.");
        text.AppendLine();
        foreach (var kind in PrototypeVocabulary.Kinds.Values.OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            var children = kind.MaxChildren switch
            {
                0 => "no children",
                < 0 => kind.MinChildren == 0 ? "any number of children" : $"at least {kind.MinChildren} children",
                var max when max == kind.MinChildren => $"exactly {max} children",
                var max => $"{kind.MinChildren} to {max} children",
            };
            var props = kind.Properties.Length == 0 ? "no properties of its own" : string.Join(", ", kind.Properties.Select(Describe));
            text.AppendLine($"- `{kind.Name}`: {children}{(kind.SupportsBind ? ", takes `bind`" : "")}; {props}");
        }
        text.AppendLine();
        text.AppendLine("Common properties, on every kind: " + string.Join(", ", PrototypeVocabulary.Common.Select(Describe)) + ".");
        text.AppendLine();
        text.AppendLine("Colour roles (a colour is one of these names or `#rgb`, `#rrggbb`, `#rrggbbaa`): " + string.Join(", ", PrototypeVocabulary.ColorRoles.Select(r => $"`{r}`")) + ".");
        text.AppendLine();
        return text.ToString();
    }

    /// <summary>What the node says to a resumed designer when the owner adds to the brief: the words as they were written, and where the rest is.</summary>
    public static string DesignerPiece(string id, string words) =>
        $"The owner added to the brief of {id}:\n\n{words.Trim()}\n\nBRIEF.md holds the whole brief. Run the loop (draft, edit, check, render) and publish only on a clean check, with `--why` a short one-line reason saying what changed; start your reply with what you changed or ask.";
}
