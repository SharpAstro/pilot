using DIR.Lib;

namespace Pilot.Buddy;

/// <summary>
/// The click-through shape of the window: where the buddy has drawn something, a click lands on the buddy; everywhere else it goes to the window below
/// (SDL's SetShape: a pixel whose alpha is not zero is in). Taken from what the scene drew on a transparent CPU surface, so the cat, its bubble and the
/// open card are in it with their antialiased edges, and nothing else is.
/// </summary>
public static class BuddySilhouette
{
    /// <summary>Redone on each change, never per frame: the scene drawn on a transparent surface of the window's size, as an alpha mask.</summary>
    public static RgbaImage Of(RgbaImage drawn)
    {
        var mask = new RgbaImage(drawn.Width, drawn.Height);
        var src = drawn.Pixels;
        var dst = mask.Pixels;
        for (var i = 3; i < src.Length; i += 4)
        {
            if (src[i] != 0)
            {
                dst[i - 3] = dst[i - 2] = dst[i - 1] = dst[i] = 255;
            }
        }
        return mask;
    }

    /// <summary>
    /// Whether every click goes through the window: the cat sleeps with nothing to say, no card is open, and "clicks pass through while asleep" is on.
    /// The shape stays the drawn silhouette, since a shape also cuts what is drawn outside it (a cat that passes clicks must still be seen); the pass is
    /// the window's own style (<c>WindowsShell.SetClickThrough</c>), which takes no mouse event at all while it holds.
    /// </summary>
    public static bool PassesClicks(BuddyGeometry geometry, bool asleep, bool setting) =>
        setting && asleep && geometry.Layout.Bubble is null && geometry.Layout.Card is null;

    /// <summary>Whether the mask lets a click at (<paramref name="x"/>, <paramref name="y"/>) land on the buddy.</summary>
    public static bool Catches(RgbaImage mask, int x, int y) =>
        x >= 0 && y >= 0 && x < mask.Width && y < mask.Height && mask.Pixels[(y * mask.Width + x) * 4 + 3] != 0;
}

/// <summary>A display's usable bounds in pixels, as the host reads them from SDL.</summary>
public readonly record struct DisplayArea(string Name, int X, int Y, int Width, int Height);

/// <summary>Where the window goes: the chosen corner of a monitor, and which monitor a drop lands on. Pure, so a test needs no display.</summary>
public static class BuddyPlacement
{
    /// <summary>The window's top left so the window sits against <paramref name="corner"/> (an index into <see cref="BuddySettings.CornerChoices"/>) of <paramref name="area"/>. The scene keeps its margin inside the window.</summary>
    public static (int X, int Y) Corner(DisplayArea area, int windowWidth, int windowHeight, int corner)
    {
        var x = corner == 1 ? area.X : area.X + area.Width - windowWidth;
        var y = corner == 2 ? area.Y : area.Y + area.Height - windowHeight;
        return (x, y);
    }

    /// <summary>The display the window's centre is on; the nearest when it is between them or off all; null with none.</summary>
    public static DisplayArea? Under(IReadOnlyList<DisplayArea> displays, int x, int y, int width, int height)
    {
        if (displays.Count == 0)
        {
            return null;
        }
        var cx = x + width / 2;
        var cy = y + height / 2;
        DisplayArea? best = null;
        var bestDistance = long.MaxValue;
        foreach (var d in displays)
        {
            var dx = cx < d.X ? d.X - cx : cx > d.X + d.Width ? cx - (d.X + d.Width) : 0;
            var dy = cy < d.Y ? d.Y - cy : cy > d.Y + d.Height ? cy - (d.Y + d.Height) : 0;
            var distance = (long)dx * dx + (long)dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = d;
            }
        }
        return best;
    }

    /// <summary>The display the settings name, or the first (the primary) when it is gone or none was chosen.</summary>
    public static DisplayArea? Chosen(IReadOnlyList<DisplayArea> displays, string? name) =>
        displays.FirstOrDefault(d => d.Name == name) is { Width: > 0 } found ? found : displays.Count > 0 ? displays[0] : null;
}
