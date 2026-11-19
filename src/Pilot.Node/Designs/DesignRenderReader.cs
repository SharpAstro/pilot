using System.Globalization;
using Pilot.Contracts;
using SharpAstro.Codecs;
using SharpAstro.Png;

namespace Pilot.Node.Designs;

/// <summary>
/// <c>design-renders</c> (PIL1186): reads the render PNGs of a design's version or draft and hands them back as bytes, under the caps of
/// <see cref="DesignRenderCaps"/>. Nothing is written; all of it is in memory.
/// </summary>
public static class DesignRenderReader
{
    /// <summary>
    /// The renders of <paramref name="id"/>: version <paramref name="version"/>, the current one when null, or the draft. <paramref name="screen"/> and <paramref name="size"/>
    /// narrow the pictures; with no screen the entry screen is sent and the others are named. An unknown screen or size is refused with the ones there are.
    /// </summary>
    public static DesignRenders Read(DesignStore store, string id, int? version, bool draft, string? screen, string? size)
    {
        var detail = store.Detail(id);
        string folder;
        int? number = null;
        if (draft)
        {
            if (!Directory.Exists(detail.DraftFolder))
            {
                throw new DesignException($"{detail.Row.Id} has no draft; pilot design draft {detail.Row.Id} makes one");
            }
            folder = detail.DraftFolder;
        }
        else
        {
            number = version ?? detail.Row.Current ?? throw new DesignException($"{detail.Row.Id} has no published version; use --draft for the draft's renders");
            if (!detail.Versions.Any(v => v.Number == number))
            {
                throw new DesignException($"{detail.Row.Id} has no version {number}; it has {string.Join(", ", detail.Versions.Select(v => "v" + v.Number.ToString(CultureInfo.InvariantCulture)))}");
            }
            folder = Path.Combine(detail.Folder!, DesignStore.VersionsFolderName, "v" + number.Value.ToString(CultureInfo.InvariantCulture));
        }
        var prototype = Path.Combine(folder, DesignStore.PrototypeFile);
        var renders = Path.Combine(folder, DesignChecker.RendersFolderName);
        var stamp = DesignChecker.StampOf(renders);
        var files = Directory.Exists(renders) ? Directory.GetFiles(renders, "*.png").Order(StringComparer.Ordinal).ToArray() : [];
        var stale = draft && (files.Length == 0 || stamp is null || !File.Exists(prototype) || stamp != DesignStore.Sha256Of(prototype));

        var document = File.Exists(prototype) ? DesignChecker.Load(prototype, out _) : null;
        var screenIds = document is not null ? document.Screens.Select(s => s.Id).ToArray() : [];
        var entries = files.Select(f => Split(Path.GetFileName(f), screenIds, f)).ToList();
        if (entries.Count == 0)
        {
            return new DesignRenders
            {
                Design = detail.Row.Id,
                Version = number,
                Stale = stale,
                Note = draft ? $"{detail.Row.Id} draft has no renders; pilot design render {detail.Row.Id} draws them" : $"{detail.Row.Id} v{number} has no renders",
            };
        }
        var screens = entries.Select(e => e.Screen).Distinct(StringComparer.Ordinal).ToArray();
        string[] others = [];
        if (!string.IsNullOrWhiteSpace(screen))
        {
            if (!screens.Contains(screen, StringComparer.Ordinal))
            {
                throw new DesignException($"There is no screen '{screen}' in the renders; the screens are {string.Join(", ", screens)}");
            }
            entries = [.. entries.Where(e => e.Screen == screen)];
        }
        else
        {
            var entry = document is not null && screens.Contains(document.Entry, StringComparer.Ordinal) ? document.Entry : screens[0];
            entries = [.. entries.Where(e => e.Screen == entry)];
            others = [.. screens.Where(s => s != entry)];
        }
        if (!string.IsNullOrWhiteSpace(size))
        {
            var sizes = entries.Select(e => e.Size).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            entries = [.. entries.Where(e => string.Equals(e.Size, size, StringComparison.OrdinalIgnoreCase))];
            if (entries.Count == 0)
            {
                throw new DesignException($"There is no size '{size}' in the renders; the sizes are {string.Join(", ", sizes)}");
            }
        }

        var pictures = new List<DesignRenderPicture>();
        var notSent = new List<string>(others);
        var note = new List<string>();
        var budget = DesignRenderCaps.Bytes;
        var overCount = false;
        foreach (var e in entries)
        {
            if (pictures.Count >= DesignRenderCaps.Pictures)
            {
                notSent.Add($"{e.Screen} ({e.Size})");
                overCount = true;
                continue;
            }
            var bytes = File.ReadAllBytes(e.File);
            var share = Math.Max(1, budget / (DesignRenderCaps.Pictures - pictures.Count));
            var fitted = Fit(bytes, share);
            if (fitted is null)
            {
                notSent.Add($"{e.Screen} ({e.Size}): too large to send and could not be scaled down");
                continue;
            }
            var (png, width, height, scaledFrom) = fitted.Value;
            budget -= png.Length;
            pictures.Add(new DesignRenderPicture { Screen = e.Screen, Size = e.Size, PrototypeSha256 = stamp, Png = png, Width = width, Height = height, ScaledFrom = scaledFrom, Path = e.File });
        }
        if (overCount)
        {
            note.Add($"at most {DesignRenderCaps.Pictures} pictures are sent per call; name a screen or a size for the others");
        }
        if (stale)
        {
            note.Add(stamp is null ? "these renders carry no stamp, so they may be older than the prototype" : "the draft's prototype changed after these renders were drawn; pilot design render draws them again");
        }
        return new DesignRenders { Design = detail.Row.Id, Version = number, Pictures = [.. pictures], NotSent = [.. notSent], Stale = stale, Note = note.Count == 0 ? null : string.Join("; ", note) };
    }

