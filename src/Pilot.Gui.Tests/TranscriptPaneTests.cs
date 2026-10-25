using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A session's transcript reads as a list: a tool call is one folded line (check or cross, the tool, what it says it does)
/// and a click unfolds the command and its output; a failed call shows its last lines while folded.
/// </summary>
public sealed class TranscriptPaneTests
{
    private static string Output(int lines) => string.Join("\n", Enumerable.Range(1, lines).Select(i => $"output-line-{i}"));

    private static readonly DateTimeOffset Called = new(2026, 10, 25, 9, 40, 12, TimeSpan.Zero);

    private static TranscriptEntry[] Entries() =>
    [
        new() { Index = 0, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "dotnet test src/Pilot.slnx --no-build", Description = "Run every test", ToolUseId = "t1", At = Called },
        new() { Index = 1, Kind = TranscriptKind.ToolResult, Text = Output(12), ToolUseId = "t1" },
        new() { Index = 2, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "dotnet build src/Pilot.slnx", Description = "Build the solution", ToolUseId = "t2" },
        new() { Index = 3, Kind = TranscriptKind.Error, Text = "restore-line-1\nrestore-line-2\nerror-line-3\nerror-line-4\nerror-line-5", ToolUseId = "t2" },
        new() { Index = 4, Kind = TranscriptKind.ToolUse, Tool = "Grep", Text = "TreeRow", ToolUseId = "t3" },
        new() { Index = 5, Kind = TranscriptKind.Assistant, Text = "assistant-words", At = Called.AddSeconds(75) },
    ];

    // WCAG 2.x relative luminance and contrast ratio.
    private static double Luminance(RGBAColor32 c)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static Layout.Content.Text TextOf(ImmutableArray<Layout.ArrangedNode<int>> arranged, string value) =>
        arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().First(t => t.Value.TrimEnd() == value);

    private static async Task<(SessionsView<RgbaImage> View, SessionsScreen Screen, RgbaImageRenderer Renderer)> Open(SubagentRow[]? subagents = null)
    {
        var renderer = new RgbaImageRenderer(1280, 800);
        var view = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = SessionsCapture.ScreenFor(SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath), "sample");
        if (subagents is not null)
        {
            screen = screen with { Lines = [screen.Lines[0] with { Row = screen.Lines[0].Row with { Subagents = subagents } }, .. screen.Lines.Skip(1)] };
        }
        view.Transcript.Load = (_, _, after, _) => Task.FromResult(after < 0
            ? new TranscriptPage { Total = 6, Entries = Entries() }
            : new TranscriptPage { Total = 6, Entries = [] });
        view.Tree(screen);
        view.HandleInput(new InputEvent.KeyDown(InputKey.Enter)).ShouldBeTrue();
        for (var i = 0; i < 100 && view.Transcript.Entries.Count < 6; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        return (view, screen, renderer);
    }

