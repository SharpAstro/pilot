using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>One card of the cats screen: the name, the description, and which cat is drawn in it.</summary>
public sealed record CatCard(string Name, string Description, bool Picked);

/// <summary>
/// The cats screen (DSN-2 v5), reached from Settings, "Other cats": three cards, Grump marked as the owner's pick with the accent, the others for
/// reference. Each cat is drawn as shapes in its card. The text wraps in the card's width and never runs out of it.
/// </summary>
public static class CatsScreen
{
    /// <summary>The three cards, in the design's order.</summary>
    public static readonly IReadOnlyList<CatCard> Cards =
    [
        new("Mochi", "Round cream-and-ginger blob, blushing, snot bubble going in and out. Pure office-cat nap.", false),
        new("Grump (picked)", "Grey loaf, half-lidded eyes, flat mouth. Pretends to sleep and is not impressed by your build.", true),
        new("Noodle", "Long black cat with a white outline, flat out, tongue stuck out (a blep). Tail up like a question mark.", false),
    ];

    /// <summary>The heading over the cards.</summary>
    public const string Heading = "Comic cats: Grump is picked. Mochi and Noodle stay for reference.";

    /// <summary>The card's geometry: where the first sits, its width, the gap, the padding and the cat's room.</summary>
    public const float Left = 24, Top = 64, CardWidth = 260, Gap = 16, Padding = 16, CatRoom = 170, Radius = 12;

    /// <summary>One laid-out text line of the screen (device-independent design pixels), for the tests' overlap check.</summary>
    public sealed record Line(string Text, float X, float Y, float Width, float Height);

    /// <summary>Draws the screen and returns every text line it drew.</summary>
    public static IReadOnlyList<Line> Draw<TSurface>(Renderer<TSurface> renderer, string fontPath, BuddyPalette palette, float scale = 1f)
    {
        var lines = new List<Line>();
        renderer.FillRectangle(new RectInt(new PointInt((int)renderer.Width, (int)renderer.Height), new PointInt(0, 0)), palette.Desktop);
        var pen = new Pen<TSurface>(renderer, fontPath, 0, 0, scale);

        var availableWidth = renderer.Width / scale - 2 * Left;
        var heading = TextWrap.Lines(t => pen.Measure(t, 16).Width, Heading, availableWidth, 2);
        var headingH = MathF.Ceiling(pen.Measure("Ag", 16).Height);
        for (var i = 0; i < heading.Lines.Count; i++)
        {
            pen.Text(heading.Lines[i], Left, 24 + i * headingH, availableWidth, headingH, 16, palette.HeaderText);
            lines.Add(new Line(heading.Lines[i], Left, 24 + i * headingH, availableWidth, headingH));
        }
        var top = Top + (heading.Lines.Count - 1) * headingH;

        // The cards wrap onto another row when the window is too narrow for three.
        var perRow = Math.Max(1, (int)((availableWidth + Gap) / (CardWidth + Gap)));
        var rowBottom = new float[(Cards.Count + perRow - 1) / perRow + 1];
        rowBottom[0] = top;
        for (var i = 0; i < Cards.Count; i++)
        {
            var row = i / perRow;
            var x = Left + i % perRow * (CardWidth + Gap);
            var y = rowBottom[row];
            var bottom = DrawCard(pen, palette, Cards[i], i, x, y, lines);
            rowBottom[row + 1] = MathF.Max(rowBottom[row + 1], bottom + Gap);
        }
        return lines;
    }

    private static float DrawCard<TSurface>(Pen<TSurface> pen, BuddyPalette palette, CatCard card, int index, float x, float y, List<Line> lines)
    {
        var inner = CardWidth - 2 * Padding;
        var nameH = MathF.Ceiling(pen.Measure("Ag", 15).Height);
        var bodyH = MathF.Ceiling(pen.Measure("Ag", 13).Height);
        var description = TextWrap.Lines(t => pen.Measure(t, 13).Width, card.Description, inner, 6);
        var height = Padding + CatRoom + 8 + nameH + 6 + bodyH * description.Lines.Count + Padding;

        var edge = card.Picked ? palette.Accent : palette.CardBorder;
        pen.Box(x, y, CardWidth, height, palette.PanelBg, Radius, edge, card.Picked ? 2f : 1f);

        // The cat, in the middle of its room, a little smaller than life so Noodle's long body fits.
        var catPen = pen.Moved(x + Padding + (inner - 200 * 0.9f) / 2, y + Padding).Scaled(0.9f);
        switch (index)
        {
            case 0:
                OtherCats.DrawMochi(catPen, palette);
                break;
            case 1:
                GrumpCat.Draw(catPen, GrumpFace.SideEye, palette);
                break;
            default:
                OtherCats.DrawNoodle(catPen, palette);
                break;
        }

        // The name takes the accent when picked; the label says so in words too ("(picked)"), so colour is not alone.
        var textY = y + Padding + CatRoom + 8;
        pen.Text(card.Name, x + Padding, textY, inner, nameH, 15, card.Picked ? palette.Accent : palette.HeaderText);
        lines.Add(new Line(card.Name, x + Padding, textY, inner, nameH));
        for (var i = 0; i < description.Lines.Count; i++)
        {
            var ly = textY + nameH + 6 + i * bodyH;
            pen.Text(description.Lines[i], x + Padding, ly, inner, bodyH, 13, palette.MutedText);
            lines.Add(new Line(description.Lines[i], x + Padding, ly, inner, bodyH));
        }
        return y + height;
    }
}
