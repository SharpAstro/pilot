using System.CommandLine;
using System.Globalization;
using System.Text;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>A design as <c>pilot designs</c> lists it: id, version, repo, when, title.</summary>
    internal static string DesignLine(DesignRow row) =>
        $"{row.Id}  {(row.Current is { } v ? "v" + v.ToString(CultureInfo.InvariantCulture) : "no version")}  {row.Repo ?? "-"}  {(row.At is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-")}  {row.Title}";

    /// <summary>Every design, newest first, a line each.</summary>
    internal static string Designs(DesignList list)
    {
        if (list.Rows.Length == 0)
        {
            return "No designs. pilot design new -- <brief> makes one.\n";
        }
        var text = new StringBuilder();
        foreach (var row in list.Rows)
        {
            text.AppendLine(DesignLine(row));
        }
        return text.ToString();
    }

    /// <summary>A design's chat: each entry as <c>[index] time who: text</c>, then the designer's status.</summary>
    internal static string DesignChatText(ChatPage page)
    {
        var text = new StringBuilder();
        foreach (var entry in page.Entries)
        {
            var who = entry.Kind switch { TranscriptKind.User => "you", TranscriptKind.Divider => "version", _ => "designer" };
            var at = entry.At is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "-";
            text.AppendLine($"[{entry.Index}] {at} {who}: {entry.Text}");
        }
        text.AppendLine($"{page.Total} entries; designer: {page.Status ?? "idle"}");
        return text.ToString();
    }

    /// <summary>One design whole: its record, its brief as written and where its folders are.</summary>
    internal static string DesignShown(DesignDetail design)
    {
        var row = design.Row;
        var text = new StringBuilder();
        text.AppendLine($"{row.Id}  {row.Title}");
        text.AppendLine($"repo: {row.Repo ?? "-"}");
        text.AppendLine($"made: {(row.At is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "-")}");
        text.AppendLine($"current version: {(row.Current is { } v ? "v" + v.ToString(CultureInfo.InvariantCulture) : "none")} of {row.Versions ?? 0}");
        if (row.Why is { Length: > 0 } why)
        {
            text.AppendLine($"why: {why}");
        }
        if (row.PrototypeSha256 is { Length: > 0 } sha)
        {
            text.AppendLine($"prototype sha256: {sha}");
        }
        if (row.Terminals.Length > 0)
        {
            text.AppendLine($"terminals: {string.Join(' ', row.Terminals)}");
        }
        text.AppendLine($"folder: {design.Folder}");
        text.AppendLine($"draft: {design.DraftFolder}");
        if (design.CurrentFolder is { Length: > 0 } current)
        {
            text.AppendLine($"current: {current}");
        }
        text.AppendLine();
        text.AppendLine("brief:");
        text.AppendLine(design.Brief?.TrimEnd() ?? "(none)");
        return text.ToString();
    }

    private sealed partial class BuildContext
    {
        /// <summary>Makes design and designs (PIL1096): a prototype of screens, briefed by the owner and kept in versions on the node.</summary>
        internal (Command, Command) DesignCommands()
        {
            var newBrief = Words("brief", "The owner's words, kept verbatim as the first piece of the design's brief (after --)");
            var newRepo = new Option<string?>("--repo") { Description = "The repo the design is for, as its name" };
            var newTitle = new Option<string?>("--title") { Description = $"The design's title; when left out, the brief's first line, whole up to {DesignTitle.MaxLength} characters, else cut at a word (at the limit when it has no space) with an ellipsis" };
            var newTerminal = new Option<string[]>("--terminal") { Description = "A terminal size the design is also checked and rendered at in cells, <cols>x<rows> (80x24, 100x30, 160x44, 250x60 are the standard ones); give it once per size", AllowMultipleArgumentsPerToken = false };
            var designNew = Action(new Command("new", "Make a design from a brief and print its id, DSN-<n>") { Arguments = { newBrief }, Options = { newRepo, newTitle, newTerminal } },
                (p, client, ct) => Send(client, Verbs.DesignNew, Verbs.NodeTarget, [p.GetValue(newRepo) ?? "", p.GetValue(newTitle) ?? "", Join(p, newBrief), .. p.GetValue(newTerminal) ?? []], who, ct));

            var terminalsId = Arg("design", "The design's id, DSN-<n>");
            var terminalsSizes = new Argument<string[]>("sizes") { Description = "The terminal sizes, <cols>x<rows> each; none clears the design's terminal targets", Arity = ArgumentArity.ZeroOrMore };
            var designTerminals = Action(new Command("terminals", "Set the terminal sizes a design is checked and rendered at in cells (the cell rules of Console.Lib; <screen>.<cols>x<rows>.txt and .png renders); none clears them") { Arguments = { terminalsId, terminalsSizes } },
                (p, client, ct) => Send(client, Verbs.DesignTerminals, Verbs.DesignTarget(p.GetValue(terminalsId)!), p.GetValue(terminalsSizes) ?? [], who, ct));

            var briefId = Arg("design", "The design's id, DSN-<n>");
            var briefText = Words("text", "The owner's words to add, verbatim, under their time (after --)");
            var designBrief = Action(new Command("brief", "Append the owner's words to a design's brief, verbatim, under their time") { Arguments = { briefId, briefText } },
                (p, client, ct) => Send(client, Verbs.DesignBrief, Verbs.DesignTarget(p.GetValue(briefId)!), [Join(p, briefText)], who, ct));

            var sayId = Arg("design", "The design's id, DSN-<n>");
            var sayText = Words("text", "The owner's words, verbatim (after --)");
            var sayPicture = new Option<string?>("--picture") { Description = "A picture to send with the words: a file, kept in the design's folder and named to the designer" };
            var designSay = Action(new Command("say", "Say something in a design's chat: the words are appended to its brief under their time and sent to the designer") { Arguments = { sayId, sayText }, Options = { sayPicture } },
                (p, client, ct) =>
                {
                    var file = p.GetValue(sayPicture);
                    string[] args = file is null ? [Join(p, sayText)] : [Join(p, sayText), Path.GetFileName(file), Convert.ToBase64String(File.ReadAllBytes(file))];
                    return Send(client, Verbs.DesignSay, Verbs.DesignTarget(p.GetValue(sayId)!), args, who, ct);
                });

            var chatId = Arg("design", "The design's id, DSN-<n>");
            var chatAfter = new Option<int?>("--after") { Description = "Only the entries after this index" };
            var designChat = Action(new Command("chat", "Read a design's chat: the owner's messages, the designer's replies, a line per published version, and what the designer is doing") { Arguments = { chatId }, Options = { chatAfter } },
                async (p, client, ct) =>
                {
                    var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.DesignChat, Target = Verbs.DesignTarget(p.GetValue(chatId)!), Args = p.GetValue(chatAfter) is { } a ? [a.ToString(CultureInfo.InvariantCulture)] : [], Who = who, Agent = byAgent }, ct);
                    if (answer is { Success: true, Response: { Ok: true, DesignChat: { } page } })
                    {
                        await output.WriteAsync(DesignChatText(page));
                        return 0;
                    }
                    await output.WriteLineAsync($"refused: design chat {p.GetValue(chatId)}: {answer.Response?.Error ?? answer.Error}");
                    return 1;
                });

            var draftId = Arg("design", "The design's id, DSN-<n>");
            var draftReplace = new Option<bool>("--replace") { Description = "Make the draft again over an existing one" };
            var designDraft = Action(new Command("draft", "Make the design's draft/ folder: the current version's DESIGN.md, prototype.json and screens/, or a starter when there is no version; refused over an existing draft unless --replace") { Arguments = { draftId }, Options = { draftReplace } },
                (p, client, ct) => Send(client, Verbs.DesignDraft, Verbs.DesignTarget(p.GetValue(draftId)!), p.GetValue(draftReplace) ? ["replace"] : [], who, ct));

            var checkId = Arg("design", "The design's id, DSN-<n>");
            var designCheck = Action(new Command("check", "Check the draft's prototype.json with DIR.Lib: every loader problem with its JSON path and every finding at the sweep's sizes, one a line; 'clean' when there are none, and a non-zero exit when there are") { Arguments = { checkId } },
                (p, client, ct) => Send(client, Verbs.DesignCheck, Verbs.DesignTarget(p.GetValue(checkId)!), [], who, ct));

            var renderId = Arg("design", "The design's id, DSN-<n>");
            var designRender = Action(new Command("render", "Write draft/renders/<screen id>.png for every screen at the design size, stamped with the hash of the prototype drawn") { Arguments = { renderId } },
                (p, client, ct) => Send(client, Verbs.DesignRender, Verbs.DesignTarget(p.GetValue(renderId)!), [], who, ct));

            var publishId = Arg("design", "The design's id, DSN-<n>");
            var publishWhy = new Option<string>("--why") { Description = "Why this version was made: a short reason, one line, at most 120 characters, saying what changed (the chat shows it as \"published v<n>: <why>\")", Required = true };
            var publishOwner = new Option<bool>("--owner") { Description = "The owner's own typed version: the why is stored as 'owner: <why>'" };
            var designPublish = Action(new Command("publish", "Make the next read-only version from the draft, when the check is clean and the renders are of the draft as it stands (a draft whose prototype and screens are the current version's takes its renders); prints the version. Only DESIGN.md, prototype.json, screens/, NOTES.md (optional) and renders/ are copied; other files in draft/ are ignored") { Arguments = { publishId }, Options = { publishWhy, publishOwner } },
                (p, client, ct) => Send(client, Verbs.DesignPublish, Verbs.DesignTarget(p.GetValue(publishId)!), p.GetValue(publishOwner) ? [p.GetValue(publishWhy)!, "owner"] : [p.GetValue(publishWhy)!], who, ct));

            var editId = Arg("design", "The design's id, DSN-<n>");
            var editPart = Arg("part", "objective, heading or notes");
            var editCell = new Option<string>("--cell") { Description = "The cell: its heading, the id of a screen it shows, or its number; left out for the design itself (objective and notes)", DefaultValueFactory = _ => "" };
            var editText = Words("text", "The new text of the part (after --)");
            var designEdit = Action(new Command("edit", "Type into the draft's DESIGN.md as the owner does: the design's objective or notes, or a cell's heading, objective or notes. Makes the draft from the current version when there is none; the designer is told at its next turn, not woken") { Arguments = { editId, editPart, editText }, Options = { editCell } },
                (p, client, ct) => Send(client, Verbs.DesignEdit, Verbs.DesignTarget(p.GetValue(editId)!), [p.GetValue(editPart)!, p.GetValue(editCell) ?? "", Join(p, editText)], who, ct));

            var discardId = Arg("design", "The design's id, DSN-<n>");
            var designDiscard = Action(new Command("discard", "Remove the design's draft: what was typed or designed there and not published is gone") { Arguments = { discardId } },
                (p, client, ct) => Send(client, Verbs.DesignDiscard, Verbs.DesignTarget(p.GetValue(discardId)!), [], who, ct));

            var buildId = Arg("design", "The design's id, DSN-<n>");
            var buildVersion = new Option<int?>("--version") { Description = "The version to build; the current one when left out" };
            var buildRepo = new Option<string?>("--repo") { Description = "The repo to file the issue and fly the flight in; the design's own when left out" };
            var buildMilestone = new Option<string?>("--milestone") { Description = "For a design that came from an idea: the open milestone to plan the idea into" };
            var designBuild = Action(new Command("build", "Build a version: file one issue holding its brief, notes, renders and prototype hash, and make one flight on it that remembers the design and version (a design from an idea plans that idea instead); refused while a flight of that version is in the air") { Arguments = { buildId }, Options = { buildVersion, buildRepo, buildMilestone } },
                (p, client, ct) => Send(client, Verbs.DesignBuild, Verbs.DesignTarget(p.GetValue(buildId)!),
                    [p.GetValue(buildVersion)?.ToString(CultureInfo.InvariantCulture) ?? "", p.GetValue(buildRepo) ?? "", p.GetValue(buildMilestone) ?? ""], who, ct));

            var showId = Arg("design", "The design's id, DSN-<n>");
            var designShow = Action(new Command("show", "Show a design: its record, its brief and the paths of its folders") { Arguments = { showId } },
                async (p, client, ct) =>
                {
                    var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.DesignShow, Target = Verbs.DesignTarget(p.GetValue(showId)!), Who = who, Agent = byAgent }, ct);
                    if (answer is { Success: true, Response: { Ok: true, Design: { } design } })
                    {
                        await output.WriteAsync(DesignShown(design));
                        return 0;
                    }
                    await output.WriteLineAsync($"refused: design show {p.GetValue(showId)}: {answer.Response?.Error ?? answer.Error}");
                    return 1;
                });

            var design = new Command("design", "Designs: a prototype of DIR.Lib screens, briefed by the owner and kept in versions on the node. new, brief, say, chat, draft, check, render, edit, discard, publish, build, show, terminals") { Subcommands = { designNew, designTerminals, designBrief, designSay, designChat, designDraft, designEdit, designDiscard, designCheck, designRender, designPublish, designBuild, designShow } };

            var designs = Action(new Command("designs", "Every design, newest first"),
                async (p, client, ct) =>
                {
                    var answer = await client.SendAsync(new CommandRequest { Verb = Verbs.Designs, Target = Verbs.NodeTarget, Who = who, Agent = byAgent }, ct);
                    if (answer is { Success: true, Response: { Ok: true, Designs: { } list } })
                    {
                        await output.WriteAsync(Designs(list));
                        return 0;
                    }
                    await output.WriteLineAsync($"refused: designs: {answer.Response?.Error ?? answer.Error}");
                    return 1;
                });
            return (design, designs);
        }
    }
}
