using Pilot.Ui;
namespace Pilot.Buddy;

/// <summary>
/// The settings card (DSN-2 v5, without the design's Watch row, which needs the buddy to read peers): Wake up for three check boxes, Sit in corner (segmented, one of three), Clicks pass through while
/// asleep (a switch), Snooze (a dropdown), then the link Done. Placement (Corner or Free), the corner (Corner only) and Size are segmented. Hit ids: <c>wakeAsk</c>, <c>wakeMerged</c>,
/// <c>wakeFailed</c>, <c>placement:&lt;i&gt;</c>, <c>corner:&lt;i&gt;</c>, <c>size:&lt;i&gt;</c>, <c>clickThrough</c>, <c>snooze</c>, <c>done</c>. <see cref="Press"/> turns a hit into the settings it makes.
/// </summary>
public sealed class SettingsCard : BuddyCard
{
    /// <summary>The card's width.</summary>
    public const float CardWidth = 340;

    /// <summary>The control heights: a dropdown and a segment, a check box, a switch's track.</summary>
    public const float ControlHeight = 30, CheckSize = 16, SwitchWidth = 34, SwitchHeight = 18;

    /// <summary>The card's own padding (the design's 14) and the gap between rows (10).</summary>
    public const float CardPadding = 14, RowGap = 10;

    private SettingsCard()
    {
        Width = CardWidth;
    }

    /// <summary>Lays out the card for <paramref name="settings"/>.</summary>
    public static SettingsCard Build<TSurface>(Pen<TSurface> pen, BuddySettings settings)
    {
        var card = new SettingsCard();
        var inner = CardWidth - 2 * CardPadding;
        var y = CardPadding;

        y += card.Row(pen, "Grump", y, inner, 16, CardTone.Header) + RowGap;
        y += card.Row(pen, "Wake up for", y, inner, 13, CardTone.Muted) + RowGap;
        y += card.CheckRow(pen, "wakeAsk", "A session asks me something", settings.WakesForAsk, y, inner) + RowGap;
        y += card.CheckRow(pen, "wakeMerged", "A PR merged or a flight landed", settings.WakesForMerged, y, inner) + RowGap;
        y += card.CheckRow(pen, "wakeFailed", "CI failed or a session got stuck", settings.WakesForFailed, y, inner) + RowGap;
        y += card.Row(pen, "Placement", y, inner, 13, CardTone.Muted) + RowGap;
        y += card.Segments(pen, "placement", BuddySettings.PlacementChoices, settings.PlacementIndex, y, inner) + RowGap;
        // The corner choice means something only while the cat snaps to a corner: in Free there is no dead control.
        if (!settings.IsFree)
        {
            y += card.Row(pen, "Sit in corner", y, inner, 13, CardTone.Muted) + RowGap;
            y += card.Segments(pen, "corner", BuddySettings.CornerChoices, settings.CornerIndex, y, inner) + RowGap;
        }
        y += card.Row(pen, "Size", y, inner, 13, CardTone.Muted) + RowGap;
        y += card.Segments(pen, "size", BuddySettings.SizeChoices, settings.SizeIndex, y, inner) + RowGap;

        // The switch: the track and its knob, then the label.
        var switchRow = MathF.Max(SwitchHeight, LineHeight(pen, 13));
        card.Items.Add(new CardItem(CardItemKind.Switch, CardPadding, y + (switchRow - SwitchHeight) / 2, SwitchWidth, SwitchHeight, On: settings.ClicksPassThrough));
        card.AddLine(pen, "Clicks pass through while asleep", CardPadding + SwitchWidth + 8, y, inner - SwitchWidth - 8, 13, CardTone.Body, "clickThrough");
        card.Hits.Add(new CardHit("clickThrough", CardPadding, y, inner, switchRow));
        y += switchRow + RowGap;

        y += card.Dropdown(pen, "snooze", "Snooze", BuddySettings.SnoozeChoices[settings.SnoozeIndex], y, inner) + RowGap;

        // The link: words in the accent, which the palette makes readable (3:1 at least) on the panel.
        var linkH = LineHeight(pen, 13);
        var doneW = pen.Measure("Done", 13).Width;
        card.AddLine(pen, "Done", CardPadding, y, doneW + 1, 13, CardTone.Accent);
        card.Hits.Add(new CardHit("done", CardPadding, y, doneW, linkH));
        y += linkH;

        card.Height = y + CardPadding;
        return card;
    }

