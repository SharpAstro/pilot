using Pilot.Ui;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Buddy;

/// <summary>
/// How big the window is, in design pixels: the cat and its cushion plus the tallest, widest thing it can ever show above (or below) it, so a card never
/// needs a bigger window and the window never resizes. Measured on the CPU with the card builders' own worst cases (ten long peek rows, the settings,
/// a chat with a long ask and its choices, the longest bubble), so the size follows the cards if they change.
/// </summary>
public static class BuddyWindowSize
{
    private const string LongWord = "Pilot.Gui.Tests.BuddyCatCaptureTests.SomeVeryLongTestNameThatHasNoSpacesAtAllAndWouldRunOutOfTheCard";

    /// <summary>The window's width and height in design pixels, for the cat at <paramref name="catSize"/> (the card and bubble text stay at the system scale).</summary>
    public static (float Width, float Height) Measure(string fontPath, float catSize = 1f)
    {
        using var renderer = new RgbaImageRenderer(4, 4);
        var pen = new Pen<RgbaImage>(renderer, fontPath, 0, 0, 1f);
        var width = 0f;
        var height = 0f;
        void Take(float w, float h)
        {
            width = MathF.Max(width, w);
            height = MathF.Max(height, h);
        }

        var rows = Enumerable.Range(0, PeekCard.MaxRows + 2)
            .Select(i => new PeekRow(i % 3 == 0 ? PeekState.Asking : PeekState.Working, "SE1", "review of PR #1351 in SharpAstro/pilot " + LongWord, "working 12 h 30 min", null)).ToList();
        var peek = PeekCard.Build(pen, new PeekView(3, rows.Count, rows));
        Take(peek.Width, peek.Height);
        // The hover list at its worst: the most rows, each with a long title and a long place (#1381 section 10: the bubble is never cut by the window).
        var hoverRows = Enumerable.Range(0, HoverCard.MaxRows + 2)
            .Select(i => new HoverRow("CI failed on the parser leg and the watchtower asks you " + LongWord, "flight DIR330 leg " + LongWord, "since 12 h 30 min", null)).ToList();
        var hover = HoverCard.Build(pen, new HoverView("9 need you " + LongWord, "12 sessions working on 3 nodes. " + LongWord, hoverRows));
        Take(hover.Width, hover.Height);
        var settings = SettingsCard.Build(pen, new BuddySettings());
        Take(settings.Width, settings.Height);

        var ask = new ChatAsk
        {
            Id = "r", Tool = "AskUserQuestion", Summary = "q",
            Questions =
            [
                new ChatAskQuestion
                {
                    Question = "Publish v3 of Desktop cat buddy, or change the bubble first? " + LongWord,
                    Options = [.. new[] { "Publish", "Change the bubble first", "Neither, ask me again tomorrow about " + LongWord }.Select(o => new ChatAskOption { Label = o })],
                },
            ],
        };
        foreach (var page in new[]
        {
            new ChatPage { Entries = [], Total = 0, Asks = [ask] },
            new ChatPage { Entries = [new TranscriptEntry { Index = 1, Kind = TranscriptKind.Assistant, Text = string.Join(' ', Enumerable.Repeat("Three flights are flying and one waits on you.", 40)) + " " + LongWord }], Total = 2 },
        })
        {
            var chat = new BuddyChat(new FixedNode(page), new NoGui(), () => null);
            chat.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            chat.Type(string.Join(' ', Enumerable.Repeat("how is flight 42 doing and is CI green on all of its pull requests", 3)));
            var card = ChatCard.Build(pen, chat);
            Take(card.Width, card.Height);
        }

        foreach (var kind in new[] { NoticeKind.Ask, NoticeKind.Trouble, NoticeKind.GoodNews })
        {
            var bubble = SpeechBubble.ForNotice(pen, new BuddyNotice("n", kind, "A title that is long enough to take two lines of the bubble " + LongWord, string.Join(' ', Enumerable.Repeat("The body says what happened and what to do.", 12))));
            Take(bubble.Width, bubble.Height);
        }

        var w = MathF.Max(BuddyScene.Margin + (GrumpCat.Width + 4) * catSize + BuddyScene.Margin, BuddyScene.Margin + 4 + width + BuddyScene.Margin);
        var h = BuddyScene.Margin + (GrumpCat.CushionY + GrumpCat.CushionHeight) * catSize + BuddyScene.BubbleGap + height + BuddyScene.Margin;
        return (MathF.Ceiling(w), MathF.Ceiling(h));
    }

    private sealed class FixedNode(ChatPage page) : IBuddyNode
    {
        public Task<string?> SayAsync(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken) => Task.FromResult<ChatPage?>(page);
    }

    private sealed class NoGui : IGuiHandOff
    {
        public bool Open(string link) => true;
    }
}
