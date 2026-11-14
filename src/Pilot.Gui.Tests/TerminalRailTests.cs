using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// pilot-tui's page bar (<see cref="TerminalRail"/>): every page of <c>PilotChrome.Pages</c> with its key, in cells, in both glyph
/// modes, the active page marked, a click on an entry showing its page, and the buffer's flush sending each emoji whole.
/// All on fakes: no terminal, no process.
/// </summary>
public sealed class TerminalRailTests
{
    private static readonly DateTimeOffset Now = Live.At;

    private static readonly (PilotPage Page, char Key)[] Expected =
    [
        (PilotPage.Home, '1'), (PilotPage.Chat, '2'), (PilotPage.Sessions, '3'), (PilotPage.Flights, '4'), (PilotPage.Repos, '5'),
        (PilotPage.PullRequests, '9'), (PilotPage.Jobs, '6'), (PilotPage.Fleet, '7'), (PilotPage.Settings, '8'), (PilotPage.Ideas, '0'),
        (PilotPage.Spend, '$'),
    ];

    private static JobsScreen JobsOf(params JobState[] states) =>
        new(JobLine.Build(states.Select((s, i) => new JobRow
        {
            Id = $"j{i}", Command = "dotnet build", Cwd = "/r", LogPath = "/r/j.log", Timeout = TimeSpan.FromMinutes(5), State = s, StartedAt = Now, EndedAt = s == JobState.Failed ? Now : null,
        }), Now), "t", Now);

    private static IdeasScreen IdeasOf(int noted) =>
        new([.. Enumerable.Range(1, noted).Select(i => new IdeaRow { Id = $"I{i}", Repo = "pilot", Title = $"idea {i}", State = IdeaState.Noted })], [], [], "t", Now);

