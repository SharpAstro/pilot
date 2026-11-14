using DIR.Lib;

namespace Pilot.Ui;

/// <summary>
/// The header's <b>?</b>: which pilot this window is and where its things are (its own folder, the node it talks to,
/// that node's version, socket and data folder), and the keys. One card, shared by every page, so it reads the same
/// wherever it is opened; the host fills <see cref="Facts"/> from what it knows, the node's own answer included.
/// </summary>
internal sealed class AboutCard
{
    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Body => Palette.Current.Body;
    private static RGBAColor32 Dim => Palette.Current.Dim;
    private static RGBAColor32 CardBg => Palette.Current.MenuBg;
    private static RGBAColor32 ButtonBg => Palette.Current.Action;
    private static RGBAColor32 ButtonHover => Palette.Current.ActionHover;

    /// <summary>The keys, as the card lists them.</summary>
    public static readonly IReadOnlyList<(string Keys, string What)> Keys =
    [
        ("Ctrl+1 to Ctrl+9", "Home, Chat, Sessions, Flights, Repos, Background tasks, Fleet, Settings and Ideas: the rail, top to bottom"),
        ("Ctrl+S", "the say bar, to the selected session"),
        ("Enter, double-click", "a session's transcript"),
        ("Right-click", "a row's menu"),
        ("Up, Down", "the selection, or a wizard's highlight"),
        ("Escape", "up a level: from a transcript or a flight's page to its list; also out of a wizard step or a menu"),
        ("Alt+Left, Alt+Right", "back and forward through the places the window has been (the mouse's back and forward buttons too)"),
    ];

    /// <summary>
    /// The most text a row of <see cref="TerminalKeys"/> holds: a terminal sets every glyph one cell wide, and a longer row is cut at its
    /// start, losing what it begins with. The card's value column is narrowest at 120 columns, where the cards panel first stands beside
    /// the page: the page is 82 cells, the card 78, and less its padding and the key column (140 units, 18 cells) the value gets 55. So
    /// no row of the terminal's list is cut at any width the cards panel shows at, nor without it from 100 columns up.
    /// </summary>
    public const int TerminalLineChars = 55;

    /// <summary>
    /// The page keys as the terminal's card lists them: every key of <see cref="TerminalRail.Keys"/> with its page, in the bar's order,
    /// broken into rows of at most <see cref="TerminalLineChars"/>; each row's keys column names the keys on that row. Built from the
    /// bar's own list, so it cannot drift from the bar.
    /// </summary>
    public static IReadOnlyList<(string Keys, string What)> TerminalPageKeys { get; } = PageRows();

    /// <summary>
    /// The keys pilot-tui handles (Program.cs's Handle, TerminalRail.Handle, the chrome's Alt+Left, Alt+Right and Escape): no Ctrl+digit,
    /// rail, right-click or mouse buttons. The page keys come first, from <see cref="TerminalPageKeys"/>.
    /// </summary>
    public static IReadOnlyList<(string Keys, string What)> TerminalKeys { get; } =
    [
        .. TerminalPageKeys,
        ("Ctrl+S", "the say bar, to the selected session"),
        ("Ctrl+T", "the chat with the watchtower"),
        ("Enter", "a session's transcript"),
        ("Up, Down", "the selection, or a wizard's highlight"),
        ("Escape", "up a level, or out of a wizard step or a menu"),
        // Not "Backspace, Alt+Left" as the GUI writes its pair: the key column is 18 cells, and a longer key is cut. One row, not two:
        // a 24-row terminal has room for this list and nothing more.
        ("Backspace", "back, as Alt+Left; Alt+Right goes forward"),
        ("q, Ctrl+C", "quit"),
        ("Left click", "a row, a button or a page on the bar"),
    ];

    // The bar's entries as "1 Home", in its order, filled into rows no longer than TerminalLineChars; the first row says what they are.
    private static List<(string Keys, string What)> PageRows()
    {
        var rows = new List<(string Keys, string What)>();
        var keys = new List<char>();
        var line = "pages: ";
        foreach (var (_, label, key) in TerminalRail.Keys)
        {
            var entry = $"{key} {label}";
            if (keys.Count > 0 && line.Length + 2 + entry.Length > TerminalLineChars)
            {
                rows.Add((PageKeys(keys), line));
                (keys, line) = ([], "");
            }
            line += keys.Count > 0 ? $", {entry}" : entry;
            keys.Add(key);
        }
        rows.Add((PageKeys(keys), line));
        return rows;
    }

