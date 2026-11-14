using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The desktop's accent ramp as Windows publishes it (HKCU Explorer\Accent\AccentPalette): eight RGBA entries, Light3
/// (lightest) to Dark3 (darkest). Pure parsing and picking, no OS calls, so it is AOT-clean and a test can feed it bytes.
/// A colour is only ever picked from the ramp, never lightened or darkened here.
/// </summary>
internal sealed record DesktopAccent(
    RGBAColor32 Light3,
    RGBAColor32 Light2,
    RGBAColor32 Light1,
    RGBAColor32 Base,
    RGBAColor32 Dark1,
    RGBAColor32 Dark2,
    RGBAColor32 Dark3,
    RGBAColor32 Extra)
{
    /// <summary>Bytes in the AccentPalette value: eight entries of four bytes.</summary>
    public const int PaletteLength = 32;

    /// <summary>Parses the value; null when it is missing or shorter than <see cref="PaletteLength"/> (longer is read from the front).</summary>
    public static DesktopAccent? Parse(ReadOnlySpan<byte> palette)
    {
        if (palette.Length < PaletteLength)
        {
            return null;
        }
        return new DesktopAccent(At(palette, 0), At(palette, 1), At(palette, 2), At(palette, 3), At(palette, 4), At(palette, 5), At(palette, 6), At(palette, 7));
    }

    // The fourth byte of an entry is unused by Windows (0), so the colour is drawn opaque.
    private static RGBAColor32 At(ReadOnlySpan<byte> palette, int i) => new(palette[i * 4], palette[i * 4 + 1], palette[i * 4 + 2], 0xff);

    /// <summary>Light2 on dark chrome, Dark1 on light chrome: the ramp's own steps that read against each.</summary>
    public RGBAColor32 Pick(bool dark) => dark ? Light2 : Dark1;
}
