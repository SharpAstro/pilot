using DIR.Lib;
using Layout = DIR.Lib.Layout;
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
    public void ACardWhoseSentenceNamesTheLegBareStillShowsItsFlightsCodeSlashLeg()
    {
        var card = new CardRow
        {
            Id = "c3", Bin = CardBin.Ring, Kind = "merge-failed", Subject = "flight/PIL355", Title = "PIL355's PR #360: check build-and-test failed",
            Summary = "The merge of PIL355 did not go through. Redo leg parser sends a fresh worker to fix it; Merge again tries once more as it is.",
            Choices = [new CardChoice { Label = Verbs.RedoLeg("parser"), Verb = Verbs.GoAround, Target = Verbs.LegTarget("PIL355", "parser") }],
            At = Now, State = CardState.Open,
        };
        using var renderer = new RgbaImageRenderer(400, 1200);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };

        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, 400, 1200), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        var texts = arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value).ToArray();

        texts.ShouldContain(t => t.Contains("PIL355/parser"));
    }

    private static string[] PaintedWords(CardRow card)
    {
        using var renderer = new RgbaImageRenderer(300, 1200);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var arranged = Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, 300, 1200), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(x => x.Value)];
    }

    [Fact]
    public void ALegNamedLikeAnOrdinaryWordLeavesThatWordPlainAndLinksOnlyItsName()
    {
        var card = new CardRow
        {
            Id = "c1", Bin = CardBin.Ring, Kind = "review-changes", Subject = "flight/PIL1/review", Title = "PIL1's review asks for changes",
            Summary = "The review of PIL1/review asked for changes. Redo leg review sends a fresh worker to make them.",
            At = Now, State = CardState.Open,
        };

        var words = PaintedWords(card).Select(w => w.Trim()).ToArray();

        words.ShouldContain("The");
        words.ShouldContain("review");
        words.Count(w => w.Contains("PIL1/review")).ShouldBe(1);
    }

    [Theory]
    [InlineData("pr-checks-failed", "The PR of PIL355 fails its checks. The node could not send the last leg back; redo it from the flight page or with pilot leg go-around, or fix them by hand, then dismiss this.", "this.")]
    [InlineData("merge-failed", "The merge of PIL355 did not go through. Redo leg parser sends a fresh worker to fix it; Re-run checks runs them again; Merge again tries once more as it is.", "is.")]
    [InlineData("leg-stopped", "The worker of PIL355/parser hit the usage limit. It resumes when the window resets; nothing to do, and the leg keeps its place in the queue until then.", "then.")]
    public void ALongSummaryIsShownWholeAtThePanelsWidth(string kind, string summary, string lastWord)
    {
        var card = new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = kind, Subject = "flight/PIL355/parser", Title = "A card", Summary = summary, At = Now, State = CardState.Open };

        PaintedWords(card).Select(w => w.Trim()).ShouldContain(lastWord);
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
                Id = "c1", Bin = CardBin.Ring, Kind = "review-changes", Subject = "flight/PIL355", Title = "PIL355's review asks for changes",
                Summary = "The review of PIL355/parser asked for changes. Redo leg sends a fresh worker to make them; Ready anyway lands it as it is.",
                Detail = "Review of 3b2b6d0: the parser drops a trailing newline; add a test for it.",
                Choices = [redo, new CardChoice { Label = "Ready anyway", Verb = Verbs.Review, Target = "flight/PIL355", Args = ["pass", "ready anyway"] }],
                Link = "https://github.com/SharpAstro/pilot/pull/360", At = Live.At, State = CardState.Open,
            }),
            ("leg-failed", new CardRow
            {
                Id = "c2", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/PIL355/parser", Title = "PIL355/parser stopped: dotnet test failed with 3 errors in ParserTests",
                Summary = "The worker of PIL355/parser stopped. Send parser round again starts a fresh worker told why; Accept parser as it stands keeps the failure in its report and the flight goes on.",
                Detail = "the last worker stopped: dotnet test failed with 3 errors in ParserTests",
                Choices = [redo, new CardChoice { Label = "Accept parser as it stands, the failure kept in its report", Verb = Verbs.FinishLeg, Target = Verbs.LegTarget("PIL355", "parser"), Args = ["accepted"] }], At = Live.At, State = CardState.Open,
            }),
            ("merge-failed", new CardRow
            {
                Id = "c3", Bin = CardBin.Ring, Kind = "merge-failed", Subject = "flight/PIL355", Title = "PIL355's PR #360: check build-and-test failed",
                Summary = "The merge of PIL355 did not go through. Redo leg parser sends a fresh worker to fix it; Re-run checks runs them again; Merge again tries once more as it is.",
                Detail = "The merge waited for the checks to pass, and build-and-test failed on 3b2b6d0. Auto-merge is off.",
                Choices = [redo, new CardChoice { Label = "Re-run checks", Verb = Verbs.RerunChecks, Target = "flight/PIL355", Args = ["3b2b6d0", "squash"] }, new CardChoice { Label = "Merge again", Verb = Verbs.Merge, Target = "flight/PIL355", Args = ["3b2b6d0"] }],
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
