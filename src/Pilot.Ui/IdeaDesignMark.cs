using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The 'needs design' mark of an idea (PIL1152), declared once for the Ideas page's list, board and tree views, in both hosts: the words that say
/// the idea wants a design, the design's id and version as a link to its page, and the Mark for design / Unmark design button. A view calls
/// <see cref="Chip"/> and <see cref="Button"/>; none draws these itself, and none knows which host it is in.
/// </summary>
internal static class IdeaDesignMark<TSurface>
{
    /// <summary>The mark: a word beside a glyph, so colour never says it alone.</summary>
    internal const string MarkText = "◆ needs design";

    internal const string MarkLabel = "Mark for design";

    internal const string UnmarkLabel = "Unmark design";

    /// <summary>The label of the question's button that makes the design from the idea.</summary>
    internal const string StartLabel = "Start its design";

    /// <summary>The label of the question's button that plans the idea although its design has no version.</summary>
    internal const string PlanAnywayLabel = "Plan anyway";

    /// <summary>The filter's label, on the page's header.</summary>
    internal const string FilterLabel = "Needs a design";

    /// <summary>The design as a link reads: its id and its current version, or that it has none yet.</summary>
    internal static string DesignText(IdeaRow idea) =>
        idea.Design is not { } id ? "" : idea.DesignVersion is { } version ? $"{id} v{version}" : $"{id}, no version yet";

    /// <summary>Every design the idea is linked to (#1185): its list, else the one design an older node sent in <see cref="IdeaRow.Design"/>.</summary>
    internal static IdeaDesignLink[] DesignsOf(IdeaRow idea) =>
        idea.Designs.Length > 0 ? idea.Designs : idea.Design is { } id ? [new IdeaDesignLink(id, null, idea.DesignVersion)] : [];

    /// <summary>
    /// Whether Plan must ask first, as the node refuses otherwise: any linked design has no published version, or the idea is marked and none of its
    /// designs has one (or it has no design at all).
    /// </summary>
    internal static bool PlanAsksFirst(IdeaRow idea) =>
        DesignsOf(idea) is var designs && (designs.Any(d => d.Version is null) || idea.NeedsDesign && designs.Length == 0);

    /// <summary>The question Plan asks first, in words; it names each linked design that has no published version.</summary>
    internal static string Question(IdeaRow idea, string name) =>
        DesignsOf(idea).Where(d => d.Version is null).Select(d => d.Id).ToArray() is { Length: > 0 } unpublished
            ? $"{string.Join(", ", unpublished)} {(unpublished.Length == 1 ? "has" : "have")} no published version yet. Start its design, or plan {name} anyway."
            : $"{name} needs a design and has none yet. Start its design, or plan it anyway.";

    /// <summary>
    /// The mark and the design link on one line, or null when the idea has neither. <paramref name="hit"/> names the link in the view (a hit id is
    /// unique on a page, and the tree's row and its detail may both show one idea); a null <paramref name="open"/> leaves the design's id as plain text.
    /// </summary>
    internal static Layout.Node? Chip(IdeaRow idea, string hit, Palette p, RGBAColor32 link, RGBAColor32 hover, Action<string>? open, float size = 12f, bool designs = true)
    {
        var parts = new List<Layout.Node>();
        if (idea.NeedsDesign)
        {
            parts.Add(Layout.Builder.Text(MarkText, size, p.Body).Selectable().WAuto().WithTooltip("The idea is marked as needing a design before it flies"));
        }
        if (designs)
        {
            foreach (var design in DesignsOf(idea))
            {
                parts.Add(LinkedThing.Chip($"{hit}:{idea.Qualified}", design.Id, null, LinkedThing.VersionText(design.Version), link, hover, p.Dim, $"Open the design {design.Id}", open, wide: false, size: size));
            }
        }
        return parts.Count == 0 ? null : Layout.Builder.HStack([.. parts]).WithGap(8f).WAuto();
    }

    /// <summary>
    /// The idea's designs as rows (#1185): each one's id, title and version as the one linked-thing component, a link to its page, with a remove when
    /// <paramref name="remove"/> is given. Empty for an idea with none.
    /// </summary>
    internal static IEnumerable<Layout.Node> Links(IdeaRow idea, string hit, Palette p, RGBAColor32 link, RGBAColor32 hover, Action<string>? open, Action<IdeaDesignLink>? remove)
    {
        foreach (var design in DesignsOf(idea))
        {
            var d = design;
            yield return LinkedThing.Chip($"{hit}:{idea.Qualified}", d.Id, d.Title, LinkedThing.VersionText(d.Version), link, hover, p.Dim,
                $"Open the design {d.Id}", open, remove is null ? null : () => remove(d), $"Take the link of {d.Id} to this idea away", 12f).RowH(22f);
        }
    }

    /// <summary>The button that marks the idea, or unmarks it once it is marked; while the node has the press it says so and takes no second one.</summary>
    internal static Layout.Node Button(IdeaRow idea, string hit, bool busy, Action onPress, (RGBAColor32 Bg, RGBAColor32 Hover) tint, RGBAColor32 text) =>
        busy
            ? CardsView<TSurface>.Dead(idea.NeedsDesign ? "Unmarking…" : "Marking…")
            : CardsView<TSurface>.Pill(idea.NeedsDesign ? UnmarkLabel : MarkLabel, hit, onPress, tint, text);
}
