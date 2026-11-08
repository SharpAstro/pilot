using System.Text.Json;
using System.Threading.Channels;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>The watchtower's long-lived streaming session (PIL855), driven by a fake process: no real claude runs.</summary>
public sealed class StreamSessionTests
{
    private static readonly WorkerStart Watchtower = new("se1-watchtower", "/repo", null, "brief", "sonnet", "low");

    private sealed class FakeProcess : IStreamProcess
    {
        private readonly Channel<string> _out = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _written = [];

        /// <summary>Called with each line written, before the write returns: how a fake answers at once.</summary>
        public Action<string>? OnWrite { get; set; }

        public bool Killed { get; private set; }

        public string ErrorTail { get; private set; } = "";

        public Task<int> Exited => _exit.Task;

        public string[] Written
        {
            get
            {
                lock (_written)
                {
                    return [.. _written];
                }
            }
        }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            lock (_written)
            {
                _written.Add(line);
            }
            OnWrite?.Invoke(line);
            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await _out.Reader.WaitToReadAsync(cancellationToken) && _out.Reader.TryRead(out var line) ? line : null;

        public void Emit(string line) => _out.Writer.TryWrite(line);

        public void Exit(int code, string error = "")
        {
            ErrorTail = error;
            _out.Writer.TryComplete();
            _exit.TrySetResult(code);
        }

