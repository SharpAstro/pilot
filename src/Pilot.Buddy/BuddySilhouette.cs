using Pilot.Ui;
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

    /// <summary>
    /// The window's top left for a stored free position: the fractions are of the distance the window can travel (the work area less the window), so any
    /// resolution keeps the cat on screen. A window bigger than the area sits at the area's top left.
    /// </summary>
    public static (int X, int Y) FromFraction(DisplayArea area, int windowWidth, int windowHeight, double fx, double fy)
    {
        var travelX = Math.Max(0, area.Width - windowWidth);
        var travelY = Math.Max(0, area.Height - windowHeight);
        return (area.X + (int)Math.Round(Math.Clamp(fx, 0, 1) * travelX), area.Y + (int)Math.Round(Math.Clamp(fy, 0, 1) * travelY));
    }

    /// <summary>The stored fraction for a window dropped with its top left at (<paramref name="x"/>, <paramref name="y"/>): clamped, so a drop part off screen is stored as the nearest place on it.</summary>
    public static (double X, double Y) ToFraction(DisplayArea area, int windowWidth, int windowHeight, int x, int y)
    {
        var travelX = area.Width - windowWidth;
        var travelY = area.Height - windowHeight;
        return (travelX > 0 ? Math.Clamp((double)(x - area.X) / travelX, 0, 1) : 0, travelY > 0 ? Math.Clamp((double)(y - area.Y) / travelY, 0, 1) : 0);
    }

    /// <summary>Where the window goes for <paramref name="settings"/> on <paramref name="area"/>: the stored free position in Free placement (the corner until one is stored), else the chosen corner.</summary>
    public static (int X, int Y) Resolve(BuddySettings settings, DisplayArea area, int windowWidth, int windowHeight) =>
        settings.IsFree && settings.FreeAt(area.Name) is var (fx, fy) ? FromFraction(area, windowWidth, windowHeight, fx, fy) : Corner(area, windowWidth, windowHeight, settings.CornerIndex);

    /// <summary>
    /// A drop with the window's top left at (<paramref name="x"/>, <paramref name="y"/>): the display it is on becomes the cat's monitor, and in Free placement
    /// the clamped position is stored for it; in Corner placement nothing more is stored (the window snaps to the corner). Null with no display.
    /// </summary>
    public static (BuddySettings Settings, DisplayArea Area)? Drop(BuddySettings settings, IReadOnlyList<DisplayArea> displays, int x, int y, int windowWidth, int windowHeight)
    {
        if (Under(displays, x, y, windowWidth, windowHeight) is not { } area)
        {
            return null;
        }
        var moved = settings with { Monitor = area.Name };
        if (settings.IsFree)
        {
            var (fx, fy) = ToFraction(area, windowWidth, windowHeight, x, y);
            moved = moved.WithFreeAt(area.Name, fx, fy);
        }
        return (moved, area);
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
