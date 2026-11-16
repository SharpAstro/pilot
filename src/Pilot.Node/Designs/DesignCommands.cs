using Pilot.Contracts;

namespace Pilot.Node.Designs;

/// <summary>
/// The design verbs (PIL1096): <c>design-new</c>, <c>design-brief</c>, <c>design-draft</c>, <c>design-show</c> and <c>designs</c>. They go through
/// <see cref="DesignStore"/>, the node its one writer; none writes under a design's <c>versions/</c>.
/// </summary>
public sealed class DesignCommands(NodeState state, DesignStore store)
{
    /// <summary>Whether <paramref name="verb"/> is one of these.</summary>
    public static bool Handles(string verb) => verb is Verbs.DesignNew or Verbs.DesignBrief or Verbs.DesignDraft or Verbs.DesignShow or Verbs.Designs;

    public CommandResult Run(CommandRequest command)
    {
        try
        {
            var args = command.Args ?? [];
            switch (command.Verb)
            {
                case Verbs.DesignNew:
                    // args: repo, title (either may be empty), brief.
                    var row = store.New(args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : "");
                    return state.Complete(command, null, made: row.Id);
                case Verbs.DesignBrief:
                    var briefed = store.Brief(IdOf(command), string.Join(' ', args));
                    return state.Complete(command, null, made: briefed.Id);
                case Verbs.DesignDraft:
                    var id = DesignStore.Normalise(IdOf(command));
                    var folder = store.Draft(id, args.Contains("replace", StringComparer.Ordinal));
                    return state.Complete(command, null, made: $"{id} draft: {folder}");
                case Verbs.DesignShow:
                    return state.Complete(command, null) with { Design = store.Detail(IdOf(command)) };
                default:
                    return state.Complete(command, null) with { Designs = new DesignList { Rows = store.List() } };
            }
        }
        catch (DesignException ex)
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
