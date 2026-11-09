using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A brief too long for <c>claude --bg</c>'s command line goes through a file the session is told to read.</summary>
public sealed class LongBriefTests : IDisposable
{
    private readonly string _folder = TestTemp.NewPath();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            TestTemp.Delete(_folder);
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

    [Fact]
    public void BriefFilesOlderThanTheirLifeAreSweptAndNewerOnesKept()
    {
        Directory.CreateDirectory(_folder);
        var old = Path.Combine(_folder, "old.md");
        var fresh = Path.Combine(_folder, "fresh.md");
        File.WriteAllText(old, "a");
        File.WriteAllText(fresh, "b");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-6));

        ClaudeWorkerLauncher.SweepBriefs(_folder, ClaudeWorkerLauncher.BriefFileLife, DateTime.UtcNow).ShouldBe(1);

        File.Exists(old).ShouldBeFalse();
        File.Exists(fresh).ShouldBeTrue();
    }

    [Fact]
    public void SweepingAFolderThatIsNotThereIsNothing() =>
        ClaudeWorkerLauncher.SweepBriefs(_folder, ClaudeWorkerLauncher.BriefFileLife, DateTime.UtcNow).ShouldBe(0);

    [Fact]
    public async Task ABriefFileThatCannotBeWrittenIsAFlightExceptionNamingIt()
    {
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "blocker");
        File.WriteAllText(blocker, "x"); // a file where the folder should be, so CreateDirectory throws IOException
        var launcher = new ClaudeWorkerLauncher(new ClaudeTrust(Path.Combine(_folder, "claude.json")), briefFolder: blocker);

        var ex = await Should.ThrowAsync<FlightException>(() =>
            launcher.StartAsync(new WorkerStart("de1-pil584-review", _folder, null, new string('y', 30_000), null, null), CancellationToken.None));

        ex.Message.ShouldContain("its file could not be written");
    }
}
