using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>
/// Mochi and Noodle, the two drafted cats that lose to Grump: drawn only for the cats screen (DSN-2 v5), as filled, outlined rounded rectangles and
/// ovals on a <see cref="Pen{TSurface}"/>, like Grump. Each draws into a 200 x 160 box.
/// </summary>
public static class OtherCats
{
    private static readonly RGBAColor32 Cream = new(0xf3, 0xe6, 0xcf, 0xff), Ginger = new(0xea, 0x9a, 0x4e, 0xff), Blush = new(0xf6, 0x9f, 0xb0, 0xff);
    private static readonly RGBAColor32 NoodleBody = new(0x2a, 0x2a, 0x33, 0xff), NoodleLine = new(0xf2, 0xf2, 0xf7, 0xff), Bubble = new(0xcf, 0xe6, 0xff, 0xff);

    /// <summary>Mochi: a round cream-and-ginger blob, blushing, a snot bubble going in and out. Pure office-cat nap.</summary>
    public static void DrawMochi<TSurface>(Pen<TSurface> pen, BuddyPalette palette)
    {
        var ink = GrumpCat.Ink;
        // Ginger tail/cushion bar under the blob.
        pen.Box(8, 146, 150, 14, Ginger, 7, ink, 3);
        // The body, the ginger patch on its side, the ears and the head.
        pen.Box(24, 70, 148, 84, Cream, 40, ink, 4);
        pen.Box(112, 82, 48, 34, Ginger, 16);
        pen.Box(34, 8, 36, 40, Cream, 12, ink, 4);
        pen.Box(106, 8, 36, 40, Cream, 12, ink, 4);
        pen.Box(40, 14, 22, 14, Blush, 6);
        pen.Box(112, 14, 22, 14, Blush, 6);
        pen.Box(26, 14, 120, 90, Cream, 44, ink, 4);
        pen.Box(60, 14, 50, 22, Ginger, 11);
        // Closed eyes, blush, nose, mouth, whiskers.
        pen.Box(52, 58, 30, 3, ink, 1);
        pen.Box(98, 58, 30, 3, ink, 1);
        pen.Box(44, 70, 22, 11, Blush, 6);
        pen.Box(116, 70, 22, 11, Blush, 6);
        pen.Box(80, 66, 12, 9, Nose(), 4);
        pen.Box(80, 78, 6, 2, ink, 1);
        pen.Box(90, 78, 6, 2, ink, 1);
        pen.Box(2, 66, 40, 3, ink, 1);
        pen.Box(4, 76, 36, 3, ink, 1);
        pen.Box(132, 66, 40, 3, ink, 1);
        // Paws, and the snot bubble swelling at the nose.
        pen.Box(50, 142, 30, 16, Cream, 8, ink, 3);
        pen.Box(98, 142, 30, 16, Cream, 8, ink, 3);
        pen.Oval(86, 70, 32, 32, Bubble, new RGBAColor32(0xa9, 0xc7, 0xe8, 0xff), 2);
        pen.Text("z", 150, 10, 20, 24, 16, palette.DesktopText, TextAlign.Center);
        pen.Text("Z", 172, 0, 26, 30, 22, palette.DesktopText, TextAlign.Center);
    }

    /// <summary>Noodle: a long black cat with a white outline, flat out, tongue stuck out (a blep). Tail up like a question mark.</summary>
    public static void DrawNoodle<TSurface>(Pen<TSurface> pen, BuddyPalette palette)
    {
        // Tail up like a question mark, the long body, then the ears and the round head.
        pen.Box(4, 28, 12, 62, NoodleBody, 6, NoodleLine, 3);
        pen.Box(4, 80, 118, 32, NoodleBody, 16, NoodleLine, 3);
        pen.Box(122, 62, 22, 24, NoodleBody, 6, NoodleLine, 3);
        pen.Box(158, 62, 22, 24, NoodleBody, 6, NoodleLine, 3);
        pen.Oval(118, 70, 84, 66, NoodleBody, NoodleLine, 3);
        // Closed eyes, nose, the tongue.
        pen.Box(134, 96, 20, 3, NoodleLine, 1);
        pen.Box(166, 96, 20, 3, NoodleLine, 1);
        pen.Box(158, 108, 12, 8, new RGBAColor32(0xf0, 0x6f, 0x9b, 0xff), 4);
        pen.Box(162, 118, 6, 14, new RGBAColor32(0xf0, 0x6f, 0x9b, 0xff), 3);
        pen.Text("zZz", 40, 2, 110, 30, 22, palette.DesktopText, TextAlign.Near);
    }

    private static RGBAColor32 Nose() => new(0xe8, 0x6f, 0x86, 0xff);
}
