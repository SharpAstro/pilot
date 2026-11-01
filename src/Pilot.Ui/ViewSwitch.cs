using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// One segment of a <see cref="ViewSwitch"/>: the mode it picks, the id its hit region has (<c>flights:view:grid</c>), and either a word or one of DIR.Lib's
/// marks with the word as its tooltip.
/// </summary>
internal readonly record struct ViewChoice<T>(T Mode, string Action, string Name, bool AsWord = false, Layout.IconKind Icon = Layout.IconKind.List);

/// <summary>
/// The switch between a page's views, which the Flights page and the Ideas pane share: a segmented control of the same segment width and the
/// header's button height, each segment keeping the id its pill had and the hand cursor. A page names its choices; nothing here is per page.
/// </summary>
internal static class ViewSwitch
{
    /// <summary>One segment's width, so the switches of two pages are the same width for the same number of views.</summary>
    public const float Segment = 60f;

    private const float Gap = 2f;

    /// <summary>The whole switch's width for <paramref name="count"/> views.</summary>
    public static float WidthOf(int count) => count * Segment + (count - 1) * Gap;

    /// <summary>
    /// The switch for <paramref name="choices"/> with <paramref name="current"/> lit; a press calls <paramref name="select"/>. Its height is given, as the
    /// header's buttons have theirs: the group's segments are height-star (an inset band between star spacers), and under a row sized by its
    /// content they got none, so the switch drew its words at the top and its icons and fills not at all (SE1, 2026-10-25).
    /// </summary>
    public static Layout.Node Build<T>(IReadOnlyList<ViewChoice<T>> choices, T current, Action<T> select, Palette p)
    {
        var options = new Layout.ButtonGroupOption<T>[choices.Count];
        for (var i = 0; i < options.Length; i++)
        {
            var choice = choices[i];
            options[i] = choice.AsWord
                ? new Layout.ButtonGroupOption<T>(choice.Mode, choice.Name) { Hit = new HitResult.ButtonHit(choice.Action) }
                : new Layout.ButtonGroupOption<T>(choice.Mode, Icon: choice.Icon) { Hit = new HitResult.ButtonHit(choice.Action), Tooltip = choice.Name };
        }
        var style = new Layout.ButtonGroupStyle(p.RowFocusBg, p.Button, p.Bright, p.Dim, p.ButtonHover)
        {
            Gap = Gap,
            InsetFraction = 0.75f,
            SegmentWidth = Segment,
        };
        var group = Layout.Builder.ButtonGroup<T>(options, current, select, style, 12f);
        return (group is Layout.Node.Stack stack ? stack with { Children = [.. stack.Children.Select(c => c.WithCursor(CursorKind.Pointer))] } : group)
            .WFixed(WidthOf(choices.Count))
            .HFixed(AboutCard.HeaderButton);
    }
}
