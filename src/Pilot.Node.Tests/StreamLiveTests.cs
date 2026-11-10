using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The stream watchtower's wire against the real <c>claude</c> (PIL855): the node's own <see cref="StreamSession"/>, launcher and
/// <see cref="BrainClient"/>, on haiku. Not a unit test: it starts a real process and talks to the API, so it is out of every default
/// run (category Live) and runs only with <c>PILOT_LIVE_CLAUDE=1</c>. Every wait has a deadline; the process is ended in teardown.
/// </summary>
[Trait("Category", "Live")]
public sealed class StreamLiveTests : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pilot-stream-live-" + Guid.NewGuid().ToString("N")[..8]);
    private StreamSession? _session;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StreamSession Start()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("PILOT_LIVE_CLAUDE") == "1", "Starts the real claude: set PILOT_LIVE_CLAUDE=1 to run it.");
        Directory.CreateDirectory(_folder);
        var start = new WorkerStart("live-watchtower", _folder, null, "", Model: "haiku", Effort: null);
        _session = StreamSession.Start(start, resumeId: null, new ClaudeStreamProcessLauncher());
        return _session;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
        TestDirs.DeleteTree(_folder);
    }

    /// <summary>Completes on the first change <paramref name="match"/> takes, or fails at the deadline.</summary>
    private static async Task<StreamSessionChange> Next(StreamSession session, Func<StreamSessionChange, bool> match, Func<Task> act)
    {
        var seen = new TaskCompletionSource<StreamSessionChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(StreamSessionChange change)
        {
            if (match(change))
            {
                seen.TrySetResult(change);
            }
        }
        session.Changed += Changed;
        try
        {
            await act();
            return await seen.Task.WaitAsync(Deadline, Ct);
        }
        finally
        {
            session.Changed -= Changed;
        }
    }

    [Fact]
    public async Task ATurnStreamsItsWordsAndEnds()
    {
        var session = Start();

        var ended = await Next(session, c => c.Kind == StreamChangeKind.TurnEnded, () => session.SendAsync("Reply with exactly: READY", Ct));

        ended.Turn.ShouldNotBeNull();
        session.PartialText.ShouldContain("READY");
        session.ExitCode.ShouldBeNull();
    }

    [Fact]
    public async Task AQuestionArrivesAsAnAskAndTheChosenOptionReachesClaude()
    {
        var session = Start();

        var asked = await Next(session, c => c.Kind == StreamChangeKind.Asked,
            () => session.SendAsync("Use the AskUserQuestion tool to ask me whether to pick red or blue (options Red and Blue), then say only the colour I picked.", Ct));
        var ask = asked.Ask.ShouldNotBeNull();
        ask.IsQuestion.ShouldBeTrue();
        var question = ask.Questions.ShouldNotBeNull().First();

        var ended = await Next(session, c => c.Kind == StreamChangeKind.TurnEnded,
            async () => (await session.AnswerAsync(ask.Id, new AskAnswer(true, Answers: new Dictionary<string, string> { [question.Question] = "Blue" }), Ct)).ShouldBeNull());

        ended.Turn.ShouldNotBeNull();
        session.PartialText.ShouldContain("blue", Case.Insensitive);
        session.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheBrainAnswersARingVerdictTheNodeCanRead()
    {
        var session = Start();
        await Next(session, c => c.Kind == StreamChangeKind.TurnEnded, () => session.SendAsync("Reply with exactly: READY", Ct));
        var card = new CardRow
        {
            Id = "c1", Bin = CardBin.Ring, Kind = "leg-failed", Subject = "flight/TIA1/parser", Title = "TIA1/parser stopped: the check fails",
            Detail = "The build fails in Parser.cs: CS0103 'tokens' does not exist.", Choices = [], At = DateTimeOffset.UnixEpoch, State = CardState.Open,
        };

        var reply = await new BrainClient(() => session, TimeProvider.System).AskAsync("ring", RingJudge.VerdictRequest(card), Deadline, Ct);

        reply.Answer.GetProperty("ring").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        reply.Answer.GetProperty("reason").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
