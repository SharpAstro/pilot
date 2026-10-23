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
        ("Ctrl+1 to Ctrl+6", "Home, Sessions, Flights, Background tasks, Fleet and Settings"),
        ("Ctrl+S", "the say bar, to the selected session"),
        ("Ctrl+K", "the command bar (a pilot command)"),
        ("Enter, double-click", "a session's transcript"),
        ("Right-click", "a row's menu"),
        ("Up, Down", "the selection, or a wizard's highlight"),
        ("Escape", "back: out of a transcript, a wizard step, a menu"),
    ];

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

    /// <summary>The header's button.</summary>
    public Layout.Node Button() =>
        Layout.Builder.Text("?", 14f, Bright, TextAlign.Center).Pad(10f, 4f).WAuto().Bg(ButtonBg).BgHover(ButtonHover)
            .Clickable(new HitResult.ButtonHit("about:toggle"), _ => Toggle())
            .WithTooltip("Which pilot this is, where its things are, and the keys");

    /// <summary><paramref name="page"/>, with the card over its top right while it is open.</summary>
    public Layout.Node Over(Layout.Node page) => IsOpen
        ? Layout.Builder.Overlay(page, Layout.Builder.Anchored(Card(), Layout.DockSide.Right, margin: 12f))
        : page;

    private Layout.Node Card()
    {
        var rows = new List<Layout.Node>
        {
            Layout.Builder.HStack(
                    Layout.Builder.Text("pilot", 16f, Bright).WStar(),
                    Layout.Builder.Text("×", 16f, Body, TextAlign.Center).Pad(8f, 0f).WAuto().BgHover(ButtonHover)
                        .Clickable(new HitResult.ButtonHit("about:close"), _ => Close()))
                .RowH(26f),
        };
        foreach (var (label, value) in Facts?.Invoke() ?? [])
        {
            rows.Add(Line(label, value));
        }
        rows.Add(Layout.Builder.Text("Keys", 13f, Bright).RowH(24f));
        foreach (var (keys, what) in Keys)
        {
            rows.Add(Line(keys, what));
        }
        return Layout.Builder.VStack([.. rows]).WithGap(2f).Pad(14f, 10f).Bg(CardBg).WAuto().HAuto();
    }

    private static Layout.Node Line(string label, string value) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(label, 12f, Dim).WFixed(140f),
                Layout.Builder.Text(value, 12f, Body, trim: TextTrim.Start).WAuto())
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
