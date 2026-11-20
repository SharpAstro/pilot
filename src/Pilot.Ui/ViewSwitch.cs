using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// One segment of a <see cref="ViewSwitch"/>: the mode it picks, the id its hit region has (<c>flights:view:grid</c>), its word (also its tooltip
/// when the words drop), and one of DIR.Lib's marks where the library has one (null: the word alone, as Canvas, until DIR.Lib draws one,
/// SharpAstro/DIR.Lib#296).
/// </summary>
internal readonly record struct ViewChoice<T>(T Mode, string Action, string Name, Layout.IconKind? Icon = null);

/// <summary>
/// The switch between a page's views, which the Flights page and the Ideas pane share: DIR.Lib's segmented control (PIL1232), icon and word, the
/// words dropping together when the row is narrow, each segment keeping the hit id its pill had and picked by Alt+1, Alt+2, Alt+3 in the order
/// the page lists them. A page names its choices; nothing here is per page.
/// </summary>
internal static class ViewSwitch
{
    // One keyboard cursor per switch (by list id), as the library asks: Left and Right step from where the reader last was.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ListCursor> Cursors = new();

    /// <summary>The cursor of the control declared as <paramref name="listId"/>.</summary>
    internal static ListCursor CursorOf(string listId) => Cursors.GetOrAdd(listId, _ => new ListCursor());

    /// <summary>
    /// The switch for <paramref name="choices"/> with <paramref name="current"/> chosen (the page's state, not the click); a press or a chord calls
    /// <paramref name="select"/>. Its height is the header buttons' (the theme's ButtonHeight), so it shares their row and middle.
    /// </summary>
    public static Layout.Node Build<T>(IReadOnlyList<ViewChoice<T>> choices, T current, Action<T> select, Palette p, string listId = "view-switch")
    {
        var options = new Layout.ButtonGroupOption<T>[choices.Count];
        for (var i = 0; i < options.Length; i++)
        {
            var choice = choices[i];
            options[i] = new Layout.ButtonGroupOption<T>(choice.Mode, choice.Name, choice.Icon)
            {
                Hit = new HitResult.ButtonHit(choice.Action),
                Tooltip = choice.Name,
                Shortcut = new KeyChord(InputKey.D1 + i, InputModifier.Alt),
            };
        }
        return Layout.Builder.Segmented<T>(options, current, select, UiThemes.For(p), CursorOf(listId), listId, 12f).WithCursor(CursorKind.Pointer);
    }
}
