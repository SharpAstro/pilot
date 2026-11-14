using DIR.Lib;

namespace Pilot.Ui;

/// <summary>The WCAG 2 contrast ratio between two opaque colours: the one copy the palette's accent guard and its tests share.</summary>
internal static class Contrast
{
    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(RGBAColor32 c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.Red) + 0.7152 * Lin(c.Green) + 0.0722 * Lin(c.Blue);
    }

    /// <summary>The ratio, 1 to 21, whichever colour is lighter.</summary>
    public static double Ratio(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (hi < lo)
        {
            (hi, lo) = (lo, hi);
        }
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>The colour <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>, opaque.</summary>
    public static RGBAColor32 Mix(RGBAColor32 from, RGBAColor32 to, double t)
    {
        static byte Ch(byte a, byte b, double t) => (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);
        return new(Ch(from.Red, to.Red, t), Ch(from.Green, to.Green, t), Ch(from.Blue, to.Blue, t), 0xff);
    }

    /// <summary>Hue in degrees (0 to 360), or null for a colour with too little colour to have one (greys).</summary>
    public static double? Hue(RGBAColor32 c)
    {
        double r = c.Red / 255.0, g = c.Green / 255.0, b = c.Blue / 255.0;
        var (max, min) = (Math.Max(r, Math.Max(g, b)), Math.Min(r, Math.Min(g, b)));
        var d = max - min;
        if (d < 0.08)
        {
            return null;
        }
        var h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>True when both colours have a hue and the hues are within <paramref name="degrees"/> of each other.</summary>
    public static bool HueNear(RGBAColor32 a, RGBAColor32 b, double degrees)
    {
        if (Hue(a) is not { } ha || Hue(b) is not { } hb)
        {
            return false;
        }
        var d = Math.Abs(ha - hb);
        return Math.Min(d, 360 - d) < degrees;
    }
}
