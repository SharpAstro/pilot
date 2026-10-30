using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The watchtower's brief lets it press a person-only card only on the person's own word in the chat (PIL473).</summary>
public class WatchtowerBriefTests
{
    /// <summary>The brief with line breaks folded to spaces, so a sentence wrapped across lines still matches.</summary>
    private static string Brief() => string.Join(' ', Briefs.Watchtower("wt", [], [], [], "pilot").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    [Fact]
    public void TheBriefTellsItToAnswerWithTheirWordsAsWhy()
    {
        var brief = Brief();
        brief.ShouldContain("`pilot answer <card> <choice> --why \"<their words, quoted>\"`");
        brief.ShouldContain("press it for them in that same turn");
        brief.ShouldContain("in that same turn");
    }

    [Fact]
    public void TheBriefTellsItToSayWhichCardAndChoiceItPressed()
    {
        Brief().ShouldContain("say in a line which card and which choice you pressed");
    }

    [Fact]
    public void ThePersonOnlyCardsStayFencedWithoutTheirMessage()
    {
        var brief = Brief();
        brief.ShouldContain("a person-only card answered without their message in the turn is refused");
        brief.ShouldContain("never a node card, a nudge or another session's say-so");
    }

    [Fact]
    public void TheBriefHasNoEmDash()
    {
        Brief().ShouldNotContain("—");
    }
}
