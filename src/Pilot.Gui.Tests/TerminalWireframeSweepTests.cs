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
    /// <summary>The sizes (columns, rows) and states the sweep classes split into; <c>SweepNamesTests</c> holds the class count to their product.</summary>
    internal static readonly (int Columns, int Rows)[] Sizes = [(80, 24), (100, 30), (160, 44), (250, 60)];

    internal static readonly string[] States = ["long", "many"];

    internal static readonly string[] Cases =
    [
        "home", "sessions", "flights", "repos", "pull requests", "jobs", "fleet", "ideas", "spend", "settings", "chat", "designs", "design page",
        PageTextOverlapTests.DesignPageNoVersion, PageTextOverlapTests.NotificationsPage, "help card", "open card", "chat long typed", "tooltip open",
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
    internal static async Task<CellWireframeFrame> RecordCase(string name, string state, int columns, int rows, CellBuffer? into = null, Action<PilotChrome<RgbaImage>>? setup = null, IdeasScreen? ideas = null)
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
        setup?.Invoke(chrome);
        var cards = name == "open card" ? data.CardsWithDetail : data.Cards;
        chrome.Cards.OnChatPage = page == PilotPage.Chat;
        var cardsBeside = columns >= TerminalFrame.CardsPanelColumns && (cards.HasAny || chrome.Cards.CanTell && !chrome.Cards.OnChatPage);
        chrome.Chat.CellColumns = TerminalFrame.PageColumns(columns, cardsBeside);
        if (page == PilotPage.Designs)
        {
            // pilot-tui plays the prototype in its own cells: the page embeds its tree, arranged in the room the page has (the cards panel beside it takes its columns), as Program.cs sets it.
            chrome.Designs.Terminal = (TerminalFrame.PageColumns(columns, cardsBeside), rows);
            if (name is "design page" or PageTextOverlapTests.DesignPageNoVersion)
            {
                // The chat under the cell view has something said: three turns and two versions, the designer checking. The no-version
                // page (#1160) is the issue's design: a long title made from its brief, a long brief, and no version, so the page says so.
                await PageData.OpenDesignChat(chrome.Designs, PageTextOverlapTests.DesignsOf(data, name).Open!.Detail.Row.Id, PageData.DesignChatPage(lengthen: state is "long" or "many"));
            }
        }
        var width = columns * 8f;
        if (page == PilotPage.Notifications)
        {
            // As Program.cs has it: the page beside the For you strip, the state's open cards under Needs you and its events in the history.
            PageData.PostEvents(chrome.Notifications, state);
            chrome.Notifications.Sync(cards.Open, c => chrome.Cards.NodeOf(c)?.Name, PageData.Now);
            width = TerminalFrame.PageColumnsWithBar(columns) * 8f;
        }
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
            case PilotPage.Ideas: tree = chrome.Ideas.Tree(ideas ?? PageTextOverlapTests.IdeasFor(state)); break;
            case PilotPage.Spend: tree = chrome.Spend.Tree(data.Spend); break;
            case PilotPage.Designs: tree = chrome.Designs.Tree(PageTextOverlapTests.DesignsOf(data, name)); break;
            case PilotPage.Notifications: tree = chrome.NotificationsPage.Tree(data.Sessions.Source, data.Sessions.AsOf, width); break;
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
        // The Notifications page is framed as pilot-tui frames every page now: beside the For you strip, with no bar over it (the page is the list).
        // The other cases still lay the cards panel beside the page, as the terminal did before the bar (#1184's follow-up brings them over).
        var bar = page == PilotPage.Notifications ? (chrome.Notifications, page) : ((NotificationsHost, PilotPage)?)null;
        var cardsTree = cardsBeside && bar is null ? chrome.Cards.Tree(cards) : null;
        var arranged = TerminalFrame.Build(rail, tree, cardsTree, columns, rows, CellMeasureContext.PixelAuthored, null, bar);
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
        return Record(arranged, columns, rows, into);
    }

    /// <summary>The frame painted into <paramref name="into"/> (a new buffer when null) and recorded.</summary>
    internal static CellWireframeFrame Record(ImmutableArray<Layout.ArrangedNode<int>> arranged, int columns, int rows, CellBuffer? into = null)
    {
        var buffer = into ?? new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        return CellWireframe.RecordCells(new BufferedViewport(buffer, columns, rows), arranged, null, false);
    }

    /// <summary>
    /// A case's cells as text, one line a row, written beside the test's output (<c>terminal-wireframe/</c>) for every case, so a page can be looked
    /// at in cells as the GUI sweep's PNG shows it in pixels; a <paramref name="failed"/> case's also goes to <c>PILOT_JOB_ARTIFACTS</c> when a
    /// pilot job runs it, to start the fix from.
    /// </summary>
    internal static string CaptureOf(CellBuffer buffer, int columns, int rows, string key, bool failed)
    {
        var text = string.Join(Environment.NewLine, Enumerable.Range(0, rows).Select(y => string.Concat(Enumerable.Range(0, columns).Select(x => buffer.BackAt(x, y).Glyph)).TrimEnd()));
        var name = string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_')) + ".txt";
        var folder = Path.Combine(AppContext.BaseDirectory, "terminal-wireframe");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        if (failed && Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts && Directory.Exists(artifacts))
        {
            File.WriteAllText(Path.Combine(artifacts, "terminal-wireframe-" + name), text);
        }
        return path;
    }

    /// <summary>
    /// The collapsed bar's edge handle (PIL1205) floats over the page's right edge and takes no room: what the page painted under it is covered
    /// by design (as under the open bar), and the handle's own tab is one chevron on a filled column, so its EmptyRows is the design. Its place
    /// and tint are judged by NotificationsCaptureTests and NotificationsHandleTests.
    /// </summary>
    internal static CellWireframeResult Check(CellWireframeFrame frame)
    {
        var handles = frame.Frame.Regions.Where(r => r.Result is HitResult.ButtonHit { Action: Pilot.Ui.NotificationsHost.HandleAction }).ToArray();
        var options = new CellWireframeOptions
        {
            Pixel = new WireframeOptions
            {
                IgnoreRegion = r => r.Result is HitResult.ButtonHit { Action: Pilot.Ui.NotificationsHost.HandleAction } || handles.Any(h => r.Order < h.Order && Covers(h.Visible, r.Visible)),
                IgnoreText = t => handles.Any(h => t.Order < h.Order && Covers(h.Visible, t.Visible)),
            },
        };
        var result = CellWireframe.CheckCells(frame, options);
        return result with
        {
            CellFindings = [.. result.CellFindings.Where(f => !handles.Any(h => Math.Abs(h.Visible.X - f.Where.X) < 0.5f && Math.Abs(h.Visible.Y - f.Where.Y) < 0.5f))],
        };
    }

    private static bool Covers(RectF32 a, RectF32 b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>A finding as the baseline lists it: the rule and the message it carries.</summary>
    internal static IEnumerable<(string Rule, string What)> Findings(CellWireframeResult result) =>
        result.Findings.Select(f => (f.Rule.ToString(), f.Message)).Concat(result.CellFindings.Select(f => (f.Rule.ToString(), f.Message)));

    internal static async Task Run(string page, string state, int columns, int rows)
    {
        var key = Key(page, state, columns, rows);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        var frame = await RecordCase(page, state, columns, rows, buffer);
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
        var capture = CaptureOf(buffer, columns, rows, key, failed: unknown.Count > 0);
        if (unknown.Count > 0)
        {
            message.AppendLine($"{key}: {unknown.Count} finding(s) not in the baseline:");
            foreach (var u in unknown) { message.AppendLine($"  [{u.Rule}] {u.What}"); }
            message.AppendLine(CellWireframe.Describe(result));
            message.AppendLine($"  capture: {capture}");
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
        // PIL1191: Build this version moved from a Build part of the document to the first row, so the document is shorter and the scroll viewport's clip falls on the played frame's well in the long state at 100x30 (the two many-state entries #1185 added no longer occur).
        ("design page | long | 100x30", "EmptyRows", "a filled container is arranged 2 rows tall but its content sits on row 16 only; 1 row(s) are empty inside its fill (own vertical padding 0 row(s), visible border 0 row(s))"), // Console.Lib #78
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

public sealed class TerminalWireframeSweep80LongTests { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(80, 24, "long"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframeSweep80ManyTests { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(80, 24, "many"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframeSweep100LongTests { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(100, 30, "long"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframeSweep100ManyTests { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(100, 30, "many"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframeSweep160LongTests { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(160, 44, "long"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframeSweep160ManyTests { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(160, 44, "many"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframeSweep250LongTests { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(250, 60, "long"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }
public sealed class TerminalWireframeSweep250ManyTests { public static IEnumerable<TheoryDataRow<string, string, int, int>> Cases() => TerminalWireframeSweepTests.RowsOf(250, 60, "many"); [Theory, MemberData(nameof(Cases))] public Task Sweep(string page, string state, int c, int r) => TerminalWireframeSweepTests.Run(page, state, c, r); }

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
