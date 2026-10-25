using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The header's <b>?</b>: which pilot this window is and where its things are (its own folder, the node it talks to,
/// that node's version, socket and data folder), and the keys. One card, shared by every page, so it reads the same
/// wherever it is opened; the host fills <see cref="Facts"/> from what it knows, the node's own answer included.
/// </summary>
internal sealed class AboutCard
{
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 CardBg = new(0x1f, 0x21, 0x2b, 0xff);
    private static readonly RGBAColor32 ButtonBg = new(0x2c, 0x35, 0x4a, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x3a, 0x46, 0x62, 0xff);

    /// <summary>The keys, as the card lists them.</summary>
    public static readonly IReadOnlyList<(string Keys, string What)> Keys =
    [
        ("Ctrl+1 to Ctrl+8", "Home, Sessions, Flights, Background tasks, Fleet, Settings, Repos and Chat"),
        ("Ctrl+S", "the say bar, to the selected session"),
        ("Enter, double-click", "a session's transcript"),
        ("Right-click", "a row's menu"),
        ("Up, Down", "the selection, or a wizard's highlight"),
        ("Escape", "back: out of a transcript or a flight's page, a wizard step, a menu"),
        ("Alt+Left, Alt+Right", "back and forward through the places the window has been (the mouse's back and forward buttons too)"),
    ];

    /// <summary>
    /// The buttons that are only a drawn mark, as the card draws them beside what they do: every one has its word as a tooltip, and
    /// this list is what keeps a reader who does not know a mark from having to guess. Add a button's mark here when it becomes one.
    /// </summary>
    public static readonly IReadOnlyList<(Layout.IconKind[] Marks, string What)> IconButtons =
    [
        ([Layout.IconKind.Grid, Layout.IconKind.List], "the Flights page's Grid and List views (Canvas and Night are words)"),
        ([Layout.IconKind.Plus], "New flight, in the Flights header"),
        ([Layout.IconKind.CaretRight, Layout.IconKind.CaretDown], "unfold and fold a row's details"),
    ];

    /// <summary>What the mark after a link's text means, as the card says it beside the keys.</summary>
    public static readonly (string Mark, string What) AwayMark = (Followable.Away.Trim(), "a link that opens elsewhere (the browser, a folder), not an action here");

    /// <summary>Whether the card shows, over the page.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>What the card states, a label and a value each: the GUI's version and folder, the node's.</summary>
    public Func<IReadOnlyList<(string Label, string Value)>>? Facts { get; set; }

    /// <summary>Called as the card opens, so the host can ask the node again what it is.</summary>
    public Action? Opening { get; set; }

    public void Toggle()
    {
        IsOpen = !IsOpen;
        if (IsOpen)
        {
            Opening?.Invoke();
        }
    }

    public void Close() => IsOpen = false;

    /// <summary>
    /// The newer pilot this machine has installed than the window runs (<c>2.0.4-beta.297</c>), or null: the node updates
    /// itself and the window runs on from its old binary. While set, a light pulses beside the button on every page.
    /// </summary>
    public Func<string?>? Newer { get; set; }

    /// <summary>Starts the window again on the installed binary: the light's click. Null where the host cannot; the light then only says it.</summary>
    public Action? Restart { get; set; }

    /// <summary>What the light's pulse counts in, milliseconds; a test sets it to see each phase.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    private static readonly RGBAColor32 LightOn = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 LightOff = new(0x5c, 0x48, 0x22, 0xff);
    private static readonly RGBAColor32 LightText = new(0x1a, 0x14, 0x08, 0xff);

    /// <summary>
    /// The header's button, and while a newer pilot is installed the update light before it. The "?" stays a word for want
    /// of a help glyph: SharpAstro/DIR.Lib#151.
    /// </summary>
    /// <summary>The side of the header's square buttons (?, +) and the height of everything else on its row that is a button.</summary>
    public const float HeaderButton = 28f;

    public Layout.Node Button(FlightsPalette? night = null)
    {
        var help = Layout.Builder.Text("?", 14f, night?.Bright ?? Bright, TextAlign.Center, TextAlign.Center).WFixed(HeaderButton).HFixed(HeaderButton)
            .Bg(night?.Button ?? ButtonBg).BgHover(night?.ButtonHover ?? ButtonHover)
            .Follows(new HitResult.ButtonHit("about:toggle"), _ => Toggle())
            .WithTooltip("Which pilot this is, where its things are, and the keys");
        return Newer?.Invoke() is { } newer ? Layout.Builder.HStack(Light(newer), help).WithGap(8f).WAuto() : help;
    }

