using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// pilot-tui's whole frame (#1032): the bar is row 0 only, the page takes rows 1 to the last, and every cell of the grid holds a set
/// background (the bar's cells past its last entry and any gap of a page or the cards panel included), so no terminal-black cell or
/// stale glyph is left on any page at the terminal's real size.
/// </summary>
public sealed class TerminalFrameTests
{
    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;

        public (int Column, int Row)? Caret { get; private set; }
        public void SetCaret(int column, int row, CaretStyle style) => Caret = (column, row);
        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static CardsScreen Cards() => CardsScreen.From([new CardRow
    {
        Id = "c1", Bin = CardBin.ForYou, Kind = "pr-ready", Subject = "flight/ORB201", Title = "A PR is up to date", Detail = "d", At = Live.At, State = CardState.Open,
    }]);

    private static Layout.Node PageTree(PilotChrome<RgbaImage> chrome, PilotPage page, CardsScreen cards)
    {
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "sample");
        var flights = new FlightsScreen([], "sample", Live.At);
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), sessions.Source, snapshot.At);
        return page switch
        {
            PilotPage.Spend => chrome.Spend.Tree(new SpendScreen(chrome.Spend.Window, null, null, sessions.Source, Live.At)),
            PilotPage.Ideas => chrome.Ideas.Tree(new IdeasScreen([], [], [], sessions.Source, Live.At)),
            _ => chrome.Home.Tree(new HomeScreen(flights, sessions, jobs, cards, [])),
        };
    }

    private static (CellBuffer Buffer, Layout.Node Page, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Arranged) Paint(PilotPage page, int columns, int rows)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var cards = Cards();
        var pageTree = PageTree(chrome, page, cards);
        var rail = TerminalRail.Tree(page, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
        var panel = columns >= TerminalFrame.CardsPanelColumns ? chrome.Cards.Tree(cards) : null;
        var tree = TerminalFrame.Tree(rail, pageTree, panel);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);
        return (buffer, pageTree, arranged);
    }

    // The Chat page as pilot-tui builds it (chrome.Chat's own Tree), holding `count` long entries, read through the page's own follow.
    private static async Task<(CellBuffer Buffer, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Painted, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Raw)> PaintChatAsync(
        int count, bool scrolledUp, int columns, int rows)
    {
        var r = await PaintChatCaretAsync(count, scrolledUp, columns, rows, 0);
        return (r.Buffer, r.Painted, r.Raw);
    }

    private static async Task<(CellBuffer Buffer, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Painted, System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Raw, (int Column, int Row)? Caret)> PaintChatCaretAsync(
        int count, bool scrolledUp, int columns, int rows, int typed)
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var at = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var feed = Enumerable.Range(0, count).Select(i => new TranscriptEntry
        {
            Index = i,
            Kind = i % 2 == 0 ? TranscriptKind.User : TranscriptKind.Assistant,
            At = at.AddSeconds(i),
            Text = $"Entry {i}: " + string.Join(' ', Enumerable.Repeat("a long line of the transcript that wraps past the width of the list", 6)),
        }).ToList();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chrome.Chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = [.. feed.Where(e => e.Index > after)], Total = feed.Count });
        chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
        chrome.Chat.Cells = true;
        chrome.Chat.CellColumns = TerminalFrame.PageColumns(columns, columns >= TerminalFrame.CardsPanelColumns);
        if (typed > 0)
        {
            chrome.Chat.Input.InsertText(new string('x', typed));
        }
        chrome.Chat.Changed = () =>
        {
            if (chrome.Chat.Entries.Count >= count)
            {
                opened.TrySetResult();
            }
        };
        try
        {
            if (count > 0)
            {
                chrome.Chat.Open();
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            }
            var cards = Cards();
            var rail = TerminalRail.Tree(PilotPage.Chat, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
            var panel = columns >= TerminalFrame.CardsPanelColumns ? chrome.Cards.Tree(cards) : null;
            var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, chrome.Chat.Tree(), panel), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
            if (scrolledUp)
            {
                // The list is anchored to its end; a person who scrolled up reads from further up, and the frame is arranged again.
                var list = arranged.Select(a => a.Node.Scroll).First(s => s is { Mode: ScrollBarMode.Interactive })!;
                list.AtomOffset = Math.Max(0, list.AtomOffset - 7);
                arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, chrome.Chat.Tree(), panel), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
            }
            var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
            buffer.Resize(columns, rows);
            var painted = TerminalFrame.Clip(arranged);
            var viewport = new BufferedViewport(buffer, columns, rows);
            CellLayout.Paint(viewport, painted);
            return (buffer, painted, arranged, chrome.Chat.CaretCell(painted));
        }
        finally
        {
            chrome.Chat.Close();
        }
    }

    [Theory]
    [InlineData(false, 160, 44)]
    [InlineData(true, 160, 44)]
    [InlineData(false, 100, 24)]
    [InlineData(true, 100, 24)]
    public async Task ATranscriptTallerThanItsListLeavesTheBarTheHeaderAndTheCardsPanelIntact(bool scrolledUp, int columns, int rows)
    {
        var size = $"Chat {columns}x{rows}{(scrolledUp ? " scrolled up" : "")}";
        var (full, painted, raw) = await PaintChatAsync(120, scrolledUp, columns, rows);

        var list = raw.First(a => a.Node.Scroll is { Mode: ScrollBarMode.Interactive });
        var listIndex = raw.IndexOf(list);
        var listTop = list.Bounds.Y;
        listTop.ShouldBeGreaterThan(1, $"{size}: the chat has header rows above its list");
        var panelLeft = columns >= TerminalFrame.CardsPanelColumns ? raw.Where(a => a.Depth == 2).Last().Bounds.X : columns;

        // The same frame painted without the transcript: the arranged tree with the list's descendants taken out (the list's own fill stays).
        var without = raw.Where((a, i) => !(i > listIndex && IsUnder(raw, a, list))).ToImmutableArray();
        var empty = new CellBuffer { ColorMode = ColorMode.TrueColor };
        empty.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(empty, columns, rows), without);

        // The bar row, the header rows above the list, and the cards panel's columns hold what that frame paints.
        var differ = new List<string>();
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var guarded = y == 0 || y < listTop || x >= panelLeft;
                if (guarded && full.BackAt(x, y) != empty.BackAt(x, y))
                {
                    differ.Add($"({x},{y})");
                }
            }
        }
        differ.ShouldBeEmpty($"{size}: {differ.Count} cells outside the list changed with a transcript, first: {string.Join(' ', differ.Take(8))}");

        // What a click reads is what was painted: no node under the list reaches outside the list's rect (the bar, the header, the panel).
        var pl = painted.First(a => a.Node.Scroll is { Mode: ScrollBarMode.Interactive });
        painted.Where(a => IsUnder(painted, a, pl) && (a.Bounds.Y < pl.Bounds.Y || a.Bounds.X < pl.Bounds.X
                || a.Bounds.Y + a.Bounds.Height > pl.Bounds.Y + pl.Bounds.Height || a.Bounds.X + a.Bounds.Width > pl.Bounds.X + pl.Bounds.Width))
            .ShouldBeEmpty($"{size}: a node under the list reaches outside the list's rect");
    }

    // PIL1043: the composer (the editor's placeholder and the 'Enter sends' hint) is always on the page, whatever the transcript.
    [Theory]
    [InlineData(120, false, 160, 44)]
    [InlineData(120, true, 160, 44)]
    [InlineData(0, false, 160, 44)]
    [InlineData(120, false, 100, 24)]
    [InlineData(120, true, 100, 24)]
    [InlineData(0, false, 100, 24)]
    public async Task TheComposerIsPaintedBelowTheListWhateverTheTranscript(int count, bool scrolledUp, int columns, int rows)
    {
        var size = $"Chat {columns}x{rows} with {count} entries{(scrolledUp ? " scrolled up" : "")}";
        var (buffer, painted, raw, caret) = await PaintChatCaretAsync(count, scrolledUp, columns, rows, 0);
        buffer.Flush(new NullSink());
        var text = Enumerable.Range(0, rows).Select(buffer.FrontRowText).ToList();
        var placeholder = text.FindIndex(r => r.Contains("Message the fleet chat", StringComparison.Ordinal));
        var hint = text.FindIndex(r => r.Contains("Enter sends", StringComparison.Ordinal));
        var dump = string.Join('\n', text);
        placeholder.ShouldBeGreaterThan(0, $"{size}: the placeholder is not painted\n{dump}");
        hint.ShouldBeGreaterThan(placeholder, $"{size}: the 'Enter sends' line is not painted at or below the box\n{dump}");
        // With the box focused (Ctrl+T, key 2) the terminal's caret goes on the box's own row, inside the page.
        caret.ShouldNotBeNull($"{size}: the composer gives no caret cell");
        caret.Value.Row.ShouldBe(placeholder, $"{size}: the caret is not on the box's row");
        var panelLeft = columns >= TerminalFrame.CardsPanelColumns ? TerminalFrame.PageColumns(columns, true) : columns;
        caret.Value.Column.ShouldBeInRange(0, panelLeft - 1, $"{size}: the caret is not left of the cards panel");
        if (count > 0)
        {
            var list = painted.First(a => a.Node.Scroll is { Mode: ScrollBarMode.Interactive });
            placeholder.ShouldBeGreaterThanOrEqualTo(list.Bounds.Y + list.Bounds.Height, $"{size}: the box is not below the list");
        }
    }

    // PIL1043 review: a typed line longer than the box stays on one row, windowed to the caret, with the caret left of the cards panel.
    [Theory]
    [InlineData(160, 44)]
    [InlineData(120, 24)]
    [InlineData(100, 24)]
    public async Task ALongTypedLineIsWindowedToTheCaretInsideTheBoxAndLeftOfThePanel(int columns, int rows)
    {
        var size = $"Chat {columns}x{rows} typed 200";
        var (buffer, painted, _, caret) = await PaintChatCaretAsync(0, false, columns, rows, 200);
        buffer.Flush(new NullSink());
        var text = Enumerable.Range(0, rows).Select(buffer.FrontRowText).ToList();
        var dump = string.Join('\n', text);
        var row = text.FindIndex(r => r.Contains("❯ x", StringComparison.Ordinal));
        row.ShouldBeGreaterThan(0, $"{size}: the typed row is not painted\n{dump}");
        caret.ShouldNotBeNull($"{size}: no caret cell for a long line\n{dump}");
        var panelLeft = columns >= TerminalFrame.CardsPanelColumns ? TerminalFrame.PageColumns(columns, true) : columns;
        caret.Value.Row.ShouldBe(row, $"{size}: the caret is not on the typed row");
        caret.Value.Column.ShouldBeInRange(0, panelLeft - 1, $"{size}: the caret is not left of the cards panel");
        // One row: the line under it is the rule, painted like the rule above the box, not a wrapped second row of x.
        text[row + 1].Contains("xx", StringComparison.Ordinal).ShouldBeFalse($"{size}: the line wrapped\n{dump}");
        buffer.BackAt(2, row + 1).ShouldBe(buffer.BackAt(2, row - 1), $"{size}: the rule is not directly under the typed row\n{dump}");
    }

    // PIL1049: the composer's rules are lines of U+2500 on the page's own background (no fill), the prompt row '❯ ' and the placeholder.
    [Fact]
    public async Task TheComposersRulesAreThinLinesOnThePageBackgroundAndThePromptRowHoldsThePlaceholder()
    {
        var (buffer, _, _, caret) = await PaintChatCaretAsync(0, false, 160, 44, 0);
        buffer.Flush(new NullSink());
        var text = Enumerable.Range(0, 44).Select(buffer.FrontRowText).ToList();
        var dump = string.Join('\n', text);
        var prompt = text.FindIndex(r => r.Contains("❯ Message the fleet chat", StringComparison.Ordinal));
        prompt.ShouldBeGreaterThan(1, $"the prompt row with the placeholder is not painted\n{dump}");
        foreach (var rule in new[] { prompt - 1, prompt + 1 })
        {
            var from = text[rule].IndexOf('─');
            from.ShouldBeGreaterThanOrEqualTo(0, $"row {rule} holds no U+2500\n{dump}");
            var width = text[rule].Skip(from).TakeWhile(c => c == '─').Count();
            width.ShouldBeGreaterThan(60, $"row {rule}: the rule is not a long run of U+2500\n{dump}");
            for (var x = from; x < from + width; x++)
            {
                buffer.BackAt(x, rule).Style.ShouldBe(buffer.BackAt(from + width, prompt).Style, $"row {rule} col {x}: the rule has a fill that the prompt row lacks\n{dump}");
            }
        }
        caret.ShouldNotBeNull();
    }

    // PIL1049: a left click on the composer's rows (resolved on the arranged tree, as the app does) focuses the box and puts the caret on the clicked column.
    [Fact]
    public async Task AClickOnTheComposersPromptRowFocusesTheBoxAndPutsTheCaretWhereClicked()
    {
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
        chrome.Chat.Cells = true;
        const int columns = 160, rows = 44;
        chrome.Chat.CellColumns = TerminalFrame.PageColumns(columns, true);
        chrome.Chat.Input.InsertText("hello world\nsecond line");
        chrome.Chat.Ui.Focus.Blur();
        var rail = TerminalRail.Tree(PilotPage.Chat, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
        var panel = chrome.Cards.Tree(Cards());
        var arranged = TerminalFrame.Clip(Layout.Engine.Arrange(TerminalFrame.Tree(rail, chrome.Chat.Tree(), panel), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored));
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(columns, rows);
        CellLayout.Paint(new BufferedViewport(buffer, columns, rows), arranged);
        buffer.Flush(new NullSink());
        var text = Enumerable.Range(0, rows).Select(buffer.FrontRowText).ToList();
        var first = text.FindIndex(r => r.Contains("❯ hello world", StringComparison.Ordinal));
        first.ShouldBeGreaterThan(0, string.Join('\n', text));
        var left = text[first].IndexOf('❯');

        chrome.Chat.Ui.Focus.Current.ShouldNotBe(chrome.Chat.Input);
        // On the second line, column 4: "seco|nd line".
        var x = left + 2 + 4;
        CellLayout.HitTest(arranged, x, first + 1).ShouldBeOfType<HitResult.ButtonHit>();
        chrome.Chat.Ui.Focus.Current.ShouldBe(chrome.Chat.Input);
        chrome.Chat.PlaceComposerCaret(arranged, x, first + 1);
        chrome.Chat.Input.Line.ShouldBe(1);
        chrome.Chat.Input.Column.ShouldBe(4);
        chrome.Chat.CaretCell(arranged).ShouldNotBeNull();
        // A click on the top rule focuses the box too.
        chrome.Chat.Ui.Focus.Blur();
        CellLayout.HitTest(arranged, left + 5, first - 1).ShouldBeOfType<HitResult.ButtonHit>();
        chrome.Chat.Ui.Focus.Current.ShouldBe(chrome.Chat.Input);
    }

    // A viewport whose caret is sticky as VirtualTerminal's is: SetCaret keeps the request until HideCaret.
    private sealed class StickyCaretViewport(int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public (int Column, int Row)? Caret { get; private set; }
        public void SetCaret(int column, int row, CaretStyle style) => Caret = (column, row);
        public void HideCaret() => Caret = null;
        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    // One frame as Program.cs makes it: tree, arrange, clip, paint. Raw is the arranged tree before the clip.
    private static (ImmutableArray<Layout.ArrangedNode<int>> Raw, ImmutableArray<Layout.ArrangedNode<int>> Painted) PaintFrameBoth(
        StickyCaretViewport viewport, PilotPage page, Layout.Node pageTree, int columns, int rows)
    {
        var rail = TerminalRail.Tree(page, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
        var raw = Layout.Engine.Arrange(TerminalFrame.Tree(rail, pageTree, null), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
        var painted = TerminalFrame.Clip(raw);
        TerminalFrame.Paint(viewport.HideCaret, () => CellLayout.Paint(viewport, painted));
        return (raw, painted);
    }

    private static ImmutableArray<Layout.ArrangedNode<int>> PaintFrame(StickyCaretViewport viewport, PilotPage page, Layout.Node pageTree, int columns, int rows)
        => PaintFrameBoth(viewport, page, pageTree, columns, rows).Painted;

    private static Layout.ArrangedNode<int>? FieldNode(ImmutableArray<Layout.ArrangedNode<int>> arranged, TextInputState field)
        => arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput t } && ReferenceEquals(t.State, field)).Select(a => (Layout.ArrangedNode<int>?)a).FirstOrDefault();

    [Fact]
    public async Task TheCaretOfOneSettingsFieldFollowsItWhenRowsArriveAboveAndGoesWhenItIsClippedOut()
    {
        const int columns = 100, rows = 40;
        var viewport = new StickyCaretViewport(columns, rows);
        var focus = new TextInputFocus();
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));

        // One page, one machine field throughout. The setup checks arrive after the first frame, so rows come in above the field.
        var settings = new[] { new SettingRow { Key = "machine", Label = "Machine prefix", Detail = "Two letters and a number", Value = "DE1", Source = SettingSources.Default } };
        var checksGate = new TaskCompletionSource<SetupChecks>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chrome.Settings.Load = _ => Task.FromResult(new SettingsPage { File = "settings.json", Settings = settings });
        chrome.Settings.LoadChecks = (_, _) => checksGate.Task;
        chrome.Settings.Changed = () =>
        {
            if (chrome.Settings.Page is not null)
            {
                loaded.TrySetResult();
            }
        };
        Layout.Node Tree() => chrome.Settings.Tree(DateTimeOffset.UtcNow, columns * 8f);
        Tree();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // First frame: the machine field focused, its caret parked in the field.
        var machine = CellLayout.TextInputs(PaintFrame(viewport, PilotPage.Settings, Tree(), columns, rows)).Last();
        focus.Focus(machine);
        var rect1 = FieldNode(PaintFrame(viewport, PilotPage.Settings, Tree(), columns, rows), machine).ShouldNotBeNull().Bounds;
        viewport.Caret.ShouldNotBeNull().Row.ShouldBe(rect1.Y);

        // The checks arrive: the same field is laid out lower. The caret is in the new rect, not the old row.
        var checksIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chrome.Settings.Changed = () =>
        {
            if (chrome.Settings.Checks is not null)
            {
                checksIn.TrySetResult();
            }
        };
        checksGate.SetResult(new SetupChecks
        {
            CheckedAt = DateTimeOffset.UtcNow,
            Checks = [.. Enumerable.Range(0, 4).Select(i => new SetupCheck { Key = $"c{i}", Title = $"Check {i}", Ok = true, Line = "fine" })],
        });
        await checksIn.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var rect2 = FieldNode(PaintFrame(viewport, PilotPage.Settings, Tree(), columns, rows), machine).ShouldNotBeNull().Bounds;
        rect2.Y.ShouldBeGreaterThan(rect1.Y);
        var caret = viewport.Caret.ShouldNotBeNull();
        caret.Row.ShouldBe(rect2.Y);
        caret.Column.ShouldBeInRange(rect2.X, rect2.X + rect2.Width);

        // The same page in a terminal too short to reach the field: it is laid out, but the clip drops it, so nothing is painted and no caret stays.
        var (raw, painted) = PaintFrameBoth(viewport, PilotPage.Settings, Tree(), columns, rect2.Y);
        FieldNode(raw, machine).ShouldNotBeNull();
        FieldNode(painted, machine).ShouldBeNull();
        viewport.Caret.ShouldBeNull();
    }

    [Fact]
    public void TheCaretOfTheFlightsWhyFieldIsWithdrawnWhenTheFrameIsAnotherPage()
    {
        const int columns = 100, rows = 40;
        var viewport = new StickyCaretViewport(columns, rows);
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        var flights = chrome.Flights;
        flights.CanvasAvailable = false;
        flights.CanCommand = true;
        var flight = new FlightRow
        {
            Code = "TIA1611", Repo = "tianwen", RepoPath = "/r", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
            Worktree = "/r/w", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        };
        LegRow[] legs = [new() { Flight = "TIA1611", Name = "render", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Planned }];
        var screen = new FlightsScreen(FlightLine.Build([flight], [], Live.At, legs), "live", Live.At);
        flights.Tree(screen);
        flights.OpenMenu(screen.Lines[0], 0, new PointerPress(10f, 10f, MouseButton.Right, InputModifier.None, 1));
        flights.MenuItems.Single(i => i.Label == "Remove render…").OnChoose!();
        new TextInputFocus().Focus(flights.WhyInput);

        var arranged = PaintFrame(viewport, PilotPage.Flights, flights.Tree(screen), columns, rows);
        var field = FieldNode(arranged, flights.WhyInput).ShouldNotBeNull();
        viewport.Caret.ShouldNotBeNull().Row.ShouldBe(field.Bounds.Y);

        // Another editor's field stays focused but the frame is another page without it: the caret is withdrawn, not left behind.
        var home = chrome.Home.Tree(new HomeScreen(screen, SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample"), new JobsScreen([], "sample", Live.At), Cards(), []));
        PaintFrame(viewport, PilotPage.Home, home, columns, rows);
        viewport.Caret.ShouldBeNull();
    }

    // The Chat composer is not a CellLayout text input: Program.cs parks its caret after the paint, only on the Chat page with the box focused.
    [Fact]
    public void TheCaretOfTheChatBoxIsParkedOnItsPageAndWithdrawnWhenTheFrameIsAnotherPage()
    {
        const int columns = 100, rows = 40;
        var viewport = new StickyCaretViewport(columns, rows);
        var focus = new TextInputFocus();
        using var vessel = new RgbaImageRenderer(1, 1);
        var chrome = new PilotChrome<RgbaImage>(vessel, new SessionsView<RgbaImage>(vessel));
        chrome.Chat.Load = (after, _) => Task.FromResult(new ChatPage { Entries = [], Total = 0 });
        chrome.Chat.OnSend = _ => Task.FromResult<string?>(null);
        chrome.Chat.Cells = true;
        chrome.Chat.CellColumns = columns;
        focus.Focus(chrome.Chat.Input);

        void Frame(PilotPage page, Layout.Node tree)
        {
            var painted = PaintFrame(viewport, page, tree, columns, rows);
            if (page == PilotPage.Chat && ReferenceEquals(focus.Current, chrome.Chat.Input) && chrome.Chat.CaretCell(painted) is var (column, row))
            {
                viewport.SetCaret(column, row, CaretStyle.BlinkingBar);
            }
        }

        Frame(PilotPage.Chat, chrome.Chat.Tree());
        var onChat = viewport.Caret.ShouldNotBeNull();
        onChat.Row.ShouldBeInRange(1, rows - 1);
        onChat.Column.ShouldBeInRange(0, columns - 1);

        // The box is still focused, but the frame is another page: the caret is withdrawn, not left on the Chat row.
        var screen = new FlightsScreen([], "sample", Live.At);
        Frame(PilotPage.Home, chrome.Home.Tree(new HomeScreen(screen, SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample"), new JobsScreen([], "sample", Live.At), Cards(), [])));
        viewport.Caret.ShouldBeNull();

        // And back on Chat it is parked again.
        Frame(PilotPage.Chat, chrome.Chat.Tree());
        viewport.Caret.ShouldBe(onChat);
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int row, int column) { }
        public void SetPen(VtStyle style, bool opaque) { }
        public void SetLink(string? uri) { }
        public void Write(ReadOnlySpan<char> text) { }
    }

    // A node is under the list when it follows it in pre-order at a deeper depth, before the next node at the list's depth or above.
    private static bool IsUnder(System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> arranged, Layout.ArrangedNode<int> node, Layout.ArrangedNode<int> list)
    {
        var from = arranged.IndexOf(list);
        var to = arranged.IndexOf(node);
        if (to <= from)
        {
            return false;
        }
        for (var i = from + 1; i <= to; i++)
        {
            if (arranged[i].Depth <= list.Depth)
            {
                return false;
            }
        }
        return true;
    }

    [Theory]
    [InlineData("Home", 160, 44)]
    [InlineData("Spend", 160, 44)]
    [InlineData("Ideas", 160, 44)]
    [InlineData("Home", 100, 24)]
    [InlineData("Spend", 100, 24)]
    [InlineData("Ideas", 100, 24)]
    public void TheBarIsRowZeroAndThePageTakesTheRestEveryCellPainted(string pageName, int columns, int rows)
    {
        var page = Enum.Parse<PilotPage>(pageName);
        var (buffer, pageTree, arranged) = Paint(page, columns, rows);
        var size = $"{page} {columns}x{rows}";

        var bar = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var act } && act.StartsWith("tui-rail:", StringComparison.Ordinal)).ToList();
        bar.ShouldNotBeEmpty();
        bar.ShouldAllBe(a => a.Bounds.Y == 0 && a.Bounds.Height == 1, $"{size}: the bar is row 0 only");

        var pageNode = arranged.Where(a => a.Depth == arranged[0].Depth + 1).ElementAt(1);
        pageNode.Bounds.Y.ShouldBe(1, $"{size}: the page's first row is 1");
        (pageNode.Bounds.Y + pageNode.Bounds.Height).ShouldBe(rows, $"{size}: the page's last row is the terminal's last");

        var unset = new List<string>();
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                if (buffer.BackAt(x, y).Style.Background == default(RGBAColor32))
                {
                    unset.Add($"({x},{y})");
                }
            }
        }
        unset.ShouldBeEmpty($"{size}: {unset.Count} cells keep the default background, first: {string.Join(' ', unset.Take(8))}");
    }
}
