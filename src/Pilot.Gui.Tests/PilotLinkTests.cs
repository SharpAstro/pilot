using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The link a desktop toast's click hands the GUI (#1356): <c>pilot://card/&lt;id&gt;</c> parses and junk is refused; a chrome given the link opens a
/// PR card on its Pull requests row and any other card in the For you bar, and says plainly when the card is closed or unknown. CPU-rendered:
/// no window, no process.
/// </summary>
public sealed class PilotLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pilot://card/c1", "c1")]
    [InlineData("pilot://card/c1/", "c1")]
    [InlineData("PILOT://CARD/c%2F9", "c/9")]
    [InlineData("pilot://card/card-42%20x", "card-42 x")]
    public void ACardLinkParsesToItsId(string text, string id)
    {
        Pilot.Ui.PilotLink.TryParse(text, out var link).ShouldBeTrue();
        link.CardId.ShouldBe(id);
    }

    [Theory]
    [InlineData("pilot:card%2Fc1", "c1")]
    [InlineData("PILOT:card%2Fc1", "c1")]
    [InlineData("pilot:card%2Fcard-42%20x%26y", "card-42 x&y")]
    [InlineData("pilot:card%2Fc1/", "c1")]
    [InlineData("  pilot:card%2Fc1  ", "c1")]
    [InlineData("pilot:card%2Fa%2F", "a/")]
    public void AnAppShellCardLinkParsesToItsId(string text, string id)
    {
        Pilot.Ui.PilotLink.TryParse(text, out var link).ShouldBeTrue();
        link.CardId.ShouldBe(id);
    }

    [Theory]
    [InlineData("pilot:cards")]
    [InlineData("pilot:cards/")]
    [InlineData("pilot://cards")]
    public void BothCardsFormsParseWithNoCard(string text)
    {
        Pilot.Ui.PilotLink.TryParse(text, out var link).ShouldBeTrue();
        link.CardId.ShouldBeNull();
    }

    [Theory]
    [InlineData("c1")]
    [InlineData("card 42/x&y?z#w")]
    [InlineData("é ids")]
    public void AppShellsOwnLinkRoundTripsForACard(string id)
    {
        var text = SharpAstro.AppShell.LaunchScheme.Link(ToastLink.Scheme, ToastLink.CardPayload(id));
        Pilot.Ui.PilotLink.TryParse(text, out var link).ShouldBeTrue(text);
        link.CardId.ShouldBe(id);
    }

    [Fact]
    public void AppShellsOwnLinkRoundTripsForTheCardsPage()
    {
        var text = SharpAstro.AppShell.LaunchScheme.Link(ToastLink.Scheme, ToastLink.CardsPayload);
        Pilot.Ui.PilotLink.TryParse(text, out var link).ShouldBeTrue(text);
        link.CardId.ShouldBeNull();
    }

    [Theory]
    [InlineData("other:card%2Fc1")]
    [InlineData("pilot:card%2F")]
    [InlineData("pilot:card%2F%20")]
    [InlineData("pilot:card%ZZ")]
    [InlineData("pilot:")]
    [InlineData("pilot:cards%2Fx")]
    [InlineData("pilot:junk")]
    public void AppShellFormRefusals(string text) => Pilot.Ui.PilotLink.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void TheBareCardsLinkParsesWithNoCard()
    {
        Pilot.Ui.PilotLink.TryParse("pilot://cards", out var link).ShouldBeTrue();
        link.CardId.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pilot://card")]
    [InlineData("pilot://card/")]
    [InlineData("pilot://card/a/b")]
    [InlineData("pilot://cards/c1")]
    [InlineData("pilot://flight/PIL1")]
    [InlineData("https://card/c1")]
    [InlineData("card/c1")]
    [InlineData("--page")]
    public void JunkIsRefused(string? text) => Pilot.Ui.PilotLink.TryParse(text, out _).ShouldBeFalse();

    private static CardRow Card(string id, string subject, string title, string? link, CardState state = CardState.Open) => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "pr-ready", Subject = subject, Title = title, Link = link, At = Now, State = state,
    };

    private static PullRequestsScreen Prs(PullRequestRow pr) =>
        new(new PullRequestsRow { Orgs = ["SharpAstro"], ReadAt = Now, Viewer = "ada", PullRequests = [pr] }, "test", Now);

    private static void Draw(Pilot.Ui.PilotChrome<RgbaImage> chrome, Pilot.Ui.CardsScreen cards, PullRequestsScreen prs) =>
        chrome.Render(new Pilot.Ui.SessionsScreen([], "test", Now), null, cards, null, null, null, prs, new RectF32(0, 0, 1280, 520));

    private static Pilot.Ui.PilotChrome<RgbaImage> Window(RgbaImageRenderer renderer)
    {
        var chrome = new Pilot.Ui.PilotChrome<RgbaImage>(renderer, new Pilot.Ui.SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.LinkPullRequests();
        return chrome;
    }

    [Fact]
    public void AChromeGivenACardLinkOpensAPullRequestCardOnItsRowAndAnyOtherCardInTheForYouBar()
    {
        using var renderer = new RgbaImageRenderer(1280, 520);
        var chrome = Window(renderer);
        var pr = new PullRequestRow
        {
            Repo = "SharpAstro/pilot", Number = 252, Title = "A change", Url = "https://github.com/SharpAstro/pilot/pull/252", Author = "ada", Kind = PullRequestKind.Ready,
            Label = "Ready", NeedsMe = true, UpdatedAt = Now, CreatedAt = Now.AddDays(-1), Head = "flight/pil252", Base = "main",
        };
        var prCard = Card("c1", Verbs.FlightTarget("PIL252"), "PIL252's PR #252 is ready to merge", pr.Url);
        var otherCard = Card("c2", Verbs.FlightTarget("PIL900"), "PIL900 needs an answer", null);
        var cards = new Pilot.Ui.CardsScreen([prCard, otherCard], []);
        var screen = Prs(pr);
        chrome.Show(PilotPage.Home);
        Draw(chrome, cards, screen);
        Draw(chrome, cards, screen);

        Pilot.Ui.PilotLink.TryParse(Contract("c1"), out var prLink).ShouldBeTrue();
        chrome.OpenCardLink(prLink).ShouldBeNull();
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Draw(chrome, cards, screen);
        Draw(chrome, cards, screen);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Url.ShouldBe(pr.Url);

        chrome.Show(PilotPage.Home);
        chrome.Notifications.Open = false;
        Pilot.Ui.PilotLink.TryParse(Contract("c2"), out var otherLink).ShouldBeTrue();
        chrome.OpenCardLink(otherLink).ShouldBeNull();
        chrome.Page.ShouldBe(PilotPage.Home);
        chrome.Notifications.Open.ShouldBeTrue("a card about no PR opens in the For you bar");
        chrome.Cards.Focus.ShouldBe("PIL900");
    }

    [Fact]
    public void AClosedOrUnknownCardSaysSo()
    {
        using var renderer = new RgbaImageRenderer(1280, 520);
        var chrome = Window(renderer);
        var open = Card("c2", Verbs.FlightTarget("PIL900"), "PIL900 needs an answer", null);
        var closed = Card("c3", Verbs.FlightTarget("PIL901"), "PIL901 was answered", null, CardState.Closed);
        var cards = new Pilot.Ui.CardsScreen([open], [closed]);
        var screen = Prs(new PullRequestRow
        {
            Repo = "SharpAstro/pilot", Number = 1, Title = "t", Url = "https://github.com/SharpAstro/pilot/pull/1", Author = "ada", Kind = PullRequestKind.Draft,
            Label = "Draft", UpdatedAt = Now, CreatedAt = Now, Head = "x", Base = "main",
        });
        Draw(chrome, cards, screen);
        Draw(chrome, cards, screen);
        chrome.Show(PilotPage.Home);
        chrome.Notifications.Open = false;

        Pilot.Ui.PilotLink.TryParse(Contract("c3"), out var closedLink).ShouldBeTrue();
        chrome.OpenCardLink(closedLink).ShouldNotBeNull().ShouldContain("closed");
        Pilot.Ui.PilotLink.TryParse(Contract("nope"), out var unknownLink).ShouldBeTrue();
        chrome.OpenCardLink(unknownLink).ShouldNotBeNull().ShouldContain("does not know");
        chrome.Page.ShouldBe(PilotPage.Home);
        chrome.Notifications.Open.ShouldBeFalse();
    }

    [Fact]
    public void AClosedOrUnknownCardSaysSoInTheWindowItselfAsAToast()
    {
        using var renderer = new RgbaImageRenderer(1280, 520);
        var chrome = Window(renderer);
        chrome.Clock = () => Now;
        var closed = Card("c3", Verbs.FlightTarget("PIL901"), "PIL901 was answered", null, CardState.Closed);
        var cards = new Pilot.Ui.CardsScreen([], [closed]);
        var screen = Prs(new PullRequestRow
        {
            Repo = "SharpAstro/pilot", Number = 1, Title = "t", Url = "https://github.com/SharpAstro/pilot/pull/1", Author = "ada", Kind = PullRequestKind.Draft,
            Label = "Draft", UpdatedAt = Now, CreatedAt = Now, Head = "x", Base = "main",
        });
        Draw(chrome, cards, screen);
        chrome.Show(PilotPage.Home);
        chrome.Notifications.Open = true;

        Pilot.Ui.PilotLink.TryParse(Contract("c3"), out var link).ShouldBeTrue();
        chrome.OpenCardLink(link).ShouldNotBeNull();
        chrome.Notifications.Open.ShouldBeFalse("the bar is collapsed so the notice is drawn");
        Draw(chrome, cards, screen);
        chrome.Notifications.Animating.ShouldBeTrue("a fading toast is up");
        var said = chrome.Notifications.History.ShouldHaveSingleItem();
        said.Title.ShouldBe("That card is closed");
        said.Text.ShouldContain("c3");

        Pilot.Ui.PilotLink.TryParse(Contract("no-such-card"), out var unknown).ShouldBeTrue();
        chrome.OpenCardLink(unknown).ShouldNotBeNull();
        chrome.Notifications.History.Select(n => n.Title).ShouldContain("That card is not here");
    }

    [Fact]
    public void OpeningTheForYouBarOrTheNotificationsPageSaysTheCardsAreShownAndClosingSaysNot()
    {
        using var renderer = new RgbaImageRenderer(1280, 520);
        var chrome = Window(renderer);
        var said = new List<bool>();
        chrome.Notifications.Open = false;
        chrome.OnCardsShown = said.Add;
        said.ShouldBeEmpty("the host says the first value itself; setting the callback calls nothing");

        chrome.Notifications.Open = true;
        said.ShouldBe([true]);
        chrome.Notifications.Open = true;
        said.ShouldBe([true], "an unchanged state calls nothing");
        chrome.Notifications.Open = false;
        said.ShouldBe([true, false]);

        chrome.Show(PilotPage.Notifications);
        said.ShouldBe([true, false, true]);
        chrome.Show(PilotPage.Home);
        said.ShouldBe([true, false, true, false]);
        chrome.ClientShowsCards.ShouldBeFalse();
    }

    // With PILOT_TEST_CAPTURE set, writes the notice a stale toast's click leaves, at a narrow and a wide window, with a long card id.
    [Fact]
    public void CapturesTheNoticeOfAClosedAndAnUnknownCardAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        var longId = "c-" + string.Join("-", Enumerable.Repeat("8f3a2b1c9d4e", 5));
        foreach (var (name, closed) in new[] { ("closed", true), ("unknown", false) })
        {
            foreach (var (width, height) in new[] { (520, 520), (1280, 520) })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var chrome = Window(renderer);
                chrome.Clock = () => Now;
                var cards = new Pilot.Ui.CardsScreen([], closed ? [Card(longId, Verbs.FlightTarget("PIL901"), "PIL901 was answered", null, CardState.Closed)] : []);
                var screen = Prs(new PullRequestRow
                {
                    Repo = "SharpAstro/pilot", Number = 1, Title = "t", Url = "https://github.com/SharpAstro/pilot/pull/1", Author = "ada", Kind = PullRequestKind.Draft,
                    Label = "Draft", UpdatedAt = Now, CreatedAt = Now, Head = "x", Base = "main",
                });
                chrome.Render(new Pilot.Ui.SessionsScreen([], "test", Now), null, cards, null, null, null, screen, new RectF32(0, 0, width, height));
                Pilot.Ui.PilotLink.TryParse(Contract(longId), out var link).ShouldBeTrue();
                chrome.OpenCardLink(link).ShouldNotBeNull();
                chrome.Render(new Pilot.Ui.SessionsScreen([], "test", Now), null, cards, null, null, null, screen, new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"card-link-{name}-{width}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }

    private static string Contract(string id) => $"pilot://card/{Uri.EscapeDataString(id)}";
}