    // <screen>.png is the design size, <screen>.<size>.png another; a screen id may hold dots, so the document's ids are tried longest first.
    private static (string Screen, string Size, string File) Split(string name, string[] screenIds, string file)
    {
        var stem = name[..^".png".Length];
        foreach (var id in screenIds.OrderByDescending(s => s.Length))
        {
            if (stem == id)
            {
                return (id, "design", file);
            }
            if (stem.StartsWith(id + ".", StringComparison.Ordinal))
            {
                return (id, stem[(id.Length + 1)..], file);
            }
        }
        var dot = stem.LastIndexOf('.');
        return dot < 0 ? (stem, "design", file) : (stem[..dot], stem[(dot + 1)..], file);
    }

    /// <summary>The picture itself when it is within the side and byte caps, else decoded, scaled down (box average, so text stays legible) and encoded again; null when it is over a cap and cannot be read or decoded.</summary>
    private static (byte[] Png, int Width, int Height, string? ScaledFrom)? Fit(byte[] encoded, int byteShare)
    {
        if (!ImageCodecs.TryReadInfo(encoded, out var info))
        {
            // Unreadable: sent as it is only when it is within the byte cap, never an oversize file unscaled.
            return encoded.Length <= byteShare ? (encoded, 0, 0, null) : null;
        }
        var side = Math.Max(info.Width, info.Height);
        if (side <= DesignRenderCaps.LongestSide && encoded.Length <= byteShare)
        {
            return (encoded, info.Width, info.Height, null);
        }
        var rgba = new byte[info.Width * info.Height * 4];
        if (!ImageCodecs.TryDecodeIntoRgba8(encoded, rgba))
        {
            return null;
        }
        var factor = Math.Min(1.0, (double)DesignRenderCaps.LongestSide / side);
        var width = info.Width;
        var height = info.Height;
        var current = encoded;
        // Shrink further until it fits the bytes too; a flat design gets a long way on the side cap alone.
        for (var round = 0; round < 8; round++)
        {
            var dstW = Math.Max(1, (int)(info.Width * factor));
            var dstH = Math.Max(1, (int)(info.Height * factor));
            var dst = new byte[dstW * dstH * 4];
            for (var y = 0; y < dstH; y++)
            {
                var y0 = (int)((long)y * info.Height / dstH);
                var y1 = Math.Min(info.Height, Math.Max(y0 + 1, (int)((long)(y + 1) * info.Height / dstH)));
                for (var x = 0; x < dstW; x++)
                {
                    var x0 = (int)((long)x * info.Width / dstW);
                    var x1 = Math.Min(info.Width, Math.Max(x0 + 1, (int)((long)(x + 1) * info.Width / dstW)));
                    // Box average of the source block, alpha-weighted so a transparent pixel adds no colour.
                    long r = 0, g = 0, b = 0, a = 0;
                    var count = 0;
                    for (var sy = y0; sy < y1; sy++)
                    {
                        for (var sx = x0; sx < x1; sx++)
                        {
                            var i = (sy * info.Width + sx) * 4;
                            var al = rgba[i + 3];
                            r += rgba[i] * al;
                            g += rgba[i + 1] * al;
                            b += rgba[i + 2] * al;
                            a += al;
                            count++;
                        }
                    }
                    var o = (y * dstW + x) * 4;
                    if (a > 0)
                    {
                        dst[o] = (byte)(r / a);
                        dst[o + 1] = (byte)(g / a);
                        dst[o + 2] = (byte)(b / a);
                    }
                    dst[o + 3] = count > 0 ? (byte)(a / count) : (byte)0;
                }
            }
            current = PngWriter.Encode(dst, dstW, dstH);
            width = dstW;
            height = dstH;
            if (current.Length <= byteShare || dstW == 1 || dstH == 1)
            {
                break;
            }
            factor *= 0.7;
        }
        return (current, width, height, $"{info.Width}x{info.Height}");
    }
}