    /// <summary>Whether the light is lit at this moment: on and off a second each, redrawn at least once a second.</summary>
    internal bool Lit => Clock() / 1000 % 2 == 0;

    // A pill in amber that pulses, saying what is installed; a click restarts the window on it.
    private Layout.Node Light(string newer)
    {
        var lit = Lit;
        var label = newer.LastIndexOf('-') is var dash and >= 0 ? newer[(dash + 1)..] : newer;
        var light = Layout.Builder.Text(Restart is null ? $"{label} installed" : $"Restart for {label}", 13f, lit ? LightText : LightOn, TextAlign.Center)
            .Pad(10f, 0f).WAuto().HFixed(HeaderButton).Bg(lit ? LightOn : LightOff)
            .WithTooltip($"pilot {newer} is installed; this window runs {NodeFeed.ClientVersion}." + (Restart is null ? " Restart it to use the new one." : " A click starts it again on the new one."));
        return Restart is { } restart ? light.Follows(new HitResult.ButtonHit("about:restart"), _ => restart()) : light;
    }

    /// <summary><paramref name="page"/>, with the card over its top right while it is open.</summary>
    public Layout.Node Over(Layout.Node page, FlightsPalette? night = null) => IsOpen
        ? Layout.Builder.Overlay(page, Layout.Builder.Anchored(Card(night), Layout.DockSide.Right, margin: 12f))
        : page;

    private Layout.Node Card(FlightsPalette? night)
    {
        var bright = night?.Bright ?? Bright;
        var body = night?.Body ?? Body;
        var hover = night?.ButtonHover ?? ButtonHover;
        var rows = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                    Layout.Builder.Text("pilot", 16f, bright).WStar(),
                    // The × stays a typeset rune for want of a close glyph: SharpAstro/DIR.Lib#151.
                    Layout.Builder.Text("×", 16f, body, TextAlign.Center).Pad(8f, 0f).WAuto().BgHover(hover)
                        .Follows(new HitResult.ButtonHit("about:close"), _ => Close()))
                .RowH(26f),
        };
        foreach (var (label, value) in Facts?.Invoke() ?? [])
        {
            rows.Add(Line(label, value, night));
        }
        rows.Add(Layout.Builder.Text("Keys", 13f, bright).RowH(24f));
        foreach (var (keys, what) in Keys)
        {
            rows.Add(Line(keys, what, night));
        }
        rows.Add(Line(AwayMark.Mark, AwayMark.What, night));
        rows.Add(Layout.Builder.Text("Icon buttons (point at one for its word)", 13f, bright).RowH(24f));
        foreach (var (marks, what) in IconButtons)
        {
            rows.Add(IconLine(marks, what, night));
        }
        return Layout.Builder.VStack([.. rows]).WithGap(2f).Pad(14f, 10f).Bg(night?.MenuBg ?? CardBg).WAuto().HAuto();
    }

    private static Layout.Node IconLine(Layout.IconKind[] marks, string value, FlightsPalette? night) =>
        Layout.Builder.HStack(
                Layout.Builder.HStack([.. marks.Select(m => Layout.Builder.Icon(m, 12f, night?.Bright ?? Bright).WAuto())]).WithGap(8f).WFixed(140f),
                Layout.Builder.Text(value, 12f, night?.Body ?? Body, trim: TextTrim.Start).WAuto())
            .WithGap(10f)
            .RowH(20f);

    private static Layout.Node Line(string label, string value, FlightsPalette? night) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(label, 12f, night?.Dim ?? Dim).WFixed(140f),
                Layout.Builder.Text(value, 12f, night?.Body ?? Body, trim: TextTrim.Start).WAuto())
            .WithGap(10f)
            .RowH(20f);
}

/// <summary>What the ? card states, for either host: this program and its folder, and the node as it answered.</summary>
internal static class AboutFacts
{
    /// <param name="program">The program's name: pilot-gui, pilot-tui.</param>
    /// <param name="node">The node's answer to <c>GET /api/v1/node</c>; null while it has not answered.</param>
    /// <param name="where">Where this program looks for the node: its socket, or the host:port on the LAN.</param>
    public static IReadOnlyList<(string Label, string Value)> For(string program, Pilot.Contracts.NodeInfo? node, string where) =>
    [
        (program, NodeFeed.ClientVersion),
        ("its folder", Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)),
        ("node", node is null ? $"not answering on {where}" : $"{node.Version.Split('+')[0]} · {node.MachinePrefix} · pid {node.ProcessId}"),
        ("account", node?.Account?.Line() ?? (node is null ? "—" : "no login on record")),
        ("node socket", node?.SocketPath ?? where),
        ("node data", node?.DataFolder ?? "—"),
    ];
}
