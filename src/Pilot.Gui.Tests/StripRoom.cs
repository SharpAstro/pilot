using Pilot.Ui;

namespace Pilot.Gui.Tests;

/// <summary>
/// The room the For you strip takes (#1184). The strip sits at the window's right edge whether the bar is open or collapsed (the open bar
/// lies over the page, the strip beside it), so every page is <see cref="Width"/> design units narrower than the window used to leave it.
/// A test written for the page a window of some width gave before the strip, and about that page rather than the window, asks for
/// <see cref="Beside"/> that width: at DPI 1 its page is as wide as it was.
/// </summary>
internal static class StripRoom
{
    /// <summary>The strip's width in design units: none since PIL1205 (the bell is on the rail), so Beside is the window.</summary>
    public static readonly int Width = 0;

    /// <summary>A window <paramref name="window"/> wide with the strip's room added.</summary>
    public static int Beside(int window) => window + Width;
}
