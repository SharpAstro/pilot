using DIR.Lib;
using Pilot.Buddy;
using Pilot.Contracts;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The peek, settings and chat cards of the desktop cat drawn on the CPU, bright and dark, at a wide and a narrow screen, empty, with many rows and with very
/// long ones: no text over text, every line inside its card and the card inside the screen above the cat, long text ended in an ellipsis with the whole in the
/// hover, and every colour readable on the panel. The PNGs go to the job's artifacts folder (PILOT_JOB_ARTIFACTS) or the temp folder, never into the plane.
/// </summary>
public sealed class BuddyPeekSettingsCaptureTests
{
    private static readonly string OutFolder = Path.Combine(Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts ? artifacts : Path.GetTempPath(), "pilot-buddy-captures");

    private static string Font => SessionsCapture.FontPath;

    private const string LongWord = "Pilot.Gui.Tests.BuddyCatCaptureTests.SomeVeryLongTestNameThatHasNoSpacesAtAllAndWouldRunOutOfTheCard";

    public static TheoryData<string, string, bool, bool> Cases()
    {
        var data = new TheoryData<string, string, bool, bool>();
        foreach (var card in new[] { "peek-empty", "peek-many", "peek-long", "settings", "chat-idle", "chat-ask", "chat-answer", "chat-down", "chat-typing" })
        {
            foreach (var dark in new[] { false, true })
            {
                foreach (var wide in new[] { true, false })
                {
                    data.Add(card, card, dark, wide);
                }
            }
        }
        return data;
    }

    private static PeekView PeekOf(string kind)
    {
        PeekRow Row(PeekState s, string what, string status) => new(s, "SE1", what, status, null);
        return kind switch
        {
            "peek-empty" => new PeekView(1, 0, []),
            "peek-many" => new PeekView(3, 14,
            [
                Row(PeekState.Asking, "DSN-2 designer", "asks you"),
                Row(PeekState.Asking, "flight 42 leg 3", "asks you"),
                .. Enumerable.Range(0, 6).Select(i => Row(PeekState.Working, $"flight {40 + i} leg {i}", $"working {i + 1} min")),
                .. Enumerable.Range(0, 6).Select(i => Row(PeekState.Idle, $"flight {20 + i}", "idle")),
            ]),
            _ => new PeekView(3, 3,
            [
                Row(PeekState.Asking, "DE1 designer of the desktop cat buddy asks you about " + LongWord, "asks you"),
                Row(PeekState.Working, "review of PR #1351 in SharpAstro/pilot for the desktop cat buddy flight " + LongWord, "working 12 h 30 min"),
                Row(PeekState.Idle, "flight 40", "idle"),
            ]),
        };
    }

    private sealed class NullNode : IBuddyNode
    {
        public ChatPage? Page { get; set; }

        public Task<string?> SayAsync(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken) => Task.FromResult(Page);
    }

    private sealed class NullGui : IGuiHandOff
    {
        public bool Open(string link) => true;
    }

