using System.Collections.Immutable;
using System.Text.Json;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The chat's 'Waiting on you' panel (PIL855): a permission prompt with Allow and Deny, a question with its options (radio marks, or
/// checkboxes for several), an Other box and Send answer. Drawn on the CPU from sample pages at a narrow and a wide size, with long
/// realistic content, in each palette; the answer is the node's answer-ask, once.
/// </summary>
public sealed class WaitingOnYouTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 8, 9, 40, 12, TimeSpan.Zero);

    private const string LongCommand =
        @"git -C C:\Users\SebastianGodelet\source\repos\sharpastro\pilot\.claude\worktrees\pil855 push origin flight/pil855:flight/pil855 --force-with-lease=flight/pil855:04804ff4a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private static ChatAsk Prompt(bool lengthen) => new()
    {
        Id = "p1",
        Tool = "Bash",
        Summary = lengthen ? $"run: {LongCommand} (Bash)" : "run: git push origin main (Bash)",
        Input = JsonSerializer.Serialize(new { command = lengthen ? LongCommand : "git push origin main", description = "Push the flight's branch" }),
        At = At,
    };

    private static ChatAsk Question(bool multi, bool lengthen) => new()
    {
        Id = "q1",
        Tool = "AskUserQuestion",
        Summary = "Which repo?",
        At = At,
        Questions =
        [
            new ChatAskQuestion
            {
                Header = "Repo",
                Question = lengthen
                    ? "Which repo should the flight for issue 12 fly in? It touches the sky map and its renderer, so both tianwen and the renderer fit, and the choice decides who reviews it and where its PR is opened."
                    : "Which repo should the flight fly in?",
                MultiSelect = multi,
                Options =
                [
                    new ChatAskOption { Label = "tianwen", Description = lengthen ? "The sky map and its tests live here; the leg's files overlap PIL850's, which is flying now" : "the sky map" },
                    new ChatAskOption { Label = "pilot", Description = "this one" },
                    new ChatAskOption { Label = lengthen ? "SdlVulkan.Renderer (a sibling clone, so a change that spans repos is its own flight)" : "renderer" },
                ],
            },
        ],
    };

    private static ChatPage Page(params ChatAsk[] asks) => new()
    {
        Entries =
        [
            new TranscriptEntry { Index = 0, Kind = TranscriptKind.User, Text = "push the flight and tell me when it is up", At = At },
            new TranscriptEntry { Index = 1, Kind = TranscriptKind.Assistant, Text = "Pushing it now, I need your leave for the push.", At = At.AddSeconds(5) },
        ],
        Total = 2,
        Working = true,
        Doing = "running pilot status",
        Asks = asks,
        Timings = StreamingChatTests.Timings(5),
    };

    private sealed record Answer(string Id, string Decision, string? Words);

    private static ChatView<RgbaImage> Chat(RgbaImageRenderer renderer, Func<ChatPage> page, List<Answer>? answers = null, bool night = false, string? refuse = null)
    {
        var chat = new ChatView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        chat.Load = (after, _) =>
        {
            var current = page();
            return Task.FromResult(after < 0 ? current : current with { Entries = [.. current.Entries.Where(e => e.Index > after)] });
        };
        chat.OnSend = _ => Task.FromResult<string?>(null);
        chat.OnAnswerAsk = (id, decision, words) =>
        {
            answers?.Add(new Answer(id, decision, words));
            return Task.FromResult(refuse);
        };
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

    private static async Task Open(ChatView<RgbaImage> chat)
    {
        chat.Open();
        await Until(() => chat.Entries.Count >= 2);
        chat.Close();
    }

    private static string Described(ChatView<RgbaImage> chat, int width = 160, int rows = 160) =>
        string.Join(' ', Layout.Engine.Arrange(chat.Tree(), new Rect<int>(0, 0, width, rows), CellMeasureContext.PixelAuthored)
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

    [Fact]
    public async Task APromptShowsTheToolAndItsInputWithAllowAndDenyAndAllowAnswersItOnce()
    {
        var answers = new List<Answer>();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Page(Prompt(false)), answers);
        await Open(chat);

        var text = Described(chat);
        text.ShouldContain("Waiting on you");
        text.ShouldContain("run: git push origin main (Bash)");
        text.ShouldContain("git push origin main");
        var actions = Actions(chat);
        actions.ShouldContain("chat:ask:p1:allow");
        actions.ShouldContain("chat:ask:p1:deny");

        Click(chat, "chat:ask:p1:allow");
        await Until(() => answers.Count == 1);
        answers.ShouldBe([new Answer("p1", "allow", null)]);
        // While the node has the answer a second press does nothing.
        chat.Presses.PendingOf("chat:ask:p1").ShouldBeNull();
    }

    [Fact]
    public async Task ADeniedPromptAndARefusedAnswerAreSaidUnderTheButtons()
    {
        var answers = new List<Answer>();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Page(Prompt(false)), answers, refuse: "refused: ask p1 is not waiting: it was answered already, or claude called it off");
        await Open(chat);

        Click(chat, "chat:ask:p1:deny");
        await Until(() => answers.Count == 1);
        answers[0].ShouldBe(new Answer("p1", "deny", null));
        await Until(() => chat.Presses.RefusedOf("chat:ask:p1") is not null);
        Described(chat).ShouldContain("not waiting");
    }

    [Fact]
    public async Task AQuestionShowsItsOptionsWithMarksAndSendsAnswerOnlyOnceItHasOne()
    {
        var answers = new List<Answer>();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Page(Question(false, false)), answers);
        await Open(chat);

        var text = Described(chat);
        text.ShouldContain("Waiting on you");
        text.ShouldContain("Which repo should the flight fly in?");
        text.ShouldContain("○ tianwen");
        text.ShouldContain("Other");
        text.ShouldContain("Choose an option or write your own.");
        // Nothing chosen: no Send answer to press, and Decline is there.
        var before = Actions(chat);
        before.ShouldNotContain("chat:ask:q1:answer");
        before.ShouldContain("chat:ask:q1:deny");

        Click(chat, "chat:ask:q1:pick:0:2");
        Described(chat).ShouldContain("● pilot");
        Actions(chat).ShouldContain("chat:ask:q1:answer");
        Click(chat, "chat:ask:q1:pick:0:1");
        // One choice: the other is let go.
        var after = Described(chat);
        after.ShouldContain("● tianwen");
        after.ShouldContain("○ pilot");

        Click(chat, "chat:ask:q1:answer");
        await Until(() => answers.Count == 1);
        answers[0].Id.ShouldBe("q1");
        answers[0].Decision.ShouldBe("answer");
        JsonDocument.Parse(answers[0].Words!).RootElement.GetProperty("Which repo should the flight fly in?").GetString().ShouldBe("tianwen");
    }

    [Fact]
    public async Task AMultiSelectQuestionTakesCheckboxesAndAnOtherBoxAndJoinsTheAnswers()
    {
        var answers = new List<Answer>();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Page(Question(true, false)), answers);
        await Open(chat);
        Described(chat).ShouldContain("[ ] tianwen");

        Click(chat, "chat:ask:q1:pick:0:1");
        Click(chat, "chat:ask:q1:pick:0:3");
        Described(chat).ShouldContain("[x] tianwen");
        chat.OtherBox("q1", 0).Text = "and the docs";
        Click(chat, "chat:ask:q1:answer");
        await Until(() => answers.Count == 1);

        JsonDocument.Parse(answers[0].Words!).RootElement.GetProperty("Which repo should the flight fly in?").GetString().ShouldBe("tianwen, renderer, and the docs");
    }

    [Fact]
    public async Task OtherWordsAnswerAQuestionWithNoOptionChosenAndDeclineDeniesIt()
    {
        var answers = new List<Answer>();
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Page(Question(false, false)), answers);
        await Open(chat);
        Described(chat);

        chat.OtherBox("q1", 0).Text = "a repo of my own";
        Click(chat, "chat:ask:q1:answer");
        await Until(() => answers.Count == 1);
        JsonDocument.Parse(answers[0].Words!).RootElement.EnumerateObject().Single().Value.GetString().ShouldBe("a repo of my own");

        Click(chat, "chat:ask:q1:deny");
        await Until(() => answers.Count == 2);
        answers[1].ShouldBe(new Answer("q1", "deny", null));
    }

    [Fact]
    public async Task SeveralAsksShowTheFirstAndSayHowManyWait()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Page(Prompt(false), Question(false, false)));
        await Open(chat);

        var text = Described(chat);
        text.ShouldContain("Waiting on you · 1 of 2");
        text.ShouldContain("run: git push origin main (Bash)");
        text.ShouldNotContain("Which repo should the flight fly in?");
    }

    [Fact]
    public async Task APageWithNoAsksShowsNoPanel()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chat = Chat(renderer, () => Page());
        await Open(chat);

        Described(chat).ShouldNotContain("Waiting on you");
    }

    [Fact]
    public void ThePanelsTextsMeetFourPointFiveToOneOnTheirDayBackgroundAndNightIsItsOwnPalette()
    {
        Palette.Use(null);
        foreach (var (name, color) in new[] { ("body", TranscriptEntryView.Body), ("bright", TranscriptEntryView.Bright) })
        {
            Contrast(color, Palette.Current.Marked).ShouldBeGreaterThanOrEqualTo(4.5, $"{name} on the panel's ground");
        }
        // Night is red on black by design (the eye stays dark-adapted): the panel's texts read the palette's own colours, as every other line does.
        Palette.Use(new NightSwitch { On = true });
        TranscriptEntryView.Body.ShouldBe(Palette.Night.Body);
        TranscriptEntryView.Bright.ShouldBe(Palette.Night.Bright);
        Palette.Use(null);
    }

    private static double Luminance(RGBAColor32 c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.Red) + 0.7152 * Channel(c.Green) + 0.0722 * Channel(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    public static IEnumerable<TheoryDataRow<int, bool, bool, string>> Sizes() =>
        from size in new[] { 500, 1280, 2000 }
        from night in new[] { false, true }
        from lengthen in new[] { false, true }
        from kind in new[] { "prompt", "question", "several" }
        select new TheoryDataRow<int, bool, bool, string>(size, night, lengthen, kind) { TestDisplayName = $"{kind} {size}px {(night ? "night" : "day")} {(lengthen ? "long" : "sample")}" };

    private static ChatPage PageOf(string kind, bool lengthen) => kind switch
    {
        "prompt" => Page(Prompt(lengthen)),
        "question" => Page(Question(true, lengthen)),
        _ => Page(Question(false, lengthen), Prompt(lengthen)),
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task NoTextOverlapsAtNarrowAndWideSizesWithLongContentInEachPalette(int width, bool night, bool lengthen, string kind)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 800);
        var chat = Chat(renderer, () => PageOf(kind, lengthen), night: night);
        await Open(chat);

        var arranged = Layout.Engine.Arrange(chat.Tree(), new Rect<float>(0, 0, width, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        TextOverlap.Faults(arranged).ShouldBeEmpty();
        // A frame draws it all: the feed, the panel with its buttons, the box.
        chat.Render(new RectF32(0, 0, width, 800));
    }

    [Fact]
    public async Task CapturesAtNarrowAndWideSizes()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        foreach (var (width, night) in new[] { (520, false), (1280, false), (2000, false), (520, true), (1280, true) })
        {
            foreach (var kind in new[] { "prompt", "question" })
            {
                using var renderer = new RgbaImageRenderer((uint)width, 800);
                var chat = Chat(renderer, () => PageOf(kind, true), night: night);
                await Open(chat);
                if (kind == "question")
                {
                    Described(chat);
                    Click(chat, "chat:ask:q1:pick:0:1");
                    Click(chat, "chat:ask:q1:pick:0:3");
                }
                chat.Render(new RectF32(0, 0, width, 800));
                File.WriteAllBytes(Path.Combine(dir, $"chat-waiting-{kind}-{width}{(night ? "-night" : "")}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, 800));
            }
        }
    }
}
