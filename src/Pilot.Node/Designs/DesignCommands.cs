using Pilot.Contracts;

namespace Pilot.Node.Designs;

/// <summary>
/// The design verbs (PIL1096): <c>design-new</c>, <c>design-brief</c>, <c>design-draft</c>, <c>design-check</c>, <c>design-render</c>, <c>design-publish</c>,
/// <c>design-show</c> and <c>designs</c>. They go through <see cref="DesignStore"/>, the node its one writer; only <c>design-publish</c> writes under a
/// design's <c>versions/</c>, and only to a version folder that is not there yet.
/// </summary>
public sealed class DesignCommands(NodeState state, DesignStore store, Designers? designers = null, DesignChat? chat = null)
{
    /// <summary>Whether <paramref name="verb"/> is one of these.</summary>
    public static bool Handles(string verb) => verb is Verbs.DesignNew or Verbs.DesignBrief or Verbs.DesignSay or Verbs.DesignChat or Verbs.DesignDraft or Verbs.DesignShow or Verbs.Designs
        or Verbs.DesignTerminals or Verbs.DesignCheck or Verbs.DesignRender or Verbs.DesignPublish or Verbs.DesignEdit or Verbs.DesignDiscard;

    public CommandResult Run(CommandRequest command)
    {
        try
        {
            var args = command.Args ?? [];
            switch (command.Verb)
            {
                case Verbs.DesignNew:
                    // args: repo, title (either may be empty), brief.
                    // args: repo, title (either may be empty), brief, then any terminal sizes.
                    var row = store.New(args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : "", args.Length > 3 ? args[3..] : []);
                    // The design's first brief wakes its designer (PIL1151); the asks run in the background, after each other.
                    if (args.Length > 2 && args[2].Trim().Length > 0)
                    {
                        designers?.Ask(row.Id, null);
                    }
                    state.RefreshIdeaDesigns();
                    return state.Complete(command, null, made: row.Id);
                case Verbs.DesignTerminals:
                    var sized = store.SetTerminals(IdOf(command), args);
                    return state.Complete(command, null, made: sized.Terminals.Length == 0 ? $"{sized.Id} terminals: none" : $"{sized.Id} terminals: {string.Join(' ', sized.Terminals)}");
                case Verbs.DesignBrief:
                    var words = string.Join(' ', args);
                    var briefed = store.Brief(IdOf(command), words);
                    // A new piece of the brief resumes the designer with it (or starts it, when there is none).
                    designers?.Ask(briefed.Id, words);
                    return state.Complete(command, null, made: briefed.Id);
                case Verbs.DesignSay:
                    // args: the words, then (optionally) a picture's name and its bytes in base64.
                    var said = args.Length > 0 ? args[0] : "";
                    var pictureBytes = args.Length > 2 && args[2].Length > 0 ? Convert.FromBase64String(args[2]) : null;
                    var sayId = DesignStore.Normalise(IdOf(command));
                    var picturePath = store.Say(sayId, said, args.Length > 1 ? args[1] : null, pictureBytes);
                    // The designer is sent the words, with the picture's path when there is one, and resumes with them (or holds them while the limit lasts).
                    designers?.Ask(sayId, picturePath is null ? said : $"{said}\n\nA picture came with this message: {picturePath}");
                    return state.Complete(command, null, made: sayId);
                case Verbs.DesignChat:
                    var feed = chat ?? throw new DesignException("this node keeps no design chat");
                    return state.Complete(command, null) with { DesignChat = feed.Page(IdOf(command), args.Length > 0 && int.TryParse(args[0], out var after) ? after : -1) };
                case Verbs.DesignDraft:
                    var id = DesignStore.Normalise(IdOf(command));
                    var folder = store.Draft(id, args.Contains("replace", StringComparer.Ordinal));
                    return state.Complete(command, null, made: $"{id} draft: {folder}");
                case Verbs.DesignCheck:
                    var problems = store.Check(IdOf(command));
                    return problems.Length == 0
                        ? state.Complete(command, null, made: "clean")
                        : state.Complete(command, $"{problems.Length} problem{(problems.Length == 1 ? "" : "s")}:\n{string.Join('\n', problems)}");
                case Verbs.DesignRender:
                    var rendered = store.Render(IdOf(command));
                    return state.Complete(command, null, made: $"{rendered.Length} render{(rendered.Length == 1 ? "" : "s")}: {string.Join(", ", rendered)}");
                case Verbs.DesignPublish:
                    // args: the why, then optionally "owner" for a version the owner typed.
                    var published = store.Publish(IdOf(command), args.Length > 0 ? args[0] : "", owner: args.Length > 1 && string.Equals(args[1], "owner", StringComparison.Ordinal));
                    state.RefreshIdeaDesigns();
                    return state.Complete(command, null, made: $"{published.Id} v{published.Current}");
                case Verbs.DesignEdit:
                    // args: part, cell (empty for the design), text.
                    var editId = DesignStore.Normalise(IdOf(command));
                    var editPart = args.Length > 0 ? args[0] : "";
                    var editCell = args.Length > 1 ? args[1] : "";
                    var typed = store.Edit(editId, editPart, editCell, args.Length > 2 ? args[2] : "");
                    // The designer is told at its next turn, never woken for it.
                    designers?.Queue(editId, editPart.Trim().ToLowerInvariant(), editCell, typed);
                    return state.Complete(command, null, made: $"{editId} draft: {editPart.Trim().ToLowerInvariant()} typed");
                case Verbs.DesignDiscard:
                    var discarded = DesignStore.Normalise(IdOf(command));
                    store.Discard(discarded);
                    return state.Complete(command, null, made: $"{discarded} draft discarded");
                case Verbs.DesignShow:
                    return state.Complete(command, null) with { Design = store.Detail(IdOf(command)) };
                default:
                    return state.Complete(command, null) with { Designs = new DesignList { Rows = store.List() } };
            }
        }
        catch (Exception ex) when (ex is DesignException or FormatException)
        {
            return state.Complete(command, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return state.Complete(command, $"{command.Verb} failed: {ex.Message}");
        }
    }

    private static string IdOf(CommandRequest command) =>
        Verbs.DesignIdOf(command.Target) is { Length: > 0 } id ? id : throw new DesignException($"{command.Verb} takes a design target, {Verbs.DesignTargetPrefix}DSN-<n>, not {command.Target}");
}
