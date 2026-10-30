using SharpAstro.Png;
using static SDL3.SDL;

namespace Pilot.Gui;

/// <summary>
/// The image on the system clipboard, as an idea's box takes it: PNG when the clipboard offers image/png, else image/bmp
/// (what a Windows screenshot tool puts there) converted to PNG. Null when it holds neither, and Ctrl+V is the text's.
/// </summary>
internal static class ClipboardImage
{
    public static (byte[] Bytes, string Ext)? Read()
    {
        if (Take("image/png") is { Length: > 0 } png)
        {
            return (png, "png");
        }
        if (Take("image/bmp") is { Length: > 0 } bmp && FromBmp(bmp) is { } converted)
        {
            return (converted, "png");
        }
        return null;
    }

    private static byte[]? Take(string mime)
    {
        if (!HasClipboardData(mime))
        {
            return null;
        }
        var data = GetClipboardData(mime, out var size);
        if (data == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var bytes = new byte[(int)Math.Min(size, int.MaxValue)];
            System.Runtime.InteropServices.Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Free(data);
        }
    }

    /// <summary>The PNG of a BMP file or a bare DIB (24 or 32 bits, uncompressed or bitfields), or null for anything else.</summary>
    internal static byte[]? FromBmp(byte[] data)
    {
        // A file starts "BM" and a 14 byte header before the DIB; the clipboard's own form has none.
        var dib = data.Length > 14 && data[0] == (byte)'B' && data[1] == (byte)'M' ? 14 : 0;
        if (data.Length < dib + 40)
        {
            return null;
        }
        var span = data.AsSpan(dib);
        var headerSize = BitConverter.ToInt32(span);
        var width = BitConverter.ToInt32(span[4..]);
        var rawHeight = BitConverter.ToInt32(span[8..]);
        var bits = BitConverter.ToUInt16(span[14..]);
        var compression = BitConverter.ToInt32(span[16..]);
        var height = Math.Abs(rawHeight);
        if (headerSize < 40 || width <= 0 || height == 0 || width > 16384 || height > 16384 || bits is not (24 or 32) || compression is not (0 or 3))
        {
            return null;
        }
        // With bitfields the three masks follow a 40 byte header; a larger header carries them inside it.
        var masks = new uint[] { 0x00ff0000, 0x0000ff00, 0x000000ff };
        var pixels = dib + headerSize;
        if (compression == 3)
        {
            if (headerSize == 40)
            {
                pixels += 12;
            }
            if (data.Length < dib + 52)
            {
                return null;
            }
            masks = [BitConverter.ToUInt32(span[40..]), BitConverter.ToUInt32(span[44..]), BitConverter.ToUInt32(span[48..])];
        }
        var bytesPerPixel = bits / 8;
        var stride = (width * bytesPerPixel + 3) & ~3;
        if ((long)pixels + (long)stride * height > data.Length)
        {
            return null;
        }
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var source = pixels + (rawHeight > 0 ? height - 1 - y : y) * stride;
            for (var x = 0; x < width; x++)
            {
                var at = source + x * bytesPerPixel;
                var value = bytesPerPixel == 4 ? BitConverter.ToUInt32(data, at) : (uint)(data[at] | data[at + 1] << 8 | data[at + 2] << 16);
                var to = (y * width + x) * 4;
                rgba[to] = Channel(value, masks[0]);
                rgba[to + 1] = Channel(value, masks[1]);
                rgba[to + 2] = Channel(value, masks[2]);
                rgba[to + 3] = 0xff;
            }
        }
        return PngWriter.Encode(rgba, width, height);
    }

    private static byte Channel(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = mask >> shift;
        return (byte)(((value & mask) >> shift) * 255 / max);
    }
}
