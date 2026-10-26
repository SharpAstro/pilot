using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DIR.Lib;
using Pilot.Ui;
using SdlVulkan.Renderer;
using SharpAstro.Png;
using Vortice.Vulkan;

namespace Pilot.Gui;

/// <summary>
/// The window's pictures of the node's attachments: each copy fetched once through the node it lives on (so a remote node's
/// works the same, over the grant), decoded once, and kept by id. A PNG is decoded; any other format the node keeps stays
/// without a picture, and the artifact's link opens it as before. <see cref="Lookup"/> never waits: it starts the fetch
/// and answers null until the picture is there, then asks for a redraw.
/// </summary>
internal sealed class AttachmentPictures(Func<AttachmentRef, CancellationToken, Task<byte[]?>> fetch, Action requestRedraw) : IDisposable
{
    // One slot per id, so a second look at a copy still being fetched does not fetch it again. The loader fills the slot
    // from the pool; the render thread only reads it.
    private sealed class Slot
    {
        public volatile AttachmentPicture? Picture;
    }

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<AttachmentPicture, VkTexture> _textures = new();
    private readonly CancellationTokenSource _stop = new();

    public AttachmentPicture? Lookup(AttachmentRef copy)
    {
        if (_slots.TryGetValue(copy.Id, out var known))
        {
            return known.Picture;
        }
        var slot = new Slot();
        if (_slots.TryAdd(copy.Id, slot))
        {
            _ = Task.Run(() => LoadAsync(copy, slot));
        }
        return null;
    }

    private async Task LoadAsync(AttachmentRef copy, Slot slot)
    {
        try
        {
            if (copy.Ext.Equals("png", StringComparison.OrdinalIgnoreCase) && await fetch(copy, _stop.Token).ConfigureAwait(false) is { } bytes
                && PngImageDecoder.TryDecodeRgba8(bytes, out var rgba, out var width, out var height))
            {
                slot.Picture = new AttachmentPicture(width, height, rgba);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A node that is away or refuses shows no picture; the link under it still opens the file.
        }
        finally
        {
            requestRedraw();
        }
    }

    /// <summary>A capture's painter: the picture scaled into the rectangle and blitted onto the surface.</summary>
    public static Action<AttachmentPicture, RectF32> PaintOn(RgbaImage surface) => (picture, rect) =>
    {
        var width = Math.Max(1, (int)MathF.Round(rect.Width));
        var height = Math.Max(1, (int)MathF.Round(rect.Height));
        var scaled = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(picture.Height - 1, y * picture.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(picture.Width - 1, x * picture.Width / width);
                Buffer.BlockCopy(picture.Rgba, (sy * picture.Width + sx) * 4, scaled, (y * width + x) * 4, 4);
            }
        }
        surface.BlitRgba((int)MathF.Round(rect.X), (int)MathF.Round(rect.Y), scaled, width, height);
    };

    /// <summary>The window's painter: each picture a Vulkan texture, uploaded at the start of the next frame (as tianwen's object pictures are) and drawn from the one after.</summary>
    public Action<AttachmentPicture, RectF32> PaintOn(VkRenderer renderer) => (picture, rect) =>
    {
        var texture = _textures.GetValue(picture, p =>
        {
            var created = VkTexture.CreateDeferred(renderer.Context, p.Rgba, p.Width, p.Height, VkFormat.R8G8B8A8Unorm);
            renderer.Context.QueueTextureUpload(created);
            return created;
        });
        if (!texture.IsUploaded)
        {
            requestRedraw();
            return;
        }
        renderer.DrawTexture(texture.DescriptorSet, rect.X, rect.Y, rect.Width, rect.Height);
    };

    public void Dispose() => _stop.Cancel();
}
