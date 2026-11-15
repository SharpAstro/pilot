using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Every pilot-tui page, built as Program.cs builds its frame (<see cref="TerminalFrame.Build"/>), painted into a cell buffer through Console.Lib's
/// <see cref="CellWireframe"/> and judged by <see cref="CellWireframe.CheckCells"/> at 80x24, 100x30, 160x44 and 250x60, over PageData's "long" and
/// "many" states. Cases: each page, the help card open, an open card (shown beside the page from 120 columns), and Chat with a long transcript
/// and a typed multi-line message.
/// <para>The findings on main are the <see cref="Baseline"/>: a case fails on a finding that is not listed and on a listed entry that no longer
/// occurs, so the list can only shrink.</para>
/// </summary>
public static class TerminalWireframeSweepTests
{
    internal static readonly string[] Cases =
    [
        "home", "sessions", "flights", "repos", "pull requests", "jobs", "fleet", "ideas", "spend", "settings", "chat",
        "help card", "open card", "chat long typed", "tooltip open",
    ];

    internal static IEnumerable<TheoryDataRow<string, string, int, int>> RowsOf(int columns, int rows, string state) =>
        Cases.Select(c => new TheoryDataRow<string, string, int, int>(c, state, columns, rows) { TestDisplayName = Key(c, state, columns, rows) });

    internal static string Key(string page, string state, int columns, int rows) => $"{page} | {state} | {columns}x{rows}";

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public void SetCaret(int column, int row, CaretStyle style) { }
        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    // Built once a state: PageData.For is a few dozen ms, and the sweep asks for it in every case.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<PageData>> Datas = new();
    private static PageData Data(string state) => Datas.GetOrAdd(state, s => new Lazy<PageData>(() => PageData.For(s))).Value;

