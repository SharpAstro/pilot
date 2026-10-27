using System.Collections.Immutable;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The overlap card (#375): it names both flights, the files they share and the choice in its title, offers Fold into, Connect to and
/// Plan in parallel (no fold into a flight of another node), and wraps its title and detail instead of clipping them to one row.
/// </summary>
public sealed class OverlapCardTests
{
    private const string LongFiles = "src/Pilot.Node/Flights/DetachedShell.cs, src/Pilot.Node/Flights/JobRunner.cs, src/Pilot.Node/Flights/LegCommander.Touches.cs, src/Pilot.Ui/CardsView.cs and 3 more";

    private static CardChoice Choice(string label, string verb) => new() { Label = label, Verb = verb, Target = "flight/PIL365", Args = ["PIL294"] };

    private static CardRow Local(string files = "DetachedShell.cs") => new()
    {
        Id = "c1", Bin = CardBin.Ring, Kind = CardKinds.Overlap, Subject = "flight/PIL365", Title = $"Connect PIL365 to PIL294 (both change {files})",
        Detail = "PIL365 (v2: a flight's push never drops commits) overlaps PIL294 (v2: the shell of a detached worker keeps its output). "
            + $"Both change 2 files: {files}. The planner says: both rework the detached shell. Connect holds PIL365 until PIL294 has landed. "
            + "Plan in parallel flies both; the second to land resolves the conflicts. Fold takes PIL365's issue into PIL294. Its planner waits for your answer.",
        Choices = [Choice("Fold into PIL294", Verbs.Fold), Choice("Connect to PIL294", Verbs.ConnectOverlap), Choice("Plan in parallel", Verbs.Separate)],
        At = Live.At, State = CardState.Open,
    };

    private static CardRow OnAnotherNode() => Local(LongFiles) with
    {
        Id = "c2", Title = $"Connect PIL365 to PIL294 on DE1 (both change {LongFiles})",
        Detail = "PIL365 (a long title that reads well in a wide window and wraps in a narrow one, all of it) overlaps PIL294 (another long title of the flight on the other node), which flies on DE1. "
            + $"Both change 7 files: {LongFiles}. The planner says: both rework the detached shell. Connect holds PIL365 until PIL294 has landed. "
            + "Plan in parallel flies both; the second to land resolves the conflicts. PIL365 cannot be folded into it from here. Its planner waits for your answer.",
        Choices = [Choice("Connect to PIL294", Verbs.ConnectOverlap), Choice("Plan in parallel", Verbs.Separate)],
    };

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(CardRow card, float width, out RgbaImageRenderer renderer)
    {
        renderer = new RgbaImageRenderer(1, 1);
        var view = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        return Layout.Engine.Arrange(view.Tree(CardsScreen.From([card])), new Rect<float>(0, 0, width, 1200), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
    }

    private static string[] Words(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];

    [Theory]
    [InlineData(320f)]
    [InlineData(420f)]
    [InlineData(900f)]
    public void TheCardOffersEachChoiceItsTitleNamesBothFlightsAndFilesAndNothingIsClippedToOneRow(float width)
    {
        var arranged = Arrange(Local(), width, out var renderer);
        using (renderer)
        {
            var words = Words(arranged);
            words.ShouldContain("Fold into PIL294");
            words.ShouldContain("Connect to PIL294");
            words.ShouldContain("Plan in parallel");
            words.ShouldNotContain(w => w.Contains("Leave it", StringComparison.OrdinalIgnoreCase));
            string.Join(' ', words).ShouldContain("Connect PIL365 to PIL294");
        }
    }

    [Fact]
    public void ALongTitleWrapsOverSeveralRowsWithTheWholeOfItOnHoverWhereItIsCut()
    {
        var card = OnAnotherNode();
        var arranged = Arrange(card, 340f, out var renderer);
        using (renderer)
        {
            var title = arranged.Where(a => a.Node.Tooltip == card.Title).ToList();
            title.ShouldNotBeEmpty("a title this long carries its whole text as hover text");
            // More than one row: a single clipped row would lose the files and the node.
            title.Max(a => a.Bounds.Height).ShouldBeGreaterThan(22f);
            Words(arranged).ShouldNotContain("Fold into PIL294");
            Words(arranged).ShouldContain("Connect to PIL294");
        }
    }

    [Fact]
    public void CapturesOfTheOverlapCardAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        var dir = Path.GetDirectoryName(capture) ?? ".";
        var cases = new (string Name, CardRow[] Cards)[]
        {
            ("this-node", [Local()]),
            ("another-node-long", [OnAnotherNode()]),
            ("both", [Local(LongFiles), OnAnotherNode()]),
        };
        foreach (var (width, height) in new[] { (1280, 800), (2000, 900) })
        {
            foreach (var (name, cards) in cases)
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
                chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From(cards), new RectF32(0, 0, width, height));
                File.WriteAllBytes(Path.Combine(dir, $"overlap-{name}-{width}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }
}
