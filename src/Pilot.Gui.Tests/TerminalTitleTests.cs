using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>pilot-tui's terminal title (<see cref="TerminalTitle"/>): page, node and the cards waiting, and no OSC escape written by pilot.</summary>
public sealed class TerminalTitleTests
{
    private static readonly NodeMark De1 = new("de1", "de1", "DE1", true);
    private static readonly NodeMark Se1 = new("se1", "se1:7", "SE1", false);

    private static NodeFilter Filter() => new() { Choices = [De1, Se1] };

    private static CardsScreen Cards(int forYou, int rings = 0) => CardsScreen.From(
        Enumerable.Range(0, forYou).Select(i => Card($"y{i}", CardBin.ForYou))
            .Concat(Enumerable.Range(0, rings).Select(i => Card($"r{i}", CardBin.Ring)))
            .Append(Card("h", CardBin.Handled, CardState.Closed)));

    private static CardRow Card(string id, CardBin bin, CardState state = CardState.Open) => new()
    {
        Id = id, Bin = bin, Kind = "pr-ready", Subject = "flight/ORB201", Title = "t", Detail = "d", At = DateTimeOffset.UnixEpoch, State = state,
    };

    private static string Title(PilotPage page, NodeMark? chat, NodeFilter nodes, CardsScreen cards) =>
        TerminalTitle.Of(page, TerminalTitle.NodeName(page, chat, nodes), TerminalTitle.Waiting(cards));

    [Fact]
    public void HomeWithNoCardsNamesThisNode() =>
        Title(PilotPage.Home, null, Filter(), Cards(0)).ShouldBe("pilot · Home · DE1");

    [Fact]
    public void FlightsWithThreeCardsForYouLeadsWithTheCount() =>
        Title(PilotPage.Flights, null, Filter(), Cards(3)).ShouldBe("(3) pilot · Flights · DE1");

    /// <summary>
    /// The title counts what the panel shows: a card dismissed and not yet closed by the node is hidden there, so it is not counted
    /// (the host passes CardsView.Visible, the review of #1058).
    /// </summary>
    [Fact]
    public void ADismissedCardNotYetClosedIsNotCounted()
    {
        var cards = Cards(3);
        var view = new CardsView<DIR.Lib.RgbaImage>(new DIR.Lib.RgbaImageRenderer(10, 10));
        view.NoteDismissed(cards.Open.First(c => c.Id == "y0"));

        Title(PilotPage.Home, null, Filter(), view.Visible(cards)).ShouldBe("(2) pilot · Home · DE1");
    }

    [Fact]
    public void RingsCountToo() =>
        Title(PilotPage.Home, null, Filter(), Cards(1, rings: 2)).ShouldBe("(3) pilot · Home · DE1");

    [Fact]
    public void ChatWithANodeChosenNamesIt() =>
        Title(PilotPage.Chat, De1, Filter(), Cards(0)).ShouldBe("pilot · Chat · DE1");

    [Fact]
    public void ChatWithNoneChosenIsTheFleet() =>
        Title(PilotPage.Chat, null, Filter(), Cards(0)).ShouldBe("pilot · Chat · fleet");

    [Fact]
    public void AnotherPageNamesTheOnePickedNode()
    {
        var nodes = Filter();
        nodes.Toggle("se1");
        Title(PilotPage.Jobs, null, nodes, Cards(0)).ShouldBe("pilot · Background tasks · SE1");
    }

    [Fact]
    public void SeveralPickedNodesAreAllNamed()
    {
        var nodes = Filter();
        nodes.Toggle("se1");
        nodes.Toggle("de1");
        Title(PilotPage.Jobs, null, nodes, Cards(0)).ShouldBe("pilot · Background tasks · DE1+SE1");
    }

    [Fact]
    public void NoNodeKnownLeavesItOut() =>
        TerminalTitle.Of(PilotPage.Home, null, 0).ShouldBe("pilot · Home");

    [Fact]
    [Trait("Category", "Invariant")]
    public void NoSourceUnderSrcWritesAnOscEscape()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        dir.ShouldNotBeNull("no folder holding src/Pilot.slnx above the test");
        var src = Path.Combine(dir.FullName, "src");
        string[] needles = ["\\e]", "\\x1b]", "\\x1B]", "\\u001b]", "\\u001B]", "ESC]"];
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}") || file.EndsWith("TerminalTitleTests.cs", StringComparison.Ordinal))
            {
                continue;
            }
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (needles.Any(n => lines[i].Contains(n, StringComparison.Ordinal)))
                {
                    found.Add($"{Path.GetRelativePath(dir.FullName, file)}:{i + 1}");
                }
            }
        }
        found.ShouldBeEmpty("pilot writes no OSC escape; the title goes through Console.Lib: " + string.Join(", ", found));
    }
}