    /// <summary>The frame Program.cs would paint for the case, arranged, cropped and recorded.</summary>
    internal static async Task<CellWireframeFrame> RecordCase(string name, string state, int columns, int rows)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var data = Data(state);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel)) { FontPath = SessionsCapture.FontPath };
        chrome.Chat.Cells = true;
        chrome.WithoutScrollBars();
        chrome.Flights.CanCommand = true;
        chrome.Jobs.CanCommand = true;
        chrome.Cards.CanTell = true;
        chrome.Nodes.Choices = data.Nodes;
        chrome.About.Facts = () => [("pilot-tui", "2.1.1")];
        chrome.About.KeyList = AboutCard.TerminalKeys;
        chrome.About.KeysFirst = true;
        var page = name switch
        {
            "help card" or "open card" or "tooltip open" => PilotPage.Home,
            "chat long typed" => PilotPage.Chat,
            _ => PageTextOverlapTests.PageOf(name),
        };
        if (page == PilotPage.Settings && PageData.SettingsFor(state) is { } settingsPage)
        {
            chrome.Settings.Load = _ => Task.FromResult(settingsPage);
            chrome.Settings.LoadRepos ??= _ => Task.FromResult<RepoInfo[]>(data.WizardRepos.Length > 0 ? data.WizardRepos : [new RepoInfo { Name = "pilot", Path = @"C:\work\pilot" }]);
        }
        chrome.Show(page);
        var cards = name == "open card" ? data.CardsWithDetail : data.Cards;
        chrome.Cards.OnChatPage = page == PilotPage.Chat;
        var cardsBeside = columns >= TerminalFrame.CardsPanelColumns && (cards.HasAny || chrome.Cards.CanTell && !chrome.Cards.OnChatPage);
        chrome.Chat.CellColumns = TerminalFrame.PageColumns(columns, cardsBeside);
        var width = columns * 8f;
        if (name == "help card")
        {
            chrome.About.Toggle();
        }
        if (name == "open card")
        {
            PageTextOverlapTests.OpenEveryDetail(chrome.Cards);
        }
        Layout.Node tree;
        switch (page)
        {
            case PilotPage.Chat:
                tree = await PageTextOverlapTests.OpenChat(chrome, data);
                if (name == "chat long typed")
                {
                    chrome.Chat.Input.InsertText("a typed message that goes on for a good while, past the width of the box\nand a second line\nand a third, with a path " + PageData.LongPath);
                    tree = chrome.Chat.Tree();
                }
                break;
            case PilotPage.Sessions: tree = chrome.Sessions.Tree(data.Sessions); break;
            case PilotPage.Flights: tree = chrome.Flights.Tree(data.Flights); break;
            case PilotPage.Repos: chrome.Repos.ExpandAll(data.Repos); tree = chrome.Repos.Tree(data.Repos); break;
            case PilotPage.PullRequests: tree = chrome.PullRequests.Tree(data.PullRequests, width); break;
            case PilotPage.Jobs: tree = chrome.Jobs.Tree(data.Jobs); break;
            case PilotPage.Fleet: tree = chrome.Fleet.Tree(data.Fleet); break;
            case PilotPage.Ideas: tree = chrome.Ideas.Tree(PageTextOverlapTests.IdeasFor(state)); break;
            case PilotPage.Spend: tree = chrome.Spend.Tree(data.Spend); break;
            case PilotPage.Settings:
                // The page is read from the node's answer on its own task: the first Tree starts the read, and Changed says the page is in.
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                chrome.Settings.Changed = () =>
                {
                    if (chrome.Settings.Page is not null)
                    {
                        loaded.TrySetResult();
                    }
                };
                // The read goes on only while the page is drawn "now" (SettingsView.Unseen), so this first draw is at the real time.
                chrome.Settings.Tree(DateTimeOffset.UtcNow, width);
                await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                tree = chrome.Settings.Tree(PageData.Now, width);
                break;
            default: tree = chrome.Home.Tree(new HomeScreen(data.Flights, data.Sessions, data.Jobs, cards, data.Machines)); break;
        }
        var rail = TerminalRail.Tree(page, GlyphMode.Emoji, data.Jobs, null, PageData.Now, _ => { }, null, columns);
        var cardsTree = cardsBeside ? chrome.Cards.Tree(cards) : null;
        var arranged = TerminalFrame.Build(rail, tree, cardsTree, columns, rows, CellMeasureContext.PixelAuthored);
        if (name == "tooltip open")
        {
            // A hovered node's tooltip is due: the first node that carries one (a cut card title when the panel stands beside the page,
            // else a rail or page node), anchored on its cells as CellInputRouter anchors it.
            var hovered = arranged.FirstOrDefault(a => a.Node.Tooltip is { Length: > 0 });
            hovered.Node.ShouldNotBeNull($"{name} | {columns}x{rows}: no node carries a tooltip, so no tooltip was opened");
            var b = hovered.Bounds;
            arranged = TerminalFrame.Build(rail, tree, cardsTree, columns, rows, CellMeasureContext.PixelAuthored,
                new TooltipRequest(hovered.Node.Tooltip!, new RectF32(b.X, b.Y, b.Width, b.Height)));
        }
        return Record(arranged, columns, rows);
    }

    internal static CellWireframeFrame Record(ImmutableArray<Layout.ArrangedNode<int>> arranged, int columns, int rows)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        return CellWireframe.RecordCells(new BufferedViewport(buffer, columns, rows), arranged, null, false);
    }

    internal static CellWireframeResult Check(CellWireframeFrame frame) => CellWireframe.CheckCells(frame, new CellWireframeOptions());

    /// <summary>A finding as the baseline lists it: the rule and the message it carries.</summary>
    internal static IEnumerable<(string Rule, string What)> Findings(CellWireframeResult result) =>
        result.Findings.Select(f => (f.Rule.ToString(), f.Message)).Concat(result.CellFindings.Select(f => (f.Rule.ToString(), f.Message)));

    internal static async Task Run(string page, string state, int columns, int rows)
    {
        var key = Key(page, state, columns, rows);
        var frame = await RecordCase(page, state, columns, rows);
        frame.Frame.Texts.IsEmpty.ShouldBeFalse($"{key}: the frame recorded no text (a frame painted outside CellLayout.Paint records empty and would pass unjudged)");
        var result = Check(frame);
        var expected = Baseline.Where(b => b.Case == key).ToList();
        var unknown = new List<(string Rule, string What)>();
        foreach (var f in Findings(result))
        {
            var at = expected.FindIndex(b => b.Rule == f.Rule && f.What.Contains(b.What, StringComparison.Ordinal));
            if (at < 0)
            {
                unknown.Add(f);
            }
            else
            {
                expected.RemoveAt(at);
            }
        }
        var message = new System.Text.StringBuilder();
        if (unknown.Count > 0)
        {
            message.AppendLine($"{key}: {unknown.Count} finding(s) not in the baseline:");
            foreach (var u in unknown) { message.AppendLine($"  [{u.Rule}] {u.What}"); }
            message.AppendLine(CellWireframe.Describe(result));
        }
        if (expected.Count > 0)
        {
            message.AppendLine($"{key}: {expected.Count} baseline entr(ies) no longer occur: remove them from TerminalWireframeSweepTests.Baseline:");
            foreach (var e in expected) { message.AppendLine($"  [{e.Rule}] {e.What}"); }
        }
        message.Length.ShouldBe(0, message.ToString());
    }

    /// <summary>
    /// The findings on main when the sweep was written, grouped by cause: the case, the rule, and a part of the finding's message.
    /// Later legs empty this list; nothing is added to it. Console.Lib 5.22's EmptyRows and SolidStrip raised the entries below (PIL1075),
    /// each tagged with the issue that owns it: Console.Lib #77 (SolidStrip on an empty TextInput), #78 (EmptyRows on a scroll viewport),
    /// pilot #1077 (rows two cells tall).
    /// </summary>
    internal static readonly (string Case, string Rule, string What)[] Baseline =
    [
        ("pull requests | long | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 11 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 13 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 21 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("settings | many | 80x24", "SolidStrip", "a filled node one row tall and 43 cells wide holds nothing and is painted solid; a rule should be a thin line, not a bar"), // Console.Lib #77
        ("pull requests | many | 80x24", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 12 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 80x24", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 14 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 80x24", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 18 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 80x24", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 22 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("settings | long | 100x30", "SolidStrip", "a filled node one row tall and 63 cells wide holds nothing and is painted solid; a rule should be a thin line, not a bar"), // Console.Lib #77
        ("jobs | long | 100x30", "EmptyRows", "a filled container is arranged 18 rows tall but its content sits on row 12 only; 17 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // Console.Lib #78
        ("settings | many | 250x60", "SolidStrip", "a filled node one row tall and 175 cells wide holds nothing and is painted solid; a rule should be a thin line, not a bar"), // Console.Lib #77
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 11 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 13 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 17 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 21 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 25 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 33 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 37 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 41 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 45 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 53 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 57 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("jobs | many | 250x60", "EmptyRows", "a filled container is arranged 18 rows tall but its content sits on row 42 only; 17 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // Console.Lib #78
        ("jobs | long | 160x44", "EmptyRows", "a filled container is arranged 18 rows tall but its content sits on row 26 only; 17 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // Console.Lib #78
        ("settings | long | 160x44", "SolidStrip", "a filled node one row tall and 85 cells wide holds nothing and is painted solid; a rule should be a thin line, not a bar"), // Console.Lib #77
        ("pull requests | long | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 11 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 13 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 21 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 29 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("jobs | long | 250x60", "EmptyRows", "a filled container is arranged 18 rows tall but its content sits on row 42 only; 17 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // Console.Lib #78
        ("pull requests | long | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 11 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 13 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 21 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 250x60", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 29 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("settings | long | 250x60", "SolidStrip", "a filled node one row tall and 175 cells wide holds nothing and is painted solid; a rule should be a thin line, not a bar"), // Console.Lib #77
        ("settings | many | 100x30", "SolidStrip", "a filled node one row tall and 63 cells wide holds nothing and is painted solid; a rule should be a thin line, not a bar"), // Console.Lib #77
        ("pull requests | many | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 12 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 14 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 18 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 22 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 26 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("settings | many | 160x44", "SolidStrip", "a filled node one row tall and 85 cells wide holds nothing and is painted solid; a rule should be a thin line, not a bar"), // Console.Lib #77
        ("pull requests | many | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 12 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 14 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 18 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 22 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 26 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 34 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 38 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | many | 160x44", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 42 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("jobs | many | 160x44", "EmptyRows", "a filled container is arranged 18 rows tall but its content sits on row 26 only; 17 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // Console.Lib #78
        ("settings | long | 80x24", "SolidStrip", "a filled node one row tall and 43 cells wide holds nothing and is painted solid; a rule should be a thin line, not a bar"), // Console.Lib #77
        ("pull requests | long | 80x24", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 12 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 80x24", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 14 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
        ("pull requests | long | 80x24", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 22 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // pilot #1077
    ];
}

// One class a size and state, so xunit runs them in parallel.

public sealed class TerminalWireframe80Long { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(80, 24, "long"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframe80Many { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(80, 24, "many"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframe100Long { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(100, 30, "long"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframe100Many { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(100, 30, "many"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframe160Long { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(160, 44, "long"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframe160Many { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(160, 44, "many"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframe250Long { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(250, 60, "long"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframe250Many { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(250, 60, "many"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }

/// <summary>The sweep's own tests: the defects that were fixed, built again, fail the same check.</summary>
public sealed class TerminalWireframeRegressionTests
{
    private static string Describe(CellWireframeResult r) => string.Join('\n', TerminalWireframeSweepTests.Findings(r).Select(f => $"[{f.Rule}] {f.What}")) + "\n" + CellWireframe.Describe(r) + "\n" + _tree;

    private static Layout.Node Page() => Layout.Builder.VStack(
        Layout.Builder.Text("A page title", 14f, Palette.Current.Bright).WStar().RowH(16f),
        Layout.Builder.Text("A line of the page body", 14f, Palette.Current.Bright).WStar().RowH(16f)).Bg(Palette.Current.Ground).WStar().HStar();

    [ThreadStatic] private static string? _tree;
    [ThreadStatic] private static ImmutableArray<Layout.ArrangedNode<int>> _arranged;

    private static CellWireframeResult Judge(Layout.Node rail, Layout.Node page, int columns = 80, int rows = 24)
    {
        var arranged = TerminalFrame.Build(rail, page, null, columns, rows, CellMeasureContext.PixelAuthored);
        _arranged = arranged;
        _tree = string.Join('\n', arranged.Select(a => $"{new string(' ', a.Depth)}{a.Bounds.X},{a.Bounds.Y} {a.Bounds.Width}x{a.Bounds.Height} {((a.Node as Layout.Node.Leaf)?.Content as Layout.Content.Text)?.Value}"));
        return TerminalWireframeSweepTests.Check(TerminalWireframeSweepTests.Record(arranged, columns, rows));
    }

    private static Layout.Node Entries() => Layout.Builder.HStack(
        Layout.Builder.Text("Home", 14f, Palette.Current.Bright), Layout.Builder.Text("Sessions", 14f, Palette.Current.Bright));

    // CellWireframe judges text and fills, not a container or a text-less strip (SharpAstro/Console.Lib#69 and #70), so these two assert on the
    // arranged bounds, which fail on the defect now. Once those issues ship, add the CellWireframe finding here as well.
    [Fact]
    public void ThePageBarGivenHFixedAndStretchIsCaught()
    {
        // PIL1027 (the flight that fixed it was PIL1032): Stretch() sets the height to Star as well and overwrote the HFixed, so the bar took the screen.
        var rail = Entries().HFixed(16f).Stretch().Bg(Palette.Current.Ground);
        var result = Judge(rail, Page());
        var total = _arranged.Max(a => a.Bounds.Y + a.Bounds.Height);
        var bar = _arranged.First(a => ReferenceEquals(a.Node, rail));
        // The defect's signature: the bar takes a quarter of the screen or more, though one row was declared. TheRealPageBarIsOneRowTall holds the real bar to the opposite.
        (bar.Bounds.Height * 4).ShouldBeGreaterThanOrEqualTo(total, Describe(result));
    }

    [Fact]
    public void TheRealPageBarIsOneRowTall()
    {
        var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, PageData.For("long").Jobs, null, PageData.Now, _ => { }, null, 80);
        Judge(rail, Page());
        var total = _arranged.Max(a => a.Bounds.Y + a.Bounds.Height);
        var bar = _arranged.First(a => ReferenceEquals(a.Node, rail));
        (bar.Bounds.Height * 4).ShouldBeLessThan(total, _tree);
    }

    [Fact]
    public void TheComposerRulesDrawnAsGreyBarsAreCaught()
    {
        // PIL1049: the rule was a Spacer with a fill on a 16-unit row: a grey bar, no text on it.
        var composer = Layout.Builder.VStack(
            Layout.Builder.Spacer().Bg(Palette.Current.Faint).WStar().RowH(16f),
            Layout.Builder.Text("❯ Message the fleet chat", 14f, Palette.Current.Bright).WStar().RowH(16f),
            Layout.Builder.Spacer().Bg(Palette.Current.Faint).WStar().RowH(16f)).WStar().HAuto();
        var result = Judge(Entries().RowH(16f), Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), composer).Bg(Palette.Current.Ground).WStar().HStar());
        var bars = _arranged.Count(a => a.Node is Layout.Node.Leaf { Content: not Layout.Content.Text } && a.Bounds.Height <= 20);
        // The defect's signature: text-less one-row leaves (the filled spacers) in the page. The real composer's rules are text (ChatView.Rule),.
        bars.ShouldBeGreaterThan(0, Describe(result));
    }

    [Fact]
    public void AOneLineRowGivenTwoRowsOfHeightIsCaught()
    {
        // A row whose container is taller than its one line: the text leaf stretches to it, so the second row is empty inside the fill.
        // PixelAuthored cells are about 20 units high, so 28 units still arrange as one row; 32 is the first height that gives two.
        var row = Layout.Builder.HStack(Layout.Builder.Text("A one-line row given too much height", 14f, Palette.Current.Bright).WStar().HStar().Bg(Palette.Current.Faint)).WStar().HFixed(32f).Bg(Palette.Current.Faint);
        var result = Judge(Entries().RowH(16f), Layout.Builder.VStack(row, Page()).Bg(Palette.Current.Ground).WStar().HStar());
        TerminalWireframeSweepTests.Findings(result).Any(f => f.Rule == nameof(CellWireframeRules.DoubledRow)).ShouldBeTrue(Describe(result));
    }
}
