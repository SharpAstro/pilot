namespace Pilot.Buddy;

/// <summary>
/// What the cat says while the pointer is over it (#1381 sections 9 and 10): the headline ("2 need you" or "Nothing needs you right now"), the working line
/// (with Grump's "Go away." when nothing is open) and, under it, the list the count badge counts: a row per open Ring card with its title, the flight or leg
/// it is about and how long it has been open. A row press hands the GUI that card's link (hit id <c>row:&lt;index&gt;</c>). A title or place too long for the
/// card ends in an ellipsis with its whole text in <see cref="BuddyCard.Tooltips"/>; more rows than <see cref="MaxRows"/> end in a line that says how many are left.
/// </summary>
public sealed class HoverCard : BuddyCard
{
    /// <summary>The card's width and the most rows drawn.</summary>
    public const float CardWidth = 336;

    /// <summary>The most rows drawn.</summary>
    public const int MaxRows = 6;

    private HoverCard()
    {
        Width = CardWidth;
    }

    /// <summary>Lays <paramref name="view"/> out.</summary>
    public static HoverCard Build<TSurface>(Pen<TSurface> pen, HoverView view)
    {
        var card = new HoverCard();
        var inner = CardWidth - 2 * Padding;
        var y = Padding;
        y = card.AddWrapped(pen, view.Headline, Padding, y, inner, 14, CardTone.Header, 2, "headline") + 2;
        y = card.AddWrapped(pen, view.Detail, Padding, y, inner, 13, CardTone.Muted, 3, "detail");
        var shown = Math.Min(MaxRows, view.Rows.Count);
        if (shown > 0)
        {
            y += Gap;
            card.Items.Add(new CardItem(CardItemKind.Rule, Padding, y, inner, 1));
            y += 1 + Gap;
        }
        var lineH = LineHeight(pen, 13);
        var smallH = LineHeight(pen, 12);
        for (var i = 0; i < shown; i++)
        {
            var row = view.Rows[i];
            var id = $"row:{i}";
            var top = y;
            card.Items.Add(new CardItem(CardItemKind.WarnIcon, Padding, y + (lineH - PeekCard.IconSize) / 2, PeekCard.IconSize, PeekCard.IconSize));
            var textX = Padding + PeekCard.IconSize + PeekCard.IconGap;
            var room = CardWidth - Padding - textX;
            card.AddLine(pen, row.Title, textX, y, room, 13, CardTone.Body, id);
            y += lineH;
            var second = row.Where.Length == 0 ? row.Age : $"{row.Where} · {row.Age}";
            card.AddLine(pen, second, textX, y, room, 12, CardTone.Muted, id + ":where");
            y += smallH;
            card.Hits.Add(new CardHit(id, 0, top - 4, CardWidth, y - top + 8));
            y += 8;
        }
        if (view.Rows.Count > shown)
        {
            card.AddLine(pen, $"+ {view.Rows.Count - shown} more in the GUI", Padding, y, inner, 13, CardTone.Muted);
            y += lineH + 8;
        }
        card.Height = (shown > 0 || view.Rows.Count > shown ? y - 8 : y) + Padding;
        return card;
    }
}
