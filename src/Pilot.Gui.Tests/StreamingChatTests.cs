using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The chat as the stream watchtower feeds it (PIL855): the words of the turn so far in a reply bubble marked as still writing, a message
/// sent while it works as "queued behind &lt;what it is doing&gt;" with an Answer now pill that calls the node's chat-now, and the status line
/// under the box with the last and median first word and turn length. Drawn on the CPU from sample pages at a narrow and a wide size, with
/// long realistic content, in each palette.
/// </summary>
public sealed class StreamingChatTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 9, 40, 12, TimeSpan.Zero);

    internal const string LongPath = @"C:\Users\SebastianGodelet\source\repos\sharpastro\pilot\.claude\worktrees\pil855\src\Pilot.Gui.Tests\StreamingChatTests.cs";

    private static TranscriptEntry[] Conversation() =>
    [
        new() { Index = 0, Kind = TranscriptKind.User, Text = "what is flying right now?", At = At },
        new() { Index = 1, Kind = TranscriptKind.ToolUse, Tool = "Bash", Text = "pilot status", Description = "Read the flights", ToolUseId = "t1", At = At.AddSeconds(3) },
        new() { Index = 2, Kind = TranscriptKind.ToolResult, Text = "PIL855 flying chat-view", ToolUseId = "t1" },
        new() { Index = 3, Kind = TranscriptKind.Assistant, Text = "Three flights are in the air: **PIL855** (chat-view), PIL856 and PIL857.", At = At.AddSeconds(9) },
        new() { Index = 4, Kind = TranscriptKind.User, Text = "and which of them is waiting on me?", At = At.AddSeconds(30) },
    ];

    private static string Words(bool lengthen) => lengthen
        ? "PIL855 has two legs left: the chat view and its tests. The chat view leg is drawing the reply in progress now, " +
          "and the next card to look at is " + LongPath + " (a path with no space in it, longer than a column), so it will wrap rather than run off the bubble.\n\n" +
          "- **chat-view:** the bubble, the queued line and the status line\n- **review:** waits for the leg"
        : "PIL855 has two legs left";

    /// <summary>
    /// A page from a watchtower in the middle of a turn: the reply so far, what it is doing, two lines waiting behind it (the first can be
    /// answered now) and the last twenty turns' timings. Entries and total are the caller's.
    /// </summary>
    internal static ChatPage Tail(bool lengthen) => new()
    {
        Entries = [],
        Total = 0,
        Working = true,
        Partial = Words(lengthen),
        Doing = lengthen ? "running pilot status --flights --legs --cards and reading what it gives back for every flight in the fleet" : "running pilot status",
        Queued = lengthen ? ["can you also tell me which of the flights needs me, and why? " + LongPath, "and the one after that"] : ["and which of them is waiting on me?", "and the one after that"],
        QueuedIds = ["s1", "s2"],
        QueuedBehind =
        [
            lengthen ? "queued behind running pilot status --flights --legs --cards and reading what it gives back for every flight in the fleet" : "queued behind running pilot status",
            "queued behind the message before it",
        ],
        Timings = Timings(20),
    };

    internal static ChatTiming[] Timings(int n) =>
        [.. Enumerable.Range(0, n).Select(i => new ChatTiming
        {
            StartedAt = At.AddMinutes(i),
            FirstWordMs = i == n - 1 ? 3100 : 2500 + (i % 5) * 900,
            TurnMs = i == n - 1 ? 28_000 : 15_000 + (i % 7) * 4_000,
            WaitedMs = i % 4 == 3 ? 6_000 : null,
        })];

    private static ChatPage Whole(ChatPage tail, TranscriptEntry[]? entries = null)
    {
        entries ??= Conversation();
        return tail with { Entries = entries, Total = entries.Length };
    }

    private static ChatView<RgbaImage> Chat(RgbaImageRenderer renderer, Func<ChatPage> page, Func<string, Task<string?>>? answerNow = null, bool night = false)
    {
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        chat.Load = (after, _) =>
        {
            var current = page();
            return Task.FromResult(after < 0 ? current : current with { Entries = [.. current.Entries.Where(e => e.Index > after)] });
        };
        chat.OnSend = _ => Task.FromResult<string?>(null);
        chat.OnAnswerNow = answerNow;
        if (night)
        {
            chat.Night = new NightSwitch { On = true };
        }
        return chat;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        done().ShouldBeTrue();
    }

    /// <summary>Opens the chat, waits for the first read, and leaves it following: <see cref="ChatView{TSurface}.Refresh"/> reads again.</summary>
    private static async Task Open(ChatView<RgbaImage> chat, int entries)
    {
        chat.Open();
        await Until(() => chat.Entries.Count >= entries);
    }

    private static string Described(ChatView<RgbaImage> chat, int rows = 120) =>
        string.Join(' ', Layout.Engine.Arrange(chat.Tree(), new Rect<int>(0, 0, 160, rows), CellMeasureContext.PixelAuthored)
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()));

    private static List<string> Actions(ChatView<RgbaImage> chat)
    {
        chat.Render(new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        return [.. regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action)];
    }

    private static void Click(ChatView<RgbaImage> chat, string action)
    {
        chat.Render(new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);
    }

    private static ImmutableArray<Layout.ArrangedNode<float>> Pixels(ChatView<RgbaImage> chat, RgbaImageRenderer renderer, int width, int height) =>
        Layout.Engine.Arrange(chat.Tree(), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

    [Fact]
    public async Task ThePartialWordsAreAReplyBubbleMarkedAsStillWritingBeforeTheQueuedLines()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Whole(Tail(false)));
        await Open(chat, 5);
        chat.Close();

        var text = Described(chat);
        text.ShouldContain("watchtower writing…");
        text.ShouldContain("PIL855 has two legs left");
        text.IndexOf("Three flights are in the air", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("writing…", StringComparison.Ordinal));
        text.IndexOf("writing…", StringComparison.Ordinal).ShouldBeLessThan(text.IndexOf("queued behind", StringComparison.Ordinal));
        text.ShouldContain("● running pilot status");
    }

    [Fact]
    public async Task TheBubbleKeepsRoomForItsFirstLinesSoTheRowsUnderItDoNotJumpAsWordsArrive()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var partial = "Pil";
        var chat = Chat(renderer, () => Whole(Tail(false) with { Partial = partial }));
        await Open(chat, 5);
        float QueuedY() => Pixels(chat, renderer, 1280, 800).Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "queued" } }).Select(a => a.Bounds.Y).First();

        var before = QueuedY();
        partial = "Pil is flying two legs now and the third is held";
        chat.Refresh();
        await Until(() => Described(chat).Contains("third is held"));
        chat.Close();

        QueuedY().ShouldBe(before, 0.5f);
        before.ShouldBeGreaterThan(0f);
    }

    [Fact]
    public async Task TheFinalEntryReplacesTheBubbleWithNoDuplicate()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var page = Whole(Tail(false) with { Queued = null, QueuedIds = null, QueuedBehind = null });
        var chat = Chat(renderer, () => page);
        await Open(chat, 5);
        Described(chat).ShouldContain("writing…");

        var final = new TranscriptEntry { Index = 5, Kind = TranscriptKind.Assistant, Text = Words(false) + " and the chat view is one of them.", At = At.AddSeconds(40) };
        page = Whole(Tail(false) with { Working = false, Partial = null, Doing = null, Queued = null, QueuedIds = null, QueuedBehind = null }, [.. Conversation(), final]);
        chat.Refresh();
        await Until(() => chat.Entries.Count == 6);
        chat.Close();

        var text = Described(chat);
        text.ShouldNotContain("writing…");
        text.Split("PIL855 has two legs left").Length.ShouldBe(2);

        // A page that still carries the words as a partial beside the same words as the last entry draws them once.
        var twice = Chat(renderer, () => Whole(Tail(false) with { Partial = final.Text, Queued = null, QueuedIds = null, QueuedBehind = null }, [.. Conversation(), final]));
        await Open(twice, 6);
        twice.Close();
        Described(twice).Split("one of them").Length.ShouldBe(2);
    }

    [Fact]
    public async Task WhileThePointerIsOverTheListTheWordsAlreadyShownHoldStill()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var partial = "PIL855 has two legs";
        var chat = Chat(renderer, () => Whole(Tail(false) with { Partial = partial, Queued = null, QueuedIds = null, QueuedBehind = null }));
        await Open(chat, 5);
        chat.Render(new RectF32(0, 0, 1280, 800));
        var list = chat.PageSelectionViewport;
        list.Width.ShouldBeGreaterThan(0f, "the list's viewport is known after a frame");
        chat.Pointer = (list.X + list.Width / 2f, list.Y + list.Height / 2f);
        // What the page says, arranged in the pixels of the frame the pointer is in: Described arranges in cells, which would rebind the
        // list's viewport to cell units and put the pointer outside it.
        string Shown() => string.Join(' ', Pixels(chat, renderer, 1280, 800).Select(a => a.Node).OfType<Layout.Node.Leaf>()
            .Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim()));
        Shown().ShouldContain("PIL855 has two legs");

        partial = "PIL855 has two legs left and then a good deal more words that would push the page";
        chat.Refresh();
        // The read carries the new words; the list, under the pointer, still shows what it showed when the pointer came.
        await Until(() => chat.Partial == partial);
        var held = Shown();
        held.Contains("good deal more", StringComparison.Ordinal).ShouldBeFalse($"pointer {chat.Pointer}, list {chat.PageSelectionViewport}");
        held.ShouldContain("PIL855 has two legs");

        chat.Pointer = null;
        Shown().ShouldContain("good deal more");
        chat.Close();
    }

    [Fact]
    public async Task AQueuedLineReadsQueuedBehindWhatItIsDoingWithAnAnswerNowPillThatCallsTheCommand()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var asked = new List<string>();
        var chat = Chat(renderer, () => Whole(Tail(false)), id =>
        {
            asked.Add(id);
            return Task.FromResult<string?>(null);
        });
        await Open(chat, 5);
        chat.Close();

        var text = Described(chat);
        text.ShouldContain("queued behind running pilot status");
        text.ShouldContain("queued behind the message before it");
        text.ShouldContain("Answer now");
        // Only the line taken next can be answered now: the second line's pill would only be refused, so it is not drawn.
        Actions(chat).Where(a => a.StartsWith("chat:now:", StringComparison.Ordinal)).ShouldBe(["chat:now:s1"]);

        Click(chat, "chat:now:s1");
        await Until(() => asked.Count == 1);
        asked.ShouldBe(["s1"]);
    }

    [Fact]
    public async Task TheAnswerNowPillIsWorkingWhileTheNodeHasItAndARefusalIsSaidUnderTheLine()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var node = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = Chat(renderer, () => Whole(Tail(false)), _ => node.Task);
        // A refusal marks the pill "refused" for a moment in its place (every node-bound button does); the test looks past it.
        chat.Presses.DoneFor = TimeSpan.Zero;
        await Open(chat, 5);
        chat.Close();
        Click(chat, "chat:now:s1");

        // The state is the node's: working until it answers, no second press.
        var working = Described(chat);
        working.ShouldContain("Answering…");
        Actions(chat).ShouldNotContain("chat:now:s1");

        node.SetResult("refused: nothing is running: the watchtower is idle, so there is no turn to cancel");
        await Until(() => Described(chat).Contains("nothing is running"));
        var refused = Described(chat);
        refused.ShouldContain("refused: nothing is running");
        // The pill is back for another try; the reason stays under that line only.
        Actions(chat).ShouldContain("chat:now:s1");
        refused.IndexOf("queued behind running pilot status", StringComparison.Ordinal).ShouldBeLessThan(refused.IndexOf("refused: nothing", StringComparison.Ordinal));
        refused.IndexOf("refused: nothing", StringComparison.Ordinal).ShouldBeLessThan(refused.IndexOf("queued behind the message before it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoAnswerNowPillWithNothingRunningWithoutTheCommandOrFromANodeThatSaysNothingOfWhatItWaitsBehind()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var idle = Chat(renderer, () => Whole(Tail(false) with { Working = false, Partial = null, Doing = null }), _ => Task.FromResult<string?>(null));
        await Open(idle, 5);
        idle.Close();
        Actions(idle).ShouldNotContain("chat:now:s1");

        var without = Chat(renderer, () => Whole(Tail(false)), null);
        await Open(without, 5);
        without.Close();
        Actions(without).ShouldNotContain("chat:now:s1");
        Described(without).ShouldContain("queued behind running pilot status");

        // An older node: no QueuedBehind, so the old line, and no pill for a command it does not have.
        var older = Chat(renderer, () => Whole(Tail(false) with { QueuedBehind = null, Partial = null, Doing = null, Timings = null }), _ => Task.FromResult<string?>(null));
        await Open(older, 5);
        older.Close();
        var text = Described(older);
        text.ShouldContain("not read yet: it reads this at its next step");
        text.ShouldNotContain("Answer now");
        text.ShouldNotContain("first word");
    }

    [Fact]
    public void TheStatusLineGivesTheLastAndMedianFirstWordAndTurnLength()
    {
        ChatStatus.Line(null).ShouldBeNull();
        ChatStatus.Line([]).ShouldBeNull();
        var one = ChatStatus.Line([new ChatTiming { StartedAt = At, FirstWordMs = 3100, TurnMs = 28_000 }]);
        one.ShouldBe("first word 3.1 s, turn 28 s · last 1 message");
        var timings = new[]
        {
            new ChatTiming { StartedAt = At, FirstWordMs = 5000, TurnMs = 40_000 },
            new ChatTiming { StartedAt = At, FirstWordMs = null, TurnMs = 9_000 },
            new ChatTiming { StartedAt = At, FirstWordMs = 3400, TurnMs = 125_000 },
            new ChatTiming { StartedAt = At, FirstWordMs = 3100, TurnMs = 28_000 },
        };
        ChatStatus.Line(timings).ShouldBe("first word 3.1 s (median 3.4 s), turn 28 s (median 34 s) · last 4 messages");
        ChatStatus.Line([timings[0], timings[1]]).ShouldBe("no words in the last turn, turn 9.0 s (median 25 s) · last 2 messages");
        ChatStatus.Duration(850).ShouldBe("850 ms");
        ChatStatus.Duration(125_000).ShouldBe("2 min 5 s");
        ChatStatus.MedianOf([4, 1, 3, 2]).ShouldBe(2);
    }

    [Fact]
    public async Task TheStatusLineIsUnderTheBoxCutWithAnEllipsisWhereNarrowWithTheWholeTextOnHoverAndHiddenWithNoTimings()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Whole(Tail(false)));
        await Open(chat, 5);
        chat.Close();
        var whole = ChatStatus.Line(Timings(20))!;
        whole.ShouldBe("first word 3.1 s (median 4.3 s), turn 28 s (median 27 s) · last 20 messages");

        // Wide: the whole line is drawn, nothing cut; it carries its whole text on hover.
        var wide = Pixels(chat, renderer, 1280, 800);
        var line = wide.Single(a => a.Node.Tooltip == whole);
        line.Truncated.ShouldBeFalse();
        var editor = wide.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextEditor });
        line.Bounds.Y.ShouldBeGreaterThanOrEqualTo(editor.Bounds.Y + editor.Bounds.Height - 0.5f);

        // Narrow: one line, cut, the whole text still on hover.
        var narrow = Pixels(chat, renderer, 260, 800).Single(a => a.Node.Tooltip == whole);
        narrow.Truncated.ShouldBeTrue();
        narrow.Bounds.Height.ShouldBeLessThanOrEqualTo(20f);

        var none = Chat(renderer, () => Whole(Tail(false) with { Timings = null }));
        await Open(none, 5);
        none.Close();
        Pixels(none, renderer, 1280, 800).Where(a => a.Node.Tooltip is { } tip && tip.Contains("first word", StringComparison.Ordinal)).ShouldBeEmpty();
    }

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

    [Fact]
    public void TheNewTextsMeetFourPointFiveToOneOnTheirDayBackgroundsAndTheNightPaletteIsItsOwn()
    {
        Palette.Use(null);
        // The status line and the hint under the box sit on the foot's ground; "queued behind", the pill's words and the refusal on the page's.
        Contrast(TranscriptEntryView.Dim, Palette.Current.RowAltBg).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(TranscriptEntryView.Dim, TranscriptPane.Ground).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(TranscriptEntryView.Failed, TranscriptPane.Ground).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(TranscriptEntryView.Bright, TranscriptPane.Ground).ShouldBeGreaterThanOrEqualTo(4.5);
        // Night is red on black by design (the eye stays dark-adapted): the new texts read the palette's own Dim, as every other line does.
        Palette.Use(new NightSwitch { On = true });
        TranscriptEntryView.Dim.ShouldBe(Palette.Night.Dim);
        Palette.Use(null);
    }

    public static IEnumerable<TheoryDataRow<int, bool, bool>> Sizes() =>
        from size in new[] { 500, 1280, 2000 }
        from night in new[] { false, true }
        from lengthen in new[] { false, true }
        select new TheoryDataRow<int, bool, bool>(size, night, lengthen) { TestDisplayName = $"{size}px {(night ? "night" : "day")} {(lengthen ? "long" : "sample")}" };

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task NoTextOverlapsAtNarrowAndWideSizesWithLongContentInEachPalette(int width, bool night, bool lengthen)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 800);
        var chat = Chat(renderer, () => Whole(Tail(lengthen), Many(lengthen ? 30 : 4, lengthen)), _ => Task.FromResult<string?>(null), night);
        await Open(chat, lengthen ? 30 : 4);
        chat.Close();

        var arranged = Pixels(chat, renderer, width, 800);
        TextOverlap.Faults(arranged).ShouldBeEmpty();
        // A frame draws it all: the bubble, the queued lines, the status line.
        chat.Render(new RectF32(0, 0, width, 800));
    }

    /// <summary>Feed entries of the chat, many and long where asked.</summary>
    private static TranscriptEntry[] Many(int n, bool lengthen) =>
        [.. Enumerable.Range(0, n).Select(i => i % 3 == 0
            ? new TranscriptEntry { Index = i, Kind = TranscriptKind.User, Text = lengthen ? $"Merge PIL{i}'s PR once CI is green and tell me what {LongPath} changed" : "Merge it.", At = At.AddSeconds(i) }
            : new TranscriptEntry { Index = i, Kind = TranscriptKind.Assistant, Text = lengthen ? Words(true) : $"Done ({i}).", At = At.AddSeconds(i) })];

    [Fact]
    public async Task CapturesBeforeAndAfterAtNarrowAndWideSizes()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var width in new[] { 1280, 2000 })
        {
            foreach (var night in new[] { false, true })
            {
                var tag = $"{width}{(night ? "-night" : "")}";
                // Before: what an older node's page gave (words only when finished, a dim "not read yet" line, no timings).
                using (var renderer = new RgbaImageRenderer((uint)width, 800))
                {
                    var chat = Chat(renderer, () => Whole(Tail(true) with { QueuedBehind = null, Partial = null, Doing = null, Timings = null, QueuedIds = null }), null, night);
                    await Open(chat, 5);
                    chat.Close();
                    chat.Render(new RectF32(0, 0, width, 800));
                    File.WriteAllBytes(Path.Combine(dir, $"chat-stream-before-{tag}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 800));
                }
                using (var renderer = new RgbaImageRenderer((uint)width, 800))
                {
                    var chat = Chat(renderer, () => Whole(Tail(true)), _ => Task.FromResult<string?>(null), night);
                    await Open(chat, 5);
                    chat.Close();
                    chat.Render(new RectF32(0, 0, width, 800));
                    File.WriteAllBytes(Path.Combine(dir, $"chat-stream-after-{tag}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 800));
                }
            }
        }
        // A narrow window, where the status line is cut.
        using var narrow = new RgbaImageRenderer(520, 800);
        var small = Chat(narrow, () => Whole(Tail(false)), _ => Task.FromResult<string?>(null));
        await Open(small, 5);
        small.Close();
        small.Render(new RectF32(0, 0, 520, 800));
        File.WriteAllBytes(Path.Combine(dir, "chat-stream-after-520.png"), PngWriter.Encode(narrow.Surface.Pixels, 520, 800));
    }
}
