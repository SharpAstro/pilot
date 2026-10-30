using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>An image the host has decoded: tightly packed RGBA, row after row.</summary>
internal sealed record AttachmentPicture(int Width, int Height, byte[] Rgba);

/// <summary>One image the node keeps a copy of: its id (<see cref="NodeWire.AttachmentRoute"/> serves it), what it was recorded as, and the node that holds it.</summary>
internal sealed record AttachmentRef(string Id, string Title, string Ext, NodeMark? Node);

/// <summary>
/// The Attachments section of a flight's page and a session's details, and the viewer that opens one full size. The pages
/// only lay out; the host fetches and decodes through <see cref="Lookup"/> (once per id, null while it is on its way or
/// cannot be shown) and paints through <see cref="Draw"/>, because how a picture reaches the screen is the surface's own
/// (a Vulkan texture in the window, a blit in a capture).
/// </summary>
internal sealed class AttachmentGallery
{
    public const float ThumbWidth = 120f;
    public const float ThumbHeight = 76f;
    private const string ThumbKey = "thumb:";
    private const string FullKey = "full:";

    private static RGBAColor32 Faint => Palette.Current.Faint;
    private static RGBAColor32 Dim => Palette.Current.Dim;
    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Well => Palette.Current.Well;
    private static RGBAColor32 WellHover => Palette.Current.RowHoverBg;
    private static RGBAColor32 Scrim => Palette.Current.Scrim;

    private readonly Dictionary<string, AttachmentRef> _known = new(StringComparer.Ordinal);

    /// <summary>The decoded picture of a copy, or null while the host is still fetching it or cannot decode it.</summary>
    public Func<AttachmentRef, AttachmentPicture?>? Lookup { get; set; }

    /// <summary>True when the host has given up on a copy's picture (a format it cannot decode, or a fetch that failed): its well shows the extension.</summary>
    public Func<AttachmentRef, bool>? Failed { get; set; }

    /// <summary>Paints a picture in a rectangle already fitted to its shape.</summary>
    public Action<AttachmentPicture, RectF32>? Draw { get; set; }

    /// <summary>The copy open full size, or null.</summary>
    public AttachmentRef? Viewing { get; private set; }

    /// <summary>The copies among <paramref name="artifacts"/>, newest title first as given.</summary>
    public static IReadOnlyList<AttachmentRef> Of(IEnumerable<Artifact>? artifacts, NodeMark? node, string? prefix = null) =>
    [
        .. (artifacts ?? [])
            .Where(a => a.Attachment is not null && a.AttachmentExt is not null)
            .Select(a => new AttachmentRef(a.Attachment!, prefix is null ? a.Title : $"{prefix}: {a.Title}", a.AttachmentExt!, node)),
    ];

    // A copy with no id is an image the node holds nothing of (a link out): it has no picture to fetch.
    private bool IsFailed(AttachmentRef copy) => copy.Id.Length == 0 || Failed?.Invoke(copy) == true;

    public void Open(AttachmentRef copy) => Viewing = copy;

    public void Close() => Viewing = null;

    /// <summary>Escape shuts the viewer; true when it was open.</summary>
    public bool HandleKey(InputKey key)
    {
        if (Viewing is null || key != InputKey.Escape)
        {
            return false;
        }
        Close();
        return true;
    }

    /// <summary>The section, or null with nothing to show: a page without attachments has no heading for them.</summary>
    public Layout.Node? Section(string owner, IReadOnlyList<AttachmentRef> copies)
    {
        if (copies.Count == 0)
        {
            return null;
        }
        var thumbs = new List<Layout.Node>();
        for (var i = 0; i < copies.Count; i++)
        {
            var copy = copies[i];
            if (copy.Id.Length > 0)
            {
                _known[copy.Id] = copy;
            }
            var picked = copy;
            var thumb = Layout.Builder.VStack(
                    (IsFailed(copy)
                        ? Layout.Builder.Text(copy.Ext.ToUpperInvariant(), 14f, Dim, trim: TextTrim.End)
                        : Layout.Builder.Fill(ThumbWidth, ThumbHeight, ThumbKey + copy.Id)).WFixed(ThumbWidth).HFixed(ThumbHeight).Bg(Well).BgHover(WellHover)
                        .Follows(new HitResult.ButtonHit($"attachment:{owner}:{(copy.Id.Length > 0 ? copy.Id : $"#{i}")}"), _ => Open(picked)),
                    Layout.Builder.Text(copy.Title, 11f, Dim, trim: TextTrim.End).RowH(16f))
                .WithGap(2f)
                .WFixed(ThumbWidth)
                .HAuto();
            thumbs.Add(thumb);
        }
        return Layout.Builder.VStack(
                Layout.Builder.Text("Attachments", 11f, Faint).RowH(18f),
                Layout.Builder.WrapH([.. thumbs]).WithGap(10f).WithLineGap(8f).WStar())
            .WithGap(4f)
            .WStar()
            .HAuto();
    }

    /// <summary><paramref name="page"/> with the open copy full size over it; a click anywhere on it, or Escape, shuts it.</summary>
    public Layout.Node Over(Layout.Node page)
    {
        if (Viewing is not { } copy)
        {
            return page;
        }
        if (copy.Id.Length > 0)
        {
            _known[copy.Id] = copy;
        }
        var viewer = Layout.Builder.VStack(
                Layout.Builder.Text($"{copy.Title} · click or Escape to close", 12f, Bright).RowH(22f),
                (IsFailed(copy)
                    ? Layout.Builder.Text($"No preview of a .{copy.Ext} here", 12f, Dim)
                    : Layout.Builder.Fill(32f, 32f, FullKey + copy.Id)).Stretch())
            .WithGap(8f)
            .Pad(24f, 16f)
            .Bg(Scrim)
            .Stretch()
            .Follows(new HitResult.ButtonHit("attachment:close"), _ => Close());
        return Layout.Builder.Overlay(page, viewer);
    }

    /// <summary>The <c>drawFill</c> of a page that carries the section or the viewer: paints thumbnails and the full-size copy.</summary>
    public void DrawFill(Layout.Content.Fill fill, RectF32 rect)
    {
        if (fill.Key is not { } key || Draw is not { } draw || Lookup is not { } lookup)
        {
            return;
        }
        var id = key.StartsWith(ThumbKey, StringComparison.Ordinal) ? key[ThumbKey.Length..]
            : key.StartsWith(FullKey, StringComparison.Ordinal) ? key[FullKey.Length..]
            : null;
        if (id is null || !_known.TryGetValue(id, out var copy) || lookup(copy) is not { Width: > 0, Height: > 0 } picture)
        {
            return;
        }
        draw(picture, Fit(rect, picture.Width, picture.Height));
    }

    /// <summary>The largest rectangle of the picture's shape that fits in <paramref name="rect"/>, centred, never larger than the picture.</summary>
    internal static RectF32 Fit(RectF32 rect, int width, int height)
    {
        var scale = MathF.Min(1f, MathF.Min(rect.Width / width, rect.Height / height));
        var w = width * scale;
        var h = height * scale;
        return new RectF32(rect.X + (rect.Width - w) / 2f, rect.Y + (rect.Height - h) / 2f, w, h);
    }
}
