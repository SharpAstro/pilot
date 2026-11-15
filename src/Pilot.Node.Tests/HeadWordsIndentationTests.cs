using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Which paths a whitespace-only change means something in (pilot #691). Pure: no git repo, so it is not in the Slow, git-backed
/// <see cref="HeadWordsTests"/> (#1063).
/// </summary>
public sealed class HeadWordsIndentationTests
{
    [Theory]
    [InlineData("src/run.py", true)]
    [InlineData(".github/workflows/dotnet.yml", true)]
    [InlineData("Makefile", true)]
    [InlineData("src/Pilot.Node/Flights/HeadWords.cs", false)]
    public void IndentationMattersWhereItIsMeaning(string path, bool matters) =>
        HeadWords.IndentationMatters(path).ShouldBe(matters);
}
