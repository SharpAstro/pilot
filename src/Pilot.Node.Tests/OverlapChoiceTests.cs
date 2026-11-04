using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public class OverlapChoiceTests
{
    private static Dictionary<string, int> L(params (string, int)[] items) => items.ToDictionary(i => i.Item1, i => i.Item2);

    [Fact]
    public void OneSharedTestFileDoesNotRing() =>
        OverlapChoice.Rings(["src/A.Tests/X.cs"], L(("src/A.Tests/X.cs", 500)), L(("src/A.Tests/X.cs", 500))).ShouldBeFalse();

    [Fact]
    public void ThreeSharedFilesDoNotRingButFourDo()
    {
        OverlapChoice.Rings(["a.cs", "b.cs", "c.cs"], null, null).ShouldBeFalse();
        OverlapChoice.Rings(["a.cs", "b.cs", "c.cs", "d.cs"], null, null).ShouldBeTrue();
    }

    [Fact]
    public void ASharedSourceFileRingsWhenHotOnBothSides()
    {
        OverlapChoice.Rings(["src/A.cs"], L(("src/A.cs", 100)), L(("SRC/a.cs", 100))).ShouldBeTrue();
        OverlapChoice.Rings(["src/A.cs"], L(("src/A.cs", 100)), L(("src/A.cs", 99))).ShouldBeFalse();
        OverlapChoice.Rings(["src/A.cs"], L(("src/A.cs", 100)), null).ShouldBeFalse();
    }

    [Fact]
    public void ASharedTestFileNeverMakesItHot() =>
        OverlapChoice.Rings(["src/Pilot.Node.Tests/X.cs"], L(("src/Pilot.Node.Tests/X.cs", 500)), L(("src/Pilot.Node.Tests/X.cs", 500))).ShouldBeFalse();

    [Theory]
    [InlineData("src/Pilot.Node.Tests/X.cs", true)]
    [InlineData("tests/test_pilot.py", true)]
    [InlineData("FooTests.cs", true)]
    [InlineData("src/Pilot.Node/Flights/CardSummaries.cs", false)]
    public void IsTestFileReadsThePath(string path, bool expected) => OverlapChoice.IsTestFile(path).ShouldBe(expected);
}