        public void Kill()
        {
            Killed = true;
            Exit(-1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLauncher(FakeProcess process) : IStreamProcessLauncher
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public string? Folder { get; private set; }

        public IStreamProcess Start(IReadOnlyList<string> arguments, string workingFolder)
        {
            Arguments = arguments;
            Folder = workingFolder;
            return process;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeProcess Process { get; } = new();
        public ManualTime Time { get; } = new(DateTimeOffset.UnixEpoch.AddHours(1));
        public FakeLauncher Launcher { get; }
        public List<string> Log { get; } = [];
        public List<StreamSessionChange> Changes { get; } = [];
        public StreamSession Session { get; }

        public Rig(string? resumeId = null, bool starting = false)
        {
            Launcher = new FakeLauncher(Process);
            Session = StreamSession.Start(Watchtower, resumeId, Launcher, Time, line => { lock (Log) { Log.Add(line); } }, starting);
            Session.Changed += change => { lock (Changes) { Changes.Add(change); } };
        }

        public StreamChangeKind[] Kinds
        {
            get
            {
                lock (Changes)
                {
                    return [.. Changes.Select(c => c.Kind)];
                }
            }
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private static string Init(string id) => $$"""{"type":"system","subtype":"init","session_id":"{{id}}","model":"sonnet"}""";

    private static string Delta(string text) =>
        JsonSerializer.Serialize(new { type = "stream_event", @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } } });

    private static string Tool(string name, object input) =>
        JsonSerializer.Serialize(new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = "let me look" }, new { type = "tool_use", id = "t1", name, input } } } });

    private const string ToolResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";

    private static string Result(int ms, bool isError = false) =>
        $$"""{"type":"result","subtype":"{{(isError ? "error_during_execution" : "success")}}","is_error":{{(isError ? "true" : "false")}},"duration_ms":{{ms}},"session_id":"x"}""";

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("timed out waiting for " + what);
            }
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task AFreshSessionStartsUnderANewIdWithTheSharedOptionsAndAResumeKeepsItsId()
    {
        await using var fresh = new Rig();
        var args = fresh.Launcher.Arguments!;

        args.Take(7).ShouldBe(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages"]);
        args.ShouldNotContain("--resume");
        var id = args[args.ToList().IndexOf("--session-id") + 1];
        Guid.TryParse(id, out _).ShouldBeTrue();
        fresh.Session.SessionId.ShouldBe(id);
        // Then the shared options, and last the flag that has every permission prompt and question arrive on stdout.
        args.Skip(9).ShouldBe([.. ClaudeWorkerLauncher.SessionArguments(Watchtower), "--permission-prompt-tool", "stdio"]);
        fresh.Launcher.Folder.ShouldBe("/repo");

        await using var resumed = new Rig("0f0f0f0f-aaaa-bbbb-cccc-111122223333");
        var resumeArgs = resumed.Launcher.Arguments!;
        resumeArgs.ShouldNotContain("--session-id");
        resumeArgs[resumeArgs.ToList().IndexOf("--resume") + 1].ShouldBe("0f0f0f0f-aaaa-bbbb-cccc-111122223333");
        resumed.Session.SessionId.ShouldBe("0f0f0f0f-aaaa-bbbb-cccc-111122223333");
    }

    [Fact]
    public async Task TheInitLineGivesTheSessionId()
    {
        await using var rig = new Rig();

        rig.Process.Emit(Init("11111111-2222-3333-4444-555555555555"));

        await Until(() => rig.Kinds.Contains(StreamChangeKind.Init), "init");
        rig.Session.SessionId.ShouldBe("11111111-2222-3333-4444-555555555555");
    }

    [Fact]
    public async Task AMessageIsWrittenAtOnceAsAStreamJsonUserLineAndPartialWordsBuildTheTurn()
    {
        await using var rig = new Rig();

        await rig.Session.SendAsync("what is flying?", TestContext.Current.CancellationToken);

        rig.Session.Working.ShouldBeTrue();
        var line = JsonDocument.Parse(rig.Process.Written.ShouldHaveSingleItem()).RootElement;
        line.GetProperty("type").GetString().ShouldBe("user");
        line.GetProperty("message").GetProperty("role").GetString().ShouldBe("user");
        line.GetProperty("message").GetProperty("content").GetString().ShouldBe("what is flying?");

        rig.Process.Emit(Delta("Two "));
        rig.Process.Emit(Delta("flights."));
        await Until(() => rig.Session.PartialText == "Two flights.", "the words");
        rig.Session.Working.ShouldBeTrue();

        rig.Process.Emit(Result(900));
        await Until(() => !rig.Session.Working, "the end of the turn");
        rig.Session.PartialText.ShouldBe("Two flights.");
        rig.Kinds.ShouldContain(StreamChangeKind.TurnEnded);
        var turn = rig.Session.Timings.ShouldHaveSingleItem();
        turn.ClaudeDuration.ShouldBe(TimeSpan.FromMilliseconds(900));
        turn.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task AToolUseIsWhatItIsDoingInPlainWordsUntilItSpeaksOrTheToolAnswers()
    {
        await using var rig = new Rig();
        await rig.Session.SendAsync("status?", TestContext.Current.CancellationToken);

        rig.Process.Emit(Tool("Bash", new { command = "pilot status --json" }));
        await Until(() => rig.Session.Doing == "running pilot status", "the doing line");

        rig.Process.Emit(ToolResult);
        await Until(() => rig.Session.Doing is null, "the tool's answer");

        rig.Process.Emit(Tool("mcp__pilot__flight_plan", new { }));
        await Until(() => rig.Session.Doing == "running pilot flight plan", "the pilot tool");

        rig.Process.Emit(Delta("Done."));
        await Until(() => rig.Session.Doing is null && rig.Session.PartialText == "Done.", "the words");
        rig.Kinds.Count(k => k == StreamChangeKind.Doing).ShouldBe(4);
    }

    [Theory]
    [InlineData("Read", """{"file_path":"C:\\repo\\src\\Program.cs"}""", "reading Program.cs")]
    [InlineData("Edit", """{"file_path":"/repo/a/b.md"}""", "editing b.md")]
    [InlineData("Grep", """{"pattern":"x"}""", "searching the repo")]
    [InlineData("Bash", """{"command":"git status\nls"}""", "running git status")]
    [InlineData("Whatever", "{}", "using Whatever")]
    public void ToolsAreDescribedInPlainWords(string tool, string input, string expected) =>
        StreamSession.Describe(tool, JsonDocument.Parse(input).RootElement).ShouldBe(expected);

    [Fact]
    public async Task AMessageSentMidTurnIsWrittenToStdinAtOnceAndTheTurnsNextStepAnswersItInTheSameTurn()
    {
        await using var rig = new Rig();
        await rig.Session.SendAsync("first", TestContext.Current.CancellationToken);
        rig.Process.Emit(Tool("Bash", new { command = "sleep 20" }));
        await Until(() => rig.Session.Doing == "running sleep 20", "the doing line");

        rig.Time.Advance(TimeSpan.FromSeconds(4));
        await rig.Session.SendAsync("and also this", TestContext.Current.CancellationToken);

        // On stdin before SendAsync returned: not held until the tool ends or the turn does.
        rig.Process.Written.Length.ShouldBe(2);
        JsonDocument.Parse(rig.Process.Written[1]).RootElement.GetProperty("message").GetProperty("content").GetString().ShouldBe("and also this");
        rig.Session.Queued.ShouldBeEmpty();
        rig.Session.ReadAtNextStep.ShouldBe(["and also this"]);
        rig.Session.Working.ShouldBeTrue();

        // The tool ends and the model's next step reads it and answers it: still the one turn, so no second turn begins.
        rig.Time.Advance(TimeSpan.FromSeconds(16));
        rig.Process.Emit(ToolResult);
        await Until(() => rig.Session.ReadAtNextStep.Count == 0, "the next step to read the message");
        rig.Process.Emit(Delta("Both done."));
        await Until(() => rig.Session.PartialText == "Both done.", "the answer in the same turn");
        rig.Session.Working.ShouldBeTrue();
        rig.Session.Timings.ShouldBeEmpty();

        rig.Process.Emit(Result(20000));
        await Until(() => !rig.Session.Working, "the turn's end");
        var turn = rig.Session.Timings.ShouldHaveSingleItem();
        turn.Length.ShouldBe(TimeSpan.FromSeconds(20));
        turn.Waited.ShouldBe(TimeSpan.Zero);
        rig.Process.Written.Length.ShouldBe(2);
    }

    [Fact]
    public async Task AMessageNoStepReadBeforeTheTurnEndedIsTakenAsTheNextTurnByTheResult()
    {
        await using var rig = new Rig();
        await rig.Session.SendAsync("first", TestContext.Current.CancellationToken);
        rig.Process.Emit(Tool("Bash", new { command = "pilot cards" }));
        await Until(() => rig.Session.Doing == "running pilot cards", "the doing line");

        rig.Time.Advance(TimeSpan.FromSeconds(4));
        await rig.Session.SendAsync("second", TestContext.Current.CancellationToken);

        rig.Session.ReadAtNextStep.ShouldBe(["second"]);
        rig.Session.Working.ShouldBeTrue();
        rig.Process.Written.Length.ShouldBe(2);
        JsonDocument.Parse(rig.Process.Written[1]).RootElement.GetProperty("message").GetProperty("content").GetString().ShouldBe("second");

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Process.Emit(Result(6000));
        await Until(() => rig.Session.ReadAtNextStep.Count == 0 && rig.Session.Timings.Count == 1, "the unread message to be taken");

        rig.Session.Working.ShouldBeTrue();
        rig.Session.PartialText.ShouldBe("");
        rig.Session.Doing.ShouldBeNull();
        rig.Session.Timings[0].Length.ShouldBe(TimeSpan.FromSeconds(6));

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Process.Emit(Delta("Second answer."));
        await Until(() => rig.Session.PartialText == "Second answer.", "the second answer");
        rig.Process.Emit(Result(1000));
        await Until(() => !rig.Session.Working, "the second turn's end");

        var second = rig.Session.Timings[1];
        second.Waited.ShouldBe(TimeSpan.FromSeconds(2));
        second.FirstWord.ShouldBe(TimeSpan.FromSeconds(1));
        rig.Kinds.Count(k => k == StreamChangeKind.Queued).ShouldBe(2);
    }

    [Fact]
    public async Task AMessageSentWhileTheProcessIsStartingIsHeldAndWrittenInOrderOnceItIsUp()
    {
        await using var rig = new Rig(resumeId: "0f0f0f0f-aaaa-bbbb-cccc-111122223333", starting: true);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Session.SendAsync("one", TestContext.Current.CancellationToken);
        await rig.Session.SendAsync("two", TestContext.Current.CancellationToken);

        rig.Session.Starting.ShouldBeTrue();
        rig.Session.Queued.ShouldBe(["one", "two"]);
        rig.Session.ReadAtNextStep.ShouldBeEmpty();
        rig.Session.Working.ShouldBeFalse();
        rig.Process.Written.ShouldBeEmpty();

        rig.Time.Advance(TimeSpan.FromSeconds(3));
        await rig.Session.ReadyAsync(TestContext.Current.CancellationToken);

        // Written, in order, the moment it is up: the first begins the turn (it waited for the start) and the second is read at its step.
        rig.Session.Starting.ShouldBeFalse();
        rig.Session.Queued.ShouldBeEmpty();
        rig.Process.Written.Select(w => JsonDocument.Parse(w).RootElement.GetProperty("message").GetProperty("content").GetString()).ShouldBe(["one", "two"]);
        rig.Session.Working.ShouldBeTrue();
        rig.Session.ReadAtNextStep.ShouldBe(["two"]);

        rig.Process.Emit(Delta("Hi"));
        await Until(() => rig.Session.PartialText == "Hi", "the words");
        rig.Process.Emit(Result(10));
        await Until(() => rig.Session.Timings.Count == 1, "the first turn's end");
        rig.Session.Timings[0].Waited.ShouldBe(TimeSpan.FromSeconds(3));

        // Once up, a message is never held again.
        await rig.Session.ReadyAsync(TestContext.Current.CancellationToken);
        await rig.Session.SendAsync("three", TestContext.Current.CancellationToken);
        rig.Session.Queued.ShouldBeEmpty();
        rig.Process.Written.Length.ShouldBe(3);
    }

    [Fact]
    public async Task InterruptWritesTheControlRequest()
    {
        await using var rig = new Rig();
        await rig.Session.SendAsync("long job", TestContext.Current.CancellationToken);

        await rig.Session.InterruptAsync(TestContext.Current.CancellationToken);

        var line = JsonDocument.Parse(rig.Process.Written[1]).RootElement;
        line.GetProperty("type").GetString().ShouldBe("control_request");
        line.GetProperty("request_id").GetString().ShouldNotBeNullOrEmpty();
        line.GetProperty("request").GetProperty("subtype").GetString().ShouldBe("interrupt");

        rig.Process.Emit("""{"type":"control_response","response":{"subtype":"success","request_id":"x"}}""");
        rig.Process.Emit(Result(10, isError: true));
        await Until(() => !rig.Session.Working, "the interrupted turn's end");
        rig.Session.Timings.ShouldHaveSingleItem().IsError.ShouldBeTrue();
        rig.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task FirstWordAndTurnLengthAreTimedFromTheSend()
    {
        await using var rig = new Rig();
        await rig.Session.SendAsync("hello", TestContext.Current.CancellationToken);
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        rig.Process.Emit(Delta("Hi"));
        await Until(() => rig.Session.PartialText == "Hi", "the first word");
        rig.Time.Advance(TimeSpan.FromMilliseconds(700));
        rig.Process.Emit(Result(1000));
        await Until(() => rig.Session.Timings.Count == 1, "the result");

        var turn = rig.Session.Timings[0];
        turn.FirstWord.ShouldBe(TimeSpan.FromMilliseconds(300));
        turn.Length.ShouldBe(TimeSpan.FromSeconds(1));
        turn.Waited.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task AFakeThatAnswersAtOnceHasItsFirstWordWithinFiveSecondsAndOnlyTheLastTwentyTurnsAreKept()
    {
        await using var rig = new Rig();
        rig.Process.OnWrite = line =>
        {
            if (line.Contains("\"type\":\"user\""))
            {
                rig.Process.Emit(Delta("ok"));
                rig.Process.Emit(Result(5));
            }
        };

        for (var i = 0; i < 25; i++)
        {
            await rig.Session.SendAsync($"message {i}", TestContext.Current.CancellationToken);
            var turns = i + 1;
            await Until(() => !rig.Session.Working && rig.Session.Timings.Count == Math.Min(turns, StreamSession.TimingsKept) && rig.Kinds.Count(k => k == StreamChangeKind.TurnEnded) == turns, $"turn {turns}");
        }

        rig.Session.Timings.Count.ShouldBe(20);
        rig.Session.Timings.ShouldAllBe(t => t.FirstWord != null && t.FirstWord < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnUnexpectedExitIsReportedWithItsCodeAndErrorAndEndsTheSession()
    {
        await using var rig = new Rig();
        await rig.Session.SendAsync("working", TestContext.Current.CancellationToken);
        await rig.Session.SendAsync("held", TestContext.Current.CancellationToken);

        rig.Process.Exit(137, "Killed");

        await Until(() => rig.Session.ExitCode == 137, "the exit");
        var exit = rig.Changes.Last();
        exit.Kind.ShouldBe(StreamChangeKind.Exited);
        exit.ExitCode.ShouldBe(137);
        exit.Requested.ShouldBeFalse();
        exit.Error.ShouldBe("Killed");
        rig.Session.Working.ShouldBeFalse();
        rig.Session.Queued.ShouldBeEmpty();
        rig.Session.ReadAtNextStep.ShouldBeEmpty();
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Session.SendAsync("anyone?", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeKillsTheProcessAndTheExitIsMarkedRequested()
    {
        var rig = new Rig();
        await rig.Session.SendAsync("working", TestContext.Current.CancellationToken);

        await rig.DisposeAsync();

        rig.Process.Killed.ShouldBeTrue();
        var exit = rig.Changes.Last();
        exit.Kind.ShouldBe(StreamChangeKind.Exited);
        exit.Requested.ShouldBeTrue();
    }

    private static string CanUseTool(string requestId, string tool, object input) =>
        JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request = new { subtype = "can_use_tool", tool_name = tool, input, tool_use_id = "toolu_1" } });

    private static readonly object Question = new
    {
        questions = new object[]
        {
            new { question = "Which repo?", header = "Repo", multiSelect = false, options = new object[] { new { label = "tianwen", description = "the sky" }, new { label = "pilot", description = "this one" } } },
        },
    };

    [Fact]
    public async Task ABashPromptIsListedPendingAndAnsweredOnceWithTheToolsInputAndADuplicateIsRefused()
    {
        await using var rig = new Rig();
        await rig.Session.SendAsync("push it", TestContext.Current.CancellationToken);

        rig.Process.Emit(CanUseTool("req-1", "Bash", new { command = "git push origin main", description = "push" }));

        await Until(() => rig.Session.Pending.Count == 1, "the prompt to be listed");
        var ask = rig.Session.Pending.Single();
        (ask.Id, ask.Tool, ask.Summary, ask.IsQuestion, ask.ToolUseId).ShouldBe(("req-1", "Bash", "git push origin main", false, "toolu_1"));
        rig.Kinds.ShouldContain(StreamChangeKind.Asked);
        // Nothing is written for it until somebody answers.
        rig.Process.Written.Length.ShouldBe(1);

        (await rig.Session.AnswerAsync("req-1", AskAnswer.Allowed, TestContext.Current.CancellationToken)).ShouldBeNull();

        rig.Process.Written.Length.ShouldBe(2);
        var line = JsonDocument.Parse(rig.Process.Written[1]).RootElement;
        line.GetProperty("type").GetString().ShouldBe("control_response");
        var response = line.GetProperty("response");
        response.GetProperty("subtype").GetString().ShouldBe("success");
        response.GetProperty("request_id").GetString().ShouldBe("req-1");
        response.GetProperty("response").GetProperty("behavior").GetString().ShouldBe("allow");
        response.GetProperty("response").GetProperty("updatedInput").GetProperty("command").GetString().ShouldBe("git push origin main");
        rig.Session.Pending.ShouldBeEmpty();
        var answered = rig.Changes.Last(c => c.Kind == StreamChangeKind.Answered);
        (answered.Ask!.Id, answered.Outcome).ShouldBe(("req-1", "allowed"));

        // A second answer to the same request, whichever it is, is refused and writes nothing.
        (await rig.Session.AnswerAsync("req-1", AskAnswer.Denied("no"), TestContext.Current.CancellationToken)).ShouldNotBeNull().ShouldContain("not waiting");
        (await rig.Session.AnswerAsync("req-1", AskAnswer.Allowed, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        rig.Process.Written.Length.ShouldBe(2);
    }

    [Fact]
    public async Task ADeniedPromptIsWrittenAsADenyWithTheReason()
    {
        await using var rig = new Rig();
        rig.Process.Emit(CanUseTool("req-2", "Edit", new { file_path = "/repo/a.cs" }));
        await Until(() => rig.Session.Pending.Count == 1, "the prompt");

        (await rig.Session.AnswerAsync("req-2", AskAnswer.Denied("not that file"), TestContext.Current.CancellationToken)).ShouldBeNull();

        var decision = JsonDocument.Parse(rig.Process.Written.Single()).RootElement.GetProperty("response").GetProperty("response");
        decision.GetProperty("behavior").GetString().ShouldBe("deny");
        decision.GetProperty("message").GetString().ShouldBe("not that file");
        rig.Changes.Last(c => c.Kind == StreamChangeKind.Answered).Outcome.ShouldBe("denied: not that file");
    }

    [Fact]
    public async Task AnAskUserQuestionIsListedWithItsOptionsAndItsAnswerIsTheInputWithAnswers()
    {
        await using var rig = new Rig();
        rig.Process.Emit(CanUseTool("req-3", "AskUserQuestion", Question));
        await Until(() => rig.Session.Pending.Count == 1, "the question");

        var ask = rig.Session.Pending.Single();
        ask.IsQuestion.ShouldBeTrue();
        var question = ask.Questions.ShouldNotBeNull().ShouldHaveSingleItem();
        (question.Question, question.Header, question.MultiSelect).ShouldBe(("Which repo?", "Repo", false));
        question.Options.Select(o => (o.Label, o.Description)).ShouldBe([("tianwen", "the sky"), ("pilot", "this one")]);

        // An answer that leaves its question out is refused and the question stays pending.
        (await rig.Session.AnswerAsync("req-3", new AskAnswer(true, Answers: new Dictionary<string, string>()), TestContext.Current.CancellationToken)).ShouldNotBeNull().ShouldContain("Which repo?");
        rig.Session.Pending.Count.ShouldBe(1);
        rig.Process.Written.ShouldBeEmpty();

        (await rig.Session.AnswerAsync("req-3", new AskAnswer(true, Answers: new Dictionary<string, string> { ["Which repo?"] = "pilot" }), TestContext.Current.CancellationToken)).ShouldBeNull();

        var decision = JsonDocument.Parse(rig.Process.Written.Single()).RootElement.GetProperty("response").GetProperty("response");
        decision.GetProperty("behavior").GetString().ShouldBe("allow");
        var input = decision.GetProperty("updatedInput");
        input.GetProperty("questions").GetArrayLength().ShouldBe(1);
        input.GetProperty("answers").GetProperty("Which repo?").GetString().ShouldBe("pilot");
        rig.Session.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAskClaudeCallsOffOrATurnThatEndsLeavesTheListAndAnExitCallsOffWhatIsLeft()
    {
        await using var rig = new Rig();
        await rig.Session.SendAsync("go", TestContext.Current.CancellationToken);
        rig.Process.Emit(CanUseTool("a", "Bash", new { command = "one" }));
        rig.Process.Emit(CanUseTool("b", "Bash", new { command = "two" }));
        rig.Process.Emit(CanUseTool("c", "Bash", new { command = "three" }));
        await Until(() => rig.Session.Pending.Count == 3, "three asks");

        rig.Process.Emit("""{"type":"control_cancel_request","request_id":"b"}""");
        await Until(() => rig.Session.Pending.Count == 2, "the called off ask to leave");
        rig.Session.Pending.Select(p => p.Id).ShouldBe(["a", "c"]);

        rig.Process.Emit(Result(10, isError: true));
        await Until(() => rig.Session.Pending.Count == 0, "the turn's end to leave none");
        rig.Changes.Where(c => c.Kind == StreamChangeKind.Answered).Select(c => (c.Ask!.Id, c.Outcome)).ShouldBe([("b", "cancelled"), ("a", "cancelled"), ("c", "cancelled")]);
        (await rig.Session.AnswerAsync("a", AskAnswer.Allowed, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        rig.Process.Emit(CanUseTool("d", "Bash", new { command = "four" }));
        await Until(() => rig.Session.Pending.Count == 1, "a fourth ask");
        rig.Process.Exit(1);
        await Until(() => rig.Session.ExitCode == 1, "the exit");
        rig.Session.Pending.ShouldBeEmpty();
        rig.Changes.Last(c => c.Kind == StreamChangeKind.Answered).Outcome.ShouldBe("cancelled");
        rig.Process.Written.Length.ShouldBe(1);
    }

    [Fact]
    public async Task AToolTheNodeAllowsItselfIsAnsweredAtOnceAndNeverListedAndAQuestionIsNeverAllowedByIt()
    {
        await using var rig = new Rig();
        rig.Session.AutoAnswer = ask => ask.Tool == "Read" || ask.IsQuestion ? AskAnswer.Allowed : null;

        rig.Process.Emit(CanUseTool("safe", "Read", new { file_path = "/repo/a.cs" }));
        await Until(() => rig.Process.Written.Length == 1, "the node's own answer");
        JsonDocument.Parse(rig.Process.Written[0]).RootElement.GetProperty("response").GetProperty("request_id").GetString().ShouldBe("safe");
        rig.Session.Pending.ShouldBeEmpty();
        rig.Kinds.ShouldNotContain(StreamChangeKind.Asked);

        rig.Process.Emit(CanUseTool("q", "AskUserQuestion", Question));
        rig.Process.Emit(CanUseTool("risky", "Bash", new { command = "rm -rf x" }));
        await Until(() => rig.Session.Pending.Count == 2, "a question and a risky call to wait");
        rig.Session.Pending.Select(p => p.Id).ShouldBe(["q", "risky"]);
        rig.Process.Written.Length.ShouldBe(1);
    }

    [Fact]
    public async Task UnknownLinesAreIgnoredAndLoggedOncePerKind()
    {
        await using var rig = new Rig();

        rig.Process.Emit("""{"type":"mystery","x":1}""");
        rig.Process.Emit("""{"type":"mystery","x":2}""");
        rig.Process.Emit("not json at all");
        rig.Process.Emit("not json either");
        rig.Process.Emit(Delta("still fine"));

        await Until(() => rig.Session.PartialText == "still fine", "the line after the unknown ones");
        rig.Log.Count.ShouldBe(2);
        rig.Log[0].ShouldContain("mystery");
        rig.Log[1].ShouldContain("non-json");
    }
}
