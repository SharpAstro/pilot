using SharpAstro.Codecs;
using SharpAstro.Png;

namespace Pilot.Cli;

/// <summary>
/// Enlarges a small image so it fills the terminal's width, up to <see cref="MaxMagnification"/> times its own size.
/// Console.Lib's renderer only ever shrinks an image to fit, so a screenshot narrower than the terminal would sit in a
/// corner at its own size; the resolver hands the renderer an already-enlarged PNG instead.
/// </summary>
public static class ImageMagnifier
{
    /// <summary>The most an image is magnified: a screenshot at 3x is still crisp, past it the blocks are what a person sees.</summary>
    public const int MaxMagnification = 3;

    /// <summary>
    /// How much to enlarge a <paramref name="srcW"/> by <paramref name="srcH"/> image to fit a <paramref name="maxPxW"/> by
    /// <paramref name="maxPxH"/> pixel budget: the tighter of the two budgets, at most <see cref="MaxMagnification"/>, and
    /// never under 1 (an image that is already too big is shrunk by the renderer, not here).
    /// </summary>
    public static double Factor(int srcW, int srcH, int maxPxW, int maxPxH)
    {
        if (srcW <= 0 || srcH <= 0 || maxPxW <= 0 || maxPxH <= 0)
        {
            return 1;
        }
        var factor = Math.Min(MaxMagnification, Math.Min((double)maxPxW / srcW, (double)maxPxH / srcH));
        return Math.Max(1, factor);
    }

    /// <summary>
    /// <paramref name="encoded"/> as a PNG enlarged by <see cref="Factor"/> (nearest neighbour, which keeps a screenshot's
    /// edges sharp), or the bytes as they are when the factor is 1, the bytes are null or empty, or they do not decode.
    /// </summary>
    public static byte[]? Magnify(byte[]? encoded, int maxPxW, int maxPxH)
    {
        if (encoded is null || encoded.Length == 0)
        {
            return encoded;
        }
        try
        {
            if (!ImageCodecs.TryReadInfo(encoded, out var info))
            {
                return encoded;
            }
            var factor = Factor(info.Width, info.Height, maxPxW, maxPxH);
            if (factor <= 1)
            {
                return encoded;
            }
            var rgba = new byte[info.Width * info.Height * 4];
            if (!ImageCodecs.TryDecodeIntoRgba8(encoded, rgba))
            {
                return encoded;
            }
            var dstW = Math.Max(1, (int)(info.Width * factor));
            var dstH = Math.Max(1, (int)(info.Height * factor));
            var dst = new byte[dstW * dstH * 4];
            for (var y = 0; y < dstH; y++)
            {
                var sy = Math.Min(info.Height - 1, (int)((long)y * info.Height / dstH));
                for (var x = 0; x < dstW; x++)
                {
                    var sx = Math.Min(info.Width - 1, (int)((long)x * info.Width / dstW));
                    rgba.AsSpan((sy * info.Width + sx) * 4, 4).CopyTo(dst.AsSpan((y * dstW + x) * 4, 4));
                }
            }
            return PngWriter.Encode(dst, dstW, dstH);
        }
        catch
        {
            return encoded;
        }
    }
}
