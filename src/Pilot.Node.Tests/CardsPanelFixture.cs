using System.Text.Json;
using Pilot.Contracts;
using Shouldly;

namespace Pilot.Node.Tests;

/// <summary>
/// The "after" cards of the cards-panel capture (pilot#797), <c>Pilot.Gui.Tests/Fixtures/cards-plain-after.jsonl</c>, one card a line as the
/// node stores it, which <c>CardsPanelCaptureTests</c> draws at 420 px. The Gui tests cannot reach the node, so the fixture is tied to the
/// raisers here: each kind's golden test raises its real card and <see cref="ShouldMatch"/> holds the fixture card's title, first line and
/// buttons to it, with the test's names put back to the real cards' (a test flies TIA1, SE1 flew PIL716). A raiser's wording that changes
/// fails its golden test until the fixture, and so the capture, says the same.
/// </summary>
internal static class CardsPanelFixture
{
    public static readonly Lazy<string> Path = new(() => System.IO.Path.Combine(CardKindTests.Src.Value, "Pilot.Gui.Tests", "Fixtures", "cards-plain-after.jsonl"));

    private static readonly Lazy<Dictionary<string, CardRow>> Cards = new(() => File.ReadAllLines(Path.Value)
        .Where(l => l.Trim().Length > 0)
        .Select(l => JsonSerializer.Deserialize(l, PilotJsonContext.Default.CardRow).ShouldNotBeNull())
        .ToDictionary(c => c.Id, StringComparer.Ordinal));

    /// <summary>The fixture's card <paramref name="id"/>.</summary>
    public static CardRow Card(string id) => Cards.Value[id];

    /// <summary>
    /// Holds the fixture's card <paramref name="id"/> to <paramref name="raised"/>, the card the raiser made in a golden test: the same kind and
    /// bin, and the same title, first line and button labels once each (test word, real word) pair of <paramref name="names"/> is put back.
    /// <paramref name="title"/> false leaves the title out, for a test that raises the kind with a title of its own.
    /// </summary>
    public static void ShouldMatch(string id, CardRow raised, bool title = true, params (string Test, string Real)[] names)
    {
        string Real(string? text)
        {
            var real = text ?? "";
            foreach (var (test, word) in names)
            {
                real = real.Replace(test, word, StringComparison.Ordinal);
            }
            return real;
        }
        var fixture = Card(id);
        (raised.Kind, raised.Bin).ShouldBe((fixture.Kind, fixture.Bin), $"fixture card {id}");
        if (title)
        {
            Real(raised.Title).ShouldBe(fixture.Title, $"fixture card {id}'s title (in {Path.Value})");
        }
        Real(raised.Summary).ShouldBe(fixture.Summary ?? "", $"fixture card {id}'s first line (in {Path.Value})");
        raised.Choices.Select(c => Real(c.Label)).ShouldBe(fixture.Choices.Select(c => c.Label), $"fixture card {id}'s buttons (in {Path.Value})");
    }
}
