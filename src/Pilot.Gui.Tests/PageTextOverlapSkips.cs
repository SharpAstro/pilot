namespace Pilot.Gui.Tests;

/// <summary>
/// Cases of <see cref="PageTextOverlapTests"/> that fail today, each with why and the first fault line it fails with.
/// "DIR.Lib#169" is the stack measure the pinned DIR.Lib (11.37.*) still has: the case goes green once the pin has it.
/// A case that fails with #169's fix applied too is the page's own layout: fix the page, never add its entry here. Run
/// with -p:UseLocalDirLib=false to see what the pinned package does with each page: only the chat and the transcript, whose rows
/// are stacks measured at the wrong width, fail.
/// </summary>
internal static class PageTextOverlapSkips
{
    public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>
    {
        // chat
        ["chat | long | 700 wide | DPI 1"] = "DIR.Lib#169: overlap: 'given' (111,256.9 39.9x16.9) is drawn over 'Tests:' (111,257.6 43.1x16.9) (+75 more)",
        ["chat | long | 700 wide | DPI 1.25"] = "DIR.Lib#169: overlap: '•' (149,218.3 19.5x21.1) is drawn over 'Bash' (60.1,230.1 105x19.5) (+52 more)",
        ["chat | long | 700 wide | DPI 1.5"] = "DIR.Lib#169: overlap: 'given' (174.2,-64.6 61.4x25.4) is drawn over 'Tests:' (174.2,-63.7 66.3x25.4) (+69 more)",
        ["chat | long | 900 wide | DPI 1"] = "DIR.Lib#169: overlap: 'ORB204\\' (137.6,333.1 57.4x16.9) is drawn over 'Every page is checked for text drawn ov…' (138.1,342.5 393.9x16.9) (+29 more)",
        ["chat | long | 900 wide | DPI 1.25"] = "DIR.Lib#169: overlap: 'given' (141.7,95.6 49.1x21.1) is drawn over 'Tests:' (141.7,96.4 53x21.1) (+68 more)",
        ["chat | long | 900 wide | DPI 1.5"] = "DIR.Lib#169: overlap: 'worktrees\\' (206.4,82.6 106.2x25.4) is drawn over 'Every page is checked for text drawn ov…' (207.1,96.7 590.9x25.4) (+31 more)",
        ["chat | long | 1300 wide | DPI 1"] = "DIR.Lib#169: overlap: '•' (122,203.6 15.6x16.9) is drawn over 'Bash' (48.1,213 84x15.6) (+53 more)",
        ["chat | long | 1300 wide | DPI 1.25"] = "DIR.Lib#169: overlap: 'Tests\\' (172,292.2 48x21.1) is drawn over 'Every page is checked for text drawn ov…' (172.6,303.9 667.4x21.1) (+21 more)",
        ["chat | long | 1300 wide | DPI 1.5"] = "DIR.Lib#169: overlap: '•' (180.8,82.6 23.4x25.4) is drawn over 'Bash' (72.1,96.7 126x23.4) (+53 more)",
        ["chat | long | 1900 wide | DPI 1.25"] = "DIR.Lib#169: overlap: '•' (152.5,230.1 19.5x21.1) is drawn over 'Bash' (60.1,241.8 105x19.5) (+5 more)",
        ["chat | long | 1900 wide | DPI 1.5"] = "DIR.Lib#169: overlap: 'very-' (206.4,258.6 51.4x25.4) is drawn over 'Every page is checked for text drawn ov…' (207.1,272.7 1020.9x25.4) (+15 more)",
        // transcript
        ["transcript | long | 700 wide | DPI 1"] = "DIR.Lib#169: overlap: 'given' (111,-477.3 39.9x16.9) is drawn over 'Tests:' (111,-476.7 43.1x16.9) (+122 more)",
        ["transcript | long | 700 wide | DPI 1.25"] = "DIR.Lib#169: overlap: '•' (149,-527.7 19.5x21.1) is drawn over 'Bash' (60.1,-515.9 105x19.5) (+76 more)",
        ["transcript | long | 700 wide | DPI 1.5"] = "DIR.Lib#169: overlap: 'given' (174.2,-1165.5 61.4x25.4) is drawn over 'Tests:' (174.2,-1164.5 66.3x25.4) (+104 more)",
        ["transcript | long | 900 wide | DPI 1"] = "DIR.Lib#169: overlap: 'ORB204\\' (137.6,-242.2 57.4x16.9) is drawn over 'Every page is checked for text drawn ov…' (138.1,-232.8 393.9x16.9) (+44 more)",
        ["transcript | long | 900 wide | DPI 1.25"] = "DIR.Lib#169: overlap: 'given' (141.7,-821.4 49.1x21.1) is drawn over 'Tests:' (141.7,-820.6 53x21.1) (+103 more)",
        ["transcript | long | 900 wide | DPI 1.5"] = "DIR.Lib#169: overlap: 'worktrees\\' (206.4,-813.2 106.2x25.4) is drawn over 'Every page is checked for text drawn ov…' (207.1,-799.1 590.9x25.4) (+47 more)",
        ["transcript | long | 1300 wide | DPI 1"] = "DIR.Lib#169: overlap: '•' (122,126.4 15.6x16.9) is drawn over 'Bash' (48.1,135.8 84x15.6) (+80 more)",
        ["transcript | long | 1300 wide | DPI 1.25"] = "DIR.Lib#169: overlap: 'Tests\\' (172,-261.8 48x21.1) is drawn over 'Every page is checked for text drawn ov…' (172.6,-250.1 667.4x21.1) (+32 more)",
        ["transcript | long | 1300 wide | DPI 1.5"] = "DIR.Lib#169: overlap: '•' (180.8,-813.2 23.4x25.4) is drawn over 'Bash' (72.1,-799.1 126x23.4) (+77 more)",
        ["transcript | long | 1900 wide | DPI 1.25"] = "DIR.Lib#169: overlap: '•' (152.5,6.1 19.5x21.1) is drawn over 'Bash' (60.1,17.8 105x19.5) (+80 more)",
        ["transcript | long | 1900 wide | DPI 1.5"] = "DIR.Lib#169: overlap: 'very-' (206.4,-318.2 51.4x25.4) is drawn over 'Every page is checked for text drawn ov…' (207.1,-304.1 1020.9x25.4) (+23 more)",
    };
}
