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

    /// <summary>The Talk button: its hit id, its label and its height.</summary>
    public const string TalkId = "talk", TalkLabel = "Talk";

    /// <summary>The Talk button's height.</summary>
    public const float TalkHeight = 28;

    /// <summary>What the card says with no session at all.</summary>
    public const string Empty = "No sessions are running.";

    private PeekCard()
    {
        Width = CardWidth;
    }

    /// <summary>Lays <paramref name="view"/> out. <paramref name="hovered"/> is the hit id under the pointer (<c>row:N</c>), which gets the highlight and, when cut, its whole text.</summary>
    public static PeekCard Build<TSurface>(Pen<TSurface> pen, PeekView view, string? hovered = null)
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
        var hoverY = -1f;
        for (var i = 0; i < shown; i++)
        {
            var row = view.Rows[i];
            var id = $"row:{i}";
            if (id == hovered)
            {
                // Drawn before the row's own parts so it sits under them, over the row's hit rectangle, so the highlight is what a press takes.
                card.Items.Add(new CardItem(CardItemKind.RowHover, 4, y - RowGap / 2 + 1, CardWidth - 8, rowH + RowGap - 2));
                hoverY = y + rowH + RowGap / 2;
            }
            var kind = row.State switch { PeekState.Asking => CardItemKind.WarnIcon, PeekState.Working => CardItemKind.InfoIcon, _ => CardItemKind.OkIcon };
            card.Items.Add(new CardItem(kind, Padding, y + (rowH - IconSize) / 2, IconSize, IconSize));
            var textX = Padding + IconSize + IconGap;
            var statusW = pen.Measure(row.Status, 13).Width;
            // The status is never the part that is cut: it gets its width, "node · what" gets the rest.
            var whatW = inner - IconSize - IconGap - statusW - IconGap;
            var what = $"{row.Node} · {row.What}";
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
        // The Talk button opens the chat (#1381 section 5); it is always there, even with no session.
        y += 2;
        card.Items.Add(new CardItem(CardItemKind.Box, Padding, y, inner, TalkHeight));
        var lineH = LineHeight(pen, 13);
        var talkW = pen.Measure(TalkLabel, 13).Width;
        var talkX = Padding + (inner - talkW) / 2;
        var talkY = y + (TalkHeight - lineH) / 2;
        card.Items.Add(new CardItem(CardItemKind.Text, talkX, talkY, talkW + 1, lineH, TalkLabel, 13, CardTone.Button));
        card.Lines.Add(new CardLine(TalkLabel, talkX, talkY, talkW + 1, lineH, 13));
        card.Hits.Add(new CardHit(TalkId, Padding, y, inner, TalkHeight));
        y += TalkHeight + RowGap;
        card.Height = y - RowGap + Padding;
        if (hovered is not null && hoverY >= 0 && card.Tooltips.TryGetValue(hovered, out var whole))
        {
            card.AddTip(pen, whole, hoverY);
        }
        return card;
    }
}
