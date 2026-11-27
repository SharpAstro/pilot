using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>Grump's face, one per mood the design draws (asleep, one eye open, alert, grudging happy, furious) and the grumpy-asleep one while a failure is open.</summary>
public enum GrumpFace
{
    /// <summary>Closed lines for eyes, a flat mouth.</summary>
    Asleep,

    /// <summary>One closed line, one half-lidded eye looking sideways.</summary>
    OneEyeOpen,

    /// <summary>Wide eyes, a little "o" mouth.</summary>
    Alert,

    /// <summary>Closed eyes, blush, a tiny smile.</summary>
    GrudgingHappy,

    /// <summary>Lids low, a hissing mouth with two fangs, ears flat, a red anger mark.</summary>
    Furious,

    /// <summary>Asleep, but with the lids' frown: the face kept while a failure is open.</summary>
    GrumpyAsleep,
}

/// <summary>
/// Grump, the owner's pick (DSN-2 v5), drawn as about fifteen filled, outlined rounded rectangles on a <see cref="Pen{TSurface}"/>: no image or SVG
/// asset. The box is <see cref="Width"/> x <see cref="Height"/> design pixels with the cushion under it; the numbers are the design's own.
/// </summary>
public static class GrumpCat
{
    /// <summary>The cat's box, without its cushion.</summary>
    public const float Width = 200, Height = 160;

    /// <summary>The cushion's size and where it sits under the cat.</summary>
    public const float CushionWidth = 190, CushionHeight = 10, CushionY = 160;

    /// <summary>The outline of every shape.</summary>
    public static readonly RGBAColor32 Ink = new(0x1b, 0x1b, 0x1f, 0xff);

    /// <summary>Grump's grey, his darker grey (stripes, tail) and the pink of ears, nose and blush.</summary>
    public static readonly RGBAColor32 Fur = new(0xa7, 0xad, 0xb6, 0xff), FurDark = new(0x7d, 0x83, 0x8c, 0xff), Pink = new(0xf2, 0x9b, 0xab, 0xff);

    /// <summary>The nose, the paws and eyes' white, the cushion, the red of the anger mark.</summary>
    public static readonly RGBAColor32 Nose = new(0xe8, 0x6f, 0x86, 0xff), White = new(0xff, 0xff, 0xff, 0xff), Cushion = new(0x7a, 0x64, 0x50, 0xff), Anger = new(0xff, 0x4d, 0x4d, 0xff);

    private const float Line = 4, Thin = 3;

    /// <summary>The paw badge a gone cat leaves at the edge, in scene design pixels (the scene scale, not the cat's size): big enough to hover and click.</summary>
    public const float BadgeWidth = 64, BadgeHeight = 56;

    /// <summary>
    /// The badge a cat that went away leaves at the edge of the screen: a grey paw reaching in from the side (<paramref name="left"/> says which), three pink toes
    /// and a pad. Ink outline and grey fill read on a bright and on a dark desktop alike.
    /// </summary>
    public static void DrawPawBadge<TSurface>(Pen<TSurface> pen, BuddyPalette palette, bool left)
    {
        // The arm runs off the edge it came in from; the paw is the rounded end, toes towards the middle of the screen.
        var armX = left ? -20f : 24f;
        pen.Box(armX, 14, 60, 28, Fur, 14, Ink, Line);
        var pawX = left ? 22f : 0f;
        pen.Box(pawX, 4, 42, 48, Fur, 20, Ink, Line);
        var toe = left ? 40f : 5f;
        pen.Oval(toe, 12, 9, 9, Pink, Ink, 2);
        pen.Oval(toe, 23, 9, 9, Pink, Ink, 2);
        pen.Oval(toe, 34, 9, 9, Pink, Ink, 2);
        pen.Oval(left ? 26f : 22f, 22, 14, 14, Nose, Ink, 2);
    }

    /// <summary>Draws the cat on its cushion with <paramref name="face"/>. <paramref name="breath"/> is the model's pose, 0 or 1: the body and head grow two pixels for a step.</summary>
    public static void Draw<TSurface>(Pen<TSurface> pen, GrumpFace face, BuddyPalette palette, int breath = 0, bool withText = true)
    {
        var furious = face == GrumpFace.Furious;
        var up = face == GrumpFace.Alert ? 4 : 0;

        // Cushion first: the cat sits on it.
        pen.Box(5, CushionY, CushionWidth, CushionHeight, Cushion, 5, Ink, Thin);

        // Tail: a plain bar; puffed up (taller, higher) when furious.
        if (furious)
        {
            pen.Box(140, 124, 54, 36, FurDark, 16, Ink, Line);
        }
        else
        {
            pen.Box(140, 132, 50, 28, FurDark, 14, Ink, Line);
        }

        // Ears (lowered and flat when furious), each with its pink inside.
        var earY = furious ? 32f : 26f - up;
        var earH = furious ? 22f : 28f;
        pen.Box(10, earY, 52, earH, Fur, 8, Ink, Line);
        pen.Box(138, earY, 48, earH, Fur, 8, Ink, Line);
        pen.Box(18, earY + 8, 20, furious ? 8 : 12, Pink, 5);
        pen.Box(162, earY + 8, 20, furious ? 8 : 12, Pink, 5);

        // Body (the loaf), then the head over it; both grow by the breath.
        pen.Box(22, 70 - 2 * breath, 160, 82 + 2 * breath, Fur, 30, Ink, Line);
        pen.Box(34, 30 - breath, 136, 84 + breath, Fur, 28, Ink, Line);

        // Stripes.
        pen.Box(86, 36 - breath, 8, 18, FurDark, 4);
        pen.Box(98, 36 - breath, 8, 18, FurDark, 4);
        pen.Box(110, 36 - breath, 8, 18, FurDark, 4);

        // Whiskers, either side of the head.
        pen.Box(10, 90, 30, Thin, Ink, 1);
        pen.Box(164, 90, 30, Thin, Ink, 1);

        // Paws.
        pen.Box(58, 132, 32, 20, White, 10, Ink, Line);
        pen.Box(114, 132, 32, 20, White, 10, Ink, Line);

        DrawFace(pen, face, palette, breath, withText);
    }

