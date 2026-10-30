using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A link that leaves the app has a right-click menu with one item, Copy URL: it hands the URL to the host's CopyText and
/// shows a short "Copied" notice at the pointer, that goes on its own. The cards panel is the first to use it
/// (SharpAstro/pilot#337).
/// </summary>
public sealed class LinkMenuTests
{
    private const string PrUrl = "https://github.com/SharpAstro/DIR.Lib/pull/138";
    private const string RcUrl = "https://claude.ai/code/session_waiting";
    private static readonly PointerPress Right = new(40f, 90f, MouseButton.Right, InputModifier.None, 1);

    private static CardRow PrCard() => new()
    {
        Id = "c5", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/DIR112", Title = "DIR112 landed", At = Live.At, State = CardState.Open, Link = PrUrl,
    };

    private static CardRow WaitingCard() => new()
    {
        Id = "c7", Bin = CardBin.ForYou, Kind = CardKinds.SessionWaiting, Subject = "session/a1b2c3d4", At = Live.At, State = CardState.Open, Link = RcUrl,
        Title = "pilot-tianwen-2 waits on a permission prompt · since 10 h 0 m", Detail = "Bash: git push origin main",
    };

    private static (CardsView<RgbaImage> View, List<string> Copied) Panel(RgbaImageRenderer renderer)
    {
        var copied = new List<string>();
        return (new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CopyText = copied.Add }, copied);
    }

    private static List<ClickableRegion> Painted(CardsView<RgbaImage> view, CardsScreen screen)
    {
        view.Render(screen, new RectF32(0, 0, CardsView<RgbaImage>.Width, 800));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions;
    }

    private static ClickableRegion Link(List<ClickableRegion> regions, string action) =>
        regions.Single(r => r.Result is HitResult.ButtonHit b && b.Action == action);

    [Fact]
    public void ARightClickOnACardsTitleLinkOffersCopyUrlAndNothingElse()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, copied) = Panel(renderer);
        var screen = CardsScreen.From([PrCard()]);

        Link(Painted(view, screen), "card:c5:title").OnContextMenu.ShouldNotBeNull().Invoke(Right);

        view.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        // The menu is on the page, and its one row is what a click on it chooses.
        Painted(view, screen).ShouldContain(r => r.Result is HitResult.ListItemHit);
        view.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([PrUrl]);
    }

    [Fact]
    public void AWaitingSessionsAnswerInTheBrowserHasTheSameMenu()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, copied) = Panel(renderer);

        Link(Painted(view, CardsScreen.From([WaitingCard()])), "card:c7:answer").OnContextMenu.ShouldNotBeNull().Invoke(Right);

        view.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        view.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([RcUrl]);
    }

    [Fact]
    public void TheWatchtowersRemoteControlLinkHasTheSameMenu()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, copied) = Panel(renderer);
        var tower = Live.Row("bbbb0002", "se1-watchtower", SessionState.Idle) with { Kind = SessionKind.Background, Role = SessionRole.Watchtower, RemoteControlUrl = RcUrl };

        Link(Painted(view, CardsScreen.From([], [tower])), "watchtower:rc").OnContextMenu.ShouldNotBeNull().Invoke(Right);

        view.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        view.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([RcUrl]);
    }

    [Fact]
    public void ALeftClickStillOpensTheLinkAndOnlyARightClickOpensTheMenu()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, _) = Panel(renderer);
        var opened = new List<string>();
        view.OnOpen = opened.Add;

        Link(Painted(view, CardsScreen.From([PrCard()])), "card:c5:title").OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

        opened.ShouldBe([PrUrl]);
        view.Links.MenuItems.ShouldBeEmpty();
    }

    [Fact]
    public void AHostWithNoClipboardGivesALinkNoMenu()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };

        Link(Painted(view, CardsScreen.From([PrCard()])), "card:c5:title").OnContextMenu.ShouldBeNull();
    }

    [Fact]
    public void ChoosingCopyUrlShowsACopiedNoticeThatGoesOnItsOwn()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, _) = Panel(renderer);
        var now = Live.At;
        view.Links.Now = () => now;
        var screen = CardsScreen.From([PrCard()]);
        var regions = Painted(view, screen);
        var plain = Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 300, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f)).Count();

        Link(regions, "card:c5:title").OnContextMenu!.Invoke(Right);
        view.Links.MenuItems[0].OnChoose!();
        view.Links.NoticeShown.ShouldBeTrue();
        Texts(view, screen).ShouldContain(LinkMenu.CopiedText);

        now += LinkMenu.NoticeFor - LinkMenu.FadeFor / 2;
        Texts(view, screen).ShouldContain(LinkMenu.CopiedText);

        now += LinkMenu.FadeFor;
        view.Links.NoticeShown.ShouldBeFalse();
        Texts(view, screen).ShouldNotContain(LinkMenu.CopiedText);
        // Nothing else is left on the screen: the tree is the one it was before the menu.
        Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 300, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f)).Count().ShouldBe(plain);
    }

    private static IEnumerable<string> Texts(CardsView<RgbaImage> view, CardsScreen screen)
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        return [.. Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, 300, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value : null).OfType<string>()];
    }

    // ---- The other pages (SharpAstro/pilot#337, link-menu-pages) ----

    private static readonly DateTimeOffset Now = Live.At.AddHours(2);
    private const string FlightPrUrl = "https://github.com/x/tianwen/pull/7";

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        PullRequest = 7, PullRequestUrl = FlightPrUrl,
    };

    private static LegRow[] Legs() =>
    [
        new()
        {
            Flight = "TIA1611", Name = "render", Order = 1, Goal = "Draw the planets.", DoneWhen = "The capture shows all eight.", State = LegState.Flying,
            Artifacts = [new Artifact { Title = "The planets", Location = @"C:\caps\planets.png", At = Live.At.AddMinutes(5) }, new Artifact { Title = "Preview", Location = "https://example.org/preview", At = Live.At.AddMinutes(20) }],
        },
    ];

    private static FlightsScreen FlightsScreenOf() => new([FlightLine.Build([Flight()], [], Now, Legs()).Single()], "test", Now);

    private static SessionRow WaitingRow() => new()
    {
        Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "push-it", Kind = SessionKind.Interactive, Cwd = "/r/pilot",
        Role = SessionRole.AdHoc, State = SessionState.Waiting, WaitingFor = "permission to use Bash", WaitingSince = Now - new TimeSpan(9, 37, 0),
        Asking = "Bash: git push origin main", RemoteControlUrl = RcUrl, ContextTokens = 50_000, ContextWindow = 200_000, SpendWeighted = 1_000,
        LastResponseAt = Now.AddHours(-9), StartedAt = Live.At,
    };

    private static SessionsScreen WaitingScreen() => new(SessionLine.Build([WaitingRow()], Now), "test", Now);

    private static ReposScreen ReposScreenOf() =>
        new(RepoLine.Build(SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath), [], [], [], [], Live.At), "live", Live.At);

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, Flights = { Mode = FlightsViewMode.List } };

    private static List<ClickableRegion> RegionsOf(IPixelWidget widget)
    {
        var regions = new List<ClickableRegion>();
        widget.CollectPaintedRegions(regions);
        return regions;
    }

    // Who a right press at the point belongs to, as the router decides it: the topmost painted part with a menu of its own.
    private static ClickableRegion? Owner(List<ClickableRegion> regions, float x, float y) =>
        regions.LastOrDefault(r => r.OnContextMenu is not null && x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height);

    private static PointerPress PressIn(ClickableRegion r, float dx = 4f, float dy = 4f) => new(r.X + dx, r.Y + dy, MouseButton.Right, InputModifier.None, 1);

    [Fact]
    public void ARightClickOnTheFlightPagesPrLinkOffersCopyUrlAndNothingElse()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var copied = new List<string>();
        var chrome = NewChrome(renderer);
        var flights = chrome.Flights;
        flights.CopyText = copied.Add;
        var screen = FlightsScreenOf();
        chrome.Show(PilotPage.Flights);
        flights.OpenDetail("TIA1611");
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));

        var pr = RegionsOf(flights).Single(r => r.Result is HitResult.ButtonHit { Action: "pr:TIA1611" });
        var press = PressIn(pr);
        Owner(RegionsOf(flights), press.X, press.Y).ShouldBe(pr);
        pr.OnContextMenu!.Invoke(press);

        flights.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        // Not the row menu, which is for a row of the list.
        flights.MenuItems.ShouldBeEmpty();
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));
        RegionsOf(flights).ShouldContain(r => r.Result is HitResult.ListItemHit);
        flights.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([FlightPrUrl]);
    }

    [Fact]
    public void EveryLinkOnTheFlightPageCopiesItsOwnLocation()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var copied = new List<string>();
        var chrome = NewChrome(renderer);
        var flights = chrome.Flights;
        flights.CopyText = copied.Add;
        chrome.Show(PilotPage.Flights);
        flights.OpenDetail("TIA1611");
        chrome.Render(new SessionsScreen([], "test", Now), FlightsScreenOf(), new RectF32(0, 0, 1280, 800));

        var expected = new Dictionary<string, string>
        {
            ["pr:TIA1611"] = FlightPrUrl,
            ["artifact:TIA1611:render:The planets"] = @"C:\caps\planets.png",
            ["artifact:TIA1611:render:Preview"] = "https://example.org/preview",
            ["artifact-folder:TIA1611:render:The planets"] = @"C:\caps",
        };
        foreach (var (action, location) in expected)
        {
            var link = RegionsOf(flights).Single(r => r.Result is HitResult.ButtonHit b && b.Action == action);
            link.OnContextMenu.ShouldNotBeNull(action).Invoke(PressIn(link));
            flights.Links.MenuItems[0].OnChoose!();
            copied[^1].ShouldBe(location, action);
        }
    }

    [Fact]
    public void ALinkInTheFlightsPanelWinsOverItsRowsMenuAndTheRestOfTheRowKeepsIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        var flights = chrome.Flights;
        flights.CopyText = _ => { };
        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", Now), FlightsScreenOf(), new RectF32(0, 0, 1280, 800));
        var regions = RegionsOf(flights);

        // On the link under the list: Copy URL.
        var pr = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "pr:TIA1611" });
        Owner(regions, pr.X + 4f, pr.Y + 4f).ShouldBe(pr);

        // Elsewhere on the row: the row's own menu, with the link menu left shut.
        var row = regions.Single(r => r.Result is HitResult.ListItemHit);
        var owner = Owner(regions, row.X + row.Width - 6f, row.Y + 6f);
        owner.ShouldNotBeNull();
        owner.Value.Result.ShouldBeOfType<HitResult.ListItemHit>();
        owner.Value.OnContextMenu!.Invoke(new PointerPress(row.X + row.Width - 6f, row.Y + 6f, MouseButton.Right, InputModifier.None, 1));
        flights.MenuItems.Select(i => i.Label).ShouldContain("Open flight");
        flights.Links.MenuItems.ShouldBeEmpty();
    }

    [Fact]
    public void SessionsAnswerInTheBrowserHasCopyUrlAndTheRowsMenuStaysOffTheLink()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var copied = new List<string>();
        var sessions = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CopyText = copied.Add, OnOpen = _ => { } };
        var screen = WaitingScreen();
        sessions.Render(screen, new RectF32(0, 0, 1280, 800));
        var regions = RegionsOf(sessions);

        var pill = regions.Single(r => r.Result is HitResult.ButtonHit { Action: "session:a1b2c3d4:answer" });
        Owner(regions, pill.X + 4f, pill.Y + 4f).ShouldBe(pill);
        pill.OnContextMenu!.Invoke(PressIn(pill));
        sessions.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        sessions.MenuOpen.ShouldBeFalse();
        sessions.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([RcUrl]);

        // Off the link, on the same row: the row's menu.
        sessions.Render(screen, new RectF32(0, 0, 1280, 800));
        regions = RegionsOf(sessions);
        var list = regions.Single(r => r.Result is HitResult.ListItemHit);
        var owner = Owner(regions, list.X + list.Width - 6f, list.Y + 6f);
        owner.ShouldNotBeNull();
        owner.Value.Result.ShouldBeOfType<HitResult.ListItemHit>();
        owner.Value.OnContextMenu!.Invoke(new PointerPress(list.X + list.Width - 6f, list.Y + 6f, MouseButton.Right, InputModifier.None, 1));
        sessions.MenuOpen.ShouldBeTrue();
        sessions.Links.MenuItems.ShouldBeEmpty();
    }

    [Fact]
    public void ATranscriptsAnswerInTheBrowserHasCopyUrl()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var copied = new List<string>();
        var sessions = new SessionsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, CopyText = copied.Add, OnOpen = _ => { } };
        var screen = WaitingScreen();
        sessions.Transcript.Load = (_, _, _, _) => Task.FromResult(new TranscriptPage { Total = 0, Entries = [] });
        sessions.Tree(screen);
        sessions.Transcript.Open(screen.Lines[0]);
        sessions.Render(screen, new RectF32(0, 0, 1280, 800));

        var answer = RegionsOf(sessions).Single(r => r.Result is HitResult.ButtonHit { Action: "transcript:answer" });
        answer.OnContextMenu.ShouldNotBeNull().Invoke(PressIn(answer));
        sessions.Transcript.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        sessions.Transcript.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe([RcUrl]);
    }

    // Every link that leaves the app, whatever the page: its text ends in the away mark, and a right-click on it has the menu. The mark
    // alone counts too: a flight's PR link opens the Pull requests page, and the small ↗ beside it is the browser's.
    private static List<Layout.Node> AwayLinks(Layout.Node tree) =>
    [
        .. Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 200, 80), CellMeasureContext.PixelAuthored)
            .Select(a => a.Node)
            .Where(n => n.Hit is HitResult.ButtonHit && n is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.EndsWith(Followable.Away.TrimStart(), StringComparison.Ordinal)),
    ];

    private static void EveryAwayLinkHasTheMenu(string page, List<Layout.Node> links, int atLeast)
    {
        links.Count.ShouldBeGreaterThanOrEqualTo(atLeast, page);
        foreach (var link in links)
        {
            link.OnContextMenu.ShouldNotBeNull($"{page}: {((HitResult.ButtonHit)link.Hit!).Action}");
        }
    }

    [Fact]
    public void EveryAwayLinkOnTheFlightSessionsReposHomeAndFleetPagesCarriesTheLinkMenu()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        chrome.Flights.CopyText = _ => { };
        chrome.Flights.OnOpen = _ => { };
        chrome.Sessions.CopyText = _ => { };
        chrome.Sessions.OnOpen = _ => { };
        chrome.Repos.CopyText = _ => { };
        chrome.Repos.OnOpen = _ => { };
        var flights = FlightsScreenOf();

        // The flights list, with its selected flight's panel under it; then the flight's own page.
        EveryAwayLinkHasTheMenu("flights", AwayLinks(chrome.Flights.Tree(flights)), 1);
        chrome.Flights.OpenDetail("TIA1611");
        EveryAwayLinkHasTheMenu("flight", AwayLinks(chrome.Flights.Tree(flights)), 4);

        EveryAwayLinkHasTheMenu("sessions", AwayLinks(chrome.Sessions.Tree(WaitingScreen())), 1);

        var repos = ReposScreenOf();
        chrome.Show(PilotPage.Repos);
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, repos, new RectF32(0, 0, 1280, 800));
        chrome.Repos.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.None)).ShouldBeTrue();
        EveryAwayLinkHasTheMenu("repos", AwayLinks(chrome.Repos.Tree(repos)), 2);

        // Home and Fleet have no link that leaves the app today; the day one gains one, it is held to the same rule.
        var home = new HomeScreen(flights, WaitingScreen(), new JobsScreen([], "test", Now), CardsScreen.Empty);
        EveryAwayLinkHasTheMenu("home", AwayLinks(chrome.Home.Tree(home)), 0);
        EveryAwayLinkHasTheMenu("fleet", AwayLinks(chrome.Fleet.Tree(new FleetScreen([], Live.At))), 0);
    }

    [Fact]
    public void AReposPrLinkOffersCopyUrlForThatPr()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var copied = new List<string>();
        var chrome = NewChrome(renderer);
        chrome.Repos.CopyText = copied.Add;
        var screen = ReposScreenOf();
        chrome.Show(PilotPage.Repos);
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, screen, new RectF32(0, 0, 1280, 800));
        chrome.Repos.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.None)).ShouldBeTrue();
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, screen, new RectF32(0, 0, 1280, 800));

        var pr = RegionsOf(chrome.Repos).First(r => r.Result is HitResult.ButtonHit { Action: "pr:205" });
        pr.OnContextMenu.ShouldNotBeNull().Invoke(PressIn(pr));
        chrome.Repos.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        chrome.Repos.Links.MenuItems[0].OnChoose!();
        copied.ShouldBe(["https://github.com/example-org/orbit/pull/205"]);
    }

    // ---- The keyboard (SharpAstro/pilot#337, link-menu-keyboard) ----

    private static InputEvent.KeyDown Key(InputKey key, InputModifier mods = InputModifier.None) => new(key, mods);

    [Fact]
    public void ShiftF10OnAFocusedCardTitleLinkOpensCopyUrlUnderItAndEnterCopies()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, copied) = Panel(renderer);
        var screen = CardsScreen.From([PrCard()]);
        view.Render(screen, new RectF32(0, 0, 300, 800));

        // Tab puts the keyboard on the first link; nothing is open until Shift+F10.
        view.HandleInput(Key(InputKey.Tab)).ShouldBeTrue();
        view.Links.Focused.ShouldBe("card:c5:title");
        view.Links.MenuItems.ShouldBeEmpty();
        view.Render(screen, new RectF32(0, 0, 300, 800));

        view.HandleInput(Key(InputKey.F10, InputModifier.Shift)).ShouldBeTrue();
        view.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        // The menu is on the page, hung under the link.
        var regions = Painted(view, screen);
        var title = Link(regions, "card:c5:title");
        var item = regions.Single(r => r.Result is HitResult.ListItemHit);
        item.X.ShouldBeGreaterThanOrEqualTo(title.X - 1f);
        item.Y.ShouldBeGreaterThanOrEqualTo(title.Y + title.Height - 1f);

        view.HandleInput(Key(InputKey.Enter)).ShouldBeTrue();
        copied.ShouldBe([PrUrl]);
        view.Links.MenuItems.ShouldBeEmpty();
        view.Links.NoticeShown.ShouldBeTrue();
    }

    [Fact]
    public void EscapeShutsTheKeyboardsMenuWithoutCopying()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, copied) = Panel(renderer);
        var screen = CardsScreen.From([PrCard()]);
        view.Render(screen, new RectF32(0, 0, 300, 800));
        view.HandleInput(Key(InputKey.Tab)).ShouldBeTrue();
        view.Render(screen, new RectF32(0, 0, 300, 800));
        view.HandleInput(Key(InputKey.F10, InputModifier.Shift)).ShouldBeTrue();
        view.Links.MenuItems.ShouldNotBeEmpty();

        view.HandleInput(Key(InputKey.Escape)).ShouldBeTrue();

        view.Links.MenuItems.ShouldBeEmpty();
        copied.ShouldBeEmpty();
        view.Links.NoticeShown.ShouldBeFalse();
    }

    [Fact]
    public void ShiftF10WithNoLinkFocusedAndPlainF10AreLeftAlone()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, _) = Panel(renderer);
        var screen = CardsScreen.From([PrCard()]);
        view.Render(screen, new RectF32(0, 0, 300, 800));

        view.HandleInput(Key(InputKey.F10, InputModifier.Shift)).ShouldBeFalse();
        view.HandleInput(Key(InputKey.Tab)).ShouldBeTrue();
        view.Render(screen, new RectF32(0, 0, 300, 800));
        view.HandleInput(Key(InputKey.F10)).ShouldBeFalse();
        view.Links.MenuItems.ShouldBeEmpty();
    }

    [Fact]
    public void TabAndShiftTabWalkTheCardsLinksAndWrap()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var (view, _) = Panel(renderer);
        var screen = CardsScreen.From([PrCard(), WaitingCard()]);
        string? Step(InputModifier mods = InputModifier.None)
        {
            view.Render(screen, new RectF32(0, 0, 300, 800));
            view.HandleInput(Key(InputKey.Tab, mods)).ShouldBeTrue();
            return view.Links.Focused;
        }

        var first = Step();
        var second = Step();
        second.ShouldNotBe(first);
        Step(InputModifier.Shift).ShouldBe(first);
        // Back from the first wraps to the last.
        Step(InputModifier.Shift).ShouldNotBe(first);
        // The focused link is lit on the page.
        view.Render(screen, new RectF32(0, 0, 300, 800));
    }

    [Fact]
    public void AHostWithNoClipboardTakesNoLinkKeys()
    {
        using var renderer = new RgbaImageRenderer(300, 800);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var screen = CardsScreen.From([PrCard()]);
        view.Render(screen, new RectF32(0, 0, 300, 800));

        view.HandleInput(Key(InputKey.Tab)).ShouldBeFalse();
        view.HandleInput(Key(InputKey.F10, InputModifier.Shift)).ShouldBeFalse();
    }

    [Fact]
    public void TheFlightPagesLinksTakeTheSameKeys()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var copied = new List<string>();
        var chrome = NewChrome(renderer);
        var flights = chrome.Flights;
        flights.CopyText = copied.Add;
        var screen = FlightsScreenOf();
        chrome.Show(PilotPage.Flights);
        flights.OpenDetail("TIA1611");
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));

        flights.HandleInput(Key(InputKey.Tab)).ShouldBeTrue();
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));
        flights.HandleInput(Key(InputKey.F10, InputModifier.Shift)).ShouldBeTrue();
        flights.Links.MenuItems.Select(i => i.Label).ShouldBe([LinkMenu.CopyLabel]);
        flights.HandleInput(Key(InputKey.Enter)).ShouldBeTrue();
        copied.Count.ShouldBe(1);
    }

    [Fact]
    public void CapturesOfAKeyboardFocusedLinkWithItsMenuOpenUnderIt()
    {
        // Written where PILOT_TEST_CAPTURE says, else beside the temp files; never into the plane.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture
            ? Path.GetDirectoryName(capture) ?? "."
            : Path.Combine(Path.GetTempPath(), "pilot-linkmenu-captures");
        Directory.CreateDirectory(dir);
        var longCard = PrCard() with { Title = "DIR112 landed: the whole of a very long title that runs past the panel's width" };
        foreach (var (width, height) in new[] { (300, 360), (480, 360) })
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var (view, _) = Panel(renderer);
            var screen = CardsScreen.From([longCard, WaitingCard()]);
            view.Render(screen, new RectF32(0, 0, width, height));
            view.HandleInput(Key(InputKey.Tab));
            view.Render(screen, new RectF32(0, 0, width, height));
            view.HandleInput(Key(InputKey.F10, InputModifier.Shift));
            view.Render(screen, new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(dir, $"link-menu-keyboard-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }
    }

    [Fact]
    public void CapturesOfTheFlightPageWithTheMenuOpenOverItsPrLink()
    {
        // Written where PILOT_TEST_CAPTURE says, else beside the temp files; never into the plane.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture
            ? Path.GetDirectoryName(capture) ?? "."
            : Path.Combine(Path.GetTempPath(), "pilot-linkmenu-captures");
        Directory.CreateDirectory(dir);
        foreach (var (width, height) in new[] { (640, 520), (1280, 640) })
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var chrome = NewChrome(renderer);
            chrome.Flights.CopyText = _ => { };
            var screen = FlightsScreenOf();
            chrome.Show(PilotPage.Flights);
            chrome.Flights.OpenDetail("TIA1611");
            chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, width, height));
            var pr = RegionsOf(chrome.Flights).Single(r => r.Result is HitResult.ButtonHit { Action: "pr:TIA1611" });
            pr.OnContextMenu!.Invoke(PressIn(pr, 30f, 10f));
            chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, width, height));
            File.WriteAllBytes(Path.Combine(dir, $"link-menu-flight-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
        }
    }

    [Fact]
    public void CapturesOfTheMenuOpenOverACardAndOfTheCopiedNotice()
    {
        // Written where PILOT_TEST_CAPTURE says, else beside the temp files; never into the plane.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is { Length: > 0 } capture
            ? Path.GetDirectoryName(capture) ?? "."
            : Path.Combine(Path.GetTempPath(), "pilot-linkmenu-captures");
        Directory.CreateDirectory(dir);
        var longCard = PrCard() with { Title = "DIR112 landed: the whole of a very long title that runs past the panel's width" };
        foreach (var (width, height) in new[] { (300, 520), (480, 520) })
        {
            foreach (var chosen in new[] { false, true })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (view, _) = Panel(renderer);
                var screen = CardsScreen.From([longCard, WaitingCard()]);
                view.Links.Now = () => Live.At;
                var title = Link(Painted(view, screen), "card:c5:title");
                title.OnContextMenu!.Invoke(new PointerPress(title.X + 30f, title.Y + 10f, MouseButton.Right, InputModifier.None, 1));
                if (chosen)
                {
                    view.Links.MenuItems[0].OnChoose!();
                }
                view.Render(screen, new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"link-menu-{(chosen ? "copied" : "open")}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }
}
