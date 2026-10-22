using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A job's log, its end, as the Background tasks page reads it: by the reader alone, and through the node's route.</summary>
public sealed class JobLogTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2l-" + Guid.NewGuid().ToString("N")[..8]);

    public JobLogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Log(string text)
    {
        var path = Path.Combine(_root, "j1.log");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void AShortLogIsSentWhole()
    {
        var log = JobLogs.Tail(Log("Restore complete\nBuild succeeded\n"))!;

        log.Lines.ShouldBe(["Restore complete", "Build succeeded"]);
        log.Clipped.ShouldBeFalse();
        log.Length.ShouldBe(33);
    }

    [Fact]
    public void ALongLogSendsItsLastLinesAndSaysItIsClipped()
    {
        var log = JobLogs.Tail(Log(string.Concat(Enumerable.Range(1, 500).Select(i => $"line {i}\n"))), lineLimit: 3)!;

        log.Lines.ShouldBe(["line 498", "line 499", "line 500"]);
        log.Clipped.ShouldBeTrue();
    }

    [Fact]
    public void ReadFromTheMiddleTheFirstPartLineGoes()
    {
        // 20 bytes from the end of "aaaaaaaaaa\nbbbbbbbbbb\ncccccccccc\n" starts inside b's line: only c's whole line is sent.
        var log = JobLogs.Tail(Log("aaaaaaaaaa\nbbbbbbbbbb\ncccccccccc\n"), byteLimit: 20)!;

        log.Lines.ShouldBe(["cccccccccc"]);
        log.Clipped.ShouldBeTrue();
    }

    [Fact]
    public void ALogStillBeingWrittenIsRead()
    {
        var path = Path.Combine(_root, "j2.log");
        using var writer = new StreamWriter(path, append: false) { AutoFlush = true };
        writer.WriteLine("Running 1200 tests");

        JobLogs.Tail(path)!.Lines.ShouldBe(["Running 1200 tests"]);
    }

    [Fact]
    public void AJobWithNoLogYetHasNone() => JobLogs.Tail(Path.Combine(_root, "none.log")).ShouldBeNull();

    [Fact]
    public async Task TheRouteSendsAJobsLogAndSaysWhenThereIsNoSuchJob()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var path = Path.Combine(_root, "j7.log");
        File.WriteAllText(path, "Passed!  - Failed: 0, Passed: 1200\n");
        node.State.PutJob(new JobRow
        {
            Id = "j7", Command = "dotnet test", Cwd = _root, State = JobState.Succeeded, LogPath = path, Timeout = TimeSpan.FromHours(1),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-9), EndedAt = DateTimeOffset.UtcNow, ExitCode = 0,
        });

        var found = await node.Client.GetJobLogAsync("j7", Ct);
        var missing = await node.Client.GetJobLogAsync("j99", Ct);

        found.Success.ShouldBeTrue();
        found.Response!.Lines.ShouldBe(["Passed!  - Failed: 0, Passed: 1200"]);
        missing.Success.ShouldBeFalse();
        missing.Error.ShouldBe("There is no job j99");
    }
}
