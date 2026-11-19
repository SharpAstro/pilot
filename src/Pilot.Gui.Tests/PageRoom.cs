using Pilot.Ui;

namespace Pilot.Gui.Tests;

/// <summary>
/// The room the collapsed For you bar takes from a page beside the rail (#1184, PIL1205). The bar used to keep a strip at the window's
/// right edge; since PIL1205 the bell is on the rail and the collapsed bar is an edge handle laid over the page, so it takes no room.
/// A test written for the page a window of some width gave, and about that page rather than the window, asks for <see cref="Beside"/>
/// that width: at DPI 1 its page is as wide as it was.
/// </summary>
internal static class PageRoom
{
    /// <summary>The collapsed bar's width in design units: none since PIL1205, so Beside is the window.</summary>
    public static readonly int Width = 0;

    /// <summary>A window <paramref name="window"/> wide with the collapsed bar's room added.</summary>
    public static int Beside(int window) => window + Width;
}