    /// <summary>A segmented control, one segment per choice (hit ids <c>&lt;id&gt;:&lt;i&gt;</c>): the chosen one has the heavier accent edge. Returns its height.</summary>
    private float Segments<TSurface>(Pen<TSurface> pen, string id, IReadOnlyList<string> choices, int chosen, float y, float width)
    {
        var segment = (width - (choices.Count - 1) * 4) / choices.Count;
        for (var i = 0; i < choices.Count; i++)
        {
            var x = CardPadding + i * (segment + 4);
            Items.Add(new CardItem(CardItemKind.Box, x, y, segment, ControlHeight, On: i == chosen));
            AddCentered(pen, choices[i], x, y, segment, ControlHeight, 13, i == chosen ? CardTone.Header : CardTone.Button);
            Hits.Add(new CardHit($"{id}:{i}", x, y, segment, ControlHeight));
        }
        return ControlHeight;
    }

    private float Row<TSurface>(Pen<TSurface> pen, string text, float y, float width, float size, CardTone tone)
    {
        AddLine(pen, text, CardPadding, y, width, size, tone);
        return LineHeight(pen, size);
    }

    private void AddCentered<TSurface>(Pen<TSurface> pen, string text, float x, float y, float w, float h, float size, CardTone tone)
    {
        var (fit, _) = Fit(pen, text, size, w - 8);
        var textW = pen.Measure(fit, size).Width;
        var lineH = LineHeight(pen, size);
        var left = x + (w - textW) / 2;
        var top = y + (h - lineH) / 2;
        Items.Add(new CardItem(CardItemKind.Text, left, top, textW + 1, lineH, fit, size, tone));
        Lines.Add(new CardLine(fit, left, top, textW + 1, lineH, size));
    }

    private float Dropdown<TSurface>(Pen<TSurface> pen, string id, string label, string value, float y, float width)
    {
        Items.Add(new CardItem(CardItemKind.Box, CardPadding, y, width, ControlHeight));
        var lineH = LineHeight(pen, 13);
        var labelW = pen.Measure(label, 13).Width;
        var top = y + (ControlHeight - lineH) / 2;
        AddLine(pen, label, CardPadding + 10, top, labelW + 1, 13, CardTone.Muted);
        var valueX = CardPadding + 10 + labelW + 10;
        AddLine(pen, value, valueX, top, width - (valueX - CardPadding) - 10 - 12 - 6, 13, CardTone.Button, id);
        Items.Add(new CardItem(CardItemKind.Chevron, CardPadding + width - 10 - 9, y + ControlHeight / 2 - 2.5f, 9, 5));
        Hits.Add(new CardHit(id, CardPadding, y, width, ControlHeight));
        return ControlHeight;
    }

    private float CheckRow<TSurface>(Pen<TSurface> pen, string id, string label, bool on, float y, float width)
    {
        var rowH = MathF.Max(CheckSize, LineHeight(pen, 13));
        Items.Add(new CardItem(CardItemKind.Check, CardPadding, y + (rowH - CheckSize) / 2, CheckSize, CheckSize, On: on));
        AddLine(pen, label, CardPadding + CheckSize + 8, y + (rowH - LineHeight(pen, 13)) / 2, width - CheckSize - 8, 13, CardTone.Body, id);
        Hits.Add(new CardHit(id, CardPadding, y, width, rowH));
        return rowH;
    }

    /// <summary>
    /// What a press on <paramref name="hitId"/> does to <paramref name="settings"/>: a check box or the switch flips, a corner is chosen, a dropdown steps to its
    /// next choice (the card is a few rows in a transparent window, not a popup). Null for Done, which the window handles, and for a hit it does not know.
    /// </summary>
    public static BuddySettings? Press(BuddySettings settings, string hitId, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (hitId.StartsWith("corner:", StringComparison.Ordinal) && int.TryParse(hitId.AsSpan("corner:".Length), out var corner))
        {
            return settings with { Corner = Math.Clamp(corner, 0, BuddySettings.CornerChoices.Count - 1) };
        }
        if (hitId.StartsWith("placement:", StringComparison.Ordinal) && int.TryParse(hitId.AsSpan("placement:".Length), out var placement))
        {
            return settings with { Placement = Math.Clamp(placement, 0, BuddySettings.PlacementChoices.Count - 1) };
        }
        if (hitId.StartsWith("size:", StringComparison.Ordinal) && int.TryParse(hitId.AsSpan("size:".Length), out var size))
        {
            return settings with { Size = Math.Clamp(size, 0, BuddySettings.SizeChoices.Count - 1) };
        }
        return hitId switch
        {
            "wakeAsk" => settings with { WakeAsk = !settings.WakesForAsk },
            "wakeMerged" => settings with { WakeMerged = !settings.WakesForMerged },
            "wakeFailed" => settings with { WakeFailed = !settings.WakesForFailed },
            "clickThrough" => settings with { ClickThrough = !settings.ClicksPassThrough },
            "snooze" => settings.WithSnooze((settings.SnoozeIndex + 1) % BuddySettings.SnoozeChoices.Count, now, zone),
            _ => null,
        };
    }
}
