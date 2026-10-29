using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>An idea's id in a card, a Handled line or the Ideas pane leads to the idea, or to its flight once planned; a flight made from an idea says so.</summary>
public sealed class IdeaLinksTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);

    private static IdeaRow Idea(int number, string repo = "pilot", IdeaState state = IdeaState.Noted) => new()
    {
        Id = IdeaIds.Id(number), Repo = repo, Title = $"idea {number}", State = state, UpdatedAt = At,
        Url = $"https://github.com/SharpAstro/{repo}/issues/{number}",
    };

    private static FlightRow Flight(string code, string repo, int issue, string? fromIdea) => new()
    {
        Code = code, Repo = repo, RepoPath = "/r/" + repo, Issue = issue, Branch = "flight/" + code.ToLowerInvariant(), Worktree = "/w/" + code,
        Base = "origin/main", State = FlightState.Flying, CreatedAt = At, FromIdea = fromIdea,
    };

    [Fact]
    public void AnIdIsALinkToItsIdeaAndOnceItIsPlannedToItsFlightEvenThroughAMovesOldId()
    {
        IdeaRow[] ideas = [Idea(381), Idea(1829, "tianwen")];
        FlightRow[] flights = [Flight("PIL384", "pilot", 384, "I384"), Flight("TIA9", "tianwen", 9, "I9")];
        IdeaMoveRow[] moves = [new("pilot", "I5", "tianwen", "I9")];

        var links = IdeaLinks.In("Plan I381 now, then tianwen I1829, I384 and the old I5; I999 is none, PR #381 and issue381 no id", ideas, flights, moves);

        links.Select(l => (l.Text, l.FlightCode, l.Url)).ShouldBe(
        [
            ("I381", null, "https://github.com/SharpAstro/pilot/issues/381"),
            ("tianwen I1829", null, "https://github.com/SharpAstro/tianwen/issues/1829"),
            ("I384", "PIL384", null),
            ("I5", "TIA9", null),
        ]);
    }

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

    [Fact]
    public void ACardAndAHandledLineLinkTheIdsTheyName()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = NewChrome(renderer);
        chrome.Cards.CanTell = true;
        // In play folds the Handled list to one line until it is opened.
        chrome.Cards.HandledOpen = true;
        var ideas = new IdeasScreen([Idea(381)], [], ["pilot"], "test", At) { Flights = [Flight("PIL384", "pilot", 384, "I384")] };
        var open = new CardRow { Id = "c1", Bin = CardBin.ForYou, Kind = "note", Subject = "node", Title = "Plan I381: idea 381?", At = At, State = CardState.Open };
        var handled = new CardRow { Id = "c2", Bin = CardBin.Handled, Kind = "note", Subject = "node", Title = "Dropped I384", Detail = "done by #377", At = At, State = CardState.Closed };
        var followed = new List<IdeaLink>();
        chrome.Cards.OnIdeaLink = followed.Add;

        chrome.Render(new SessionsScreen([], "test", At), null, CardsScreen.From([open, handled]), null, null, null, new RectF32(0, 0, 1280, 900), null, ideas);
        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);

        var actions = regions.Select(r => (r.Result as HitResult.ButtonHit)?.Action).OfType<string>().ToList();
        actions.ShouldContain("card:c1:idea:I381");
        actions.ShouldContain("handled:c2:idea:I384");
        regions.First(r => r.Result is HitResult.ButtonHit { Action: "handled:c2:idea:I384" }).OnClick!.Invoke(InputModifier.None);
        followed.ShouldHaveSingleItem().FlightCode.ShouldBe("PIL384");
    }

    [Fact]
    public void ThePlannedRowOfThePaneLinksToTheFlightAndTheFlightsPageSaysFromTheIdea()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var chrome = NewChrome(renderer);
        chrome.Show(PilotPage.Ideas);
        chrome.Ideas.ShowFolded = true;
        var opened = new List<string>();
        chrome.Ideas.OnFlight = opened.Add;
        var flight = Flight("PIL370", "pilot", 370, "I370");
        var screen = new IdeasScreen([Idea(370, state: IdeaState.Planned)], [], ["pilot"], "test", At) { Flights = [flight] };

        chrome.Render(new SessionsScreen([], "test", At), null, null, null, null, null, new RectF32(0, 0, 1280, 900), null, screen);
        var regions = new List<ClickableRegion>();
        chrome.Ideas.CollectPaintedRegions(regions);
        regions.First(r => r.Result is HitResult.ButtonHit { Action: "ideas:flight:pilot I370" }).OnClick!.Invoke(InputModifier.None);

        opened.ShouldBe(["PIL370"]);
    }

    [Fact]
    public void AFlightMadeFromAnIdeaSaysFromTheIdeaOnItsPage()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        var screen = new FlightsScreen(FlightLine.Build([Flight("PIL370", "pilot", 370, "I370")], [], At, []), "test", At);
        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", At), screen, new RectF32(0, 0, 1280, 800));
        chrome.Flights.HandleInput(new InputEvent.KeyDown(InputKey.Enter, InputModifier.None)).ShouldBeTrue();

        var arranged = DIR.Lib.Layout.Engine.Arrange(chrome.Flights.Tree(screen), new Rect<float>(0, 0, 1280, 3000),
            new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var spoken = arranged.Where(a => a.Node is DIR.Lib.Layout.Node.Leaf { Content: DIR.Lib.Layout.Content.Text })
            .Select(a => ((DIR.Lib.Layout.Content.Text)((DIR.Lib.Layout.Node.Leaf)a.Node).Content).Value).ToList();

        spoken.ShouldContain(t => t.Contains("from I370", StringComparison.Ordinal));
    }
}
