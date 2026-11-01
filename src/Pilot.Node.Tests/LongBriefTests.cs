using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A brief too long for <c>claude --bg</c>'s command line goes through a file the session is told to read.</summary>
public sealed class LongBriefTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-long-brief-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ABriefThatFitsIsPassedAsItIs()
    {
        var brief = new string('x', ClaudeWorkerLauncher.MaxInlineBrief);

        ClaudeWorkerLauncher.Inline("de1-pil1-review", brief, _folder).ShouldBe(brief);
        Directory.Exists(_folder).ShouldBeFalse();
    }

    [Fact]
    public void ABriefTooLongIsWrittenWholeToAFileTheShortBriefNames()
    {
        var brief = "review PIL584 " + new string('y', 35_509);

        var inline = ClaudeWorkerLauncher.Inline("de1-pil584-review", brief, _folder);

        inline.Length.ShouldBeLessThan(1_000);
        var file = Directory.GetFiles(_folder).ShouldHaveSingleItem();
        Path.GetFileName(file).ShouldStartWith("de1-pil584-review-");
        inline.ShouldContain(file);
        File.ReadAllText(file).ShouldBe(brief);
    }
}
