using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// PIL1036: the node's "A card opened:" message to the watchtower shows on the Chat page as one quote per card (id and title, a few
/// lines of detail, the choices), not as the raw paragraph. The entry is what the transcript book gives a node say: a User note with the
/// [node] prefix cut off (TranscriptBookTests, WatchtowerChatTests).
/// </summary>
public sealed partial class ChatFrameTests
{
    private static CardRow NoticeCard(string id, string title, string? detail, params string[] choices) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "review", Subject = "flight/PIL1027", Title = title, Detail = detail, At = Local(11, 0), State = CardState.Open,
        Choices = [.. choices.Select(c => new CardChoice { Label = c, Verb = Verbs.Dismiss, Target = Verbs.CardTarget(id) })],
    };

    private static TranscriptEntry Note(string text) => new() { Index = 0, Kind = TranscriptKind.User, Note = true, At = Local(11, 5), Text = text };

    [Fact]
    public async Task ACardOpenedNoteShowsAsAQuoteWithTitleDetailAndChoicesNotTheRawParagraph()
    {
        var longDetail = string.Join(' ', Enumerable.Range(1, 80).Select(i => $"word{i}"));
        var text = CardNotice.Compose([NoticeCard("c21082", "PIL1027 needs a review (round 2)", longDetail, "Merge", "Redo")]);
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, [Note(text)]);
        await held.OpenAsync();

        var frame = Draw(renderer, held.Chat);

        frame.Texts.Any(t => t.Text.Contains("card c21082: PIL1027 needs a review (round 2)", StringComparison.Ordinal)).ShouldBeTrue();
        frame.Texts.Any(t => t.Text.Contains("1 Merge, 2 Redo", StringComparison.Ordinal)).ShouldBeTrue();
        frame.Texts.Any(t => t.Text.Contains("A card opened:", StringComparison.Ordinal)).ShouldBeTrue("the header stays");
        frame.Texts.Any(t => t.Text.Contains("about flight/PIL1027", StringComparison.Ordinal)).ShouldBeFalse("the raw card line is gone");
        frame.Texts.Any(t => t.Text.Contains("[for you]", StringComparison.Ordinal)).ShouldBeFalse();
        var detail = frame.Texts.Where(t => t.Text.Contains("word1 ", StringComparison.Ordinal) || t.Text.Contains("word", StringComparison.Ordinal) && t.Text.StartsWith("> word", StringComparison.Ordinal)).ToArray();
        detail.Length.ShouldBeInRange(1, 3);
        frame.Texts.Any(t => t.Text.Contains("word80", StringComparison.Ordinal)).ShouldBeFalse("the detail is cut");
        frame.Texts.Any(t => t.Text.Contains("...", StringComparison.Ordinal)).ShouldBeTrue();
        frame.Texts.Any(t => t.Text.Contains("These are yours to see through", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task SeveralCardsEachGetTheirOwnQuoteAndAnOlderLineStaysRaw()
    {
        var text = CardNotice.Compose([NoticeCard("c1", "First", "Detail one."), NoticeCard("c2", "Second", null, "OK")]);
        using var renderer = new RgbaImageRenderer(1600, 900);
        using var held = new Held(renderer, [Note(text), Note("A card opened: c17 asks to dismiss the stall") with { Index = 1 }]);
        await held.OpenAsync();

        var frame = Draw(renderer, held.Chat);

        frame.Has("2 cards opened:").ShouldBeTrue();
        frame.Texts.Any(t => t.Text.Contains("card c1: First", StringComparison.Ordinal)).ShouldBeTrue();
        frame.Texts.Any(t => t.Text.Contains("card c2: Second", StringComparison.Ordinal)).ShouldBeTrue();
        frame.Texts.Any(t => t.Text.Contains("A card opened: c17 asks to dismiss the stall", StringComparison.Ordinal)).ShouldBeTrue("a line that does not parse shows as before");
    }
}