    /// <summary>
    /// A row's page keys in the bar's order, as its text names them (Pull requests is 9, sixth), with a run of three or more digits
    /// counting up written as one range: "1 to 5", then "9, 6, 7".
    /// </summary>
    private static string PageKeys(IReadOnlyList<char> keys)
    {
        var parts = new List<string>();
        for (var i = 0; i < keys.Count;)
        {
            var end = i;
            while (end + 1 < keys.Count && char.IsAsciiDigit(keys[end]) && keys[end + 1] == keys[end] + 1)
            {
                end++;
            }
            parts.Add(end - i >= 2 ? $"{keys[i]} to {keys[end]}" : string.Join(", ", Enumerable.Range(i, end - i + 1).Select(k => keys[k])));
            i = end + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>The keys this host's card lists: the GUI's by default, pilot-tui sets <see cref="TerminalKeys"/>.</summary>
    public IReadOnlyList<(string Keys, string What)> KeyList { get; set; } = Keys;

    /// <summary>
    /// Whether the keys come straight under the card's header, before the facts (which then get a heading of their own); pilot-tui sets it.
    /// The card drops the rows it has no room for from its end, and a terminal can be shorter than the card: there the keys matter most,
    /// so a short terminal keeps them and gives up the facts and then the icon list. The GUI's card keeps the facts first.
    /// </summary>
    public bool KeysFirst { get; set; }

    /// <summary>The heading over the facts when <see cref="KeysFirst"/> puts them below the keys.</summary>
    public const string FactsHeading = "This pilot and its node";

    /// <summary>
    /// The buttons that are only a drawn mark, as the card draws them beside what they do: every one has its word as a tooltip, and
    /// this list is what keeps a reader who does not know a mark from having to guess. Add a button's mark here when it becomes one.
    /// </summary>
    public static readonly IReadOnlyList<(Layout.IconKind[] Marks, string What)> IconButtons =
    [
        ([Layout.IconKind.Grid, Layout.IconKind.List], "the Flights page's Grid and List views (Canvas is a word; Night is the header's own switch)"),
        ([Layout.IconKind.Plus], "New flight, in the Flights header"),
        ([Layout.IconKind.CaretRight, Layout.IconKind.CaretDown], "unfold and fold a row's details"),
    ];

    /// <summary>The heading over <see cref="IconButtons"/>.</summary>
    public const string IconButtonsHeading = "Icon buttons (point at one for its word)";

    /// <summary>
    /// What the mark after a link's text means, as the card says it beside the keys: "opens elsewhere" is the card's word for it, which
    /// CursorAndLinksTests reads, and the row holds <see cref="TerminalLineChars"/> or fewer, so the terminal's card shows it whole too.
    /// </summary>
    public static readonly (string Mark, string What) AwayMark = (Followable.Away.Trim(), "a link that opens elsewhere (the browser, a folder)");

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

    private static RGBAColor32 LightOn => Palette.Current.Held;
    private static RGBAColor32 LightOff => Palette.Current.LightOff;
    private static RGBAColor32 LightText => Palette.Current.LightText;

    /// <summary>
    /// The header's button, and while a newer pilot is installed the update light before it. The "?" stays a word for want
    /// of a help glyph: SharpAstro/DIR.Lib#151.
    /// </summary>
    /// <summary>The side of the header's square buttons (?, +) and the height of everything else on its row that is a button.</summary>
    public const float HeaderButton = 28f;

    public Layout.Node Button(Palette? night = null)
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

    /// <summary>
    /// <paramref name="page"/>, with the card at its top right while it is open, hanging under the top bar's row of buttons.
    /// Pinned to the window's corner it lay over that row and covered most of the ?, whose click then landed on the card's ×
    /// (DE1, 2026-10-26): the same toggle by luck. Under the row, the ? stays itself, and closes it as it opened it.
    /// <para>
    /// The overlay and its top layer stretch, as the page does. The engine arranges an overlay's layers in the overlay's own rect, and a
    /// stack places a child that does not stretch at its measured size: so as the root of a window the card had the whole window, but
    /// as pilot-tui's page inside its frame (under the page bar, beside the cards panel) its anchor got about one row, the card's fill
    /// was arranged 0 rows tall and its rows were drawn over the page with nothing behind them (pilot #1027, 2026-11-14).
    /// </para>
    /// </summary>
    public Layout.Node Over(Layout.Node page, Palette? night = null) => IsOpen
        ? Layout.Builder.Overlay(page, Layout.Builder.VStack(
            Layout.Builder.Spacer().WStar().HFixed(TopBar.ButtonsBottom),
            Layout.Builder.Anchored(Card(night), Layout.DockSide.Right, margin: 12f).WStar().HStar()).Stretch()).Stretch()
        : page;

    /// <summary>
    /// The card's width, a little over its widest row (the keys list): fixed, so the engine clamps it to the window on a narrow one and the
    /// values, which trim at their start, give way. Left to measure itself, its rows kept their natural width past the window's edge.
    /// </summary>
    public const float CardWidth = 840f;

    private Layout.Node Card(Palette? night)
    {
        var bright = night?.Bright ?? Bright;
        var body = night?.Body ?? Body;
        var hover = night?.ButtonHover ?? ButtonHover;
        var rows = new List<Layout.Node>
        {
            // A Dock measures as wide as the room it is given, where a stack measures to its content: the column's rows
            // stretch to its widest measured row, and without this one they stopped at the widest heading's text, about
            // 40 cells, cutting every value (pilot #1027).
            Layout.Builder.Dock(Layout.Builder.HStack(
                    Layout.Builder.Text("pilot", 16f, bright).WStar(),
                    // The × stays a typeset rune for want of a close glyph: SharpAstro/DIR.Lib#151.
                    Layout.Builder.Text("×", 16f, body, TextAlign.Center).Pad(8f, 0f).WAuto().BgHover(hover)
                        .Follows(new HitResult.ButtonHit("about:close"), _ => Close()))
                .Stretch())
                .RowH(26f),
        };
        var facts = (Facts?.Invoke() ?? []).Select(f => Line(f.Label, f.Value, night)).ToList();
        if (!KeysFirst)
        {
            rows.AddRange(facts);
        }
        rows.Add(Layout.Builder.Text("Keys", 13f, bright).RowH(24f));
        foreach (var (keys, what) in KeyList)
        {
            rows.Add(Line(keys, what, night));
        }
        rows.Add(Line(AwayMark.Mark, AwayMark.What, night));
        if (KeysFirst && facts.Count > 0)
        {
            rows.Add(Layout.Builder.Text(FactsHeading, 13f, bright).RowH(24f));
            rows.AddRange(facts);
        }
        rows.Add(Layout.Builder.Text(IconButtonsHeading, 13f, bright).RowH(24f));
        foreach (var (marks, what) in IconButtons)
        {
            rows.Add(IconLine(marks, what, night));
        }
        // One column that drops the rows it has no room for, where a stack would draw them on past its own height: in a terminal shorter
        // than the card, whose fill is cut to the room it has, its last rows hung below that fill over the page with nothing behind them
        // (pilot #1027). KeysFirst decides what such a terminal keeps. Each row stretches across the card, no wider than its inside.
        return Layout.Builder.WrapV([.. rows.Select(r => r.W(Layout.Sizing.Star(max: CardWidth - 28f)))]).WithGap(2f).WithMaxLines(1)
            .Pad(14f, 10f).Bg(night?.MenuBg ?? CardBg).WFixed(CardWidth).HAuto();
    }

    private static Layout.Node IconLine(Layout.IconKind[] marks, string value, Palette? night) =>
        Layout.Builder.HStack(
                Layout.Builder.HStack([.. marks.Select(m => Layout.Builder.Icon(m, 12f, night?.Bright ?? Bright).WAuto())]).WithGap(8f).WFixed(140f),
                Layout.Builder.Text(value, 12f, night?.Body ?? Body, trim: TextTrim.Start).WStar())
            .WithGap(10f)
            .RowH(20f);

    private static Layout.Node Line(string label, string value, Palette? night) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(label, 12f, night?.Dim ?? Dim).WFixed(140f),
                Layout.Builder.Text(value, 12f, night?.Body ?? Body, trim: TextTrim.Start).WStar())
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
