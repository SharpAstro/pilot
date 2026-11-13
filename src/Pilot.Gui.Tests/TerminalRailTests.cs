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
        (PilotPage.Jobs, '6'), (PilotPage.Fleet, '7'), (PilotPage.Settings, '8'), (PilotPage.PullRequests, '9'), (PilotPage.Ideas, '0'),
        (PilotPage.Spend, '$'),
    ];

    private static JobsScreen JobsOf(params JobState[] states) =>
        new(JobLine.Build(states.Select((s, i) => new JobRow
        {
            Id = $"j{i}", Command = "dotnet build", Cwd = "/r", Timeout = TimeSpan.FromMinutes(5), State = s, StartedAt = Now, EndedAt = s == JobState.Failed ? Now : null,
        }), Now), "t", Now);

    private static IdeasScreen IdeasOf(int noted) =>
        new([.. Enumerable.Range(1, noted).Select(i => new IdeaRow { Id = $"I{i}", Repo = "pilot", Title = $"idea {i}", State = IdeaState.Noted })], [], [], "t", Now);

    private static System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> Arrange(Layout.Node tree) =>
        Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 100, 1), CellMeasureContext.PixelAuthored);

    private static Layout.ArrangedNode<int> EntryOf(System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<int>> arranged, PilotPage page) =>
        arranged.First(a => a.Node.Hit is HitResult.ButtonHit hit && hit.Action == TerminalRail.Action(page));

    [Fact]
    public void TheKeysFollowTheGuisCtrlDigitOrderAndSpendHasADollar()
    {
        TerminalRail.Keys.Select(k => (k.Page, k.Key)).ShouldBe(Expected);
        TerminalRail.PageFor('0').ShouldBe(PilotPage.Ideas);
        TerminalRail.PageFor('$').ShouldBe(PilotPage.Spend);
        TerminalRail.PageFor('x').ShouldBeNull();
        TerminalRail.PageFor(InputKey.D6, InputModifier.None).ShouldBe(PilotPage.Jobs);
        TerminalRail.PageFor(InputKey.D4, InputModifier.Shift).ShouldBe(PilotPage.Spend);
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

    [Fact]
    public void AFlushSendsEachEmojisWholeUtf16SequenceInOneRunEvenWhenTwoShareAHighSurrogate()
    {
        var buffer = new CellBuffer();
        buffer.Resize(40, 1);
        var sink = new Recording();

        // Background tasks: 📋 (D83D DCCB), 🔴 (D83D DD34), 🔄 (D83D DD04): the high half is the same, only the low half differs.
        foreach (var jobs in new[] { null, JobsOf(JobState.Failed), JobsOf(JobState.Running), null })
        {
            var row = TerminalRail.EntryText(PilotPage.Jobs, GlyphMode.Emoji, jobs, null, Now);
            buffer.MoveTo(0, 0);
            buffer.Write(row);
            sink.Runs.Clear();
            buffer.Flush(sink);
            var glyph = TerminalRail.Glyph(PilotPage.Jobs, GlyphMode.Emoji, jobs, null, Now);
            // Whatever was emitted holds the glyph whole, or none of its chars.
            if (string.Concat(sink.Runs).Any(char.IsSurrogate))
            {
                sink.Runs.ShouldContain(r => r.Contains(glyph), $"the runs {string.Join("|", sink.Runs)} split {glyph}");
            }
            buffer.FrontRowText(0).ShouldStartWith(row);
        }
    }
}
