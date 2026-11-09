using DIR.Lib;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;
using Layout = DIR.Lib.Layout;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL907, the behaviours the culled chat keeps, on a transcript of two thousand entries: the view follows the end as entries arrive, the
/// streaming bubble and the queued lines show last, Jump to end is there only when scrolled up and takes the view back, and a reader scrolled
/// up to older rows is not moved by new entries or by the reply in progress growing. CPU-rendered; the page reads the feed on its own signal
/// and nothing waits on the clock.
/// </summary>
public sealed partial class ChatFrameTests
{
    private const int Long = 2000;

    // What a frame drew: each text leaf inside the feed's viewport with where it sits, top to bottom.
    private readonly record struct Seen(string Text, float X, float Y, float Bottom);

    private sealed record Frame(Seen[] Texts, RectF32 Feed, bool JumpShown)
    {
        public Seen Find(string text) => Texts.First(t => t.Text.Contains(text, StringComparison.Ordinal));
        public bool Has(string text) => Texts.Any(t => t.Text.Contains(text, StringComparison.Ordinal));
    }

    // Draws the page (so the regions and the scroll bind) and reads what was arranged under the feed's viewport.
    private static Frame Draw(RgbaImageRenderer renderer, ChatView<RgbaImage> chat)
    {
        chat.Render(Bounds);
        var arranged = Layout.Engine.Arrange(chat.Tree(), new Rect<float>(0, 0, Bounds.Width, Bounds.Height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        // The feed is the tallest scrolled node (the page's short-window scroll is not in play at this size).
        var feed = arranged.Where(a => a.Node.Scroll is not null).OrderByDescending(a => a.Bounds.Height).First().Bounds;
        // Text is drawn a word a leaf: the words on one line of the page (one rounded top) are read as that line, left to right.
        var texts = arranged
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(a => (Text: ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value.Trim(), a.Bounds))
            .Where(t => t.Text.Length > 0 && t.Bounds.Y >= feed.Y - 0.5f && t.Bounds.Y + t.Bounds.Height <= feed.Y + feed.Height + 0.5f)
            .GroupBy(t => MathF.Round(t.Bounds.Y))
            .Select(g => g.OrderBy(t => t.Bounds.X).ToArray())
            .Select(g => new Seen(string.Join(' ', g.Select(t => t.Text)), g[0].Bounds.X, g.Min(t => t.Bounds.Y), g.Max(t => t.Bounds.Y + t.Bounds.Height)))
            .OrderBy(t => t.Y).ThenBy(t => t.X)
            .ToArray();
        var jump = arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "Jump to end" } });
        return new Frame(texts, new RectF32(feed.X, feed.Y, feed.Width, feed.Height), jump);
    }

    private static ListScrollController FeedScroll(ChatView<RgbaImage> chat, Frame frame) =>
        chat.ScrollTargetAt(frame.Feed.X + frame.Feed.Width / 2, frame.Feed.Y + frame.Feed.Height / 2).ShouldNotBeNull();

    // The wheel, up by `notches` (a positive delta scrolls toward the start).
    private static void WheelUp(ChatView<RgbaImage> chat, Frame frame, int notches)
    {
        var scroll = FeedScroll(chat, frame);
        for (var i = 0; i < notches; i++)
        {
            scroll.HandleInput(new InputEvent.Scroll(1f, frame.Feed.X + frame.Feed.Width / 2, frame.Feed.Y + frame.Feed.Height / 2, InputModifier.None)).ShouldBeTrue();
        }
    }

    private static TranscriptEntry Said(string text) => new() { Index = 0, Kind = TranscriptKind.Assistant, At = At, Text = text };

    private static ChatPage Writing(string partial) => StreamingChatTests.Tail(false) with { Partial = partial };

    [Fact]
    public async Task TheViewFollowsTheEndAsEntriesArriveOnATwoThousandEntryTranscript()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, Transcript(Long));
        await held.OpenAsync();
        var chat = held.Chat;

        var first = Draw(renderer, chat);
        first.JumpShown.ShouldBeFalse("a page opened at its end follows it");
        first.Has($"Paragraph 5 of the report on flight {(Long - 1) / 8}").ShouldBeTrue("the last entry is in view");
        first.Has("Please look at flight 0:").ShouldBeFalse("the start of the transcript is not drawn");

        for (var n = 1; n <= 3; n++)
        {
            var words = $"Arrival number {n}: the flight landed.";
            await held.AppendAsync(Said(words));
            var frame = Draw(renderer, chat);
            frame.JumpShown.ShouldBeFalse($"still following after arrival {n}");
            var last = frame.Find(words);
            last.Bottom.ShouldBeLessThanOrEqualTo(frame.Feed.Y + frame.Feed.Height + 0.5f, $"arrival {n} is whole in view");
            FeedScroll(chat, frame).Offset.ShouldBe(FeedScroll(chat, frame).MaxOffset, 0.01f, $"at the end after arrival {n}");
        }
    }

    [Fact]
    public async Task TheStreamingBubbleAndTheQueuedLinesShowLastAndKeepFollowingAsTheReplyGrows()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, Transcript(Long)) { Tail = Writing("PIL907 has one leg left") };
        await held.OpenAsync();
        var chat = held.Chat;

        var frame = Draw(renderer, chat);
        frame.JumpShown.ShouldBeFalse();
        var lastReply = frame.Find($"Paragraph 5 of the report on flight {(Long - 1) / 8}");
        var bubble = frame.Find("writing…");
        var queued = frame.Find("queued behind running pilot status");
        var second = frame.Find("queued behind the message before it");
        var working = frame.Find("● running pilot status");
        lastReply.Y.ShouldBeLessThan(bubble.Y);
        bubble.Y.ShouldBeLessThan(queued.Y);
        queued.Y.ShouldBeLessThan(second.Y);
        second.Y.ShouldBeLessThan(working.Y);
        working.Bottom.ShouldBeLessThanOrEqualTo(frame.Feed.Y + frame.Feed.Height + 0.5f, "the last line is whole in view");

        // The reply grows by lines: the view stays at the end, the last line stays in view.
        for (var n = 1; n <= 3; n++)
        {
            var words = $"PIL907 has one leg left, and growing round {n}:\n\n- the chat page lays out only the rows in view\n- the box and the queued lines stay at the foot";
            held.Tail = Writing(words);
            await held.ReadAsync(() => chat.Partial == words);
            var grown = Draw(renderer, chat);
            grown.JumpShown.ShouldBeFalse($"following while the reply grows ({n})");
            grown.Has("the box and the queued lines stay at the foot").ShouldBeTrue($"the new words are in view ({n})");
            grown.Find("● running pilot status").Bottom.ShouldBeLessThanOrEqualTo(grown.Feed.Y + grown.Feed.Height + 0.5f, $"the last line stays in view ({n})");
        }
    }

    [Fact]
    public async Task JumpToEndShowsOnlyScrolledUpAndTakesTheViewBackToTheEnd()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, Transcript(Long)) { Tail = Writing("PIL907 has one leg left") };
        await held.OpenAsync();
        var chat = held.Chat;

        var atEnd = Draw(renderer, chat);
        atEnd.JumpShown.ShouldBeFalse();

        WheelUp(chat, atEnd, 10);
        var up = Draw(renderer, chat);
        up.JumpShown.ShouldBeTrue("scrolled up, the page offers the way back");
        up.Has("● running pilot status").ShouldBeFalse("the foot of the feed is out of view");

        // The pill's own click, as a person makes it.
        var regions = new List<ClickableRegion>();
        chat.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "chat:end" }).OnClick!.Invoke(InputModifier.None);
        var back = Draw(renderer, chat);
        back.JumpShown.ShouldBeFalse("back at the end the pill goes");
        back.Has("● running pilot status").ShouldBeTrue();
        FeedScroll(chat, back).Offset.ShouldBe(FeedScroll(chat, back).MaxOffset, 0.01f);

        // And the page follows again: an entry that arrives is in view.
        await held.AppendAsync(Said("After the jump, the flight landed."));
        Draw(renderer, chat).Has("After the jump, the flight landed.").ShouldBeTrue();
    }

    [Fact]
    public async Task AReaderScrolledUpIsNotMovedByNewEntriesOrTheReplyInProgress()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, Transcript(Long)) { Tail = Writing("PIL907 has one leg left") };
        await held.OpenAsync();
        var chat = held.Chat;

        var atEnd = Draw(renderer, chat);
        // Far up into the older rows, a wheel notch at a time as a person does.
        WheelUp(chat, atEnd, 150);
        var before = Draw(renderer, chat);
        before.JumpShown.ShouldBeTrue();
        before.Texts.Length.ShouldBeGreaterThan(3, "older rows are in view");
        before.Has("Paragraph 5 of the report on flight 249").ShouldBeFalse("the reader is not at the end");
        var offset = FeedScroll(chat, before).Offset;
        var firstInView = before.Texts[0];

        // Entries arrive and the reply in progress grows, over several reads.
        for (var n = 1; n <= 4; n++)
        {
            held.Tail = Writing(string.Join("\n\n", Enumerable.Range(0, n * 3).Select(p => $"Paragraph {p} of the reply in progress, round {n}.")));
            await held.AppendAsync(Said($"Arrival number {n}: the flight landed. {LongLine}"));
            var after = Draw(renderer, chat);

            after.JumpShown.ShouldBeTrue($"still scrolled up after read {n}");
            FeedScroll(chat, after).Offset.ShouldBe(offset, 0.01f, $"the content offset from the top after read {n}");
            after.Texts.Select(t => (t.Text, t.X, t.Y)).ShouldBe(before.Texts.Select(t => (t.Text, t.X, t.Y)), $"the rows in view are where they were after read {n}");
            after.Texts[0].ShouldBe(firstInView, $"the first row in view and its top after read {n}");
            after.Has("the flight landed").ShouldBeFalse("what arrived is below the view");
        }
        held.Chat.Entries.Count.ShouldBe(Long + 4);
    }

    /// <summary>
    /// The Chat page on a transcript of two thousand entries, with the reply in progress and two lines queued, as PNGs at a narrow (760x640) and a
    /// wide (1600x900) window: following the end, and scrolled up (the Jump to end pill). They go to the folder in PILOT_CHAT_CAPTURES
    /// (docs/captures, for the PR), else to a folder that goes with the test.
    /// </summary>
    [Fact]
    public async Task ALongTranscriptIsCapturedAtANarrowAndAWideWindow()
    {
        var given = Environment.GetEnvironmentVariable("PILOT_CHAT_CAPTURES");
        var folder = given is { Length: > 0 } ? Directory.CreateDirectory(given) : Directory.CreateTempSubdirectory("pilot-chat-");
        try
        {
            foreach (var (size, width, height) in new[] { ("narrow", 760, 640), ("wide", 1600, 900) })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                using var held = new Held(renderer, Transcript(Long)) { Tail = Writing("PIL907 has one leg left, and the chat page now lays out only the rows in view.\n\n- **before:** every row, every frame\n- **after:** the rows in view, and the box at the foot") };
                await held.OpenAsync();
                var view = new RectF32(0, 0, width, height);
                foreach (var (label, up) in new[] { ("long", false), ("long-up", true) })
                {
                    held.Chat.Render(view);
                    if (up)
                    {
                        var scroll = held.Chat.ScrollTargetAt(width / 2f, height / 2f).ShouldNotBeNull();
                        for (var i = 0; i < 40; i++)
                        {
                            scroll.HandleInput(new InputEvent.Scroll(1f, width / 2f, height / 2f, InputModifier.None));
                        }
                        held.Chat.Render(view);
                    }
                    var png = Path.Combine(folder.FullName, $"chat-{label}-{size}.png");
                    File.WriteAllBytes(png, PngWriter.Encode(renderer.Surface.Pixels, width, height));
                    File.ReadAllBytes(png).Take(4).ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
                }
            }
        }
        finally
        {
            if (given is not { Length: > 0 })
            {
                folder.Delete(recursive: true);
            }
        }
    }
}