    private static string Described(SessionsView<RgbaImage> view, SessionsScreen screen) =>
        CellLayout.Describe(Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 160, 80), CellMeasureContext.PixelAuthored));

    // Everything the described tree puts on the cell row that holds `text`: a dump has a node a line, each with its cell position.
    private static string RowOf(string described, string text)
    {
        var lines = described.ReplaceLineEndings("\n").Split('\n');
        var at = lines.Single(l => l.Contains($"\"{text}\"", StringComparison.Ordinal));
        var y = Regex.Match(at, @"\((\d+),(\d+) ").Groups[2].Value;
        return string.Join("\n", lines.Where(l => Regex.Match(l, @"\((\d+),(\d+) ") is { Success: true } m && m.Groups[2].Value == y));
    }

    private static void Click(SessionsView<RgbaImage> view, SessionsScreen screen, string action)
    {
        view.Render(screen, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);
    }

    [Fact]
    public async Task EachEntryAndCallSaysWhenInThisMachinesTime()
    {
        var (view, screen, renderer) = await Open();
        using var _ = renderer;

        var described = Described(view, screen);

        RowOf(described, "Run every test").ShouldContain(Called.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        RowOf(described, "claude").ShouldContain(Called.AddSeconds(75).ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        view.Transcript.Close();
    }

    [Fact]
    public async Task ACallIsOneFoldedLineWithItsDescriptionAndUnfoldsOnItsToggleToTheCommandAndOutput()
    {
        var (view, screen, renderer) = await Open();
        using var _ = renderer;

        var folded = Described(view, screen);
        var line = RowOf(folded, "Run every test");
        line.ShouldContain("✓");
        line.ShouldContain("Bash");
        // The command and the output wait behind the click.
        folded.ShouldNotContain("dotnet test src/Pilot.slnx");
        folded.ShouldNotContain("output-line-1");
        folded.ShouldNotContain("output-line-12");
        // A call without a description says its short form.
        RowOf(folded, "TreeRow").ShouldContain("Grep");

        Click(view, screen, "transcript:call:0");
        var unfolded = Described(view, screen);
        unfolded.ShouldContain("Run every test");
        unfolded.ShouldContain("dotnet test src/Pilot.slnx --no-build");
        unfolded.ShouldContain("output-line-1");
        unfolded.ShouldContain("output-line-12");

        Click(view, screen, "transcript:call:0");
        Described(view, screen).ShouldNotContain("output-line-1");
        view.Transcript.Close();
    }

    [Fact]
    public async Task AFailedCallShowsACrossAndItsLastLinesWhileFolded()
    {
        var (view, screen, renderer) = await Open();
        using var _ = renderer;

        var folded = Described(view, screen);
        RowOf(folded, "Build the solution").ShouldContain("✗");
        folded.ShouldContain("error-line-3");
        folded.ShouldContain("error-line-4");
        folded.ShouldContain("error-line-5");
        folded.ShouldNotContain("restore-line-1");
        folded.ShouldNotContain("dotnet build src/Pilot.slnx");

        Click(view, screen, "transcript:call:2");
        var unfolded = Described(view, screen);
        unfolded.ShouldContain("dotnet build src/Pilot.slnx");
        unfolded.ShouldContain("restore-line-1");
        view.Transcript.Close();
    }

    [Fact]
    public async Task UnfoldedCommandAndOutputSitInAShadedBlockInColoursMeetingTheContrastFloor()
    {
        var (view, screen, renderer) = await Open();
        using var _ = renderer;

        Click(view, screen, "transcript:call:0");
        Click(view, screen, "transcript:call:2");
        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 160, 120), CellMeasureContext.PixelAuthored);

        foreach (var (line, failed) in new[] { ("output-line-1", false), ("dotnet test src/Pilot.slnx --no-build", false), ("restore-line-1", true) })
        {
            var text = TextOf(arranged, line);
            // A descendant of a node painted with the block's shade: the arrangement is pre-order, so a node's subtree is what
            // follows it while the depth stays deeper.
            var at = arranged.ToList().FindIndex(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == line);
            at.ShouldBeGreaterThan(0);
            // Walking back, only the nodes shallower than the last one are its ancestors.
            var shaded = false;
            var depth = arranged[at].Depth;
            for (var i = at - 1; i >= 0 && !shaded; i--)
            {
                if (arranged[i].Depth < depth)
                {
                    depth = arranged[i].Depth;
                    shaded = arranged[i].Node.Background == TranscriptPane.OutputShade;
                }
            }
            shaded.ShouldBeTrue($"{line} sits in a shaded block");
            // Readable on the shade and on the pane behind it, WCAG's 4.5:1 floor.
            Contrast(text.Color, TranscriptPane.OutputShade).ShouldBeGreaterThanOrEqualTo(4.5);
            Contrast(text.Color, TranscriptPane.Ground).ShouldBeGreaterThanOrEqualTo(4.5);
            (text.Color == TranscriptPane.OutputFailed).ShouldBe(failed);
        }
        // The shade is a block of its own, apart from the pane.
        Contrast(TranscriptPane.OutputShade, TranscriptPane.Ground).ShouldBeGreaterThan(1.0);
        TranscriptPane.OutputShade.ShouldNotBe(TranscriptPane.Ground);
        view.Transcript.Close();
    }

    [Fact]
    public async Task ClaudesWordsAreAtFullContrastAndSpacedFurtherFromToolLinesThanTheyAreFromEachOther()
    {
        var (view, screen, renderer) = await Open();
        using var _ = renderer;

        var arranged = Layout.Engine.Arrange(view.Tree(screen), new Rect<int>(0, 0, 160, 120), CellMeasureContext.PixelAuthored);
        // Full contrast (WCAG AAA, 7:1) for the words and what they emphasise; a link and inline code still clear the floor.
        var words = TextOf(arranged, "assistant-words");
        Contrast(words.Color, TranscriptPane.Ground).ShouldBeGreaterThanOrEqualTo(7.0);
        Contrast(TranscriptPane.Prose.Text, TranscriptPane.Ground).ShouldBeGreaterThanOrEqualTo(7.0);
        Contrast(TranscriptPane.Prose.Strong, TranscriptPane.Ground).ShouldBeGreaterThanOrEqualTo(7.0);
        Contrast(TranscriptPane.Prose.Link, TranscriptPane.Ground).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(TranscriptPane.Prose.Code, TranscriptPane.Prose.CodeBackground).ShouldBeGreaterThanOrEqualTo(4.5);
        // More room around Claude's words than between tool lines: padding on its row, above the gap tool lines keep.
        var row = arranged.Select(a => a.Node).First(n => n is Layout.Node.Stack { Axis: Layout.Axis.Horizontal } s
            && s.Children.Any(c => c is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "claude" } }));
        row.PadDown.ShouldBe(TranscriptPane.ProseSpace);
        TranscriptPane.ProseSpace.ShouldBeGreaterThan(TranscriptPane.ToolGap);
        view.Transcript.Close();
    }

    private static SubagentRow[] Reviewers(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new SubagentRow { Id = $"r{i}", Type = "pilot:reviewer", Description = $"task {i}", Working = i % 2 == 1 })];

    [Fact]
    public async Task ThirteenReviewersAreLabelledWithoutTheirPluginPrefixNumberedInTheOrderTheyStartedAndNamingTheirTask()
    {
        var (view, screen, renderer) = await Open(Reviewers(13));
        using var _ = renderer;
        // Room for every chip: only the labels are looked at.
        view.Transcript.ChipRowChars = 100_000;

        var described = Described(view, screen);
        for (var i = 1; i <= 13; i++)
        {
            described.ShouldContain($"reviewer {i}: task {i}");
        }
        described.ShouldNotContain("pilot:reviewer");
        // One of its type, a subagent is named by its type alone.
        view.Transcript.MenuItems.ShouldBeEmpty();
        view.Transcript.Close();
    }

    [Fact]
    public async Task AChipWithNoDescriptionIsLabelledFromItsPromptsFirstLineAndAnAgentOfItsOwnTypeHasNoNumber()
    {
        var (view, screen, renderer) = await Open([
            new() { Id = "a1", Type = "Explore", Prompt = "Find the clock in the tree" },
            new() { Id = "b2", Type = "pilot:reviewer", Description = "Check the fix", Prompt = "ignored: a description wins" },
            new() { Id = "c3", Type = "pilot:reviewer", Prompt = "Review a prompt much longer than a chip can hold" },
            new() { Id = "d4" },
        ]);
        using var _ = renderer;
        view.Transcript.ChipRowChars = 100_000;

        var described = Described(view, screen);
        described.ShouldContain("Explore: Find the clock in the tree");
        described.ShouldContain("reviewer 1: Check the fix");
        described.ShouldNotContain("ignored");
        // Clipped to the chip, with an ellipsis.
        described.ShouldContain("reviewer 2: Review a prompt much longer…");
        described.ShouldContain("○ subagent");
        view.Transcript.Close();
    }

    [Fact]
    public async Task MainIsFirstAndChipsPastOneRowGoIntoADropdownThatOpensTheirTranscript()
    {
        var (view, screen, renderer) = await Open(Reviewers(13));
        using var _ = renderer;
        view.Transcript.ChipRowChars = 85;

        var described = Described(view, screen);
        var chips = RowOf(described, "main");
        chips.ShouldContain("reviewer 1: task 1");
        chips.IndexOf("main", StringComparison.Ordinal).ShouldBeLessThan(chips.IndexOf("reviewer 1: task 1", StringComparison.Ordinal));
        chips.ShouldContain("more ▾");
        described.ShouldNotContain("reviewer 13: task 13");
        view.Transcript.MenuItems.ShouldBeEmpty();

        // The "more" chip opens the rest, in order, ending at the last.
        Click(view, screen, "transcript:agents");
        var items = view.Transcript.MenuItems;
        items.Count.ShouldBeGreaterThan(0);
        items[^1].Label.ShouldContain("reviewer 13: task 13");
        items[0].Label.ShouldNotContain("reviewer 1:");
        var numbers = items.Select(i => int.Parse(Regex.Match(i.Label, @"reviewer (\d+):").Groups[1].Value)).ToList();
        numbers.ShouldBe([.. Enumerable.Range(numbers[0], 14 - numbers[0])]);
        view.Render(screen, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        regions.Count(r => r.Result is HitResult.ListItemHit { ListId: DropdownMenuState<string>.ListId }).ShouldBe(items.Count);

        // Choosing one opens that subagent's transcript.
        items[^1].OnChoose.ShouldNotBeNull().Invoke();
        view.Transcript.Agent.ShouldBe("r13");
        view.Transcript.Close();
    }
}
