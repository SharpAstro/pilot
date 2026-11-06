using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DIR.Lib;

namespace Pilot.Ui;

/// <summary>One <see cref="TextFault"/> as plain data: what the inspector hands a tool outside the process.</summary>
internal sealed record PageFault(string Kind, string Line, string Text, Rect<float> Bounds, string? Other, Rect<float> OtherBounds)
{
    public static PageFault Of(TextFault fault) =>
        new(fault.Kind.ToString().ToLowerInvariant(), fault.Line, fault.Text, fault.Bounds, fault.Other, fault.OtherBounds);
}

/// <summary>
/// The text faults of the page the window shows, as plain data (beside <see cref="LiveTextCheck"/>): its name, the window's size and the
/// page's own, the DPI, and each fault <see cref="TextOverlap.Faults"/> finds on the page's arranged nodes. It runs that one check, not a
/// second copy of it, over the nodes the page's widget painted, so what the running Debug window answers over the inspector is what
/// the CPU-rendered tests and the live card see.
/// </summary>
internal sealed record PageFaults(string Page, (int Width, int Height)? Window, (int Width, int Height) PageSize, float Dpi, IReadOnlyList<PageFault> Faults)
{
    /// <summary>
    /// The faults of <paramref name="arranged"/>, the nodes <paramref name="page"/> last painted at <paramref name="dpi"/>. With no
    /// nodes (a page not drawn yet) the page size is 0x0 and there are no faults.
    /// </summary>
    public static PageFaults Of(string page, (int Width, int Height)? window, ImmutableArray<Layout.ArrangedNode<float>> arranged, float dpi)
    {
        if (arranged.IsDefaultOrEmpty)
        {
            return new PageFaults(page, window, (0, 0), dpi, []);
        }

        var root = arranged[0].Bounds;
        var faults = TextOverlap.Faults(arranged, maxLeaves: LiveTextCheck.MaxLeaves);
        return new PageFaults(page, window, ((int)MathF.Round(root.Width), (int)MathF.Round(root.Height)), dpi, [.. faults.Select(PageFault.Of)]);
    }

    /// <summary>Number of faults.</summary>
    public int Count => Faults.Count;

    /// <summary>The faults as one JSON array, for a tool that parses them: each has its kind, line, texts and the two rects (x, y, w, h).</summary>
    public string FaultsJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var f in Faults)
            {
                w.WriteStartObject();
                w.WriteString("kind", f.Kind);
                w.WriteString("line", f.Line);
                w.WriteString("text", f.Text);
                WriteRect(w, "bounds", f.Bounds);
                if (f.Other is null)
                {
                    w.WriteNull("other");
                }
                else
                {
                    w.WriteString("other", f.Other);
                }

                WriteRect(w, "otherBounds", f.OtherBounds);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The window as <c>WxH</c>, or null where the host gave none.</summary>
    public string? WindowText => Window is { } w ? $"{w.Width}x{w.Height}" : null;

    /// <summary>The page's own size as <c>WxH</c>: the window less the rail and the cards panel.</summary>
    public string PageSizeText => $"{PageSize.Width}x{PageSize.Height}";

    /// <summary>The DPI as the card says it (<c>1</c>, <c>1.5</c>).</summary>
    public string DpiText => Dpi.ToString("0.##", CultureInfo.InvariantCulture);

    private static void WriteRect(Utf8JsonWriter w, string name, Rect<float> r)
    {
        w.WriteStartObject(name);
        w.WriteNumber("x", r.X);
        w.WriteNumber("y", r.Y);
        w.WriteNumber("w", r.Width);
        w.WriteNumber("h", r.Height);
        w.WriteEndObject();
    }
}
