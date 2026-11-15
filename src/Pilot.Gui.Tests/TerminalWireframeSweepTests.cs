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
        "help card", "open card", "chat long typed",
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
        chrome.Flights.CanCommand = true;
        chrome.Jobs.CanCommand = true;
        chrome.Cards.CanTell = true;
        chrome.Nodes.Choices = data.Nodes;
        chrome.About.Facts = () => [("pilot-tui", "2.1.1")];
        chrome.About.KeyList = AboutCard.TerminalKeys;
        chrome.About.KeysFirst = true;
        var page = name switch
        {
            "help card" or "open card" => PilotPage.Home,
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
        var arranged = TerminalFrame.Build(rail, tree, cardsBeside ? chrome.Cards.Tree(cards) : null, columns, rows, CellMeasureContext.PixelAuthored);
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

    private static readonly Lock DumpLock = new();

    internal static async Task Run(string page, string state, int columns, int rows)
    {
        var key = Key(page, state, columns, rows);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frame = await RecordCase(page, state, columns, rows);
        var recorded = watch.ElapsedMilliseconds;
        frame.Frame.Texts.IsEmpty.ShouldBeFalse($"{key}: the frame recorded no text (a frame painted outside CellLayout.Paint records empty and would pass unjudged)");
        var result = Check(frame);
        if (@"C:\Users\SebastianGodelet\.claude\jobs\028da901\tmp\dump.txt" is { Length: > 0 } dump)
        {
            lock (DumpLock)
            {
                File.AppendAllText(dump, key + "\n" + CellWireframe.Describe(result) + "\n");
                File.AppendAllLines(dump, [$"{key}	TIME	record {recorded} ms, check {watch.ElapsedMilliseconds - recorded} ms"]);
                File.AppendAllLines(dump, Findings(result).Select(f => $"{key}\t{f.Rule}\t{f.What}"));
            }
        }
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
    /// Later legs empty this list; nothing is added to it.
    /// </summary>
    internal static readonly (string Case, string Rule, string What)[] Baseline =
    [
        // A text leaf a cell or more short of the room its row has: cut while free cells remain to its right
        ("fleet | long | 100x30", "CutWithRoom", "text is cut (98 of 117 cells)"),
        ("fleet | long | 80x24", "CutWithRoom", "text is cut (78 of 117 cells)"),
        ("fleet | many | 100x30", "CutWithRoom", "text is cut (98 of 117 cells)"),
        ("fleet | many | 80x24", "CutWithRoom", "text is cut (78 of 117 cells)"),
        ("flights | long | 100x30", "CutWithRoom", "text is cut (98 of 287 cells)"),
        ("flights | long | 160x44", "CutWithRoom", "text is cut (120 of 287 cells)"),
        ("flights | long | 250x60", "CutWithRoom", "text is cut (210 of 287 cells)"),
        ("flights | long | 80x24", "CutWithRoom", "text is cut (78 of 287 cells)"),
        ("help card | long | 100x30", "CutWithRoom", "text is cut (98 of 287 cells)"),
        ("help card | long | 160x44", "CutWithRoom", "text is cut (120 of 287 cells)"),
        ("help card | long | 250x60", "CutWithRoom", "text is cut (210 of 287 cells)"),
        ("help card | long | 80x24", "CutWithRoom", "text is cut (78 of 287 cells)"),
        ("home | long | 100x30", "CutWithRoom", "text is cut (98 of 287 cells)"),
        ("home | long | 160x44", "CutWithRoom", "text is cut (120 of 287 cells)"),
        ("home | long | 250x60", "CutWithRoom", "text is cut (210 of 287 cells)"),
        ("home | long | 80x24", "CutWithRoom", "text is cut (78 of 287 cells)"),
        ("jobs | long | 100x30", "CutWithRoom", "text is cut (46 of 47 cells)"),
        ("jobs | long | 100x30", "CutWithRoom", "text is cut (98 of 147 cells)"),
        ("jobs | long | 160x44", "CutWithRoom", "text is cut (120 of 147 cells)"),
        ("jobs | long | 80x24", "CutWithRoom", "text is cut (78 of 147 cells)"),
        ("open card | long | 100x30", "CutWithRoom", "text is cut (98 of 287 cells)"),
        ("open card | long | 160x44", "CutWithRoom", "text is cut (120 of 287 cells)"),
        ("open card | long | 250x60", "CutWithRoom", "text is cut (210 of 287 cells)"),
        ("open card | long | 80x24", "CutWithRoom", "text is cut (78 of 287 cells)"),
        ("pull requests | long | 100x30", "CutWithRoom", "text is cut (75 of 89 cells)"),
        ("pull requests | long | 100x30", "CutWithRoom", "text is cut (98 of 111 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | long | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | long | 80x24", "CutWithRoom", "text is cut (55 of 89 cells)"),
        ("pull requests | long | 80x24", "CutWithRoom", "text is cut (78 of 111 cells)"),
        ("pull requests | long | 80x24", "CutWithRoom", "text is cut (78 of 85 cells)"),
        ("pull requests | many | 100x30", "CutWithRoom", "text is cut (76 of 90 cells)"),
        ("pull requests | many | 100x30", "CutWithRoom", "text is cut (98 of 113 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 14 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | many | 160x44", "CutWithRoom", "text is cut (13 of 15 cells)"),
        ("pull requests | many | 80x24", "CutWithRoom", "text is cut (56 of 90 cells)"),
        ("pull requests | many | 80x24", "CutWithRoom", "text is cut (78 of 113 cells)"),
        ("pull requests | many | 80x24", "CutWithRoom", "text is cut (78 of 85 cells)"),
        ("settings | long | 100x30", "CutWithRoom", "text is cut (81 of 87 cells)"),
        ("settings | long | 100x30", "CutWithRoom", "text is cut (98 of 185 cells)"),
        ("settings | long | 160x44", "CutWithRoom", "text is cut (120 of 185 cells)"),
        ("settings | long | 80x24", "CutWithRoom", "text is cut (61 of 87 cells)"),
        ("settings | long | 80x24", "CutWithRoom", "text is cut (78 of 185 cells)"),
        ("settings | many | 100x30", "CutWithRoom", "text is cut (81 of 87 cells)"),
        ("settings | many | 80x24", "CutWithRoom", "text is cut (61 of 87 cells)"),
        ("settings | many | 80x24", "CutWithRoom", "text is cut (78 of 88 cells)"),
        ("spend | long | 80x24", "CutWithRoom", "text is cut (78 of 85 cells)"),
        ("spend | long | 80x24", "CutWithRoom", "text is cut (78 of 98 cells)"),
        // Text lying partly over a control of another node (a header over the divider handle, a count over the row it sits in)
        ("fleet | many | 100x30", "TextOverRegion", "text \"1 ringing\""),
        ("fleet | many | 100x30", "TextOverRegion", "text \"2 ringing\""),
        ("jobs | long | 80x24", "TextOverRegion", "text \"Every page is checked for t…\""),
        ("jobs | long | 80x24", "TextOverRegion", "text \"Every page is checked for t…\""),
        ("jobs | long | 80x24", "TextOverRegion", "text \"Every page is checked for t…\""),
        ("jobs | long | 80x24", "TextOverRegion", "text \"The leg is done and every s…\""),
        ("jobs | long | 80x24", "TextOverRegion", "text \"cancelled · owner not told\""),
        ("jobs | long | 80x24", "TextOverRegion", "text \"succeeded · owner not told\""),
        ("sessions | long | 250x60", "TextOverRegion", "text \"Model\""),
        ("sessions | many | 250x60", "TextOverRegion", "text \"Model\""),
        // A control inside another control (a row button inside its list item, a divider handle over a header button)
        ("pull requests | long | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/example-org/Pilot.Ui.PageTextOverlapTests.Eve"),
        ("pull requests | long | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/example-org/Pilot.Ui.PageTextOverlapTests.Eve"),
        ("pull requests | long | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/example-org/Pilot.Ui.PageTextOverlapTests.Eve"),
        ("pull requests | long | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/example-org/Pilot.Ui.PageTextOverlapTests.Eve"),
        ("pull requests | long | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/example-org/Pilot.Ui.PageTextOverlapTests.Eve"),
        ("pull requests | long | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/example-org/Pilot.Ui.PageTextOverlapTests.Eve"),
        ("pull requests | long | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/example-org/Pilot.Ui.PageTextOverlapTests.Eve"),
        ("pull requests | long | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/example-org/Pilot.Ui.PageTextOverlapTests.Eve"),
        ("pull requests | many | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/SharpAstro/repo-0/pull/100:open }"),
        ("pull requests | many | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/SharpAstro/repo-0/pull/101:open }"),
        ("pull requests | many | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/SharpAstro/repo-0/pull/102:open }"),
        ("pull requests | many | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/SharpAstro/repo-0/pull/103:open }"),
        ("pull requests | many | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/SharpAstro/repo-1/pull/110:open }"),
        ("pull requests | many | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/SharpAstro/repo-1/pull/111:open }"),
        ("pull requests | many | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/SharpAstro/repo-1/pull/112:open }"),
        ("pull requests | many | 100x30", "RegionOverRegion", "the control ButtonHit { Action = pr:https://github.com/SharpAstro/repo-1/pull/113:open }"),
        ("sessions | long | 250x60", "RegionOverRegion", "the control ButtonHit { Action = sessions:table:divider:0 }"),
        ("sessions | many | 250x60", "RegionOverRegion", "the control ButtonHit { Action = sessions:table:divider:0 }"),
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

    private static CellWireframeResult Judge(Layout.Node rail, Layout.Node page, int columns = 80, int rows = 24)
    {
        var arranged = TerminalFrame.Build(rail, page, null, columns, rows, CellMeasureContext.PixelAuthored);
        _tree = string.Join('\n', arranged.Select(a => $"{new string(' ', a.Depth)}{a.Bounds.X},{a.Bounds.Y} {a.Bounds.Width}x{a.Bounds.Height} {((a.Node as Layout.Node.Leaf)?.Content as Layout.Content.Text)?.Value}"));
        return TerminalWireframeSweepTests.Check(TerminalWireframeSweepTests.Record(arranged, columns, rows));
    }

    private static Layout.Node Entries() => Layout.Builder.HStack(
        Layout.Builder.Text("Home", 14f, Palette.Current.Bright), Layout.Builder.Text("Sessions", 14f, Palette.Current.Bright));

    [Fact(Skip = "Not caught: the bar's fill is 12 empty-but-for-one-row rows and no CellWireframe rule looks at a container (SharpAstro/Console.Lib#69)")]
    public void ThePageBarGivenHFixedAndStretchIsCaught()
    {
        // PIL1027 (the flight that fixed it was PIL1032): Stretch() sets the height to Star as well and overwrote the HFixed, so the bar took the screen.
        var result = Judge(Entries().HFixed(16f).Stretch().Bg(Palette.Current.Ground), Page());
        TerminalWireframeSweepTests.Findings(result).ShouldNotBeEmpty(Describe(result));
    }

    [Fact(Skip = "Not caught: a filled one-row strip with no text breaks no CellWireframe rule (SharpAstro/Console.Lib#70)")]
    public void TheComposerRulesDrawnAsGreyBarsAreCaught()
    {
        // PIL1049: the rule was a Spacer with a fill on a 16-unit row: a grey bar, no text on it.
        var composer = Layout.Builder.VStack(
            Layout.Builder.Spacer().Bg(Palette.Current.Faint).WStar().RowH(16f),
            Layout.Builder.Text("❯ Message the fleet chat", 14f, Palette.Current.Bright).WStar().RowH(16f),
            Layout.Builder.Spacer().Bg(Palette.Current.Faint).WStar().RowH(16f)).WStar().HAuto();
        var result = Judge(Entries().RowH(16f), Layout.Builder.VStack(Layout.Builder.Spacer().HStar(), composer).Bg(Palette.Current.Ground).WStar().HStar());
        TerminalWireframeSweepTests.Findings(result).ShouldNotBeEmpty(Describe(result));
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
