using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// #1206 (DE1 PIL1191): a say resumed a worker whose process still ran, and the copy came up beside it on another effort and renamed itself.
/// A resume now confirms the old process is gone first, and a copy on recorded options keeps the recorded name.
/// </summary>
public sealed class LiveWorkerResumeTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Recent => DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();

    private static readonly WorkerStart Start = new WorkerStart("se1-pil1191-leg", "/r/pilot", "pil1191", "b", "sonnet", "medium");

    private readonly TempProfile _profile = new TempProfile();
    private readonly FakeClaudeAgents _agents = new FakeClaudeAgents();
    private readonly NodeState _state = new NodeState(new InMemoryJournal(), TimeProvider.System);
    private readonly StartOptionsBook _book = new StartOptionsBook();
    private readonly SessionTracker _tracker;
    private readonly FakeSessionDelivery _delivery;

    public LiveWorkerResumeTests()
    {
        _tracker = new SessionTracker(_state, _agents, _profile.Options(), () => false, TimeProvider.System, NullLogger<SessionTracker>.Instance)
        {
            Starts = _book,
        };
        _delivery = new FakeSessionDelivery(_agents);
        _book.Record(new SessionStartOptions(Start.Name, AgentRows.SessionB[..8], [.. ClaudeWorkerLauncher.SessionArguments(Start)]));
    }

    public void Dispose() => _profile.Dispose();

    private string Cwd => _profile.Folder.Replace("\\", "\\\\", StringComparison.Ordinal);

    /// <summary>An idle worker the transcript says runs on opus at xhigh (the start says sonnet, medium) with process <paramref name="pid"/>.</summary>
    private async Task<SessionRow> IdleMismatchedAsync(int pid)
    {
        _profile.Append(_profile.TranscriptPath(AgentRows.SessionB), Lines.Assistant("m1", input: 10, output: 100, model: "claude-opus-5-5", effort: "xhigh"));
        _agents.Json = AgentRows.List(AgentRows.Background(AgentRows.SessionB, Start.Name, "working", pid: pid, status: "idle", cwd: Cwd, startedAt: Recent));
        await _tracker.PollAsync(Ct);
        return _state.Sessions().Single();
    }

    private SessionMessenger Messenger(Func<int, TimeSpan, CancellationToken, Task<bool>> waitGone, StringWriter? log = null) =>
        new SessionMessenger(_state, _tracker, _delivery, log ?? new StringWriter(), _book) { WaitGone = waitGone, GoneWait = TimeSpan.FromMilliseconds(300) };

    private static CommandRequest Say(SessionRow row) =>
        new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = ["go on"], Who = "tests" };

    [Fact]
    public async Task ASayToAnIdleWorkerWhoseStopDidNotTakeIsRelayedAndNeverResumed()
    {
        var row = await IdleMismatchedAsync(4242);
        var seen = new List<int>();
        var messenger = Messenger((pid, _, _) => { seen.Add(pid); return Task.FromResult(false); });

        var result = await messenger.SayAsync(Say(row), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        seen.ShouldBe([4242]);
        _delivery.Resumes.ShouldBeEmpty();
        _delivery.Calls.ShouldNotContain(c => c.StartsWith("resume ", StringComparison.Ordinal));
        _delivery.Relays.ShouldHaveSingleItem().DirectOnly.ShouldBeTrue();
        _state.Sessions().Count(r => r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ShouldBe(1);
    }

    [Fact]
    public async Task AMismatchedWorkersCopyKeepsTheRecordedNameAndStartsOnlyAfterTheOldPidIsGone()
    {
        var row = await IdleMismatchedAsync(4242);
        IReadOnlyList<string>? callsWhenChecked = null;
        var log = new StringWriter();
        var messenger = Messenger((_, _, _) => { callsWhenChecked = _delivery.Calls; return Task.FromResult(true); }, log);

        var result = await messenger.SayAsync(Say(row), Ct);

        result.Ok.ShouldBeTrue(result.Error);
        callsWhenChecked.ShouldNotBeNull().ShouldBe([$"stop {AgentRows.SessionB}"]);
        var request = _delivery.Resumes.ShouldHaveSingleItem();
        request.Name.ShouldBe(Start.Name);
        var args = ClaudeCliDelivery.ResumeArguments(request);
        args.ShouldBe(["--bg", "--resume", AgentRows.SessionB, .. ClaudeWorkerLauncher.SessionArguments(Start)]);
        args.Count(a => a is "-n" or "--name").ShouldBe(1);
        log.ToString().ShouldContain(Start.Name);
    }

    [Fact]
    public void ACopyOnOptionsThatLostTheNameStillCarriesTheRecordedOne()
    {
        var request = new ResumeRequest(AgentRows.SessionB, Start.Name, "/w", "text", StartOptions: ["--model", "sonnet"]);

        ClaudeCliDelivery.ResumeArguments(request).ShouldBe(["--bg", "--resume", AgentRows.SessionB, "--model", "sonnet", "--name", Start.Name]);
    }

    // Slow: the issue asks to test with a real second process. A real sleeping child stands in for the live worker's pid (it never spins,
    // and it is ended in the finally); the real exit-wait is what refuses the resume.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ASayUnderAnEffortMismatchLeavesOneLiveSessionWhenARealProcessStillRuns()
    {
        var shell = OperatingSystem.IsWindows() ? "pwsh" : "sleep";
        var args = OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 120" } : ["120"];
        using var child = Process.Start(new ProcessStartInfo(shell, args) { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var row = await IdleMismatchedAsync(child.Id);
            var messenger = new SessionMessenger(_state, _tracker, _delivery, new StringWriter(), _book)
            {
                WaitGone = SessionMessenger.WaitProcessGoneAsync,
                GoneWait = TimeSpan.FromMilliseconds(500),
            };

            var result = await messenger.SayAsync(Say(row), Ct);

            result.Ok.ShouldBeTrue(result.Error);
            _delivery.Resumes.ShouldBeEmpty();
            _state.Sessions().Count(r => r.State is SessionState.Working or SessionState.Waiting or SessionState.Idle).ShouldBe(1);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
        }
    }
}
