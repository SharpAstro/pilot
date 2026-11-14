using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1036: the card message the node says to the watchtower is written and read back in one place (CardNotice).</summary>
public sealed class CardNoticeTests
{
    private static CardRow Card(string id, string title, string? detail = null, params string[] choices) => new()
    {
        Id = id, Bin = CardBin.ForYou, Kind = "review", Subject = "flight/PIL1027", Title = title, Detail = detail, At = DateTimeOffset.UnixEpoch,
        Choices = [.. choices.Select(c => new CardChoice { Label = c, Verb = Verbs.Dismiss, Target = Verbs.CardTarget(id) })],
    };

    private static CardNoticeMessage Parsed(params CardRow[] cards)
    {
        CardNotice.TryParse(CardNotice.Compose(cards), out var notice).ShouldBeTrue();
        return notice;
    }

    [Fact]
    public void ThePrefixTheWatchtowerReadsIsUnchanged()
    {
        Briefs.CardLine(Card("c1", "Title", "Some detail", "Merge", "Redo"))
            .ShouldBe("- c1 [for you] about flight/PIL1027: Title (Some detail). Choices: 1 Merge, 2 Redo.");
        CardNotice.Compose([Card("c1", "T")]).ShouldStartWith("A card opened:\n- c1 [for you] about flight/PIL1027: T.\nThese are yours to see through");
    }

    [Fact]
    public void OneCardRoundTrips()
    {
        var notice = Parsed(Card("c21082", "Review failed", "Two findings.", "Redo", "Dismiss"));

        notice.Header.ShouldBe("A card opened:");
        notice.Instruction.ShouldBe(CardNotice.Instruction);
        var card = notice.Cards.ShouldHaveSingleItem();
        (card.Id, card.Bin, card.Subject, card.Title, card.Detail).ShouldBe(("c21082", "for you", "flight/PIL1027", "Review failed", "Two findings."));
        card.Choices.ShouldBe(["Redo", "Dismiss"]);
    }

    [Fact]
    public void SeveralCardsRoundTripInOrder()
    {
        var notice = Parsed(Card("c1", "One", "a"), Card("c2", "Two", null, "OK"), Card("c3", "Three"));

        notice.Header.ShouldBe("3 cards opened:");
        notice.Cards.Select(c => c.Id).ShouldBe(["c1", "c2", "c3"]);
        notice.Cards[1].Choices.ShouldBe(["OK"]);
    }

    [Fact]
    public void ATitleHoldingParenthesesAndColonsRoundTripsWithAndWithoutDetail()
    {
        var withDetail = Parsed(Card("c1", "Fix (round 2): the stall (again)", "Detail (with parens): yes")).Cards.Single();
        (withDetail.Title, withDetail.Detail).ShouldBe(("Fix (round 2): the stall (again)", "Detail (with parens): yes"));

        var bare = Parsed(Card("c2", "Review failed (round 2)")).Cards.Single();
        (bare.Title, bare.Detail).ShouldBe(("Review failed (round 2)", null));

        var middle = Parsed(Card("c3", "A (b) c")).Cards.Single();
        (middle.Title, middle.Detail).ShouldBe(("A (b) c", null));
    }

    [Fact]
    public void NoDetailAndNoChoicesGiveNullAndNone()
    {
        var card = Parsed(Card("c1", "Plain")).Cards.Single();

        card.Detail.ShouldBeNull();
        card.Choices.ShouldBeEmpty();
    }

    [Fact]
    public void ADetailOverSeveralLinesIsOneLine()
    {
        Parsed(Card("c1", "T", "line one\nline two")).Cards.Single().Detail.ShouldBe("line one line two");
    }

    [Theory]
    [InlineData("A card opened: c17 asks to dismiss the stall")]
    [InlineData("A card opened:\n- c10277 [ring] about session/500fb3b1")]
    [InlineData("2 cards opened")]
    [InlineData("hello")]
    [InlineData("")]
    public void AnOlderOrOtherLineDoesNotParse(string text)
    {
        CardNotice.TryParse(text, out _).ShouldBeFalse();
    }
}
