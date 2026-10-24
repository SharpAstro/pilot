using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The card a waiting session gets from its node: shown with the node it was raised on, its title and the tool it asks, and
/// buttons that do their thing: Answer in the browser opens the Remote Control URL, Its transcript opens the session's
/// transcript on the Sessions page, and Open (an interactive session only) shows the session itself there.
/// </summary>
public sealed class WaitingCardTests
{
    private const string Url = "https://claude.ai/code/session_waiting";
    private static readonly DateTimeOffset Now = Live.At.AddHours(10);

    private static NodeMark De1 => new("de1.sock", "de1.sock", "DE1", Here: false);

    private static NodeMark Se1 => new("se1.sock", "se1.sock", "SE1", Here: true);

    private static SessionRow Row(SessionKind kind, string? url = Url) => Live.Row("a1b2c3d4", "pilot-tianwen-2", SessionState.Waiting) with
    {
        Kind = kind, WaitingFor = "a permission prompt", WaitingSince = Now.AddHours(-10), Asking = "Bash: git push origin main", RemoteControlUrl = url,
    };

    private static CardRow Card(string? link = Url) => new()
    {
        Id = "c7", Bin = CardBin.ForYou, Kind = CardKinds.SessionWaiting, Subject = "session/a1b2c3d4", At = Now, State = CardState.Open, Link = link,
        Title = "pilot-tianwen-2 waits on a permission prompt · since 10 h 0 m", Detail = "Bash: git push origin main",
    };

    // The window: a chrome on the Home page with the card of DE1 merged in, and DE1's session in the sessions it links to.
    private static (PilotChrome<RgbaImage> Chrome, CardsScreen Cards, SessionsScreen Sessions, List<string> Opened) Window(RgbaImageRenderer renderer, SessionKind kind, string? url = Url)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var opened = new List<string>();
        chrome.Cards.OnOpen = opened.Add;
        var sessions = new SessionsScreen(SessionLine.Order([
            SessionLine.From(Row(kind, url), Now) with { Node = De1 },
            // The same id on this window's own node: the card is DE1's, so it is not this one's.
            SessionLine.From(Live.Row("a1b2c3d4", "se1-other", SessionState.Idle), Now) with { Node = Se1 },
        ]), "test", Now);
        chrome.LinkCardTranscripts(() => sessions, () => new FlightsScreen([], "test", Now));
        var cards = CardsScreen.Merge([(De1, CardsScreen.From([Card(url)])), (Se1, CardsScreen.Empty)], null);
        return (chrome, cards, sessions, opened);
    }

    private static string Described(PilotChrome<RgbaImage> chrome, CardsScreen cards, SessionsScreen sessions)
    {
        chrome.Render(sessions, null, cards, new RectF32(0, 0, 1280, 800));
        return CellLayout.Describe(Layout.Engine.Arrange(chrome.Cards.Build(cards), new Rect<int>(0, 0, 60, 60), CellMeasureContext.PixelAuthored));
    }

    // The panel's text as read: each word is a leaf of its own in the description, so the leaves are joined and the
    // whitespace collapsed before a sentence is looked for.
    private static bool Says(string described, string text) =>
        string.Join(' ', string.Join(' ', System.Text.RegularExpressions.Regex.Matches(described, "Leaf Text \"([^\"]*)\"").Select(m => m.Groups[1].Value))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Contains(text, StringComparison.Ordinal);

    private static ClickableRegion Region(PilotChrome<RgbaImage> chrome, string action)
    {
        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        return regions.Single(r => r.Result is HitResult.ButtonHit { Action: var a } && a == action);
    }

    private static bool Has(PilotChrome<RgbaImage> chrome, string action)
    {
        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        return regions.Any(r => r.Result is HitResult.ButtonHit { Action: var a } && a == action);
    }

    [Fact]
    public void TheCardIsShownWithItsNodesNameItsTitleWhatItAsksAndItsButtons()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, cards, sessions, _) = Window(renderer, SessionKind.Interactive);

        var described = Described(chrome, cards, sessions);

        Says(described, "for you · DE1").ShouldBeTrue(described);
        Says(described, "pilot-tianwen-2 waits on a permission prompt · since 10 h 0 m").ShouldBeTrue(described);
        Says(described, "Bash: git push origin main").ShouldBeTrue(described);
        Says(described, "Answer in the browser" + Followable.Away).ShouldBeTrue(described);
        Says(described, "Its transcript").ShouldBeTrue(described);
        Says(described, "Open").ShouldBeTrue(described);
        Says(described, "Dismiss").ShouldBeTrue(described);
        // The browser button is the way in: the title is not a second link to the same page.
        Has(chrome, "card:c7:title").ShouldBeFalse();
    }

    [Fact]
    public void AnswerInTheBrowserOpensTheSessionsRemoteControlUrl()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, cards, sessions, opened) = Window(renderer, SessionKind.Background);
        Described(chrome, cards, sessions);

        var button = Region(chrome, "card:c7:answer");
        button.Cursor.ShouldBe(CursorKind.Pointer);
        button.OnClick!.Invoke(InputModifier.None);

        opened.ShouldBe([Url]);
    }

    [Fact]
    public void ItsTranscriptOpensThatSessionsTranscriptOnTheCardsNodeNotAnotherWithTheSameId()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, cards, sessions, _) = Window(renderer, SessionKind.Background);
        Described(chrome, cards, sessions);

        Region(chrome, "card:c7:transcript").OnClick!.Invoke(InputModifier.None);

        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.RowId.ShouldBe("a1b2c3d4");
        chrome.Sessions.Transcript.Node.ShouldBe(De1);
        chrome.Sessions.Transcript.Title.ShouldNotBeNull().ShouldContain("pilot-tianwen-2");
        chrome.Sessions.Transcript.Close();
    }

    [Fact]
    public void OpenShowsAnInteractiveSessionOnTheSessionsPageSelectedAndOnlyAnInteractiveOneHasIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, cards, sessions, _) = Window(renderer, SessionKind.Interactive);
        Described(chrome, cards, sessions);

        Region(chrome, "card:c7:open").OnClick!.Invoke(InputModifier.None);

        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Sessions.Transcript.IsOpen.ShouldBeFalse();
        chrome.Render(sessions, null, cards, new RectF32(0, 0, 1280, 800));
        chrome.Sessions.Tree(sessions);
        chrome.Sessions.Selected.ShouldBeGreaterThanOrEqualTo(0);
        sessions.Lines[chrome.Sessions.Selected].Name.ShouldBe("pilot-tianwen-2");

        var (background, backgroundCards, backgroundSessions, _) = Window(renderer, SessionKind.Background);
        Described(background, backgroundCards, backgroundSessions);
        Has(background, "card:c7:open").ShouldBeFalse();
    }

    [Fact]
    public void ACardWithoutALinkHasNoBrowserButton()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, cards, sessions, _) = Window(renderer, SessionKind.Interactive, url: null);

        var described = Described(chrome, cards, sessions);

        Says(described, "Answer in the browser").ShouldBeFalse(described);
        Has(chrome, "card:c7:answer").ShouldBeFalse();
        Has(chrome, "card:c7:transcript").ShouldBeTrue();
    }
}
