using DIR.Lib;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// A test filter such as <c>(FullyQualifiedName~TopBarTests|FullyQualifiedName~...)</c> is one word of 300 characters with no space
/// (pilot #1286): the shared wrap breaks it after each <c>|</c> and <c>~</c>, so it wraps at its terms and runs past nothing.
/// </summary>
public sealed class WrappedProseBreakTests
{
    internal static string Filter()
    {
        var names = new[] { "TopBarTests", "CardsViewTests", "RailTests", "JobsPageTests", "FleetPageTests", "SettingsTests", "IdeasPageTests", "FlightPageTests", "ReposPageTests", "DesignPageTests" };
        return "(" + string.Join("|", names.Select(n => "FullyQualifiedName~" + n)) + ")";
    }

    [Fact]
    public void ATestFilterBreaksAfterEachPipeAndTilde()
    {
        var word = Filter();
        word.Length.ShouldBeGreaterThanOrEqualTo(300);
        word.ShouldNotContain(" ");

        var pieces = WrappedProse.Break(word).ToArray();
        string.Concat(pieces).ShouldBe(word, "the pieces joined are the word");
        pieces.ShouldAllBe(p => p.Length <= WrappedProse.LongWord, "no piece is longer than LongWord");
        foreach (var piece in pieces)
        {
            // A separator ends its piece: none sits inside one.
            piece.TrimEnd('|', '~').ShouldNotContain("|");
            piece.TrimEnd('|', '~').ShouldNotContain("~");
        }
        pieces.Count(p => p.EndsWith('|')).ShouldBe(word.Count(c => c == '|'));
        pieces.Count(p => p.EndsWith('~')).ShouldBe(word.Count(c => c == '~'));
    }

    [Fact]
    public void AShortWordAndAPathBreakAsBefore()
    {
        WrappedProse.Break("short").ShouldBe(["short"]);
        WrappedProse.Break("src/Pilot.Ui/WrappedProse.cs").ShouldBe(["src/", "Pilot.", "Ui/", "WrappedProse.", "cs"]);
    }

    [Fact]
    public void APieceStillLongerThanLongWordIsCutByLength()
    {
        var run = new string('x', 60);
        var pieces = WrappedProse.Break(run).ToArray();
        string.Concat(pieces).ShouldBe(run);
        pieces.ShouldAllBe(p => p.Length <= WrappedProse.LongWord);
    }

    [Fact]
    public void AnIdeaBreaksATableRowAfterItsPipesAndKeepsAStrikethroughWhole()
    {
        // The Ideas page's Markdown (IdeasView.Soften) breaks a long word with real spaces: a tight table row stays a row, its cells parted after each |.
        IdeasView<RgbaImage>.Soften("|NameOfTheColumn|ValueOfTheColumn|Description|").ShouldBe("| NameOfTheColumn| ValueOfTheColumn| Description|");
        // No space goes beside a ~: a strikethrough's ~~ stays on its text, and a filter's terms run on after their ~ and break after their |.
        IdeasView<RgbaImage>.Soften("~~src/Pilot.Ui/WrappedProse.cs~~").ShouldBe("~~src/ Pilot. Ui/ WrappedProse. cs~~");
        IdeasView<RgbaImage>.Soften("FullyQualifiedName~TopBarTests|FullyQualifiedName~RailTests").ShouldBe("FullyQualifiedName~TopBarTests| FullyQualifiedName~RailTests");
    }
}
