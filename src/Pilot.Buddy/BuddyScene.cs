using Pilot.Ui;
using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>What the scene draws now: the cat's face and breath, and the bubble's notice or the hover summary. Derived from the model by <see cref="BuddyScene.ViewOf"/>.</summary>
public sealed record BuddyView(GrumpFace Face, int Breath, BuddyNotice? Notice, string? Summary, int Badge = 0, AwayPhase Away = AwayPhase.Present, float AwayProgress = 0f);

/// <summary>
/// Where the scene put the cat, its bubble and the open card (device pixels), for a test or the window's hit testing. <paramref name="Scale"/> is the
/// device pixels per design pixel the scene drew at; <paramref name="Card"/> is the card drawn in the bubble's place, if one was asked for.
/// </summary>
public sealed record SceneLayout(SpeechBubble? Bubble, float BubbleX, float BubbleY, float CatX, float CatY, BuddyCard? Card = null, float CardX = 0, float CardY = 0, float Scale = 1f, float CatScale = 0f,
    float BadgeX = 0, float BadgeY = 0, float BadgeW = 0, float BadgeH = 0, bool GoneAway = false)
{
    /// <summary>The device pixels per cat design pixel: the scene scale times the size setting (the card and bubble text stay at <see cref="Scale"/>). Zero in a hand-made layout means the scene scale.</summary>
    public float CatDevice => CatScale > 0 ? CatScale : Scale;
}

/// <summary>
/// The buddy's scene as the transparent window draws it: the cat on its cushion in the bottom-right corner, the bubble above it, right aligned
/// (DSN-2 v5: asleep, one eye open, a session needs you, good news, something broke). It paints no desktop unless asked: the real window is transparent.
/// </summary>
public static class BuddyScene
{
    /// <summary>The distance from the screen's corner to the cushion and the bubble's right edge, and the gap between the bubble and the cat's ears.</summary>
    public const float Margin = 24, BubbleGap = 10;

    /// <summary>
    /// The count of normal cards another client (the GUI) shows, as a round badge at the cat's top right where the "!" of an alert goes: amber with dark ink in
    /// bright mode and dark, both well above 4.5:1. "9+" past nine.
    /// </summary>
    private static void DrawBadge<TSurface>(Pen<TSurface> catPen, BuddyPalette palette, int count)
    {
        var label = count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        catPen.Oval(164, 4, 34, 34, palette.Alert, GrumpCat.Ink, 2);
        catPen.Text(label, 164, 4, 34, 34, label.Length > 1 ? 18 : 22, palette.Dark ? GrumpCat.Ink : GrumpCat.White, TextAlign.Center, TextAlign.Center);
    }

    /// <summary>The face the model's mood asks for: a failure open keeps the grumpy face while the cat sleeps.</summary>
    public static BuddyView ViewOf(BuddyModel model, string? summary = null)
    {
        var mood = model.Mood;
        var face = mood switch
        {
            BuddyMood.Attention => GrumpFace.Alert,
            BuddyMood.GoodNews => GrumpFace.GrudgingHappy,
            BuddyMood.Trouble => GrumpFace.Furious,
            BuddyMood.OneEyeOpen => GrumpFace.OneEyeOpen,
            _ => model.GrumpyFace ? GrumpFace.GrumpyAsleep : GrumpFace.Asleep,
        };
        // The summary is the hover's; a badge alone wakes the cat without a bubble. While the cat walks off, is away or walks back there is no bubble: the notice shows once it is back.
        var away = model.Phase;
        var here = away == AwayPhase.Present;
        return new BuddyView(face, model.BreathPose, here ? model.Current : null, here && mood == BuddyMood.OneEyeOpen && model.Hovering ? summary : null,
            here && model.Current is null ? model.BadgeCount : 0, away, model.AwayProgress);
    }

