namespace Pilot.Contracts;

/// <summary>One surface a design is shown, checked and rendered at (PIL1097): a name, a design size in design units and a device scale.</summary>
public sealed record DesignSurface(string Name, int Width, int Height, double Scale)
{
    /// <summary>The surface's width in pixels, the design width times the scale.</summary>
    public int PixelWidth => (int)Math.Round(Width * Scale);

    /// <summary>The surface's height in pixels, the design height times the scale.</summary>
    public int PixelHeight => (int)Math.Round(Height * Scale);
}

/// <summary>
/// The built-in targets of a design (PIL1097), one catalogue the node's check and render and the GUI's Designs page both read: Desktop 1440x900 at 1,
/// Laptop 1280x800 at 1.5, Tablet 820x1180 at 2 and Phone 390x844 at 3. A document's <c>targets</c> entry names one of these or gives its own size.
/// </summary>
public static class DesignTargets
{
    public static readonly DesignSurface Desktop = new("Desktop", 1440, 900, 1);
    public static readonly DesignSurface Laptop = new("Laptop", 1280, 800, 1.5);
    public static readonly DesignSurface Tablet = new("Tablet", 820, 1180, 2);
    public static readonly DesignSurface Phone = new("Phone", 390, 844, 3);

    /// <summary>Every built-in target, in the order the GUI offers them.</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<DesignSurface> BuiltIn = [Desktop, Laptop, Tablet, Phone];

    /// <summary>The built-in target called <paramref name="name"/> (case does not matter), or null.</summary>
    public static DesignSurface? Named(string? name) =>
        BuiltIn.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The surface a document's <c>targets</c> entry stands for: a built-in name alone gives that target; a size gives a surface of that size, named
    /// <paramref name="name"/> or, with none, <c>&lt;width&gt;x&lt;height&gt;</c>, at <paramref name="scale"/> (the built-in's, else 1, when none). A name that is
    /// no built-in and has no size is null.
    /// </summary>
    public static DesignSurface? Resolve(string? name, int? width = null, int? height = null, double? scale = null)
    {
        var known = Named(name);
        if (width is not > 0 || height is not > 0)
        {
            return known;
        }
        return new DesignSurface(
            string.IsNullOrWhiteSpace(name) ? $"{width}x{height}" : known?.Name ?? name,
            width.Value, height.Value, scale is > 0 ? scale.Value : known?.Scale ?? 1);
    }

    /// <summary>The surfaces of a document's entries, in order; Desktop alone when the document names none.</summary>
    public static DesignSurface[] ResolveAll(IEnumerable<DesignSurface?> entries)
    {
        var resolved = entries.OfType<DesignSurface>().ToArray();
        return resolved.Length == 0 ? [Desktop] : resolved;
    }

    /// <summary>The file name of a screen's render at a target: <c>&lt;screen&gt;.&lt;target&gt;.png</c>.</summary>
    public static string RenderFile(string screen, string target) => $"{screen}.{target}.png";
}
