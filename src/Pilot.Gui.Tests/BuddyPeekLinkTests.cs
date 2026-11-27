using DIR.Lib;
using Pilot.Buddy;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// #1381 section 8 on fakes: each peek row carries the link of its asking card, else its flight (and leg), else its session, with <c>?node=</c> when the
/// row is another node's; a click on a row hands exactly that link to the GUI hand-off; the row under the pointer is highlighted in the accent with the
/// text's contrast kept on light and dark, and a cut row's whole text shows on hover without the card changing size. Drawn on the CPU, no window.
/// </summary>
public sealed class BuddyPeekLinkTests
{
    private static string Font => SessionsCapture.FontPath;

    private static readonly string OutFolder = Path.Combine(Environment.GetEnvironmentVariable("PILOT_JOB_ARTIFACTS") is { Length: > 0 } artifacts ? artifacts : Path.GetTempPath(), "pilot-buddy-captures");

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeWindow : IBuddyWindow
    {
        public void MoveBy(int dx, int dy)
        {
        }

        public void Dropped()
        {
        }
    }

    private sealed class NullNode : IBuddyNode
    {
        public Task<string?> SayAsync(string text, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken) => Task.FromResult<ChatPage?>(null);
    }

    private sealed class FakeGui : IGuiHandOff
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _links = new();

        public IReadOnlyList<string> Links => [.. _links];

        public SemaphoreSlim Done { get; } = new(0);

