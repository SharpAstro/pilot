using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// While the node runs a merge, the card's Merge reads <c>Merging… waiting for checks (2 pending)</c> as a chip, not a button, on the
/// panel, on a card relayed from another node and on the flight's page (PIL511, pilot#511).
/// </summary>
public sealed class MergingChipTests
{
    private const string Chip = "Merging… waiting for checks (2 pending)";

    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 41, 9, TimeSpan.Zero);

    private static CardRow Card() => new()
    {
        Id = "c9", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/TIA1611", Title = "TIA1611's PR #7 is ready to merge", At = Now, State = CardState.Open,
        Choices = [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/TIA1611", Args = ["abc1234"], Status = ChoiceStatus.Merging, Note = "waiting for checks (2 pending)" }],
    };

    private static string[] Words(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardsScreen screen, float width, out RgbaImageRenderer renderer)
    {
        renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(screen), new Rect<float>(0, 0, width, 1200), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    private static void ShouldBeAChip(ImmutableArray<Layout.ArrangedNode<float>> arranged)
    {
        Words(arranged).ShouldContain(Chip);
        var actions = arranged.Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        actions.ShouldNotContain("card:c9:1");
        actions.ShouldContain("card:c9:dismiss");
        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(t => t.Contains("nothing to press", StringComparison.Ordinal));
        arranged.Single(a => a.Node.Background is { } bg && bg == CardsView<RgbaImage>.ArmedBg && a.Node.Hit is null);
    }

    [Theory]
    [InlineData("waiting for checks (2 pending)", Chip)]
    [InlineData(null, "Merging…")]
    public void TheLabelIsMergingAndWhatItIsDoing(string? note, string label)
    {
        var choice = Card().Choices[0] with { Note = note };
        CardsView<RgbaImage>.StatusLabel(choice).ShouldBe(label);
        CardsView<RgbaImage>.IsChip(ChoiceStatus.Merging).ShouldBeTrue();
    }

    [Theory]
    [InlineData(340f)]
    [InlineData(520f)]
    public void AMergingCardDrawsTheChipAndNoMergeButton(float width)
    {
        var arranged = Arrange(CardsScreen.From([Card()]), width, out var renderer);
        using var _ = renderer;

        ShouldBeAChip(arranged);
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width}");
    }

    [Fact]
    public void ACardRelayedFromAnotherNodeDrawsTheSameChip()
    {
        var de1 = new NodeMark("10.0.0.9:47731", "10.0.0.9:47731", "DE1", Here: false);
        var screen = CardsScreen.Merge([(de1, CardsScreen.From([Card()]))], null);
        var arranged = Arrange(screen, 340, out var renderer);
        using var _ = renderer;

        Words(arranged).ShouldContain("RING · DE1");
        ShouldBeAChip(arranged);
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(700, 1.5f)]
    [InlineData(1600, 1f)]
    public void TheFlightsPageShowsTheSameChipAndNoMergeButton(int width, float dpi)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        var flights = chrome.Flights;
        flights.CanCommand = true;
        var row = new FlightRow
        {
            Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
            Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Now.AddDays(-1),
        };
        var screen = new FlightsScreen([FlightLine.Build([row], [], Now, []).Single()], "test", Now);
        chrome.Show(PilotPage.Flights);
        flights.OpenDetail("TIA1611");

        chrome.Render(new SessionsScreen([], "test", Now), screen, CardsScreen.From([Card()]), new RectF32(0, 0, width, 900));

        var arranged = Layout.Engine.Arrange(flights.Tree(screen), new Rect<float>(0, 0, width, 900), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi));
        Words(arranged).ShouldContain(Chip);
        var regions = new List<ClickableRegion>();
        flights.CollectPaintedRegions(regions);
        regions.Any(r => r.Result is HitResult.ButtonHit { Action: "flight:TIA1611:merge-card" }).ShouldBeFalse();
        TextOverlap.Faults(arranged).Select(f => f.Line).ShouldBeEmpty($"at {width} wide, DPI {dpi}");
    }
}
