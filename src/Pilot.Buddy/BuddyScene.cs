using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>What the scene draws now: the cat's face and breath, and the bubble's notice or the hover summary. Derived from the model by <see cref="BuddyScene.ViewOf"/>.</summary>
public sealed record BuddyView(GrumpFace Face, int Breath, BuddyNotice? Notice, string? Summary);

/// <summary>Where the scene put the cat and its bubble (device pixels), for a test or the window's hit testing.</summary>
public sealed record SceneLayout(SpeechBubble? Bubble, float BubbleX, float BubbleY, float CatX, float CatY);

/// <summary>
/// The buddy's scene as the transparent window draws it: the cat on its cushion in the bottom-right corner, the bubble above it, right aligned
/// (DSN-2 v5: asleep, one eye open, a session needs you, good news, something broke). It paints no desktop unless asked: the real window is transparent.
/// </summary>
public static class BuddyScene
{
    /// <summary>The distance from the screen's corner to the cushion and the bubble's right edge, and the gap between the bubble and the cat's ears.</summary>
    public const float Margin = 24, BubbleGap = 10;

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
        return new BuddyView(face, model.BreathPose, model.Current, mood == BuddyMood.OneEyeOpen ? summary : null);
    }

    /// <summary>Draws the scene into a <paramref name="width"/> x <paramref name="height"/> surface at <paramref name="scale"/> (device pixels per design pixel).</summary>
    public static SceneLayout Draw<TSurface>(Renderer<TSurface> renderer, string fontPath, BuddyPalette palette, BuddyView view, float scale = 1f, bool paintDesktop = false)
    {
        if (paintDesktop)
        {
            renderer.FillRectangle(new RectInt(new PointInt((int)renderer.Width, (int)renderer.Height), new PointInt(0, 0)), palette.Desktop);
        }

        // The cushion's bottom sits Margin above the bottom edge, the cat's box right aligned Margin in from the right.
        var catX = renderer.Width - (Margin + GrumpCat.Width + 4) * scale;
        var catY = renderer.Height - (Margin + GrumpCat.CushionY + GrumpCat.CushionHeight) * scale;
        var catPen = new Pen<TSurface>(renderer, fontPath, catX, catY, scale);
        GrumpCat.Draw(catPen, view.Face, palette, view.Breath);

        SpeechBubble? bubble = null;
        var bubbleX = 0f;
        var bubbleY = 0f;
        var measure = new Pen<TSurface>(renderer, fontPath, 0, 0, scale);
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
            bubbleX = renderer.Width - (Margin + 4 + bubble.Width) * scale;
            bubbleY = catY - (BubbleGap + bubble.Height) * scale;
            bubble.Draw(new Pen<TSurface>(renderer, fontPath, bubbleX, bubbleY, scale), palette);
        }
        return new SceneLayout(bubble, bubbleX, bubbleY, catX, catY);
    }
}
