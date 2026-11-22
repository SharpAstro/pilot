using Pilot.Contracts;

namespace Pilot.Node.Designs;

/// <summary>
/// The design verbs (PIL1096): <c>design-new</c>, <c>design-brief</c>, <c>design-draft</c>, <c>design-check</c>, <c>design-render</c>, <c>design-publish</c>,
/// <c>design-show</c> and <c>designs</c>. They go through <see cref="DesignStore"/>, the node its one writer; only <c>design-publish</c> writes under a
/// design's <c>versions/</c>, and only to a version folder that is not there yet.
/// </summary>
public sealed class DesignCommands(NodeState state, DesignStore store, Designers? designers = null, DesignChat? chat = null, DesignSync? sync = null, DesignLock? locks = null)
{
    /// <summary>Whether <paramref name="verb"/> is one of these.</summary>
    public static bool Handles(string verb) => verb is Verbs.DesignNew or Verbs.DesignBrief or Verbs.DesignSay or Verbs.DesignChat or Verbs.DesignDraft or Verbs.DesignShow or Verbs.DesignRenders or Verbs.Designs
        or Verbs.DesignTerminals or Verbs.DesignCheck or Verbs.DesignRender or Verbs.DesignPublish or Verbs.DesignEdit or Verbs.DesignSetTitle or Verbs.DesignDiscard or Verbs.DesignIssue or Verbs.DesignGitHub or Verbs.DesignLink or Verbs.DesignUnlink;

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
                    // The labels come off once the version is on the issue (the sync settles them at its end); with no sync here they come off now.
                    locks?.Release(published.Id, kick: sync is null);
                    // The version is on the node whatever GitHub does: it is written to the design's issue in the background, and a failure is on the record.
                    sync?.Kick(published.Id);
                    return state.Complete(command, null, made: $"{published.Id} v{published.Current}");
                case Verbs.DesignEdit:
                    // args: part, cell (empty for the design), text, and optionally the heading the cell had when the typing began (#1182 N4.1).
                    var editId = DesignStore.Normalise(IdOf(command));
                    var editPart = args.Length > 0 ? args[0] : "";
                    var editCell = args.Length > 1 ? args[1] : "";
                    var typed = store.Edit(editId, editPart, editCell, args.Length > 2 ? args[2] : "", args.Length > 3 ? args[3] : null);
                    // The designer is told at its next turn, never woken for it.
                    designers?.Queue(editId, editPart.Trim().ToLowerInvariant(), editCell, typed);
                    // The owner typing has the design open for editing: the issue is labelled, in the background, and a failure never blocks the edit.
                    locks?.Want(editId);
                    return state.Complete(command, null, made: $"{editId} draft: {editPart.Trim().ToLowerInvariant()} typed");
                case Verbs.DesignSetTitle:
                    // args: the title (words joined by the CLI).
                    var titled = store.SetTitle(IdOf(command), args.Length > 0 ? args[0] : "");
                    return state.Complete(command, null, made: $"{titled.Id} title: {titled.Title}");
                case Verbs.DesignDiscard:
                    var discarded = DesignStore.Normalise(IdOf(command));
                    store.Discard(discarded);
                    locks?.Release(discarded);
                    return state.Complete(command, null, made: $"{discarded} draft discarded");
                case Verbs.DesignGitHub:
                    var github = locks ?? throw new DesignException("this node does not read designs back from GitHub");
                    var ghId = DesignStore.Normalise(IdOf(command));
                    var ghAction = args.Length > 0 ? args[0] : "";
                    var ghKey = args.Length > 1 ? args[1] : "";
                    var ghWhere = ghAction switch
                    {
                        "apply" => github.Apply(ghId, ghKey),
                        "dismiss" => github.Dismiss(ghId, ghKey),
                        _ => throw new DesignException($"design-github takes apply or dismiss, not '{ghAction}'"),
                    };
                    return state.Complete(command, null, made: $"{ghId} GitHub edit of the {ghWhere}: {ghAction}");
                case Verbs.DesignRenders:
                    // args: version (empty for the current one), "draft" or empty, screen, size.
                    int? asked = null;
                    if (args.Length > 0 && args[0].Length > 0)
                    {
                        // A version that is not a number, or too large for an int, is a refusal naming it, never an unhandled OverflowException.
                        if (!int.TryParse(args[0].TrimStart('v', 'V'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                        {
                            throw new DesignException($"'{args[0]}' is not a version number");
                        }
                        asked = parsed;
                    }
                    var renders = DesignRenderReader.Read(store, IdOf(command), asked, args.Length > 1 && args[1] == "draft", args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null);
                    return state.Complete(command, null) with { DesignRenders = renders };
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

    /// <summary><c>design-issue</c> (PIL1171): links the design to an issue; the one verb here that asks GitHub, so it is async.</summary>
    public async Task<CommandResult> IssueAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        try
        {
            var linker = sync ?? throw new DesignException("this node does not write designs to GitHub");
            var args = command.Args ?? [];
            var reference = args.Length > 0 ? args[0].Trim() : "";
            var hash = reference.LastIndexOf('#');
            if (hash <= 0 || !int.TryParse(reference.AsSpan(hash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1)
            {
                throw new DesignException($"design-issue takes <repo>#<n>, for example SharpAstro/pilot#1171, not '{reference}'");
            }
            var told = await linker.LinkAsync(DesignStore.Normalise(IdOf(command)), reference[..hash], number, cancellationToken);
            return state.Complete(command, null, made: told);
        }
        catch (DesignException ex)
        {
            return state.Complete(command, ex.Message);
        }
    }

    /// <summary>
    /// <c>design-link</c> and <c>design-unlink</c> (#1185): the design is for the idea, or no longer is. The idea is <c>I381</c> (in the design's repo, else the default repo) or <c>repo I381</c>;
    /// it must be an idea the node reads to be linked, and follows a move or fold to where it is now. A change tells GitHub (the design's issue lists its ideas, the idea's issue is told once).
    /// </summary>
    public async Task<CommandResult> LinkAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        try
        {
            var id = DesignStore.Normalise(IdOf(command));
            var link = command.Verb == Verbs.DesignLink;
            var text = (command.Args ?? []).FirstOrDefault() ?? "";
            var parsed = IdeaIds.Parse(text);
            if (parsed.Ref is not { } idea)
            {
                throw new DesignException(parsed.Error!);
            }
            var design = store.Find(id) ?? throw new DesignException($"There is no design {id}");
            var repo = idea.Repo ?? design.Repo ?? IdeaIds.DefaultRepo;
            var (now, number) = IdeaIds.Follow(state.IdeaMoves(), repo, idea.Number);
            if (link && !state.Ideas().Any(i => string.Equals(i.Repo, now, StringComparison.OrdinalIgnoreCase) && i.Number == number))
            {
                throw new DesignException($"{IdeaIds.Id(number)} is no idea of {now} that the node reads: a design is linked to an open idea");
            }
            var key = NodeState.IdeaKey(now, IdeaIds.Id(number));
            // An unlink names the link as it was written: the key it was made under, or where the idea is now.
            if (!link && !design.Ideas().Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                key = NodeState.IdeaKey(repo, IdeaIds.Id(idea.Number));
            }
            var (row, changed) = link ? store.Link(id, key) : store.Unlink(id, key);
            state.RefreshIdeaDesigns();
            if (changed && sync is not null)
            {
                await sync.IdeaLinkChangedAsync(row.Id, key, link, cancellationToken);
            }
            var word = link ? (changed ? "linked to" : "is linked to already:") : (changed ? "unlinked from" : "was not linked to");
            return state.Complete(command, null, made: $"{row.Id} {word} {key}");
        }
        catch (DesignException ex)
        {
            return state.Complete(command, ex.Message);
        }
    }

    private static string IdOf(CommandRequest command) =>
        Verbs.DesignIdOf(command.Target) is { Length: > 0 } id ? id : throw new DesignException($"{command.Verb} takes a design target, {Verbs.DesignTargetPrefix}DSN-<n>, not {command.Target}");
}
