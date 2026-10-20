using DIR.Lib;
using SharpAstro.Png;

namespace Pilot.Gui;

/// <summary>pilot's window icon, read from the PNG beside the binary.</summary>
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
