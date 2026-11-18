using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The target a design is shown at on its page (PIL1097), the window's own choice: a built-in target by name, or a custom size, and whether the
/// sides are swapped (a portrait target turned to landscape, or the other way round). It is kept per design in gui-settings.json and never goes to the node.
/// </summary>
/// <param name="Name">The built-in target's name, or null for a custom size.</param>
/// <param name="Width">The custom width in design units; ignored for a built-in.</param>
/// <param name="Height">The custom height in design units; ignored for a built-in.</param>
/// <param name="Scale">The custom device scale; ignored for a built-in.</param>
/// <param name="Swapped">Whether width and height are swapped.</param>
internal sealed record DesignTargetChoice(string? Name, int? Width, int? Height, double? Scale, bool Swapped)
{
    /// <summary>The widest or tallest custom side, design units: a guard for a typo, not a design rule.</summary>
    public const int MaxSide = 8192;

    /// <summary>The largest custom scale.</summary>
    public const double MaxScale = 8;

    /// <summary>Desktop, as it was before the page had a switcher.</summary>
    public static DesignTargetChoice Default { get; } = new(DesignTargets.Desktop.Name, null, null, null, false);

    /// <summary>The built-in target <paramref name="surface"/> by name.</summary>
    public static DesignTargetChoice Of(DesignSurface surface) => new(surface.Name, null, null, null, false);

    /// <summary>The surface this stands for, with the sides swapped when <see cref="Swapped"/>; Desktop when the name is no built-in and there is no size.</summary>
    public DesignSurface Surface()
    {
        var surface = Name is { } name && DesignTargets.Named(name) is { } known
            ? known
            : Width is > 0 && Height is > 0 ? new DesignSurface($"{Width}x{Height}", Width.Value, Height.Value, Scale is > 0 ? Scale.Value : 1) : DesignTargets.Desktop;
        return Swapped ? new DesignSurface(surface.Name, surface.Height, surface.Width, surface.Scale) : surface;
    }

    /// <summary>The same target with the sides swapped the other way.</summary>
    public DesignTargetChoice Swap() => this with { Swapped = !Swapped };

    /// <summary>
    /// Reads a typed size, <c>1024x768</c> or <c>1024x768@2</c> (an <c>x</c> or a multiplication sign, spaces allowed), into a custom choice that keeps
    /// <paramref name="swapped"/>. A bad entry is a null choice and the reason in <paramref name="problem"/>.
    /// </summary>
    public static DesignTargetChoice? Parse(string? text, bool swapped, out string? problem)
    {
        problem = null;
        var entry = (text ?? "").Trim().ToLowerInvariant().Replace('×', 'x').Replace(" ", "");
        if (entry.Length == 0)
        {
            problem = "Type a size such as 1024x768 or 1024x768@2";
            return null;
        }
        var at = entry.IndexOf('@');
        var size = at < 0 ? entry : entry[..at];
        var sides = size.Split('x');
        if (sides.Length != 2 || !int.TryParse(sides[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w) || !int.TryParse(sides[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h))
        {
            problem = $"'{(text ?? "").Trim()}' is not a size: use width x height, such as 1024x768";
            return null;
        }
        if (w is < 1 or > MaxSide || h is < 1 or > MaxSide)
        {
            problem = $"A side is 1 to {MaxSide} design units, not {w}x{h}";
            return null;
        }
        var scale = 1d;
        if (at >= 0 && (!double.TryParse(entry[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || scale is <= 0 or > MaxScale || double.IsNaN(scale)))
        {
            problem = $"The scale after @ is above 0 and at most {MaxScale}, such as @2";
            return null;
        }
        return new DesignTargetChoice(null, w, h, scale, swapped);
    }
}

/// <summary>The choice as gui-settings.json keeps it, one per design: every member but the swap is optional, so a file written by another build reads.</summary>
internal sealed record SavedDesignTarget(string? Name, int? Width, int? Height, double? Scale, bool Swapped)
{
    public static SavedDesignTarget Of(DesignTargetChoice c) => new(c.Name, c.Width, c.Height, c.Scale, c.Swapped);

    /// <summary>The choice, or null for a saved entry that names neither a built-in target nor a size.</summary>
    public DesignTargetChoice? ToChoice() =>
        Name is { } n && DesignTargets.Named(n) is { } known ? new DesignTargetChoice(known.Name, null, null, null, Swapped)
        : Width is > 0 and <= DesignTargetChoice.MaxSide && Height is > 0 and <= DesignTargetChoice.MaxSide
            ? new DesignTargetChoice(null, Width, Height, Scale is > 0 and <= DesignTargetChoice.MaxScale ? Scale : 1, Swapped)
            : null;
}

/// <summary>
/// The size one frame of a design is shown at, as gui-settings.json keeps it (PIL1171): a built-in target's name or a terminal size <c>&lt;cols&gt;x&lt;rows&gt;</c>
/// (null: the size the document's block gives), and whether the sides are swapped (Rotate).
/// </summary>
internal sealed record SavedFrameSize(string? Size, bool Swapped)
{
    /// <summary>True for no size of its own, a built-in target or a terminal size; a file naming anything else is read as the block's own size.</summary>
    public bool IsValid => Size is null || DesignDocument.IsValidSize(Size);
}
