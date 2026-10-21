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

    [Fact]
    public void ACallAndItsResultCarryTheCallsIdAndAFoldedCommandKeepsItsWholeText()
    {
        var path = _profile.TranscriptPath(Session);
        _profile.Append(path,
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"cd src &&\ndotnet test"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""");

        var entries = new TranscriptBook(_profile.Profile).Read(Session, -1).ShouldNotBeNull().Entries;

        entries.Select(e => (e.Kind, e.ToolUseId)).ShouldBe([(TranscriptKind.ToolUse, "t1"), (TranscriptKind.ToolResult, "t1")]);
        entries[0].Text.ShouldBe("cd src && dotnet test");
        entries[0].Full.ShouldBe("cd src &&\ndotnet test");
    }

    [Fact]
    public void PromptsReadAsAPersonSaysThemNotesHandBacksAndCommandsTidied()
    {
        var path = _profile.TranscriptPath(Session);
        _profile.Append(path,
            """{"type":"user","message":{"role":"user","content":"Fix it<system-reminder>for the model</system-reminder>"}}""",
            """{"type":"user","message":{"role":"user","content":"<system-reminder>only this</system-reminder>"}}""",
            """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>b1</task-id>\n<status>completed</status>\n<summary>Background command \"Run the tests\" completed (exit code 0)</summary>\n</task-notification>"}}""",
            """{"type":"user","message":{"role":"user","content":"<command-name>/goal</command-name>\n            <command-message>goal</command-message>\n            <command-args>work the list</command-args>"}}""",
            """{"type":"user","message":{"role":"user","content":"<local-command-caveat>Caveat: for the model</local-command-caveat>"}}""",
            """{"type":"user","message":{"role":"user","content":"<local-command-stdout>Goal set: work the list</local-command-stdout>"}}""",
            """{"type":"attachment","attachment":{"type":"queued_command","prompt":"<agent-message from=\"a1\">\n[Subagent hand-back] The text below is the final report. The report follows:\n  ## Found it\n  \n  - the clock\n</agent-message>","origin":{"kind":"peer","from":"a1","name":"Explore"}}}""",
            """{"type":"attachment","attachment":{"type":"queued_command","prompt":"and the other test too"}}""");

        var entries = new TranscriptBook(_profile.Profile).Read(Session, -1).ShouldNotBeNull().Entries;

        entries.Select(e => (e.Text, e.Note, e.From)).ShouldBe([
            ("Fix it", false, (string?)null),
            ("Background command \"Run the tests\" completed (exit code 0)", true, null),
            ("/goal work the list", false, null),
            ("Goal set: work the list", true, null),
            ("## Found it\n\n- the clock", false, "Explore"),
            ("and the other test too", false, null),
        ]);
        entries.ShouldAllBe(e => e.Kind == TranscriptKind.User);
        entries[4].FromAgent.ShouldBe("a1");
    }

    [Fact]
    public void AMessageQueuedForABusySessionWaitsUntilItIsTakenAsATurnOrMidTurn()
    {
        var path = _profile.TranscriptPath(Session);
        _profile.Append(path,
            """{"type":"queue-operation","operation":"enqueue","content":"and the other test too"}""",
            """{"type":"queue-operation","operation":"enqueue","content":"<task-notification>\n<summary>done</summary>\n</task-notification>"}""",
            """{"type":"queue-operation","operation":"enqueue","content":"then push it"}""");
        var book = new TranscriptBook(_profile.Profile);

        // Claude Code's own (a task finishing) is not someone's words waiting.
        book.Read(Session, -1).ShouldNotBeNull().Queued.ShouldBe(["and the other test too", "then push it"]);

        // Taken mid-turn: a remove, then the attachment.
        _profile.Append(path,
            """{"type":"queue-operation","operation":"remove","content":"and the other test too"}""",
            """{"type":"attachment","attachment":{"type":"queued_command","prompt":"and the other test too"}}""");
        book.Read(Session, -1).ShouldNotBeNull().Queued.ShouldBe(["then push it"]);

        // Taken as a new turn: a dequeue, then the user line.
        _profile.Append(path,
            """{"type":"queue-operation","operation":"dequeue"}""",
            """{"type":"user","message":{"role":"user","content":"then push it"}}""");
        book.Read(Session, -1).ShouldNotBeNull().Queued.ShouldBeNull();
    }

    [Theory]
    [InlineData("Permission for this action was denied by the Claude Code auto mode classifier. Reason: [Destructive] deletes the repo. Try again.", "auto mode: [Destructive]")]
    [InlineData("Permission for this command was denied by a built-in Claude Code safety check: it writes outside the project.", "a built-in safety check")]
    [InlineData("Permission to use Bash has been denied because Claude Code is running in don't ask mode.", "don't-ask mode")]
    [InlineData("Permission to use Bash with command rm -rf / has been denied.", "permissions")]
    [InlineData("The user doesn't want to proceed with this tool use. The tool use was rejected.", "you said no")]
    [InlineData("Exit code 1\nPermission denied", null)]
    public void ARefusedCallSaysWhatRefusedIt(string result, string? denied) => TranscriptBook.DenialOf(result).ShouldBe(denied);
}
