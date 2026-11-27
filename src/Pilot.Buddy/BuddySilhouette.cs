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
        setting && asleep && geometry.Layout.Bubble is null && geometry.Layout.Card is null && !geometry.Layout.GoneAway;

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

    /// <summary>The cat's own box on the screen, in window units: its size, and how far it sits from the window's edge it is in the corner of (the scene's margin).</summary>
    public readonly record struct CatBox(int Width, int Height, int Margin);

    /// <summary>A scene corner index: the three the person can choose (0 bottom right, 1 bottom left, 2 top right) and the top left (3), which only Free placement uses.</summary>
    public static int SceneCornerOf(bool left, bool top) => left ? (top ? 3 : 1) : (top ? 2 : 0);

    /// <summary>The cat's top left on the screen for a stored fraction: the fractions are of the distance the cat's own box can travel, so any resolution keeps the cat on screen.</summary>
    public static (int X, int Y) CatAt(DisplayArea area, CatBox cat, double fx, double fy) =>
        (area.X + (int)Math.Round(Math.Clamp(fx, 0, 1) * Math.Max(0, area.Width - cat.Width)),
         area.Y + (int)Math.Round(Math.Clamp(fy, 0, 1) * Math.Max(0, area.Height - cat.Height)));

    /// <summary>The window's top left that puts the cat's box at (<paramref name="catX"/>, <paramref name="catY"/>) when the scene draws it in <paramref name="corner"/>.</summary>
    public static (int X, int Y) WindowFor(int catX, int catY, int windowWidth, int windowHeight, CatBox cat, int corner)
    {
        var left = corner is 1 or 3;
        var top = corner is 2 or 3;
        return (left ? catX - cat.Margin : catX + cat.Width + cat.Margin - windowWidth, top ? catY - cat.Margin : catY + cat.Height + cat.Margin - windowHeight);
    }

    /// <summary>The cat's box top left on the screen for a window at (<paramref name="x"/>, <paramref name="y"/>) drawing it in <paramref name="corner"/> (the inverse of <see cref="WindowFor"/>).</summary>
    public static (int X, int Y) CatOf(int x, int y, int windowWidth, int windowHeight, CatBox cat, int corner)
    {
        var left = corner is 1 or 3;
        var top = corner is 2 or 3;
        return (left ? x + cat.Margin : x + windowWidth - cat.Margin - cat.Width, top ? y + cat.Margin : y + windowHeight - cat.Margin - cat.Height);
    }

    /// <summary>
    /// Which corner of the window the scene draws the cat in when it sits at (<paramref name="catX"/>, <paramref name="catY"/>): the one whose opposite side has the room
    /// for what the cat says (the bubble and the cards open towards the middle of the window), the bottom right when both fit. Where neither side fits, the one with more room.
    /// </summary>
    public static int FreeCorner(DisplayArea area, CatBox cat, int windowWidth, int windowHeight, int catX, int catY)
    {
        // The cat in the window's right (bottom) corner: the window spreads left (up) from the cat.
        var roomLeft = catX + cat.Width + cat.Margin - area.X;
        var roomRight = area.X + area.Width - (catX - cat.Margin);
        var roomUp = catY + cat.Height + cat.Margin - area.Y;
        var roomDown = area.Y + area.Height - (catY - cat.Margin);
        var left = roomLeft >= windowWidth ? false : roomRight >= windowWidth ? true : roomRight > roomLeft;
        var top = roomUp >= windowHeight ? false : roomDown >= windowHeight ? true : roomDown > roomUp;
        return SceneCornerOf(left, top);
    }

    /// <summary>Whether the cat's box is nearer the left side of the work area than the right: where the cat walks off to and its paw badge sits.</summary>
    public static bool NearestIsLeft(DisplayArea area, CatBox cat, int catX) => catX + cat.Width / 2 < area.X + area.Width / 2;

    /// <summary>
    /// Where the window goes for a cat at the stored fraction (<paramref name="fx"/>, <paramref name="fy"/>) of <paramref name="area"/>, and the scene corner it draws in.
    /// Present: the cat is exactly where it was dropped and the corner follows the room. Leaving or returning (<paramref name="away"/>): the cat walks off the nearest side,
    /// so the scene draws towards it, the cat staying where it is. Gone: the window sits flush with that screen edge, so the paw badge is on the edge itself.
    /// </summary>
    public static (int X, int Y, int Corner) FreeWindow(DisplayArea area, int windowWidth, int windowHeight, CatBox cat, double fx, double fy, AwayPhase away = AwayPhase.Present)
    {
        var (catX, catY) = CatAt(area, cat, fx, fy);
        var corner = FreeCorner(area, cat, windowWidth, windowHeight, catX, catY);
        if (away != AwayPhase.Present)
        {
            corner = SceneCornerOf(NearestIsLeft(area, cat, catX), corner is 2 or 3);
        }
        var (x, y) = WindowFor(catX, catY, windowWidth, windowHeight, cat, corner);
        if (away == AwayPhase.Gone)
        {
            x = corner is 1 or 3 ? area.X : area.X + area.Width - windowWidth;
        }
        return (x, y, corner);
    }

    /// <summary>The stored fraction for a cat dropped with its box at (<paramref name="catX"/>, <paramref name="catY"/>): clamped, so a drop partly off screen is stored as the nearest place that is fully on it.</summary>
    public static (double X, double Y) ToFraction(DisplayArea area, CatBox cat, int catX, int catY)
    {
        var travelX = area.Width - cat.Width;
        var travelY = area.Height - cat.Height;
        return (travelX > 0 ? Math.Clamp((double)(catX - area.X) / travelX, 0, 1) : 0, travelY > 0 ? Math.Clamp((double)(catY - area.Y) / travelY, 0, 1) : 0);
    }

    /// <summary>Where the window goes for <paramref name="settings"/> on <paramref name="area"/>: the stored free position in Free placement (the bottom right corner until one is stored), else the chosen corner. The corner the scene draws in comes with it.</summary>
    public static (int X, int Y, int Corner) Resolve(BuddySettings settings, DisplayArea area, int windowWidth, int windowHeight, CatBox cat, AwayPhase away = AwayPhase.Present)
    {
        if (settings.IsFree && settings.FreeAt(area.Name) is var (fx, fy))
        {
            return FreeWindow(area, windowWidth, windowHeight, cat, fx, fy, away);
        }
        var corner = settings.IsFree ? 0 : settings.CornerIndex;
        var (x, y) = Corner(area, windowWidth, windowHeight, corner);
        return (x, y, corner);
    }

    /// <summary>
    /// A drop with the window's top left at (<paramref name="x"/>, <paramref name="y"/>) while the scene draws in <paramref name="sceneCorner"/>: the display the cat is on becomes
    /// its monitor, and in Free placement the cat's own box, clamped onto that work area, is stored for it; in Corner placement nothing more is stored (the window snaps to
    /// the corner). Null with no display.
    /// </summary>
    public static (BuddySettings Settings, DisplayArea Area)? Drop(BuddySettings settings, IReadOnlyList<DisplayArea> displays, int x, int y, int windowWidth, int windowHeight, CatBox cat, int sceneCorner)
    {
        var (catX, catY) = CatOf(x, y, windowWidth, windowHeight, cat, sceneCorner);
        if (Under(displays, catX, catY, cat.Width, cat.Height) is not { } area)
        {
            return null;
        }
        var moved = settings with { Monitor = area.Name };
        if (settings.IsFree)
        {
            var (fx, fy) = ToFraction(area, cat, catX, catY);
            moved = moved.WithFreeAt(area.Name, fx, fy);
        }
        return (moved, area);
    }

    /// <summary>The display the box's centre is on; the nearest when it is between them or off all; null with none.</summary>
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
