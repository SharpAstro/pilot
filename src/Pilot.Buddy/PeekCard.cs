namespace Pilot.Buddy;

/// <summary>
/// The peek card (DSN-2 v5): "Watchtower · N nodes · M sessions", a rule, then a row per session, the ones that ask first, then working, then idle:
/// an icon, "node · what", and how it stands. A row press opens it in the GUI (its hit id is <c>row:&lt;index&gt;</c>). A row too long for the card ends
/// in an ellipsis with its whole text in <see cref="BuddyCard.Tooltips"/>; more rows than <see cref="MaxRows"/> end in a line that says how many are left.
/// </summary>
public sealed class PeekCard : BuddyCard
{
    /// <summary>The card's width and the most rows it shows before it says how many more there are.</summary>
    public const float CardWidth = 360;

    /// <summary>The most rows drawn.</summary>
    public const int MaxRows = 10;

    /// <summary>The icon's size, and the gap after it.</summary>
    public const float IconSize = 14, IconGap = 6, RowGap = 6;

    /// <summary>What the card says with no session at all.</summary>
    public const string Empty = "No sessions are running.";

    private PeekCard()
    {
        Width = CardWidth;
    }

    /// <summary>Lays <paramref name="view"/> out.</summary>
    public static PeekCard Build<TSurface>(Pen<TSurface> pen, PeekView view)
    {
        var card = new PeekCard();
        var inner = CardWidth - 2 * Padding;
        var y = Padding;
        card.AddLine(pen, view.Header, Padding, y, inner, 14, CardTone.Header, "header");
        y += LineHeight(pen, 14) + RowGap;
        card.Items.Add(new CardItem(CardItemKind.Rule, Padding, y, inner, 1));
        y += 1 + RowGap;

        var rowH = MathF.Max(IconSize, LineHeight(pen, 13));
        if (view.Rows.Count == 0)
        {
            card.AddLine(pen, Empty, Padding, y, inner, 13, CardTone.Muted);
            y += rowH;
        }
        var shown = Math.Min(MaxRows, view.Rows.Count);
        for (var i = 0; i < shown; i++)
        {
            var row = view.Rows[i];
            var kind = row.State switch { PeekState.Asking => CardItemKind.WarnIcon, PeekState.Working => CardItemKind.InfoIcon, _ => CardItemKind.OkIcon };
            card.Items.Add(new CardItem(kind, Padding, y + (rowH - IconSize) / 2, IconSize, IconSize));
            var textX = Padding + IconSize + IconGap;
            var statusW = pen.Measure(row.Status, 13).Width;
            // The status is never the part that is cut: it gets its width, "node · what" gets the rest.
            var whatW = inner - IconSize - IconGap - statusW - IconGap;
            var what = $"{row.Node} · {row.What}";
            var id = $"row:{i}";
            var drawn = card.AddLine(pen, what, textX, y, whatW, 13, CardTone.Body, id);
            card.AddLine(pen, row.Status, textX + drawn + IconGap, y, statusW + 1, 13, row.State == PeekState.Asking ? CardTone.Warn : CardTone.Muted);
            card.Hits.Add(new CardHit(id, 0, y - RowGap / 2, CardWidth, rowH + RowGap));
            y += rowH + RowGap;
        }
        if (view.Rows.Count > shown)
        {
            card.AddLine(pen, $"+ {view.Rows.Count - shown} more in the GUI", Padding, y, inner, 13, CardTone.Muted);
            y += rowH + RowGap;
        }
        card.Height = y - RowGap + Padding;
        return card;
    }
}
