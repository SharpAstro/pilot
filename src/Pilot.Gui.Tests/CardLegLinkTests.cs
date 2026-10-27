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
    }
}
