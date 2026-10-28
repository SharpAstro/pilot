namespace Pilot.Gui.Tests;

/// <summary>
/// Cases of <see cref="PageTextOverlapTests"/> that fail today, each with why and the first fault line it fails with.
/// "DIR.Lib#169" is the stack measure the pinned DIR.Lib (11.37.*) still has: the case goes green once the pin has it.
/// A case that fails with #169's fix applied too is the page's own layout: fix the page, never add its entry here.
/// </summary>
internal static class PageTextOverlapSkips
{
    public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>
    {
        // chat
        ["chat | long | 1900 wide | DPI 1.25"] = "DIR.Lib#169: overlap: '•' (152.5,230.1 19.5x21.1) is drawn over 'Bash' (60.1,241.8 105x19.5) (+5 more)",
    };
}
