using DIR.Lib;
using SharpAstro.Png;

namespace Pilot.Gui;

/// <summary>pilot's window icon, read from the PNG beside the binary.</summary>
/// <remarks>
/// A marked copy: PNG into <see cref="RgbaImage"/> is the reading half SharpAstro.Png's PngWriter lacks, and Console.Lib and
/// tianwen hand-roll it too. Upstream: SharpAstro/Codecs#32. Once it ships, this class goes and the icon is
/// <c>window.SetIcon(…)</c> on what the codec reads.
/// </remarks>
internal static class WindowIcon
{
    /// <summary>The image at <paramref name="path"/>, or null when it is missing or not a PNG this decoder reads.</summary>
    public static RgbaImage? Load(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (!PngImageDecoder.TryReadInfo(data, out var info))
            {
                return null;
            }
            var image = new RgbaImage(info.Width, info.Height);
            return PngImageDecoder.TryDecodeIntoRgba8(data, image.Pixels) ? image : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
