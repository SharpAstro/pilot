using System.Collections.Concurrent;
using Pilot.Ui;
using SharpAstro.Png;

namespace Pilot.Gui;

/// <summary>
/// The Designs page's renders: each PNG of a version (<c>renders/&lt;screen&gt;.png</c>, read-only once published) decoded once on the pool and kept by
/// its path. <see cref="Lookup"/> never waits: it starts the read and answers null until the picture is there, then asks for a redraw.
/// </summary>
internal sealed class DesignPictures(Action requestRedraw)
{
    private readonly ConcurrentDictionary<string, AttachmentPicture?> _pictures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _loading = new(StringComparer.Ordinal);

    public AttachmentPicture? Lookup(string path)
    {
        if (_pictures.TryGetValue(path, out var known))
        {
            return known;
        }
        if (path.Length > 0 && _loading.TryAdd(path, true))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _pictures[path] = Decode(path);
                }
                finally
                {
                    requestRedraw();
                }
            });
        }
        return null;
    }

    /// <summary>The picture in the PNG at <paramref name="path"/>, or null when it cannot be read or decoded.</summary>
    internal static AttachmentPicture? Decode(string path)
    {
        try
        {
            return PngImageDecoder.TryDecodeRgba8(File.ReadAllBytes(path), out var rgba, out var width, out var height) ? new AttachmentPicture(width, height, rgba) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