    /// <summary>
    /// Draws the scene into a surface the renderer draws on at <paramref name="scale"/> (device pixels per design pixel). <paramref name="corner"/> is the
    /// settings' index into <see cref="BuddySettings.CornerChoices"/> (or 3, the top left, which Free placement picks when the cat sits there): the cat sits in that corner of the surface and what it says opens towards the
    /// middle (above a bottom corner, below the top one, right aligned on the right and left aligned on the left). <paramref name="cardFactory"/>
    /// builds the peek, settings or chat card, measured with a pen of the same scale, and draws it in the bubble's place.
    /// </summary>
    public static SceneLayout Draw<TSurface>(Renderer<TSurface> renderer, string fontPath, BuddyPalette palette, BuddyView view, float scale = 1f, bool paintDesktop = false,
        int corner = 0, Func<Pen<TSurface>, BuddyCard>? cardFactory = null, float catSize = 1f)
    {
        if (paintDesktop)
        {
            renderer.FillRectangle(new RectInt(new PointInt((int)renderer.Width, (int)renderer.Height), new PointInt(0, 0)), palette.Desktop);
        }

        var left = corner is 1 or 3;
        var top = corner is 2 or 3;
        var catBottom = GrumpCat.CushionY + GrumpCat.CushionHeight;
        // The cushion's bottom sits Margin from the edge it is in the corner of, the cat's box Margin in from the side.
        var cs = scale * catSize;
        var catX = left ? Margin * scale : renderer.Width - Margin * scale - (GrumpCat.Width + 4) * cs;
        var catY = top ? Margin * scale : renderer.Height - Margin * scale - catBottom * cs;
        if (view.Away != AwayPhase.Present)
        {
            return DrawAway(renderer, fontPath, palette, view, scale, cs, catX, catY, left);
        }
        var catPen = new Pen<TSurface>(renderer, fontPath, catX, catY, cs);
        GrumpCat.Draw(catPen, view.Face, palette, view.Breath);
        if (view.Badge > 0)
        {
            DrawBadge(catPen, palette, view.Badge);
        }

        float PlaceX(float width) => left ? (Margin + 4) * scale : renderer.Width - (Margin + 4 + width) * scale;
        float PlaceY(float height) => top ? catY + catBottom * cs + BubbleGap * scale : catY - (BubbleGap + height) * scale;

        var measure = new Pen<TSurface>(renderer, fontPath, 0, 0, scale);
        if (cardFactory is not null)
        {
            var card = cardFactory(measure);
            var cardX = PlaceX(card.Width);
            var cardY = PlaceY(card.Height);
            card.Draw(new Pen<TSurface>(renderer, fontPath, cardX, cardY, scale), palette);
            return new SceneLayout(null, 0, 0, catX, catY, card, cardX, cardY, scale, cs);
        }

        SpeechBubble? bubble = null;
        var bubbleX = 0f;
        var bubbleY = 0f;
        if (view.Notice is { } notice)
        {
            bubble = SpeechBubble.ForNotice(measure, notice);
        }
        else if (view.Summary is { } summary)
        {
            bubble = SpeechBubble.ForSummary(measure, summary);
        }
        if (bubble is not null)
        {
            bubbleX = PlaceX(bubble.Width);
            bubbleY = PlaceY(bubble.Height);
            bubble.Draw(new Pen<TSurface>(renderer, fontPath, bubbleX, bubbleY, scale), palette);
        }
        return new SceneLayout(bubble, bubbleX, bubbleY, catX, catY, Scale: scale, CatScale: cs);
    }

    /// <summary>How far the cat has walked off the edge for <paramref name="progress"/> of an animation: 0 is on its cushion, 1 is out of the window (smoothstep, so it starts and ends gently).</summary>
    public static float WalkedOff(AwayPhase phase, float progress)
    {
        var e = progress * progress * (3 - 2 * progress);
        return phase switch
        {
            AwayPhase.Leaving => e,
            AwayPhase.Returning => 1 - e,
            AwayPhase.Gone => 1,
            _ => 0,
        };
    }

    // The cat away: walking off the nearest edge (one eye open, then out of the window), only its paw badge there while gone, and walking back in.
    private static SceneLayout DrawAway<TSurface>(Renderer<TSurface> renderer, string fontPath, BuddyPalette palette, BuddyView view, float scale, float cs, float catX, float catY, bool left)
    {
        var off = WalkedOff(view.Away, view.AwayProgress);
        var dir = left ? -1f : 1f;
        var gone = view.Away == AwayPhase.Gone;
        if (!gone)
        {
            var distance = (GrumpCat.Width + 4) * cs + Margin * scale;
            var catPen = new Pen<TSurface>(renderer, fontPath, catX + dir * off * distance, catY, cs);
            GrumpCat.Draw(catPen, GrumpFace.OneEyeOpen, palette, 0, withText: false);
        }
        // The badge shows once the cat is mostly out (leaving) or until it is mostly in (returning), and all the time while gone.
        var showBadge = gone || off > 0.7f;
        var (bw, bh) = (GrumpCat.BadgeWidth * scale, GrumpCat.BadgeHeight * scale);
        var bx = left ? 0f : renderer.Width - bw;
        var by = MathF.Min(catY + (GrumpCat.CushionY * cs - bh) / 2 + GrumpCat.CushionY * cs / 2, renderer.Height - bh);
        if (showBadge)
        {
            GrumpCat.DrawPawBadge(new Pen<TSurface>(renderer, fontPath, bx, by, scale), palette, left);
        }
        return new SceneLayout(null, 0, 0, catX, catY, Scale: scale, CatScale: cs, BadgeX: showBadge ? bx : 0, BadgeY: showBadge ? by : 0, BadgeW: showBadge ? bw : 0, BadgeH: showBadge ? bh : 0, GoneAway: gone);
    }
}
