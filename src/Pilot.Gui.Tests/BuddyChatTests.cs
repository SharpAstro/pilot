using Pilot.Buddy;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The buddy's chat on a fake node: what Enter, a choice button and the idle cat's click send, and how the answer streams into the bubble. No node, no network.</summary>
public sealed class BuddyChatTests
{
    private sealed class FakeNode : IBuddyNode
    {
        public List<string> Said { get; } = [];

        public List<string> Asked { get; } = [];

        public List<string> ChatNow { get; } = [];

        public List<(string Id, string Decision, string? Words)> Answers { get; } = [];

        public ChatPage Page { get; set; } = new() { Entries = [], Total = 0 };

        public string? Refuse { get; set; }

        public Task<string?> SayAsync(string text, CancellationToken cancellationToken)
        {
            Said.Add(text);
            return Task.FromResult(Refuse);
        }

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken)
        {
            Asked.Add(question);
            return Task.FromResult(Refuse);
        }

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken)
        {
            ChatNow.Add(sendId);
            return Task.FromResult<string?>(null);
        }

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken)
        {
            Answers.Add((requestId, decision, words));
            return Task.FromResult(Refuse);
        }

        public Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken) => Task.FromResult<ChatPage?>(Page);
    }

    private sealed class FakeGui : IGuiHandOff
    {
        public List<string> Opened { get; } = [];

        public bool Open(string link)
        {
            Opened.Add(link);
            return true;
        }
    }

    private static TranscriptEntry Said(int index, TranscriptKind kind, string text) => new() { Index = index, Kind = kind, Text = text };

    [Fact]
    public async Task EnterSaysTheInputToTheWatchtowerAndClearsIt()
    {
        var node = new FakeNode();
        var chat = new BuddyChat(node, new FakeGui(), () => null);
        chat.Type("how is flight 42?");
        chat.CanSend.ShouldBeTrue();

        (await chat.EnterAsync(CancellationToken.None)).ShouldBeTrue();

        node.Said.ShouldBe(["how is flight 42?"]);
        chat.Input.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMessageSentWhileTheWatchtowerWorksIsAnsweredAtOnceWithChatNow()
    {
        var node = new FakeNode { Page = new ChatPage { Entries = [], Total = 4, Working = true, Queued = ["why is CI red?"], QueuedIds = ["send-9"] } };
        var chat = new BuddyChat(node, new FakeGui(), () => null);
        chat.Type("why is CI red?");

        await chat.EnterAsync(CancellationToken.None);

        node.Said.ShouldBe(["why is CI red?"]);
        node.ChatNow.ShouldBe(["send-9"]);
    }

    [Fact]
    public async Task TheAnswerStreamsIntoTheBubbleFirstAsPartialWordsThenAsTheFinishedEntry()
    {
        var node = new FakeNode { Page = new ChatPage { Entries = [Said(0, TranscriptKind.Assistant, "an old answer")], Total = 1 } };
        var chat = new BuddyChat(node, new FakeGui(), () => null);
        chat.Type("status?");
        await chat.EnterAsync(CancellationToken.None);
        chat.Answer.ShouldBeNull("the old answer is not the answer to this");

        node.Page = new ChatPage { Entries = [Said(0, TranscriptKind.Assistant, "an old answer"), Said(1, TranscriptKind.User, "status?")], Total = 2, Working = true, Partial = "Three flights" };
        await chat.RefreshAsync(CancellationToken.None);
        chat.Answer.ShouldBe("Three flights");
        chat.Working.ShouldBeTrue();

        node.Page = new ChatPage { Entries = [Said(0, TranscriptKind.Assistant, "an old answer"), Said(1, TranscriptKind.User, "status?"), Said(2, TranscriptKind.Assistant, "Three flights are flying.")], Total = 3 };
        await chat.RefreshAsync(CancellationToken.None);
        chat.Answer.ShouldBe("Three flights are flying.");
    }

    [Fact]
    public async Task ALongAnswerIsCutAndSaysMoreInTheGuiWithALinkThatHandsOffToIt()
    {
        var node = new FakeNode();
        var gui = new FakeGui();
        var fresh = new BuddyChat(node, gui, () => null);
        fresh.Type("go");
        await fresh.EnterAsync(CancellationToken.None);
        node.Page = new ChatPage { Entries = [Said(1, TranscriptKind.Assistant, string.Join(' ', Enumerable.Repeat("word", 400)))], Total = 2 };
        await fresh.RefreshAsync(CancellationToken.None);

        var (text, more) = fresh.ShownAnswer;
        more.ShouldBeTrue();
        text!.Length.ShouldBeLessThanOrEqualTo(BuddyChat.AnswerLimit + 1);
        text.ShouldEndWith(TextWrap.Ellipsis);

        fresh.OpenInGui().ShouldBeTrue();
        gui.Opened.ShouldBe([string.Empty]);
        BuddyChat.MoreInTheGui.ShouldBe("more in the GUI");
    }

    [Fact]
    public async Task AnAsksChoicesAreButtonsThatPressAnswerAsk()
    {
        var node = new FakeNode
        {
            Page = new ChatPage
            {
                Entries = [], Total = 0,
                Asks =
                [
                    new ChatAsk
                    {
                        Id = "req-1", Tool = "AskUserQuestion", Summary = "Publish v3?",
                        Questions = [new ChatAskQuestion { Question = "Publish v3?", Options = [new ChatAskOption { Label = "Publish" }, new ChatAskOption { Label = "Change the bubble first" }] }],
                    },
                ],
            },
        };
        var chat = new BuddyChat(node, new FakeGui(), () => null);
        await chat.RefreshAsync(CancellationToken.None);

        var ask = chat.PendingAsk.ShouldNotBeNull();
        ask.Question.ShouldBe("Publish v3?");
        ask.Choices.Select(c => c.Label).ShouldBe(["Publish", "Change the bubble first"]);

        (await chat.PressAsync(ask.Choices[1], CancellationToken.None)).ShouldBeTrue();
        var answer = node.Answers.ShouldHaveSingleItem();
        answer.Id.ShouldBe("req-1");
        answer.Decision.ShouldBe("answer");
        answer.Words.ShouldBe("{\"Publish v3?\":\"Change the bubble first\"}");
    }

    [Fact]
    public async Task APermissionPromptHasAllowAndDenyButtons()
    {
        var node = new FakeNode { Page = new ChatPage { Entries = [], Total = 0, Asks = [new ChatAsk { Id = "req-2", Tool = "Bash", Summary = "run: git push origin main (Bash)" }] } };
        var chat = new BuddyChat(node, new FakeGui(), () => null);
        await chat.RefreshAsync(CancellationToken.None);

        var ask = chat.PendingAsk.ShouldNotBeNull();
        ask.Choices.Select(c => c.Decision).ShouldBe(["allow", "deny"]);
        await chat.PressAsync(ask.Choices[0], CancellationToken.None);
        node.Answers.ShouldHaveSingleItem().Decision.ShouldBe("allow");
    }

    [Fact]
    public async Task WhatsGoingOnGoesThroughTheChatAsAQuestion()
    {
        var node = new FakeNode();
        var chat = new BuddyChat(node, new FakeGui(), () => null);

        (await chat.AskWhatsGoingOnAsync(CancellationToken.None)).ShouldBeTrue();

        node.Asked.ShouldBe(["What's going on?"]);
        node.Said.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhileTheWatchtowerIsDownNothingIsTypedOrSentAndTheReasonIsKept()
    {
        var node = new FakeNode();
        var chat = new BuddyChat(node, new FakeGui(), () => "The watchtower is not running: replaced");
        chat.Type("hello");

        chat.Input.ShouldBeEmpty();
        chat.CanSend.ShouldBeFalse();
        (await chat.EnterAsync(CancellationToken.None)).ShouldBeFalse();
        (await chat.AskWhatsGoingOnAsync(CancellationToken.None)).ShouldBeFalse();
        node.Said.ShouldBeEmpty();
        node.Asked.ShouldBeEmpty();
        chat.DisabledReason.ShouldBe("The watchtower is not running: replaced");
    }

    [Fact]
    public async Task ARefusalIsShownAndTheInputIsKeptToTryAgain()
    {
        var node = new FakeNode { Refuse = "refused: maintenance" };
        var chat = new BuddyChat(node, new FakeGui(), () => null);
        chat.Type("hi");

        (await chat.EnterAsync(CancellationToken.None)).ShouldBeFalse();

        chat.Refused.ShouldBe("refused: maintenance");
        chat.Input.ShouldBe("hi");
    }
}
