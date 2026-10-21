using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>The transcript page's reading: a transcript's lines as entries, read on from where the last read stopped.</summary>
public sealed class TranscriptBookTests : IDisposable
{
    private const string Session = "a1b2c3d4-0000-4000-8000-000000000001";

    private readonly TempProfile _profile = new();

    public void Dispose() => _profile.Dispose();

    [Fact]
    public void ATranscriptReadsAsPromptsWordsToolCallsAndTheirResults()
    {
        var path = _profile.TranscriptPath(Session);
        _profile.Append(path,
            """{"type":"user","timestamp":"2026-10-21T08:00:00Z","message":{"role":"user","content":"Fix the flaky clock test"}}""",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"<system-reminder>not for people</system-reminder>"}}""",
            """{"type":"assistant","timestamp":"2026-10-21T08:00:05Z","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Look at the test first."},{"type":"text","text":"Running the tests."},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet test --filter Clock","description":"Run the clock tests"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"Passed!  - Failed: 0, Passed: 3"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"C:\\src\\Clock.cs"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","is_error":true,"content":[{"type":"text","text":"File does not exist."}]}]}}""",
            """{"type":"summary","summary":"bookkeeping"}""");
        var book = new TranscriptBook(_profile.Profile);

        var page = book.Read(Session, after: -1).ShouldNotBeNull();

        page.Entries.Select(e => (e.Kind, e.Text, e.Tool)).ShouldBe([
            (TranscriptKind.User, "Fix the flaky clock test", (string?)null),
            (TranscriptKind.Thinking, "Look at the test first.", null),
            (TranscriptKind.Assistant, "Running the tests.", null),
            (TranscriptKind.ToolUse, "dotnet test --filter Clock", "Bash"),
            (TranscriptKind.ToolResult, "Passed!  - Failed: 0, Passed: 3", null),
            (TranscriptKind.ToolUse, @"C:\src\Clock.cs", "Read"),
            (TranscriptKind.Error, "File does not exist.", null),
        ]);
        page.Total.ShouldBe(7);
        page.Entries[0].At.ShouldBe(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ALaterReadGivesWhatCameAfterAndALineCutShortWaitsForItsEnd()
    {
        var path = _profile.TranscriptPath(Session);
        _profile.Append(path, """{"type":"user","message":{"role":"user","content":"one"}}""");
        var book = new TranscriptBook(_profile.Profile);
        book.Read(Session, -1).ShouldNotBeNull().Entries.ShouldHaveSingleItem().Index.ShouldBe(0);

        // A write in progress: half a line, then the rest of it.
        File.AppendAllText(path, """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"tw""");
        book.Read(Session, 0).ShouldNotBeNull().Entries.ShouldBeEmpty();
        File.AppendAllText(path, "o\"}]}}\n");

        var later = book.Read(Session, 0).ShouldNotBeNull();
        later.Entries.Select(e => (e.Index, e.Text)).ShouldBe([(1, "two")]);
        later.Total.ShouldBe(2);
    }

    [Fact]
    public void ASessionWithNoTranscriptHasNone() => new TranscriptBook(_profile.Profile).Read(Session, -1).ShouldBeNull();
}
