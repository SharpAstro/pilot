using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// A thing another page owns that this one is linked to (#1185), declared once: an idea's designs on the Ideas page and a design's ideas on the Designs
/// page are the same component, in the pixel host and the terminal's. It reads its id, its title and its state (a design's version, an idea's state) as one link
/// that opens the other page, with a remove beside it where the link can be taken away. A line too long for its room ends in an ellipsis and carries the whole text
/// on hover; none is cut silently. No view draws this itself, and none knows which host it is in.
/// </summary>
internal static class LinkedThing
{
    /// <summary>The label of the control that opens the list of designs an idea can be linked to.</summary>
    internal const string LinkDesignLabel = "Link a design";

    /// <summary>The label of the control that opens the list of ideas a design can be linked to.</summary>
    internal const string LinkIdeaLabel = "Link an idea";

    /// <summary>The Ideas filter's label: only the ideas that have a design.</summary>
    internal const string HasDesignFilterLabel = "Has a design";

    /// <summary>The glyph of a link's remove button.</summary>
    internal const string RemoveGlyph = "✕";

    /// <summary>What a design reads in a link: its current version, or that it has none yet.</summary>
    internal static string VersionText(int? version) => version is { } v ? $"v{v}" : "no version yet";

    /// <summary>What an idea reads in a link: where it is.</summary>
    internal static string StateText(IdeaState state) => state switch
    {
        IdeaState.Planned => "planned",
        IdeaState.Dropped => "dropped",
        _ => "noted",
    };

    /// <summary>The link's words: <paramref name="id"/>, the title when there is one, and the state.</summary>
    internal static string Text(string id, string? title, string? state) =>
        string.Join(" · ", new[] { title is { Length: > 0 } ? $"{id} {title}" : id, state ?? "" }.Where(t => t.Length > 0));

    /// <summary>
    /// The link as one row item. <paramref name="hit"/> is unique on its page and the thing's id follows it; a null <paramref name="open"/> leaves the words plain,
    /// and a null <paramref name="remove"/> leaves no remove button. <paramref name="wide"/> gives it the room that is left (its words end in an ellipsis where it is short).
    /// </summary>
    internal static Layout.Node Chip(string hit, string id, string? title, string? state, RGBAColor32 link, RGBAColor32 hover, RGBAColor32 removeColor,
        string openTip, Action<string>? open, Action? remove = null, string removeTip = "", float size = 12f, bool wide = true)
    {
        var text = Text(id, title, state);
        var words = WrappedProse.OneLine(text, size, link, selectable: open is null, vAlign: TextAlign.Center, cut: true);
        var shown = open is null
            ? words.Whole(text)
            : words.BgHover(hover).Follows(new HitResult.ButtonHit($"{hit}:{id}"), _ => open(id)).WithTooltip($"{text}\n{openTip}");
        shown = wide ? shown.WStar() : shown.WAuto();
        if (remove is null)
        {
            return shown;
        }
        var x = Layout.Builder.Text(RemoveGlyph, size, removeColor, TextAlign.Center, TextAlign.Center).Pad(6f, 0f).WAuto().BgHover(hover)
            .WithTooltip(removeTip).Follows(new HitResult.ButtonHit($"{hit}-remove:{id}"), _ => remove());
        // The remove stands before its words, so it is beside the link it takes away and not at the far end of a long row.
        return Layout.Builder.HStack(x, shown).WithGap(2f).WStar();
    }
}