    private static void DrawFace<TSurface>(Pen<TSurface> pen, GrumpFace face, BuddyPalette palette, int breath, bool withText)
    {
        var b = -breath;
        switch (face)
        {
            case GrumpFace.Asleep:
                ClosedEyes(pen, b);
                Nose_(pen, 90 + b);
                FlatMouth(pen, b);
                if (withText)
                {
                    Zzz(pen, palette);
                }
                break;

            case GrumpFace.GrumpyAsleep:
                ClosedEyes(pen, b);
                // The lids' frown: a brow slanting down to the middle over each closed eye.
                pen.Line(60, 66 + b, 92, 74 + b, Ink, 3);
                pen.Line(144, 66 + b, 112, 74 + b, Ink, 3);
                Nose_(pen, 90 + b);
                FlatMouth(pen, b);
                if (withText)
                {
                    Zzz(pen, palette);
                }
                break;

            case GrumpFace.OneEyeOpen:
                pen.Box(62, 74 + b, 30, Thin, Ink, 1);
                HalfLidEye(pen, 114, 64 + b, 0.5f);
                Nose_(pen, 90 + b);
                FlatMouth(pen, b);
                break;

            case GrumpFace.Alert:
                for (var i = 0; i < 2; i++)
                {
                    var x = i == 0 ? 58f : 112f;
                    pen.Box(x, 58 + b, 34, 26, White, 13, Ink, Thin);
                    pen.Oval(x + 10, 64 + b, 14, 14, Ink);
                }
                Nose_(pen, 90 + b);
                // The little "o".
                pen.Oval(94, 106 + b, 10, 12, Ink);
                if (withText)
                {
                    ComicBang.Draw(pen, 172, 0);
                }
                break;

            case GrumpFace.GrudgingHappy:
                ClosedEyes(pen, b);
                pen.Box(54, 86 + b, 22, 12, Pink, 6);
                pen.Box(128, 86 + b, 22, 12, Pink, 6);
                Nose_(pen, 90 + b);
                // A tiny smile: a short bar with an upturned tick at each end.
                pen.Polyline(Ink, 3, (90, 103 + b), (94, 107 + b), (110, 107 + b), (114, 103 + b));
                if (withText)
                {
                    pen.Text("...fine.", 120, 2, 80, 22, 16, palette.DesktopText, TextAlign.Far);
                }
                break;

            case GrumpFace.Furious:
                HalfLidEye(pen, 60, 62, 0.75f, furious: true);
                HalfLidEye(pen, 112, 62, 0.75f, furious: true);
                pen.Box(96, 88, 12, 8, Nose, 4);
                // The hissing mouth with its two fangs.
                pen.Box(86, 98, 32, 16, Ink, 8);
                pen.Box(91, 98, 6, 7, White, 1);
                pen.Box(107, 98, 6, 7, White, 1);
                // The red anger mark on the forehead.
                pen.Box(132, 44, 16, 5, Anger, 2);
                pen.Box(137, 38, 5, 16, Anger, 2);
                if (withText)
                {
                    pen.Text("#@!", 140, 0, 64, 30, 22, palette.Red, TextAlign.Far);
                }
                break;
        }
    }

    private static void ClosedEyes<TSurface>(Pen<TSurface> pen, int b)
    {
        pen.Box(62, 74 + b, 30, Thin, Ink, 1);
        pen.Box(114, 74 + b, 30, Thin, Ink, 1);
    }

    private static void Nose_<TSurface>(Pen<TSurface> pen, float y) => pen.Box(96, y, 12, 8, Nose, 4);

    private static void FlatMouth<TSurface>(Pen<TSurface> pen, int b) => pen.Box(90, 104 + b, 24, Thin, Ink, 1);

    /// <summary>An eye with a lid over its top: the white, a pupil looking to the side, the grey lid (a share of the eye).</summary>
    private static void HalfLidEye<TSurface>(Pen<TSurface> pen, float x, float y, float lid, bool furious = false)
    {
        pen.Box(x, y, 32, 20, White, 10, Ink, Thin);
        pen.Oval(x + 17, y + 7, 9, 9, Ink);
        pen.Box(x, y - 2, 32, 15 * lid / 0.75f * (furious ? 1f : 0.8f), Fur, 4, Ink, Thin);
    }

    /// <summary>The "z Z" over a sleeping cat, drawn on the desktop.</summary>
    private static void Zzz<TSurface>(Pen<TSurface> pen, BuddyPalette palette)
    {
        pen.Text("z", 152, 8, 18, 22, 16, palette.DesktopText, TextAlign.Center);
        pen.Text("Z", 170, 0, 26, 30, 22, palette.DesktopText, TextAlign.Center);
    }
}