        public bool Open(string link)
        {
            _links.Enqueue(link);
            Done.Release();
            return true;
        }
    }

    private static SessionRow Session(string id, SessionState state, string? flight = null, string? leg = null, string? asking = null, string? name = null) => new()
    {
        Id = id, SessionId = id, Name = name ?? "name-" + id, Kind = SessionKind.Background, Cwd = "C:\\x", Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 1,
        State = state, SpendWeighted = 0, StartedAt = new DateTimeOffset(2026, 11, 28, 7, 0, 0, TimeSpan.Zero), Flight = flight, Leg = leg, Asking = asking,
        WaitingSince = state == SessionState.Waiting ? new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero) : null,
    };

    private static CardRow AskCard(string id, string sessionId) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "session-waiting", Subject = "session/" + sessionId, Title = "Asks", At = new DateTimeOffset(2026, 11, 28, 8, 0, 0, TimeSpan.Zero), State = CardState.Open,
    };

    private sealed class Rig
    {
        private long _seq;

        public Rig(bool otherNode = false, string nodeName = "SE1", params SessionRow[] sessions)
        {
            var clock = new Clock();
            Model = new BuddyModel(clock);
            Feed = new BuddyFeed(Model, clock, () => new BuddySettings()) { NodeName = nodeName, OtherNode = otherNode };
            Feed.Apply(new SnapshotEvent
            {
                Seq = _seq++, At = clock.GetUtcNow(), Stream = "s", Sessions = sessions, Notes = [],
                Cards = [AskCard("card-1", "ask1")], Flights = [],
            });
            var chat = new BuddyChat(new NullNode(), Gui, () => null);
            Desk = new BuddyDesk(Model, Feed, chat, Gui, new FakeWindow(), () => new BuddySettings(), _ => { }, clock, TimeZoneInfo.Utc, new object(), () => Woke.Release());
            Draw();
        }

        public BuddyModel Model { get; }

        public BuddyFeed Feed { get; }

        public BuddyDesk Desk { get; }

        public FakeGui Gui { get; } = new();

        public SemaphoreSlim Woke { get; } = new(0);

        public SceneLayout Draw(bool dark = false)
        {
            using var renderer = new RgbaImageRenderer(480, 720);
            var layout = BuddyScene.Draw(renderer, Font, BuddyPalette.For(dark), BuddyScene.ViewOf(Model), 1f, false, 0, Desk.CardFactory<RgbaImage>());
            Desk.Geometry = new BuddyGeometry(layout);
            return layout;
        }

        // Opens the peek the way a person does: a click on the cat opens the chat, whose Sessions link goes to the peek.
        public PeekCard OpenPeek()
        {
            var layout = Desk.Geometry!.Layout;
            Click((layout.CatX + 100, layout.CatY + 80));
            Draw();
            var chat = Desk.Geometry!.Layout.Card.ShouldBeOfType<ChatCard>();
            var sessions = chat.Hits.Single(h => h.Id == ChatCard.SessionsId);
            layout = Desk.Geometry.Layout;
            Click((layout.CardX + sessions.X + 4, layout.CardY + sessions.Y + 4));
            Draw();
            return Desk.Geometry!.Layout.Card.ShouldBeOfType<PeekCard>();
        }

        public (float X, float Y) Inside(CardHit hit)
        {
            var layout = Desk.Geometry!.Layout;
            return (layout.CardX + hit.X + 20, layout.CardY + hit.Y + 4);
        }

        public void Click((float X, float Y) at)
        {
            Desk.ButtonDown(1, at.X, at.Y);
            Desk.ButtonUp(1, at.X, at.Y);
        }

        // The hand-off runs on the pool; wait for the hand-off itself (an event), with a deadline as a guard against a hang.
        public async Task TakenAsync() =>
            (await Gui.Done.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeTrue("the GUI was handed a link");
    }

    private static SessionRow[] Four() =>
    [
        Session("ask1", SessionState.Waiting, asking: "Publish?"),
        Session("fl1", SessionState.Working, flight: "PIL1381"),
        Session("lg1", SessionState.Working, flight: "PIL1381", leg: "peek-links"),
        Session("plain1", SessionState.Idle),
    ];

    [Fact]
    public void EveryPeekRowCarriesTheLinkOfItsAskingCardElseItsFlightLegOrSession()
    {
        var rig = new Rig(false, "SE1", Four());

        var links = rig.Feed.Peek().Rows.Select(r => r.Link).ToArray();

        links.ShouldContain("pilot://card/card-1");
        links.ShouldContain("pilot://flight/PIL1381");
        links.ShouldContain("pilot://flight/PIL1381/peek-links");
        links.ShouldContain("pilot://session/plain1");
        foreach (var l in links)
        {
            PilotLink.TryParse(l, out var back).ShouldBeTrue("each one parses back");
        }
    }

    [Fact]
    public void AnotherNodesRowsCarryItsNodeAsAQuery()
    {
        var rig = new Rig(true, "DE1 box", Four());

        var links = rig.Feed.Peek().Rows.Select(r => r.Link).ToArray();

        links.ShouldContain("pilot://card/card-1?node=DE1%20box");
        links.ShouldContain("pilot://flight/PIL1381?node=DE1%20box");
        links.ShouldContain("pilot://flight/PIL1381/peek-links?node=DE1%20box");
        links.ShouldContain("pilot://session/plain1?node=DE1%20box");
        foreach (var link in links)
        {
            PilotLink.TryParse(link, out var parsed).ShouldBeTrue();
            parsed.Node.ShouldBe("DE1 box");
        }
    }

    [Theory]
    [InlineData("pilot://card/card-1")]
    [InlineData("pilot://flight/PIL1381")]
    [InlineData("pilot://flight/PIL1381/peek-links")]
    [InlineData("pilot://session/plain1")]
    public async Task AClickOnARowHandsItsLinkToTheGui(string expected)
    {
        var rig = new Rig(false, "SE1", Four());
        var peek = rig.OpenPeek();
        var index = rig.Feed.Peek().Rows.ToList().FindIndex(r => r.Link == expected);
        index.ShouldBeGreaterThanOrEqualTo(0);

        rig.Click(rig.Inside(peek.Hits.Single(h => h.Id == $"row:{index}")));
        await rig.TakenAsync();

        rig.Gui.Links.ShouldBe([expected]);
    }

    [Fact]
    public async Task AClickOnAnotherNodesRowHandsALinkWithItsNode()
    {
        var rig = new Rig(true, "DE1", Four());
        var peek = rig.OpenPeek();
        var index = rig.Feed.Peek().Rows.ToList().FindIndex(r => r.Link == "pilot://flight/PIL1381/peek-links?node=DE1");

        rig.Click(rig.Inside(peek.Hits.Single(h => h.Id == $"row:{index}")));
        await rig.TakenAsync();

        rig.Gui.Links.ShouldBe(["pilot://flight/PIL1381/peek-links?node=DE1"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRowUnderThePointerIsHighlightedAndItsTextKeepsItsContrast(bool dark)
    {
        var rig = new Rig(false, "SE1", Four());
        var peek = rig.OpenPeek();
        var hit = peek.Hits.Single(h => h.Id == "row:1");
        var (x, y) = rig.Inside(hit);

        rig.Draw(dark);
        rig.Desk.HoveredHit.ShouldBeNull();
        rig.Desk.PointerMoved(x, y);
        rig.Desk.HoveredHit.ShouldBe("row:1");
        rig.Desk.OverRow.ShouldBeTrue("the host shows the hand cursor");
        rig.Draw(dark);
        var hovered = rig.Desk.Geometry!.Layout.Card.ShouldBeOfType<PeekCard>();

        hovered.Items.Count(i => i.Kind == CardItemKind.RowHover).ShouldBe(1);
        hovered.Height.ShouldBe(peek.Height, "nothing moves under the pointer");
        hovered.Hits.Select(h => (h.Id, h.Y)).ShouldBe(peek.Hits.Select(h => (h.Id, h.Y)));

        var palette = BuddyPalette.For(dark);
        BuddyContrast.Ratio(palette.Accent, palette.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Edge, "the row's edge");
        // The row keeps the panel's fill, so its text is drawn on exactly what the palette's text contrast is checked against.
        foreach (var text in new[] { palette.BodyText, palette.MutedText, palette.Amber })
        {
            BuddyContrast.Ratio(text, palette.PanelBg).ShouldBeGreaterThanOrEqualTo(BuddyContrast.Text);
        }

        rig.Desk.PointerLeft();
        rig.Desk.HoveredHit.ShouldBeNull();
    }

    [Fact]
    public void ACutRowShowsItsWholeTextOnHoverWithoutChangingTheCard()
    {
        var longName = "review of PR #1351 in SharpAstro/pilot " + string.Join(' ', Enumerable.Repeat("extraordinarily-long-session-name", 3));
        var rig = new Rig(false, "SE1", [Session("long1", SessionState.Idle, name: longName)]);
        var peek = rig.OpenPeek();
        peek.Tooltips.ShouldContainKey("row:0");
        var (x, y) = rig.Inside(peek.Hits.Single(h => h.Id == "row:0"));

        rig.Desk.PointerMoved(x, y);
        rig.Draw();
        var hovered = rig.Desk.Geometry!.Layout.Card.ShouldBeOfType<PeekCard>();

        hovered.Items.ShouldContain(i => i.Kind == CardItemKind.TipBox);
        var prefix = "SE1 · " + longName.Substring(0, 10);
        hovered.Items.Where(i => i.Kind == CardItemKind.Text).Select(i => i.Text).ShouldContain(t => t.StartsWith(prefix, StringComparison.Ordinal));
        hovered.Height.ShouldBe(peek.Height);
        hovered.Hits.Count.ShouldBe(peek.Hits.Count, "the box covers no hit");
    }

    // With PILOT_TEST_CAPTURE set, writes the peek with a hovered row, bright and dark, outside the plane.
    [Fact]
    public void CapturesThePeekWithAHoveredRowBrightAndDark()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 })
        {
            return;
        }
        Directory.CreateDirectory(OutFolder);
        foreach (var dark in new[] { false, true })
        {
            var rig = new Rig(false, "SE1", [.. Four(), Session("long1", SessionState.Idle, name: "review of PR #1351 in SharpAstro/pilot extraordinarily-long-session-name another-long-word")]);
            var peek = rig.OpenPeek();
            var (x, y) = rig.Inside(peek.Hits.Single(h => h.Id == "row:4"));
            rig.Desk.PointerMoved(x, y);
            using var renderer = new RgbaImageRenderer(480, 720);
            BuddyScene.Draw(renderer, Font, BuddyPalette.For(dark), BuddyScene.ViewOf(rig.Model), 1f, false, 0, rig.Desk.CardFactory<RgbaImage>());
            File.WriteAllBytes(Path.Combine(OutFolder, $"peek-hover-{(dark ? "dark" : "bright")}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, 480, 720));
        }
    }
}
