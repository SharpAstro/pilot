using Pilot.Node.Flights;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

public sealed class SessionStartLogTests : IDisposable
{
    private readonly string _jobs = Path.Combine(Path.GetTempPath(), "pilot-startlog-" + Guid.NewGuid().ToString("N"));

    private readonly StringWriter _log = new();

    private readonly FakeWorkers _inner = new();

    public SessionStartLogTests() => Directory.CreateDirectory(_jobs);

    public void Dispose() => Directory.Delete(_jobs, recursive: true);

    private LoggingWorkerLauncher Launcher() => new(_inner, _log, new ClaudeJobState(_jobs), TimeProvider.System);

    private void Record(string job, string json)
    {
        Directory.CreateDirectory(Path.Combine(_jobs, job));
        File.WriteAllText(Path.Combine(_jobs, job, "state.json"), json);
    }

    private static WorkerStart Watchtower() => new("de1-watchtower", @"C:\w", null, new string('x', 123), "sonnet", "low");

    [Fact]
    public async Task A_start_logs_role_job_model_and_brief_length()
    {
        var job = await Launcher().StartAsync(Watchtower(), TestContext.Current.CancellationToken);

        var line = _log.ToString();
        line.ShouldContain("start: role=watchtower name=de1-watchtower");
        line.ShouldContain($"job={job}");
        line.ShouldContain("model=sonnet");
        line.ShouldContain("brief=123 chars");
    }

    [Fact]
    public async Task A_refused_start_logs_its_reason_and_still_throws()
    {
        _inner.Fail = new FlightException("claude --bg did not start de1-watchtower (1): boom");

        await Should.ThrowAsync<FlightException>(() => Launcher().StartAsync(Watchtower(), TestContext.Current.CancellationToken));

        _log.ToString().ShouldContain("start refused: role=watchtower name=de1-watchtower claude --bg did not start de1-watchtower (1): boom");
    }

    [Fact]
    public async Task A_failed_record_is_logged_once_across_looks()
    {
        var launcher = Launcher();
        var job = await launcher.StartAsync(Watchtower(), TestContext.Current.CancellationToken);
        Record(job, """{"state":"failed","detail":"ENAMETOOLONG: name too long, uv_spawn"}""");

        await launcher.LookAsync();
        await launcher.LookAsync();
        await launcher.LookAsync();

        var failures = _log.ToString().Split('\n').Where(l => l.StartsWith("start failed:", StringComparison.Ordinal)).ToList();
        failures.Count.ShouldBe(1);
        failures[0].ShouldContain($"role=watchtower name=de1-watchtower job={job} state=failed detail=ENAMETOOLONG: name too long, uv_spawn");
        launcher.FailureOf(job)!.Detail.ShouldBe("ENAMETOOLONG: name too long, uv_spawn");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("""{"state":"running"}""")]
    public async Task An_absent_malformed_or_running_record_logs_nothing(string? json)
    {
        var launcher = Launcher();
        var job = await launcher.StartAsync(Watchtower(), TestContext.Current.CancellationToken);
        if (json is not null)
        {
            Record(job, json);
        }

        await launcher.LookAsync();

        _log.ToString().ShouldNotContain("start failed");
        launcher.FailureOf(job).ShouldBeNull();
    }
}
