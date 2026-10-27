using DIR.Lib;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The leg a card is about is a link in the cards panel: it opens the flight's page with that leg's block in view.</summary>
public sealed class CardLegLinkTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static CardRow Card(string id, string title, string? summary, string subject = "flight/TIA1611/leg9") => new()
    {
        Id = id, Bin = CardBin.Ring, Kind = "leg-failed", Subject = subject, Title = title, Summary = summary,
        At = Now, State = CardState.Open,
    };

    private static List<ClickableRegion> Regions(CardsView<RgbaImage> view, CardsScreen screen)
    {
        view.Render(screen, new RectF32(0, 0, 400, 1200));
        var regions = new List<ClickableRegion>();
        view.CollectPaintedRegions(regions);
        return regions;
    }

    [Theory]
    [InlineData("flight/TIA1611/parser", null, "TIA1611", "parser")]
    [InlineData("flight/TIA1611", null, null, null)]
    [InlineData("session/a1b2c3d4", null, null, null)]
    [InlineData("flight/TIA1611", "flight/TIA1611/render", "TIA1611", "render")]
    [InlineData("flight/TIA1611/", null, null, null)]
    public void TheLegComesFromTheSubjectOrTheGoAroundChoicesTarget(string subject, string? goAroundTarget, string? code, string? leg)
    {
        var card = Card("c1", "t", null, subject) with
        {
            Choices = goAroundTarget is null ? [] : [new CardChoice { Label = Verbs.RedoLeg("render"), Verb = Verbs.GoAround, Target = goAroundTarget }],
        };

        CardLeg.Of(card).ShouldBe(code is null ? null : new CardLeg(code, leg!));
        CardLeg.Of(card)?.ToString().ShouldBe(code is null ? null : $"{code}/{leg}");
    }

    [Theory]
    [InlineData("TIA1611/leg9 failed its check", null)]
    [InlineData("A leg failed", "Redo leg9 or dismiss it.")]
    [InlineData("A leg failed", "Nothing here names it.")]
    public void ClickingTheLegRaisesOnLegWithTheFlightAndTheLeg(string title, string? summary)
    {
        using var renderer = new RgbaImageRenderer(400, 1200);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        (string, string)? opened = null;
        view.OnLeg = (code, leg) => opened = (code, leg);

        var regions = Regions(view, CardsScreen.From([Card("c1", title, summary)]));
        regions.Where(r => r.Result is HitResult.ButtonHit { Action: "card:c1:leg" }).ShouldHaveSingleItem().OnClick!.Invoke(InputModifier.None);

        opened.ShouldBe(("TIA1611", "leg9"));
    }

    [Fact]
    public void ACardAboutNoLegHasNoLegLink()
    {
        using var renderer = new RgbaImageRenderer(400, 1200);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };

        Regions(view, CardsScreen.From([Card("c1", "TIA1611 is ready", "Merge it.", "flight/TIA1611")]))
            .Any(r => r.Result is HitResult.ButtonHit { Action: "card:c1:leg" }).ShouldBeFalse();
    }

    [Fact]
    public void CapturesOfAReviewChangesALegFailedAndAMergeFailedCardAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        var redo = new CardChoice { Label = Verbs.RedoLeg("parser"), Verb = Verbs.GoAround, Target = Verbs.LegTarget("PIL355", "parser") };
        var cases = new (string Name, CardRow Card)[]
        {
            ("review-changes", new CardRow
            {
                Id = "c1", Bin = CardBin.Ring, Kind = "review-changes", Subject = "flight/PIL355", Title = "PIL355's PR #360 needs changes",
                Summary = "The review asked for changes on the parser leg. Redo leg parser to fix them, or dismiss the card.",
                Detail = "Review of 3b2b6d0: the parser drops a trailing newline; add a test for it.",
                Choices = [redo],
                Link = "https://github.com/SharpAstro/pilot/pull/360", At = Live.At, State = CardState.Open,
            }),
            ("leg-failed", new CardRow
            {
                Id = "c2", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/PIL355/parser", Title = "PIL355/parser failed",
                Summary = "The worker for leg parser gave up before its check passed. Redo leg parser to fly it again.",
                Detail = "the last worker gave up: dotnet test failed with 3 errors in ParserTests",
                Choices = [redo], At = Live.At, State = CardState.Open,
            }),
            ("merge-failed", new CardRow
            {
                Id = "c3", Bin = CardBin.Ring, Kind = "merge-failed", Subject = "flight/PIL355", Title = "PIL355's PR #360: check build-and-test failed",
                Summary = "The merge waited for the checks and one failed. Redo leg parser to fix it, re-run the checks, or merge again.",
                Detail = "The merge waited for the checks to pass, and build-and-test failed on 3b2b6d0. Auto-merge is off.",
                Choices = [redo, new CardChoice { Label = "Merge again", Verb = Verbs.Merge, Target = "flight/PIL355", Args = ["3b2b6d0"] }],
                Link = "https://github.com/SharpAstro/pilot/pull/360", At = Live.At, State = CardState.Open,
            }),
        };
        foreach (var (width, height) in new[] { (900, 700), (1800, 700) })
        {
            foreach (var (name, card) in cases)
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([card]), new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"card-leg-{name}-{width}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }

    [Fact]
    public void TheLegLinkOpensTheFlightsPageWithAFarDownLegInView()
    {
        var flight = new FlightRow
        {
            Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets", Branch = "flight/tia1611",
            Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        };
        var legs = Enumerable.Range(1, 12).Select(i => new LegRow
        {
            Flight = "TIA1611", Name = $"leg{i}", Order = i, Goal = $"Goal of leg {i}.", DoneWhen = $"Leg {i} is done.", State = i < 9 ? LegState.Done : LegState.Flying,
            Checklist = [new CheckItem { Text = "one" }, new CheckItem { Text = "two" }],
        }).ToArray();
        var line = FlightLine.Build([flight], [], Now, legs).Single();
        var screen = new FlightsScreen([line], "test", Now);

        using var renderer = new RgbaImageRenderer(900, 700);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var cards = CardsScreen.From([Card("c1", "TIA1611/leg9 failed its check", "Redo leg9 or dismiss it.")]);
        chrome.LinkCardTranscripts(() => new SessionsScreen([], "test", Now), () => screen);
        chrome.Show(PilotPage.Home);
        chrome.Render(new SessionsScreen([], "test", Now), screen, cards, new RectF32(0, 0, 900, 700));

        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "card:c1:leg" }).OnClick!.Invoke(InputModifier.None);
        chrome.Render(new SessionsScreen([], "test", Now), screen, cards, new RectF32(0, 0, 900, 700));

        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.DetailOffset.ShouldBeGreaterThan(0);
        chrome.Flights.LegBlockInView("leg9").ShouldBeTrue();
        chrome.Flights.LegBlockInView("leg1").ShouldBeFalse();
    }

    [Fact]
    public void ALegLinkThatNeverResolvedDoesNotScrollTheNextFlightsPage()
    {
        LegRow[] Legs(string code) => [.. Enumerable.Range(1, 12).Select(i => new LegRow
        {
            Flight = code, Name = $"leg{i}", Order = i, Goal = $"Goal of leg {i}.", DoneWhen = $"Leg {i} is done.", State = LegState.Done,
            Checklist = [new CheckItem { Text = "one" }, new CheckItem { Text = "two" }],
        })];
        FlightRow Row(string code) => new()
        {
            Code = code, Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets", Branch = $"flight/{code.ToLowerInvariant()}",
            Worktree = $"/r/tianwen/.claude/worktrees/{code.ToLowerInvariant()}", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
        };
        var lines = FlightLine.Build([Row("TIA1"), Row("TIA2")], [], Now, [.. Legs("TIA1"), .. Legs("TIA2")]);
        var screen = new FlightsScreen(lines, "test", Now);
        using var renderer = new RgbaImageRenderer(900, 700);
        var view = new FlightsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };

        // The link names a leg TIA1's page does not have, and the page is shut before it resolves.
        view.OpenDetail("TIA1", "nope");
        view.Render(screen, new RectF32(0, 0, 900, 700));
        view.CloseDetail();
        view.OpenDetail("TIA2");
        view.Render(screen, new RectF32(0, 0, 900, 700));
        view.DetailOffset.ShouldBe(0);

        // A leg link left pending and a plain open of another page does the same.
        view.CloseDetail();
        view.OpenDetail("TIA1", "leg9");
        view.OpenDetail("TIA2");
        view.Render(screen, new RectF32(0, 0, 900, 700));
        view.DetailOffset.ShouldBe(0);
    }
}