    private static System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Arrange(Layout.Node tree) =>
        Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 100, 1), CellMeasureContext.PixelAuthored);

    private static Layout.ArrangedNode<int> EntryOf(System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> arranged, PilotPage page) =>
        arranged.First(a => a.Node.Hit is HitResult.ButtonHit hit && hit.Action == TerminalRail.Action(page));

    [Fact]
    public void TheBarFollowsTheGuisRailOrderWithItsCtrlDigitsAsKeysAndSpendHasADollar()
    {
        TerminalRail.Keys.Select(k => (k.Page, k.Key)).ShouldBe(Expected);
        TerminalRail.PageFor('0').ShouldBe(PilotPage.Ideas);
        TerminalRail.PageFor('$').ShouldBe(PilotPage.Spend);
        TerminalRail.PageFor('x').ShouldBeNull();
        TerminalRail.PageFor(InputKey.D6, InputModifier.None).ShouldBe(PilotPage.Jobs);
        TerminalRail.PageFor(InputKey.D4, InputModifier.Shift).ShouldBe(PilotPage.Spend);
        foreach (var (page, _, key) in TerminalRail.Keys.Where(k => k.Key != TerminalRail.SpendKey))
        {
            TerminalRail.PageFor(new InputEvent.KeyDown((InputKey)((int)InputKey.D0 + (key - '0')), InputModifier.None)).ShouldBe(page);
        }
        TerminalRail.PageFor(new InputEvent.KeyDown(InputKey.D4, InputModifier.Shift)).ShouldBe(PilotPage.Spend);
        TerminalRail.PageFor(new InputEvent.KeyDown(InputKey.D2, InputModifier.Ctrl)).ShouldBeNull();
    }

    [Fact]
    public void HandleTakesEveryPageKeyAndFocusesChatOnTwoAndNothingElse()
    {
        foreach (var (page, _, key) in TerminalRail.Keys.Where(k => k.Key != TerminalRail.SpendKey))
        {
            var shown = new List<PilotPage>();
            TerminalRail.Handle(new InputEvent.KeyDown((InputKey)((int)InputKey.D0 + (key - '0')), InputModifier.None), null, shown.Add, () => { }).ShouldBeTrue();
            shown.ShouldBe([page]);
        }
        var spend = new List<PilotPage>();
        TerminalRail.Handle(new InputEvent.KeyDown(InputKey.D4, InputModifier.Shift), null, spend.Add, () => { }).ShouldBeTrue();
        TerminalRail.Handle(null, new System.Text.Rune('$'), spend.Add, () => { }).ShouldBeTrue();
        spend.ShouldBe([PilotPage.Spend, PilotPage.Spend]);

        var focused = 0;
        TerminalRail.Handle(new InputEvent.KeyDown(InputKey.D2, InputModifier.None), null, _ => { }, () => focused++).ShouldBeTrue();
        focused.ShouldBe(1);
        TerminalRail.Handle(new InputEvent.KeyDown(InputKey.D3, InputModifier.None), null, _ => { }, () => focused++).ShouldBeTrue();
        focused.ShouldBe(1);

        var reached = new List<PilotPage>();
        TerminalRail.Handle(new InputEvent.KeyDown(InputKey.D2, InputModifier.Ctrl), null, reached.Add, () => focused++).ShouldBeFalse();
        TerminalRail.Handle(null, new System.Text.Rune('x'), reached.Add, () => focused++).ShouldBeFalse();
        reached.ShouldBeEmpty();
        focused.ShouldBe(1);
    }

    [Theory]
    [InlineData("emoji", 80)]
    [InlineData("initials", 80)]
    [InlineData("initials", 77)]
    public void ANarrowTerminalKeepsAllElevenEntriesOnScreenAndClickable(string modeName, int columns)
    {
        var mode = TerminalRail.Parse(modeName)!.Value;
        var shown = new List<PilotPage>();
        var tree = TerminalRail.Tree(PilotPage.Home, mode, null, IdeasOf(120), Now, shown.Add, null, columns);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, columns, 1), CellMeasureContext.PixelAuthored);

        foreach (var (page, key) in Expected)
        {
            var entry = EntryOf(arranged, page);
            (entry.Bounds.X + entry.Bounds.Width).ShouldBeLessThanOrEqualTo(columns);
            entry.Bounds.Width.ShouldBe(TerminalRail.NarrowEntryCells);
            TerminalRail.EntryText(page, mode, null, IdeasOf(120), Now, narrow: true).TrimEnd().ShouldEndWith(key.ToString());
        }
        var last = EntryOf(arranged, PilotPage.Spend);
        CellLayout.HitTest(arranged, last.Bounds.X + 2, last.Bounds.Y);
        shown.ShouldBe([PilotPage.Spend]);
    }

    [Theory]
    [InlineData("emoji")]
    [InlineData("initials")]
    public void EveryPagesEntryShowsInCellsWithItsKeyAtAFixedWidth(string modeName)
    {
        var mode = TerminalRail.Parse(modeName)!.Value;
        var arranged = Arrange(TerminalRail.Tree(PilotPage.Home, mode, null, null, Now, _ => { }));

        foreach (var (page, key) in Expected)
        {
            EntryOf(arranged, page).Bounds.Width.ShouldBe(TerminalRail.EntryCells);
            var text = TerminalRail.EntryText(page, mode, null, null, Now);
            text.Length.ShouldBe(TerminalRail.EntryCells);
            text.TrimEnd().ShouldEndWith(key.ToString());
        }
        Expected.Select(e => EntryOf(arranged, e.Page).Bounds.X).ShouldBe(Enumerable.Range(0, Expected.Length).Select(i => i * TerminalRail.EntryCells));
    }

    [Fact]
    public void EmojiModeDrawsTheEmojiAndInitialsModeLettersOfItsOwnForFlightsAndSettings()
    {
        TerminalRail.Glyph(PilotPage.Home, GlyphMode.Emoji, null, null, Now).ShouldBe("\U0001F3E0");
        // The plane and the cog are one char: U+FE0F asks for the two-wide emoji form.
        TerminalRail.Glyph(PilotPage.Flights, GlyphMode.Emoji, null, null, Now).ShouldBe("✈️");
        TerminalRail.Glyph(PilotPage.Settings, GlyphMode.Emoji, null, null, Now).ShouldBe("⚙️");
        TerminalRail.Glyph(PilotPage.Flights, GlyphMode.Initials, null, null, Now).ShouldBe("Fl");
        TerminalRail.Glyph(PilotPage.Settings, GlyphMode.Initials, null, null, Now).ShouldBe("Se");
        TerminalRail.Glyph(PilotPage.Home, GlyphMode.Initials, null, null, Now).ShouldBe("H");
        Expected.Select(e => TerminalRail.Glyph(e.Page, GlyphMode.Initials, null, null, Now)).ShouldAllBe(g => g.All(c => c < 128));
    }

    [Fact]
    public void TheActivePageIsMarkedWithABackgroundAndOnlyThatOne()
    {
        var arranged = Arrange(TerminalRail.Tree(PilotPage.Fleet, GlyphMode.Initials, null, null, Now, _ => { }));

        EntryOf(arranged, PilotPage.Fleet).Node.Background.ShouldBe(Palette.Current.RowFocusBg);
        Expected.Where(e => e.Page != PilotPage.Fleet).ShouldAllBe(e => EntryOf(arranged, e.Page).Node.Background == Palette.Current.RowAltBg);
    }

    [Fact]
    public void AClickOnAnEntrysCellShowsThatPage()
    {
        var shown = new List<PilotPage>();
        var arranged = Arrange(TerminalRail.Tree(PilotPage.Home, GlyphMode.Initials, null, null, Now, shown.Add));

        foreach (var (page, _) in Expected)
        {
            var entry = EntryOf(arranged, page);
            CellLayout.HitTest(arranged, entry.Bounds.X + 2, entry.Bounds.Y);
        }
        shown.ShouldBe(Expected.Select(e => e.Page));
    }

    [Fact]
    public void TheBackgroundTasksEntryFollowsJobsGlyphAndIdeasItsCount()
    {
        TerminalRail.Glyph(PilotPage.Jobs, GlyphMode.Emoji, null, null, Now).ShouldBe("📋");
        TerminalRail.Glyph(PilotPage.Jobs, GlyphMode.Emoji, JobsOf(JobState.Running), null, Now).ShouldBe("🔄");
        TerminalRail.Glyph(PilotPage.Jobs, GlyphMode.Emoji, JobsOf(JobState.Queued), null, Now).ShouldBe("🕓");
        TerminalRail.Glyph(PilotPage.Jobs, GlyphMode.Emoji, JobsOf(JobState.Failed), null, Now).ShouldBe("🔴");
        TerminalRail.Glyph(PilotPage.Jobs, GlyphMode.Initials, JobsOf(JobState.Failed), null, Now).ShouldBe("B!");

        TerminalRail.Glyph(PilotPage.Ideas, GlyphMode.Emoji, null, null, Now).ShouldBe("\U0001F4A1");
        TerminalRail.Glyph(PilotPage.Ideas, GlyphMode.Emoji, null, IdeasOf(3), Now).ShouldBe("\U0001F4A13");
        TerminalRail.Glyph(PilotPage.Ideas, GlyphMode.Initials, null, IdeasOf(3), Now).ShouldBe("I3");
        TerminalRail.EntryText(PilotPage.Ideas, GlyphMode.Emoji, null, IdeasOf(120), Now).Length.ShouldBe(TerminalRail.EntryCells);
    }

    [Fact]
    public void TheModeComesFromTheOptionOrTheEnvironment()
    {
        TerminalRail.Parse("emoji").ShouldBe(GlyphMode.Emoji);
        TerminalRail.Parse(" Initials ").ShouldBe(GlyphMode.Initials);
        TerminalRail.Parse("auto").ShouldBe(GlyphMode.Auto);
        TerminalRail.Parse("pictures").ShouldBeNull();
        TerminalRail.Detect(n => n == "WT_SESSION" ? "x" : null).ShouldBe(GlyphMode.Emoji);
        TerminalRail.Detect(n => n == "TERM_PROGRAM" ? "vscode" : null).ShouldBe(GlyphMode.Emoji);
        TerminalRail.Detect(n => n == "TERM_PROGRAM" ? "WezTerm" : null).ShouldBe(GlyphMode.Emoji);
        TerminalRail.Detect(n => n == "TERM_PROGRAM" ? "iTerm.app" : null).ShouldBe(GlyphMode.Emoji);
        TerminalRail.Detect(n => n == "KITTY_WINDOW_ID" ? "1" : null).ShouldBe(GlyphMode.Emoji);
        TerminalRail.Detect(_ => null).ShouldBe(GlyphMode.Initials);
        TerminalRail.Resolve(GlyphMode.Emoji, _ => null).ShouldBe(GlyphMode.Emoji);
        TerminalRail.Resolve(GlyphMode.Auto, _ => null).ShouldBe(GlyphMode.Initials);
    }

    private sealed class Recording : ICellSink
    {
        public List<string> Runs { get; } = [];
        public void MoveTo(int row, int column) { }
        public void SetPen(VtStyle style, bool opaque) { }
        public void SetLink(string? uri) { }
        public void Write(ReadOnlySpan<char> text) => Runs.Add(text.ToString());
    }

    private static string Describe(string glyph, int frame, List<string> runs) =>
        $"frame {frame} for {string.Concat(glyph.Select(c => $"U+{(int)c:X4} "))}sent runs [{string.Join(" | ", runs.Select(r => string.Concat(r.Select(c => $"{(int)c:X4} "))))}]";

    [Fact]
    public void AFlushSendsEachEmojisWholeUtf16SequenceInOneRunEvenWhenTwoShareAHighSurrogate()
    {
        var buffer = new CellBuffer();
        buffer.Resize(40, 1);
        var sink = new Recording();

        // Background tasks: 📋 (D83D DCCB), 🔴 (D83D DD34), 🔄 (D83D DD04): the high half is the same, only the low half differs.
        // Console.Lib's buffer emits only the changed low half for such a change (filed as SharpAstro/Console.Lib), so the bar blanks
        // an entry's glyph for one frame when it changes: TerminalRail.Memory, which pilot-tui draws through.
        var memory = new TerminalRail.Memory();
        var surrogates = 0;
        foreach (var jobs in new[] { null, JobsOf(JobState.Failed), JobsOf(JobState.Running), null })
        {
            var glyph = TerminalRail.Glyph(PilotPage.Jobs, GlyphMode.Emoji, jobs, null, Now);
            var row = TerminalRail.EntryText(PilotPage.Jobs, GlyphMode.Emoji, jobs, null, Now);
            var frames = 0;
            do
            {
                memory.Settling = false;
                buffer.MoveTo(0, 0);
                buffer.Write(memory.Entry(PilotPage.Jobs, row));
                sink.Runs.Clear();
                buffer.Flush(sink);
                // Whatever was emitted holds each surrogate pair whole: a lone half is a replacement character on screen.
                foreach (var run in sink.Runs)
                {
                    for (var i = 0; i < run.Length; i++)
                    {
                        if (char.IsHighSurrogate(run[i]))
                        {
                            (i + 1 < run.Length && char.IsLowSurrogate(run[i + 1])).ShouldBeTrue($"{Describe(glyph, frames, sink.Runs)} splits a pair");
                            i++;
                            surrogates++;
                        }
                        else
                        {
                            char.IsLowSurrogate(run[i]).ShouldBeFalse($"{Describe(glyph, frames, sink.Runs)} holds a lone low surrogate");
                        }
                    }
                }
                frames++;
            }
            while (memory.Settling && frames < 3);
            frames.ShouldBeLessThan(3);
            buffer.FrontRowText(0).ShouldStartWith(row);
            glyph.ShouldNotBeEmpty();
        }
        surrogates.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(190, 44, true)] // about the owner's terminal, with the cards panel
    [InlineData(130, 40, true)]
    [InlineData(110, 40, false)]
    [InlineData(100, 24, false)]
    public void InTheFrameTheBarIsOneRowAtTheTopAndThePageHasTheRest(int columns, int rows, bool panel)
    {
        // pilot-tui's frame as Program.cs arranges it. The bar once ended in .Stretch(), which set its height to Star over its HFixed,
        // so in the frame's VStack it shared the height with the page half and half (pilot #1027).
        var rail = TerminalRail.Tree(PilotPage.Home, GlyphMode.Emoji, null, null, Now, _ => { }, null, columns);
        var page = Layout.Builder.Text("the page", 12f).Stretch();
        var cards = panel ? Layout.Builder.Text("the cards", 12f).Stretch() : null;
        var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, cards), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);

        foreach (var (entry, _, _) in TerminalRail.Keys)
        {
            var bounds = EntryOf(arranged, entry).Bounds;
            (bounds.Y, bounds.Height).ShouldBe((0, 1), $"{entry}'s entry");
        }
        Layout.ArrangedNode<int> Leaf(string text) => arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == text);
        var width = panel ? columns - (int)MathF.Round(CardsView<RgbaImage>.Width / 8f) : columns;
        Leaf("the page").Bounds.ShouldBe(new Rect<int>(0, 1, width, rows - 1));
        if (panel)
        {
            Leaf("the cards").Bounds.ShouldBe(new Rect<int>(width, 1, columns - width, rows - 1));
        }
    }
}