    private static BuddyChat ChatOf(string kind)
    {
        ChatAsk Ask(string[] options) => new()
        {
            Id = "r", Tool = "AskUserQuestion", Summary = "q",
            Questions = [new ChatAskQuestion { Question = "Publish v3 of Desktop cat buddy, or change the bubble first? " + LongWord, Options = [.. options.Select(o => new ChatAskOption { Label = o })] }],
        };
        var node = new NullNode();
        string? down = null;
        var typed = string.Empty;
        switch (kind)
        {
            case "chat-ask":
                node.Page = new ChatPage { Entries = [], Total = 0, Asks = [Ask(["Publish", "Change the bubble first", "Neither, ask me again tomorrow about " + LongWord])] };
                break;
            case "chat-answer":
                node.Page = new ChatPage
                {
                    Entries = [new TranscriptEntry { Index = 1, Kind = TranscriptKind.Assistant, Text = string.Join(' ', Enumerable.Repeat("Three flights are flying and one waits on you.", 30)) + " " + LongWord }],
                    Total = 2,
                };
                break;
            case "chat-down":
                down = "The watchtower is not running: replaced by a fresh one, which is starting.";
                break;
            case "chat-typing":
                typed = string.Join(' ', Enumerable.Repeat("how is flight 42 doing and is CI green on all of its pull requests", 3));
                break;
        }
        var chat = new BuddyChat(node, new NullGui(), () => down);
        chat.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (kind == "chat-answer")
        {
            // The answer is what came after the send: take the baseline as before it.
            typeof(BuddyChat).GetField("_fromIndex", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(chat, 1);
        }
        chat.Type(typed);
        return chat;
    }

    private static BuddyCard Build<TSurface>(Pen<TSurface> pen, string kind) => kind switch
    {
        "settings" => SettingsCard.Build(pen, new BuddySettings { WakeAsk = false, Corner = 1 }),
        var k when k.StartsWith("peek", StringComparison.Ordinal) => PeekCard.Build(pen, PeekOf(k)),
        var k => ChatCard.Build(pen, ChatOf(k)),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCardDrawsInsideTheScreenAboveTheCatWithNoTextOverTextAndNothingCutSilently(string kind, string _, bool dark, bool wide)
    {
        var (width, height) = wide ? (1280, 720) : (480, 720);
        var palette = BuddyPalette.For(dark);
        using var renderer = new RgbaImageRenderer(width, height);
        var model = new BuddyModel(TimeProvider.System);
        var layout = BuddyScene.Draw(renderer, Font, palette, BuddyScene.ViewOf(model), paintDesktop: true);

        var measure = new Pen<RgbaImage>(renderer, Font, 0, 0, 1f);
        var card = Build(measure, kind);
        var (x, y) = card.PlaceAboveCat(renderer, layout.CatY);
        card.Draw(new Pen<RgbaImage>(renderer, Font, x, y, 1f), palette);
        Save($"{kind}-{(dark ? "dark" : "light")}-{width}", renderer, width, height);

        x.ShouldBeGreaterThanOrEqualTo(0);
        y.ShouldBeGreaterThanOrEqualTo(0, "even the tallest card has its top on the screen");
        (y + card.Height).ShouldBeLessThanOrEqualTo(layout.CatY);

        // Every line inside the card and no two over each other.
        card.Lines.ShouldAllBe(l => l.X >= 0 && l.X + l.Width <= card.Width + 0.5f && l.Y >= 0 && l.Y + l.Height <= card.Height);
        Overlaps(card.Lines).ShouldBeEmpty();
        card.Hits.ShouldAllBe(h => h.Width > 0 && h.Height > 0);

        // A line as wide as its room, or ended in an ellipsis; a card with an ellipsis keeps the whole text for the hover.
        foreach (var line in card.Lines)
        {
            measure.Measure(line.Text, line.Size).Width.ShouldBeLessThanOrEqualTo(line.Width + 0.5f, line.Text);
        }
        if (kind is "peek-long" or "chat-ask" or "chat-down" or "chat-typing" or "chat-answer")
        {
            card.Lines.ShouldContain(l => l.Text.Contains(TextWrap.Ellipsis));
            card.Tooltips.ShouldNotBeEmpty("the whole text of what ended in an ellipsis");
        }
        else
        {
            card.Tooltips.ShouldBeEmpty();
        }
    }

    [Fact]
    public void ThePeekDrawsTenRowsAndSaysHowManyMoreAreInTheGui()
    {
        using var renderer = new RgbaImageRenderer(400, 400);
        var card = PeekCard.Build(new Pen<RgbaImage>(renderer, Font, 0, 0, 1f), PeekOf("peek-many"));
        card.Hits.Count(h => h.Id.StartsWith("row:", StringComparison.Ordinal)).ShouldBe(PeekCard.MaxRows);
        card.Lines.ShouldContain(l => l.Text == "+ 4 more in the GUI");
        card.Lines.First().Text.ShouldBe("Watchtower · 3 nodes · 14 sessions");
    }

    [Fact]
    public void APressOnARowIsTheRowsHitAndTheSettingsHitsTurnIntoSettings()
    {
        using var renderer = new RgbaImageRenderer(400, 700);
        var pen = new Pen<RgbaImage>(renderer, Font, 0, 0, 1f);
        var peek = PeekCard.Build(pen, PeekOf("peek-many"));
        var second = peek.Hits.Single(h => h.Id == "row:1");
        peek.HitAt(second.X + 5, second.Y + 5)!.Id.ShouldBe("row:1");

        var card = SettingsCard.Build(pen, new BuddySettings());
        card.Hits.Select(h => h.Id).ShouldBe(["source", "wakeAsk", "wakeMerged", "wakeFailed", "corner:0", "corner:1", "corner:2", "clickThrough", "snooze", "done", "cats"]);
        var now = new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
        SettingsCard.Press(new BuddySettings(), "wakeFailed", now, TimeZoneInfo.Utc)!.WakesForFailed.ShouldBeFalse();
        SettingsCard.Press(new BuddySettings(), "corner:2", now, TimeZoneInfo.Utc)!.CornerIndex.ShouldBe(2);
        SettingsCard.Press(new BuddySettings(), "clickThrough", now, TimeZoneInfo.Utc)!.ClicksPassThrough.ShouldBeFalse();
        var snoozed = SettingsCard.Press(new BuddySettings(), "snooze", now, TimeZoneInfo.Utc)!;
        snoozed.IsSnoozed(now).ShouldBeTrue();
        snoozed.IsSnoozed(now.AddHours(1)).ShouldBeFalse();
        SettingsCard.Press(new BuddySettings(), "done", now, TimeZoneInfo.Utc).ShouldBeNull();
        SettingsCard.Press(new BuddySettings(), "cats", now, TimeZoneInfo.Utc).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryCardTextColourReadsOnThePanelAndTheNewGreenAsAnIcon(bool dark)
    {
        var p = BuddyPalette.For(dark);
        foreach (var (name, color) in new[] { ("header", p.HeaderText), ("body", p.BodyText), ("muted", p.MutedText), ("warn", p.Amber), ("bad", p.Red), ("accent", p.Accent) })
        {
            BuddyContrast.Ratio(color, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, name + " on the panel");
        }
        BuddyContrast.Ratio(p.ButtonText, p.ButtonBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, "text in a control");
        BuddyContrast.Ratio(p.MutedText, p.ButtonBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text, "the input's placeholder and reason");
        BuddyContrast.Ratio(p.Green, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "an icon");
        BuddyContrast.Ratio(p.CardBorder, p.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "a control's edge");
        BuddyContrast.Ratio(p.Accent, p.ButtonBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "the chosen segment's edge");
    }

    private static List<string> Overlaps(IReadOnlyList<CardLine> lines)
    {
        var faults = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = i + 1; j < lines.Count; j++)
            {
                var a = lines[i];
                var b = lines[j];
                var w = MathF.Min(a.X + a.Width, b.X + b.Width) - MathF.Max(a.X, b.X);
                var h = MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y);
                if (w > 0.5f && h > 0.5f)
                {
                    faults.Add($"'{a.Text}' over '{b.Text}'");
                }
            }
        }
        return faults;
    }

    private static void Save(string name, RgbaImageRenderer renderer, int width, int height)
    {
        Directory.CreateDirectory(OutFolder);
        var path = Path.Combine(OutFolder, name + ".png");
        File.WriteAllBytes(path, PngWriter.Encode(renderer.Surface.Pixels, width, height));
        global::System.Console.WriteLine($"buddy capture: {path}");
    }
}
