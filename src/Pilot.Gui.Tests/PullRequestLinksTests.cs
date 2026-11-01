using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The links between a PR and what is about it: a card's title, a flight's page, chip and list row open the Pull requests page on that
/// PR's row (selected, in view, its group unfolded, whatever the pills and the search said), a smaller ↗ beside a card's title opens the
/// browser, a row's flight chip opens the flight, the Repos page's open-PRs line lists the repo's PRs, the notification's route is the
/// card's, and Back returns to where each jump came from.
/// </summary>
public sealed class PullRequestLinksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private const float Height = 520f;

    private static PullRequestRow Pr(string repo, int number, PullRequestKind kind, string label, string? flight = null, string? review = null, string author = "sebgod", int minutes = 180) => new()
    {
        Repo = repo, Number = number, Title = $"A change in {repo} ({number})", Url = $"https://github.com/{repo}/pull/{number}", Author = author, Kind = kind, Label = label,
        NeedsMe = kind is PullRequestKind.Ready or PullRequestKind.Conflicts or PullRequestKind.ChangesRequested,
        Flight = flight, Review = review, UpdatedAt = Now - TimeSpan.FromMinutes(minutes), CreatedAt = Now - TimeSpan.FromDays(2),
        Head = flight is null ? "feature/x" : $"flight/{flight.ToLowerInvariant()}", Base = "main",
    };

    // The pressing repos first, and six repos of three drafts after them: more rows than a short page holds, so the last is out of view until a jump brings it.
    private static PullRequestsRow Row()
    {
        var prs = new List<PullRequestRow>
        {
            Pr("SharpAstro/pilot", 252, PullRequestKind.Ready, "Ready", "PIL252", PullRequestReview.Passed, minutes: 5),
            Pr("SharpAstro/pilot", 261, PullRequestKind.ChecksRunning, "Checks running 4/7", "PIL261", PullRequestReview.Reviewing),
            Pr("SharpAstro/tianwen", 1799, PullRequestKind.Conflicts, "Conflicts", "TIA1799", PullRequestReview.Changes, minutes: 6000),
            Pr("example-org/orbit", 205, PullRequestKind.ChecksFailed, "Checks failed: build", author: "ada"),
            Pr("example-org/orbit", 203, PullRequestKind.Draft, "Draft", author: "ada"),
        };
        for (var repo = 1; repo <= 6; repo++)
        {
            for (var n = 1; n <= 3; n++)
            {
                prs.Add(Pr($"sebgod/filler{repo}", n, PullRequestKind.Draft, "Draft", minutes: 600 + repo * 10 + n));
            }
        }
        return new PullRequestsRow { Orgs = ["SharpAstro", "sebgod"], ReadAt = Now - TimeSpan.FromSeconds(40), Viewer = "ada", PullRequests = [.. prs] };
    }

    private static PullRequestsScreen Prs(PullRequestsRow? row = null) => new(row ?? Row(), "live from the node on node.sock", Now);

    private static PullRequestRow LastRow(PullRequestsRow row) => row.Groups().Last().PullRequests.Last();

    private static FlightRow Flight(string code, string repo, int issue, int number) => new()
    {
        Code = code, Repo = repo, RepoPath = $"/r/{repo}", Issue = issue, Title = "Pull requests page", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"/r/{repo}/.claude/worktrees/{code.ToLowerInvariant()}", Base = "origin/main", State = FlightState.Flying, CreatedAt = Now.AddHours(-3),
        PullRequest = number, PullRequestUrl = $"https://github.com/SharpAstro/{repo}/pull/{number}",
    };

    private static FlightsScreen Flights(params FlightRow[] flights) => new(FlightLine.Build(flights, [], Now), "live", Now);

    private static CardRow Card(string id, string kind, string subject, string title, string? link, CardBin bin = CardBin.Ring) => new()
    {
        Id = id, Bin = bin, Kind = kind, Subject = subject, Title = title, Link = link, At = Now, State = CardState.Open,
    };

    private static CardRow ReadyCard(string flight, PullRequestRow pr) =>
        Card("c1", "pr-ready", Verbs.FlightTarget(flight), $"{flight}'s PR #{pr.Number} is ready to merge", pr.Url);

    private static PilotChrome<RgbaImage> Window(RgbaImageRenderer renderer, List<string>? opened = null)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.LinkPullRequests();
        chrome.LinkRepos();
        if (opened is not null)
        {
            chrome.Cards.OnOpen = opened.Add;
            chrome.Flights.OnOpen = opened.Add;
        }
        return chrome;
    }

    private static readonly RectF32 Bounds = new(0, 0, 1280, Height);

    private static void Draw(PilotChrome<RgbaImage> chrome, FlightsScreen? flights = null, CardsScreen? cards = null, PullRequestsScreen? prs = null, ReposScreen? repos = null) =>
        chrome.Render(new SessionsScreen([], "test", Now), flights, cards, null, null, repos, prs ?? Prs(), Bounds);

    private static List<ClickableRegion> Regions(PixelWidgetBase<RgbaImage> widget)
    {
        var regions = new List<ClickableRegion>();
        widget.CollectPaintedRegions(regions);
        return regions;
    }

    private static void Click(PixelWidgetBase<RgbaImage> widget, string action) =>
        Regions(widget).Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

    private static bool Has(PixelWidgetBase<RgbaImage> widget, string action) => Regions(widget).Any(r => r.Result is HitResult.ButtonHit b && b.Action == action);

    // The page's list laid out as the window gives it (the cards panel takes the right when there are cards), with its viewport and its row <pr>'s place.
    private static (Rect<float> Viewport, Rect<float>? Row) ListOf(RgbaImageRenderer renderer, PullRequestsView<RgbaImage> page, PullRequestsScreen screen, PullRequestRow pr, bool withCards = true)
    {
        var width = 1280f - 52f - (withCards ? CardsView<RgbaImage>.Width : 0f);
        var arranged = Layout.Engine.Arrange(page.Tree(screen, width), new Rect<float>(0, 0, width, Height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var viewport = arranged.Single(a => a.Node.Scroll is not null).Bounds;
        var index = page.IndexOf(pr.Url);
        return (viewport, index < 0 ? null : arranged.FirstOrDefault(a => a.Node.Hit is HitResult.ListItemHit h && h.Index == index).Bounds);
    }

    private static bool InView((Rect<float> Viewport, Rect<float>? Row) list) =>
        list.Row is { } row && row.Y >= list.Viewport.Y - 0.5f && row.Y + row.Height <= list.Viewport.Y + list.Viewport.Height + 0.5f;

    // A page that was never laid out waits for its first layout to scroll, and asks the host for the next frame: two draws are what the window does.
    private static void Settle(PilotChrome<RgbaImage> chrome, FlightsScreen? flights = null, CardsScreen? cards = null, PullRequestsScreen? prs = null, ReposScreen? repos = null)
    {
        Draw(chrome, flights, cards, prs, repos);
        Draw(chrome, flights, cards, prs, repos);
    }

    [Fact]
    public void ACardsTitleLinkOpensThePullRequestsPageOnThatPRsRowSelectedInViewAndItsGroupUnfoldedWhateverThePillsAndTheSearchSaid()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var screen = Prs();
        var target = LastRow(screen.Row!);
        // A review card of a PR that is no flight's: subject pr/<owner>/<repo>#<n>, its link the PR.
        var card = Card("c7", "pr-review-ready", Verbs.PrTarget(target.Repo, target.Number), $"{target.Repo}#{target.Number}: review ready, passed", target.Url);
        var cards = CardsScreen.From([card]);

        // The page as the person left it: its group folded, Mine lit, and a search that does not name the repo.
        chrome.Show(PilotPage.PullRequests);
        Draw(chrome, null, cards, screen);
        chrome.PullRequests.Toggle(target.Repo);
        chrome.PullRequests.Filter = PullRequestFilter.Mine;
        chrome.PullRequests.Search.Text = "tianwen";
        Draw(chrome, null, cards, screen);
        chrome.PullRequests.VisiblePullRequests.ShouldNotContain(r => r.Url == target.Url);
        chrome.Show(PilotPage.Home);
        Draw(chrome, null, cards, screen);

        Click(chrome.Cards, "card:c7:title");
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Settle(chrome, null, cards, screen);

        var page = chrome.PullRequests;
        page.SelectedRow.ShouldNotBeNull().Url.ShouldBe(target.Url);
        page.IsFolded(target.Repo).ShouldBeFalse();
        page.Filter.ShouldBeNull();
        page.Search.Text.ShouldBe("");
        InView(ListOf(renderer, page, screen, target)).ShouldBeTrue("the selected row is inside the list's viewport");
    }

    [Fact]
    public void TheTargetRowIsBelowTheFoldUntilTheJumpBringsItIntoView()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var screen = Prs();
        var target = LastRow(screen.Row!);
        chrome.Show(PilotPage.PullRequests);
        Settle(chrome, null, null, screen);
        InView(ListOf(renderer, chrome.PullRequests, screen, target, withCards: false)).ShouldBeFalse("a short window holds twelve rows at most");

        chrome.OpenPullRequest(target.Repo, target.Number);
        Settle(chrome, null, null, screen);
        InView(ListOf(renderer, chrome.PullRequests, screen, target, withCards: false)).ShouldBeTrue();
    }

    [Fact]
    public void APageNeverDrawnAsksForAnotherFrameWhenItJumpsAndHoldsStillOnceItHasScrolled()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var frames = 0;
        chrome.PullRequests.Changed = () => frames++;
        var screen = Prs();
        var target = LastRow(screen.Row!);

        chrome.OpenPullRequest(target.Repo, target.Number);
        Draw(chrome, null, null, screen);
        frames.ShouldBe(1, "the first layout told the list its viewport; the scroll waits for the next frame");
        Draw(chrome, null, null, screen);
        frames.ShouldBe(1, "scrolled: no further frame is asked for");
        Draw(chrome, null, null, screen);
        frames.ShouldBe(1);
    }

    [Fact]
    public void ACardsTitleIsTheLinkInAndASmallerArrowBesideItIsTheWayOutToTheBrowser()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var opened = new List<string>();
        var chrome = Window(renderer, opened);
        var screen = Prs();
        var pr = screen.Row!.PullRequests.Single(p => p.Number == 252);
        var cards = CardsScreen.From([ReadyCard("PIL252", pr)]);
        Draw(chrome, null, cards, screen);

        Click(chrome.Cards, "card:c1:browser");
        opened.ShouldBe([pr.Url]);
        chrome.Page.ShouldBe(PilotPage.Home, "the arrow only opens the browser");

        // Side by side on one row of the card, the arrow the smaller of the two, the title the wider.
        var arranged = Layout.Engine.Arrange(chrome.Cards.Tree(cards), new Rect<float>(0, 0, CardsView<RgbaImage>.Width, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        Rect<float> Of(string action) => arranged.Single(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action).Bounds;
        var title = Of("card:c1:title");
        var away = Of("card:c1:browser");
        away.Y.ShouldBe(title.Y);
        away.Height.ShouldBe(title.Height);
        away.X.ShouldBeGreaterThanOrEqualTo(title.X + title.Width - 0.5f);
        away.Width.ShouldBeLessThan(title.Width);
        arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "card:c1:browser" }).Node.Tooltip.ShouldNotBeNull().ShouldContain("GitHub");
    }

    [Theory]
    [InlineData("pr-ready", true)]
    [InlineData("review-changes", true)]
    [InlineData("queue-dropped", true)]
    [InlineData("merge-dropped", true)]
    [InlineData("pr-conflicts", true)]
    [InlineData("pr-review-ready", true)]
    [InlineData("autopilot-merge-refused", true)]
    [InlineData("landed", false)]
    [InlineData("leg-failed", false)]
    [InlineData("pr-checks-failed", false)]
    public void TheCardsAboutAnOpenPRRouteAndNoOtherDoes(string kind, bool routes)
    {
        var card = Card("c1", kind, Verbs.FlightTarget("PIL252"), "title", "https://github.com/SharpAstro/pilot/pull/252");
        PullRequestLinks.Of(card).ShouldBe(routes ? new PrRef("SharpAstro", "pilot", 252) : null);
    }

    [Fact]
    public void AnAssistantsAskOnAPRCardNamesItAndOneOnAnythingElseDoesNot()
    {
        PullRequestLinks.Of(Card("c2", CardKinds.AssistantAsk, "pr/SharpAstro/tianwen#1820", "se1-watchtower asks to review tianwen#1820", null, CardBin.ForYou))
            .ShouldBe(new PrRef("SharpAstro", "tianwen", 1820));
        PullRequestLinks.Of(Card("c3", CardKinds.AssistantAsk, "flight/PIL252", "asks to merge", null, CardBin.ForYou)).ShouldBeNull();
        PullRequestLinks.Of(Card("c4", CardKinds.SessionWaiting, "session/abc", "waits", "https://claude.ai/code/session_x")).ShouldBeNull();
        // A review's card that has lost its link is still about the PR its subject names.
        PullRequestLinks.Of(Card("c5", "pr-review-left", "pr/SharpAstro/pilot#9", "review worktree is still there", null)).ShouldBe(new PrRef("SharpAstro", "pilot", 9));
    }

    [Fact]
    public void ACardOfAnotherKindKeepsItsTitleAsTheBrowserLinkAndAHostWithNoPageKeepsAPRCardsToo()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var opened = new List<string>();
        var chrome = Window(renderer, opened);
        var landed = Card("c5", "landed", Verbs.FlightTarget("DIR112"), "DIR112 landed", "https://github.com/SharpAstro/DIR.Lib/pull/138", CardBin.ForYou);
        Draw(chrome, null, CardsScreen.From([landed]));
        Has(chrome.Cards, "card:c5:browser").ShouldBeFalse();
        Click(chrome.Cards, "card:c5:title");
        opened.ShouldBe(["https://github.com/SharpAstro/DIR.Lib/pull/138"]);
        chrome.Page.ShouldBe(PilotPage.Home);

        // pilot-tui has no such page to open: its PR card's title is the browser link as before.
        using var cardsOnly = new RgbaImageRenderer(300, 300);
        var cards = new CardsView<RgbaImage>(cardsOnly) { FontPath = SessionsCapture.FontPath };
        cards.OnOpen = opened.Add;
        var screen = CardsScreen.From([ReadyCard("PIL252", Prs().Row!.PullRequests.Single(p => p.Number == 252))]);
        cards.Render(screen, new RectF32(0, 0, 300, 300));
        Has(cards, "card:c1:browser").ShouldBeFalse();
        Click(cards, "card:c1:title");
        opened.Last().ShouldBe("https://github.com/SharpAstro/pilot/pull/252");
    }

    [Fact]
    public void TheNotificationsRouteIsTheCardsOwnAndAnythingElseIsLeftToItsLink()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var screen = Prs();
        var pr = screen.Row!.PullRequests.Single(p => p.Number == 1799);
        var card = Card("c9", "pr-conflicts", Verbs.FlightTarget("TIA1799"), "TIA1799's PR #1799 conflicts with its base", pr.Url);

        // What a click on a notification for the card does: the same as its title.
        Draw(chrome, null, CardsScreen.From([card]), screen);
        chrome.OpenCard(card).ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Settle(chrome, null, CardsScreen.From([card]), screen);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Url.ShouldBe(pr.Url);
        // Its flight's cards light in the panel: one selection source, as the Flights page's.
        chrome.Cards.Focus.ShouldBe("TIA1799");

        chrome.Show(PilotPage.Home);
        chrome.OpenCard(Card("c10", "landed", "flight/DIR112", "landed", null)).ShouldBeFalse();
        chrome.Page.ShouldBe(PilotPage.Home);
    }

    [Fact]
    public void BackFromACardsJumpReturnsToWhereItCameFromAndForwardGoesBack()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var screen = Prs();
        var pr = screen.Row!.PullRequests.Single(p => p.Number == 252);
        var cards = CardsScreen.From([ReadyCard("PIL252", pr)]);
        chrome.Show(PilotPage.Sessions);
        Draw(chrome, null, cards, screen);

        Click(chrome.Cards, "card:c1:title");
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Settle(chrome, null, cards, screen);
        chrome.CanGoBack.ShouldBeTrue();

        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Sessions);
        chrome.Forward().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Url.ShouldBe(pr.Url);
    }

    private static bool Says(Layout.Node tree, RgbaImageRenderer renderer, string text) =>
        Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 1280, Height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void APRTheNodeHasNotListedYetClearsTheOldSelectionSaysSoAndIsSelectedByTheReadThatListsIt()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        // Before the node's first read the row waits; once read, the PR is selected.
        chrome.OpenPullRequest("SharpAstro/pilot", 252);
        Draw(chrome, null, null, new PullRequestsScreen(null, "live", Now));
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Settle(chrome, null, null, Prs());
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Number.ShouldBe(252);
        chrome.Cards.Focus.ShouldBe("PIL252");

        // A PR the read does not list yet (just opened): the old selection is let go, so the panel lights no other flight's cards, and the page says why.
        chrome.OpenPullRequest("SharpAstro/pilot", 9999);
        Settle(chrome, null, null, Prs());
        chrome.PullRequests.SelectedRow.ShouldBeNull();
        chrome.PullRequests.SelectedUrl.ShouldBeNull();
        chrome.Cards.Focus.ShouldBeNull();
        Says(chrome.PullRequests.Tree(Prs()), renderer, "SharpAstro/pilot#9999 is not among the open pull requests the node has read yet").ShouldBeTrue();

        // The next read lists it: it is selected, in view, and the notice is gone.
        var read = Row();
        var later = read with { PullRequests = [.. read.PullRequests, Pr("SharpAstro/pilot", 9999, PullRequestKind.Ready, "Ready", "PIL9999")] };
        var screen = Prs(later);
        Settle(chrome, null, null, screen);
        var selected = chrome.PullRequests.SelectedRow.ShouldNotBeNull();
        selected.Number.ShouldBe(9999);
        chrome.Cards.Focus.ShouldBe("PIL9999");
        InView(ListOf(renderer, chrome.PullRequests, screen, selected, withCards: false)).ShouldBeTrue();
        Says(chrome.PullRequests.Tree(screen), renderer, "has not been read").ShouldBeFalse();
        Says(chrome.PullRequests.Tree(screen), renderer, "not among the open pull requests").ShouldBeFalse();
    }

    [Fact]
    public void AWaitingJumpEndsWithAKeyOrAFewMinutesAndNothingHoldsTheSelectionForever()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        chrome.OpenPullRequest("SharpAstro/pilot", 9999);
        Settle(chrome, null, null, Prs());
        chrome.PullRequests.SelectedRow.ShouldBeNull();

        // A PR closed meanwhile never comes: after the wait the page is as any page, its first row selected.
        var later = new PullRequestsScreen(Row(), "live", Now + TimeSpan.FromMinutes(4));
        Settle(chrome, null, null, later);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull();

        // And a key of the person's own is a new pick.
        chrome.OpenPullRequest("SharpAstro/pilot", 9998);
        Settle(chrome, null, null, Prs());
        chrome.PullRequests.SelectedRow.ShouldBeNull();
        chrome.PullRequests.HandleInput(new InputEvent.KeyDown(InputKey.Down, InputModifier.None)).ShouldBeTrue();
        chrome.PullRequests.SelectedRow.ShouldNotBeNull();
    }

    [Fact]
    public void TheTargetRowIsBelowTheFoldAfterAFrameLaidOutWithItsGroupFoldedAndStillComesIntoView()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var screen = Prs();
        var target = LastRow(screen.Row!);
        chrome.Show(PilotPage.PullRequests);
        // The last frame was laid out with the target's group folded: a list as short as that, so the scroll's limit is the short list's.
        chrome.PullRequests.Toggle(target.Repo);
        Settle(chrome, null, null, screen);

        chrome.OpenPullRequest(target.Repo, target.Number);
        Settle(chrome, null, null, screen);
        chrome.PullRequests.IsFolded(target.Repo).ShouldBeFalse();
        InView(ListOf(renderer, chrome.PullRequests, screen, target, withCards: false)).ShouldBeTrue();
    }

    [Fact]
    public void AFlightThatLandedKeepsItsPRLinkAsTheBrowsersAndACardAboutAClosedPRKeepsItsTitleThere()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var opened = new List<string>();
        var chrome = Window(renderer, opened);
        var screen = Prs();
        // DIR9's PR is merged: the node lists no row for it, so there is no page row to open and GitHub is the link, not left behind a small arrow.
        var merged = Flight("DIR9", "DIR.Lib", 9, 9) with { State = FlightState.Landed };
        var flights = Flights(Flight("PIL252", "pilot", 252, 252), merged);
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenDetail("DIR9");
        Draw(chrome, flights, null, screen);

        Has(chrome.Flights, "pr:DIR9:browser").ShouldBeFalse();
        Has(chrome.Flights, "flight:DIR9:pr-chip").ShouldBeFalse();
        Click(chrome.Flights, "pr:DIR9");
        opened.ShouldBe(["https://github.com/SharpAstro/DIR.Lib/pull/9"]);
        chrome.Page.ShouldBe(PilotPage.Flights);

        // The same for a card whose PR is no longer listed (queue-dropped, then merged by hand).
        chrome.Show(PilotPage.Home);
        var dropped = Card("c8", "merge-dropped", Verbs.FlightTarget("DIR9"), "DIR9's merge was dropped", "https://github.com/SharpAstro/DIR.Lib/pull/9");
        Draw(chrome, flights, CardsScreen.From([dropped]), screen);
        Has(chrome.Cards, "card:c8:browser").ShouldBeFalse();
        Click(chrome.Cards, "card:c8:title");
        opened.Last().ShouldBe("https://github.com/SharpAstro/DIR.Lib/pull/9");
        chrome.Page.ShouldBe(PilotPage.Home);
        chrome.OpenCard(dropped).ShouldBeFalse("the notification's click leaves it to the card's own link");
    }

    [Fact]
    public void AFlightsPageLinksItsPRToItsRowSaysItsStateInThePageChipAndHasTheBrowserBesideIt()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var opened = new List<string>();
        var chrome = Window(renderer, opened);
        var screen = Prs();
        var flights = Flights(Flight("PIL252", "pilot", 252, 252), Flight("TIA1799", "tianwen", 1799, 1799));
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenDetail("PIL252");
        Draw(chrome, flights, null, screen);

        // The chip is the Pull requests page's: its label, its colours.
        var chip = Regions(chrome.Flights).Single(r => r.Result is HitResult.ButtonHit { Action: "flight:PIL252:pr-chip" });
        var arranged = Layout.Engine.Arrange(chrome.Flights.Tree(flights), new Rect<float>(0, 0, 1280, Height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var chipNode = arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "flight:PIL252:pr-chip" }).Node;
        var (bg, fg) = PullRequestsView<RgbaImage>.ChipColors(Palette.Standard, PullRequestKind.Ready);
        chipNode.Background.ShouldBe(bg);
        (chipNode as Layout.Node.Leaf)?.Content.ShouldBeOfType<Layout.Content.Text>().Value.ShouldBe("Ready");
        fg.ShouldBe(Palette.Standard.Bright);

        Click(chrome.Flights, "pr:PIL252:browser");
        opened.ShouldBe(["https://github.com/SharpAstro/pilot/pull/252"]);
        chrome.Page.ShouldBe(PilotPage.Flights);

        Click(chrome.Flights, "pr:PIL252");
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Settle(chrome, flights, null, screen);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Url.ShouldBe("https://github.com/SharpAstro/pilot/pull/252");
        chrome.PullRequests.FocusFlight.ShouldBe("PIL252");

        // Back is the flight's page it came from.
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.OpenCode.ShouldBe("PIL252");
    }

    [Theory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("List")]
    [InlineData("Night")]
    public void EachViewOfTheFlightsPageShowsAFlightsPRStateInTheChipAndAClickOpensItsRow(string view)
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var screen = Prs();
        // The second flight's PR is not on the node's list (merged, say): it has no chip, and nothing else changes.
        var flights = Flights(Flight("PIL252", "pilot", 252, 252), Flight("TIA1799", "tianwen", 1799, 1799), Flight("DIR9", "DIR.Lib", 9, 9));
        chrome.Show(PilotPage.Flights);
        chrome.Flights.Mode = view == "Night" ? FlightsViewMode.Canvas : Enum.Parse<FlightsViewMode>(view);
        chrome.Night.On = view == "Night";
        Draw(chrome, flights, null, screen);

        Has(chrome.Flights, "flight:PIL252:pr-chip").ShouldBeTrue();
        Has(chrome.Flights, "flight:TIA1799:pr-chip").ShouldBeTrue();
        Has(chrome.Flights, "flight:DIR9:pr-chip").ShouldBeFalse();
        if (view == "List")
        {
            // The chip is inside its row's cell, above the row's own bottom padding (6), level with the other cells' text: it never runs out under the row.
            var arranged = Layout.Engine.Arrange(chrome.Flights.Tree(flights), new Rect<float>(0, 0, 1280, Height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
            var rows = arranged.Where(a => a.Node.Hit is HitResult.ListItemHit).Select(a => a.Bounds).ToList();
            var chips = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var act } && act.EndsWith(":pr-chip", StringComparison.Ordinal)).Select(a => a.Bounds).ToList();
            chips.Count.ShouldBe(2);
            foreach (var chip in chips)
            {
                var row = rows.Single(r => chip.Y >= r.Y && chip.Y < r.Y + r.Height);
                chip.Y.ShouldBeGreaterThanOrEqualTo(row.Y + 6f - 0.5f);
                (chip.Y + chip.Height).ShouldBeLessThanOrEqualTo(row.Y + row.Height - 6f + 0.5f);
            }
        }
        Click(chrome.Flights, "flight:TIA1799:pr-chip");
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Settle(chrome, flights, null, screen);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Number.ShouldBe(1799);
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Flights);
    }

    [Fact]
    public void ACanvasDestinationHoldsItsChipClickOnTheMapAndAFlightWithoutAListedPRHasNone()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var screen = Prs();
        var flights = Flights(Flight("PIL252", "pilot", 252, 252), Flight("DIR9", "DIR.Lib", 9, 9));
        chrome.Show(PilotPage.Flights);
        Draw(chrome, flights, null, screen);

        // The chip's click is the map's destination for that flight, inside the window; the flight with no listed PR has none.
        var chip = Regions(chrome.Flights).Single(r => r.Result is HitResult.ButtonHit { Action: "flight:PIL252:pr-chip" });
        chip.Width.ShouldBeGreaterThan(0f);
        (chip.X + chip.Width).ShouldBeLessThanOrEqualTo(1280f);
        Has(chrome.Flights, "flight:DIR9:pr-chip").ShouldBeFalse();
    }

    [Fact]
    public void APRRowsFlightChipOpensTheFlightsPageAndBackReturnsToTheRow()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var screen = Prs();
        var flights = Flights(Flight("PIL252", "pilot", 252, 252));
        chrome.Show(PilotPage.PullRequests);
        Draw(chrome, flights, null, screen);
        var pr = screen.Row!.PullRequests.Single(p => p.Number == 252);
        // Revealed, so the list scrolls to it: SharpAstro's repos come after sebgod's filler ones by name.
        chrome.PullRequests.Reveal(pr.Repo, pr.Number);
        Draw(chrome, flights, null, screen);
        Draw(chrome, flights, null, screen);

        Click(chrome.PullRequests, $"pr:{pr.Url}:flight");
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.OpenCode.ShouldBe("PIL252");

        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Url.ShouldBe(pr.Url);
    }

    [Fact]
    public void TheReposPagesOpenPRsLineListsThatReposPRsOnThePullRequestsPageAndBackReturns()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var repos = new ReposScreen(RepoLine.Build(SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath), [], [], [], [], Now), "live", Now);
        var orbit = repos.Lines.Single(l => l.Name == "orbit");
        orbit.Status.GitHub.ShouldBe("example-org/orbit");
        var screen = Prs();
        chrome.Show(PilotPage.Repos);
        Draw(chrome, null, null, screen, repos);

        // "2 open PRs" says the count the line used to carry as "+N more", as a link.
        var link = Regions(chrome.Repos).Single(r => r.Result is HitResult.ButtonHit b && b.Action == $"repo:{orbit.Key}:prs");
        link.OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Draw(chrome, null, null, screen, repos);
        var page = chrome.PullRequests;
        page.Search.Text.ShouldBe("example-org/orbit");
        page.Filter.ShouldBeNull();
        page.VisiblePullRequests.Select(r => r.Repo).Distinct().ShouldBe(["example-org/orbit"]);
        page.VisiblePullRequests.Count().ShouldBe(2);

        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Repos);
    }

    [Fact]
    public void ACaptureRendersACardWithBothLinksAndAFlightsPRChipAtBothWidthsInBothPalettes()
    {
        // PILOT_TEST_CAPTURE_DIR keeps the frames as PNGs, for looking at; without it the frames are only painted.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        var screen = Prs();
        var longTitle = "PIL252's PR #252 is ready to merge: Pull requests page, every open PR across the orgs, grouped by repo";
        var cards = CardsScreen.From(
        [
            Card("c1", "pr-ready", Verbs.FlightTarget("PIL252"), longTitle, "https://github.com/SharpAstro/pilot/pull/252"),
            Card("c2", "pr-review-ready", "pr/example-org/orbit#205", "example-org/orbit#205: review ready", "https://github.com/example-org/orbit/pull/205", CardBin.ForYou),
            Card("c3", "pr-conflicts", Verbs.FlightTarget("TIA1799"), "TIA1799's PR #1799 conflicts", "https://github.com/SharpAstro/tianwen/pull/1799"),
        ]);
        var flights = Flights(
            Flight("PIL252", "pilot", 252, 252), Flight("PIL261", "pilot", 261, 261), Flight("TIA1799", "tianwen", 1799, 1799), Flight("DIR9", "DIR.Lib", 9, 9));
        foreach (var (width, height) in new[] { (1280, 800), (2000, 1000) })
        {
            foreach (var view in new[] { "canvas", "night", "list", "page", "jumped" })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var chrome = Window(renderer);
                chrome.Show(PilotPage.Flights);
                chrome.Flights.Mode = view == "list" ? FlightsViewMode.List : FlightsViewMode.Canvas;
                chrome.Night.On = view == "night";
                var bounds = new RectF32(0, 0, width, height);
                if (view == "page")
                {
                    chrome.Flights.OpenDetail("PIL252");
                }
                chrome.Render(new SessionsScreen([], "live", Now), flights, cards, null, null, null, screen, bounds);
                if (view == "jumped")
                {
                    // A card's title clicked: the Pull requests page on that PR's row, the card lit beside it.
                    Click(chrome.Cards, "card:c3:title");
                }
                chrome.Render(new SessionsScreen([], "live", Now), flights, cards, null, null, null, screen, bounds);
                chrome.Render(new SessionsScreen([], "live", Now), flights, cards, null, null, null, screen, bounds);
                if (dir is { Length: > 0 })
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"pr-links-{width}-{view}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
                }
            }
        }
    }

    [Fact]
    public void ARepoWithNoOpenPRsHasNoLinkAndOneWithManyKeepsItsRowTheSameHeight()
    {
        using var renderer = new RgbaImageRenderer(1280, (uint)Height);
        var chrome = Window(renderer);
        var repos = new ReposScreen(RepoLine.Build(SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath), [], [], [], [], Now), "live", Now);
        chrome.Show(PilotPage.Repos);
        Draw(chrome, null, null, null, repos);

        var buttons = Regions(chrome.Repos).Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).Where(a => a.EndsWith(":prs", StringComparison.Ordinal)).ToList();
        var withPrs = repos.Lines.Where(l => l.Pulls.Count > 0 && l.Status.GitHub is not null).Select(l => $"repo:{l.Key}:prs");
        buttons.Order().ShouldBe(withPrs.Order());
        repos.Lines.Where(l => l.Pulls.Count == 0).ShouldAllBe(l => !buttons.Contains($"repo:{l.Key}:prs"));
    }
}
