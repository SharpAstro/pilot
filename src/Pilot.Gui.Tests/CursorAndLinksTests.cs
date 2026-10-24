using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Buttons and links read as clickable: the hand cursor on each, the arrow on a list row, and ↗ after every link that opens elsewhere.</summary>
public sealed class CursorAndLinksTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        PullRequest = 7, PullRequestUrl = "https://github.com/x/tianwen/pull/7",
    };

    private static LegRow[] Legs() =>
    [
        new()
        {
            Flight = "TIA1611", Name = "render", Order = 1, Goal = "Draw the planets.", DoneWhen = "The capture shows all eight.", State = LegState.Flying,
            Artifacts = [new Artifact { Title = "The planets", Location = @"C:\caps\planets.png", At = Live.At.AddMinutes(5) }, new Artifact { Title = "Preview", Location = "https://example.org/preview", At = Live.At.AddMinutes(20) }],
        },
    ];

    private static SessionRow Tower() => new()
    {
        Id = "bbbb0002", SessionId = "bbbb0002-0000-4000-8000-000000000000", Name = "se1-watchtower", Kind = SessionKind.Background, Cwd = "/w",
        Role = SessionRole.Watchtower, State = SessionState.Idle, ContextTokens = 50_000, ContextWindow = 200_000, SpendWeighted = 0, StartedAt = Live.At, RemoteControlUrl = "https://claude.ai/code/session_x",
    };

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

    // A table's column dividers are buttons too, which the pointer marks as a resize and not as a link.
    private static bool IsLink(ClickableRegion r) => r.Result is HitResult.ButtonHit b && !b.Action.Contains(":table:divider:", StringComparison.Ordinal);

    private static void ShouldPointAtButtonsOnly(List<ClickableRegion> regions)
    {
        regions.ShouldContain(r => IsLink(r));
        regions.Where(IsLink).ShouldAllBe(r => r.Cursor == CursorKind.Pointer);
        regions.Where(r => r.Result is HitResult.ListItemHit).ShouldAllBe(r => r.Cursor == null);
    }

    private static List<(string Action, string Text)> Buttons(Layout.Node tree, int columns) =>
    [
        .. Layout.Engine.Arrange(tree, new Rect<int>(0, 0, columns, 60), CellMeasureContext.PixelAuthored)
            .Where(a => a.Node.Hit is HitResult.ButtonHit)
            .Select(a => (((HitResult.ButtonHit)a.Node.Hit!).Action, a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } ? t.Value : "")),
    ];

    private static string TextOf(List<(string Action, string Text)> buttons, string action) => buttons.First(b => b.Action == action).Text;

    [Fact]
    public void TheFlightPageAndItsPanelPointAtEveryButtonAndMarkTheirOutsideLinks()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        var flights = chrome.Flights;
        var screen = new FlightsScreen([FlightLine.Build([Flight()], [], Now, Legs()).Single()], "test", Now);
        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));

        // The selected flight's panel, under the list.
        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        ShouldPointAtButtonsOnly(regions);
        regions.ShouldContain(r => r.Result is HitResult.ListItemHit);
        TextOf(Buttons(flights.Tree(screen), 200), "pr:TIA1611").ShouldEndWith(Followable.Away);

        flights.HandleInput(new InputEvent.KeyDown(InputKey.Enter, InputModifier.None)).ShouldBeTrue();
        chrome.Render(new SessionsScreen([], "test", Now), screen, new RectF32(0, 0, 1280, 800));
        regions.Clear();
        flights.CollectPaintedRegions(regions);
        ShouldPointAtButtonsOnly(regions);

        // The flight's own page.
        var page = Buttons(flights.Tree(screen), 200);
        TextOf(page, "pr:TIA1611").ShouldEndWith(Followable.Away);
        TextOf(page, "artifact:TIA1611:render:The planets").ShouldEndWith(Followable.Away);
        TextOf(page, "artifact:TIA1611:render:Preview").ShouldEndWith(Followable.Away);
        TextOf(page, "artifact-folder:TIA1611:render:The planets").ShouldEndWith(Followable.Away);
        // An action of the app is not a link out.
        TextOf(page, "flight:back").ShouldNotContain(Followable.Away);
    }

    [Fact]
    public void TheWatchtowerPanelPointsAtItsButtonsAndMarksRemoteControlAndCardTitles()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        chrome.Cards.CanTell = true;
        var card = new CardRow
        {
            Id = "c5", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/DIR112", Title = "DIR112 landed", At = Live.At, State = CardState.Open,
            Link = "https://github.com/SharpAstro/DIR.Lib/pull/138",
        };
        var screen = CardsScreen.From([card], [Tower()]);

        chrome.Render(new SessionsScreen([], "test", Live.At), null, screen, new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        ShouldPointAtButtonsOnly(regions);
        var buttons = Buttons(chrome.Cards.Tree(screen), 38);
        TextOf(buttons, "watchtower:rc").ShouldEndWith(Followable.Away);
        TextOf(buttons, "card:c5:title").ShouldBe("🛬 DIR112 landed" + Followable.Away);
    }

    [Fact]
    public void TheReposPagePointsAtItsButtonsAndMarksItsPullRequests()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        var repos = chrome.Repos;
        var screen = new ReposScreen(RepoLine.Build(SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath), [], [], [], [], Live.At), "live", Live.At);
        chrome.Show(PilotPage.Repos);
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, screen, new RectF32(0, 0, 1280, 800));
        repos.HandleInput(new InputEvent.KeyDown(InputKey.Right, InputModifier.None)).ShouldBeTrue();
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, screen, new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        repos.CollectPaintedRegions(regions);
        ShouldPointAtButtonsOnly(regions);
        regions.ShouldContain(r => r.Result is HitResult.ListItemHit);
        var buttons = Buttons(repos.Tree(screen), 200);
        TextOf(buttons, "pr:205").ShouldEndWith(Followable.Away);
        buttons.Where(b => b.Action.Contains(":pr:", StringComparison.Ordinal)).ShouldAllBe(b => b.Text.EndsWith(Followable.Away, StringComparison.Ordinal));
    }

    [Fact]
    public void TheHelpCardSaysWhatTheArrowMeans()
    {
        var about = new AboutCard { Facts = () => [("node", "test")] };
        about.Toggle();

        var text = CellLayout.Describe(Layout.Engine.Arrange(about.Over(Layout.Builder.Spacer()), new Rect<int>(0, 0, 160, 50), CellMeasureContext.PixelAuthored));

        text.ShouldContain(Followable.Away.Trim());
        text.ShouldContain("opens elsewhere");
    }
}
