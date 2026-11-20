using Microsoft.Extensions.Logging;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The live usage read (#1193): the answer parsed, the token's rules, the pacing, and the file as the fallback. No network.</summary>
public sealed class UsageApiTests : IDisposable
{
    private const string Token = "sk-ant-oat01-SECRET-TOKEN";
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Answer = """{"five_hour":{"utilization":15.0,"resets_at":"2026-11-20T15:10:00+00:00"},"seven_day":{"utilization":44.5,"resets_at":"2026-11-24T10:00:00Z"}}""";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2ua-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualTime _time = new(Now);
    private readonly FakeHandler _handler = new();
    private readonly CaptureLog _log = new();

    public UsageApiTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TestDirs.DeleteTree(_folder);

    private string Credentials => Path.Combine(_folder, ".credentials.json");

    private void WriteCredentials(DateTimeOffset expires) =>
        File.WriteAllText(Credentials, $$$"""{"claudeAiOauth":{"accessToken":"{{{Token}}}","refreshToken":"r-secret","expiresAt":{{{expires.ToUnixTimeMilliseconds()}}}}}""");

    private UsageApi Api() => new(new HttpClient(_handler), Credentials, _time, _log);

    [Fact]
    public async Task TheAnswerIsParsedIntoBothWindowsAndTheRequestIsAsUsageMakesIt()
    {
        WriteCredentials(Now.AddHours(1));
        _handler.Reply(System.Net.HttpStatusCode.OK, Answer);

        var row = (await Api().ReadAsync(default)).ShouldNotBeNull();

        row.FiveHour.ShouldBe(new UsageWindow { UsedPercent = 15, ResetsAt = new DateTimeOffset(2026, 11, 20, 15, 10, 0, TimeSpan.Zero) });
        row.SevenDay.ShouldBe(new UsageWindow { UsedPercent = 44.5, ResetsAt = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero) });
        row.ReadAt.ShouldBe(Now);
        row.Source.ShouldBe("server");
        var request = _handler.Requests.ShouldHaveSingleItem();
        request.Url.ShouldBe("https://api.anthropic.com/api/oauth/usage");
        request.Beta.ShouldBe("oauth-2025-04-20");
        request.Authorization.ShouldBe("Bearer " + Token);
    }

    [Fact]
    public async Task AnExpiredTokenIsSkippedWithNoRequestAndNeverRefreshed()
    {
        WriteCredentials(Now.AddSeconds(-1));

        (await Api().ReadAsync(default)).ShouldBeNull();

        _handler.Requests.ShouldBeEmpty();
        // Nothing was paced either: a fresh login is read at once.
        var api = Api();
        (await api.ReadAsync(default)).ShouldBeNull();
        WriteCredentials(Now.AddHours(1));
        _handler.Reply(System.Net.HttpStatusCode.OK, Answer);
        (await api.ReadAsync(default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AMissingCredentialsFileIsNoRequestAndNoFailure()
    {
        var api = Api();

        (await api.ReadAsync(default)).ShouldBeNull();

        _handler.Requests.ShouldBeEmpty();
        _log.Lines.ShouldBeEmpty();
        WriteCredentials(Now.AddHours(1));
        _handler.Reply(System.Net.HttpStatusCode.OK, Answer);
        (await api.ReadAsync(default)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AtMostOneRequestPerTwoMinutes()
    {
        WriteCredentials(Now.AddHours(1));
        _handler.Reply(System.Net.HttpStatusCode.OK, Answer);
        var api = Api();

        (await api.ReadAsync(default)).ShouldNotBeNull();
        _time.Advance(TimeSpan.FromSeconds(119));
        (await api.ReadAsync(default)).ShouldBeNull();
        _handler.Requests.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        (await api.ReadAsync(default)).ShouldNotBeNull();
        _handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TenMinutesAfterAFailureAndTheFailureIsLoggedByStatusCodeOnly()
    {
        WriteCredentials(Now.AddHours(1));
        _handler.Reply(System.Net.HttpStatusCode.TooManyRequests, "rate limited for " + Token);
        var api = Api();

        (await api.ReadAsync(default)).ShouldBeNull();
        _time.Advance(TimeSpan.FromMinutes(9));
        (await api.ReadAsync(default)).ShouldBeNull();
        _handler.Requests.Count.ShouldBe(1);

        _handler.Reply(System.Net.HttpStatusCode.OK, Answer);
        _time.Advance(TimeSpan.FromMinutes(1));
        (await api.ReadAsync(default)).ShouldNotBeNull();
        _handler.Requests.Count.ShouldBe(2);
        _log.Lines.ShouldHaveSingleItem().ShouldContain("429");
    }

    [Fact]
    public async Task TheTokenIsInNoLogLineWhateverFails()
    {
        WriteCredentials(Now.AddHours(1));
        var api = Api();
        _handler.Reply(System.Net.HttpStatusCode.Unauthorized, "bad " + Token);
        await api.ReadAsync(default);
        _time.Advance(TimeSpan.FromMinutes(10));
        _handler.Throw(new HttpRequestException("connect failed with " + Token));
        await api.ReadAsync(default);
        _time.Advance(TimeSpan.FromMinutes(10));
        _handler.Reply(System.Net.HttpStatusCode.OK, "not json " + Token);
        await api.ReadAsync(default);

        _log.Lines.Count.ShouldBe(3);
        _log.Lines.ShouldAllBe(l => !l.Contains(Token) && !l.Contains("SECRET"));
    }

    [Fact]
    public async Task TheTokenIsReadFromDiskForEachRequestAndHeldInNoField()
    {
        WriteCredentials(Now.AddHours(1));
        _handler.Reply(System.Net.HttpStatusCode.OK, Answer);
        var api = Api();
        await api.ReadAsync(default);

        File.WriteAllText(Credentials, $$$"""{"claudeAiOauth":{"accessToken":"second-token","expiresAt":{{{Now.AddHours(1).ToUnixTimeMilliseconds()}}}}}""");
        _time.Advance(UsageApi.Every);
        await api.ReadAsync(default);

        _handler.Requests[1].Authorization.ShouldBe("Bearer second-token");
        typeof(UsageApi).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Where(f => f.FieldType == typeof(string)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailureFallsBackToTheStatusLinesFile()
    {
        var state = new NodeState(new InMemoryJournal(), _time);
        var file = Path.Combine(_folder, "rate-limits.json");
        File.WriteAllText(file, $$$"""{"rate_limits":{"five_hour":{"used_percentage":33,"resets_at":{{{Now.AddHours(2).ToUnixTimeSeconds()}}}}},"written_at":{{{Now.ToUnixTimeSeconds()}}}}""");
        WriteCredentials(Now.AddHours(1));
        _handler.Reply(System.Net.HttpStatusCode.InternalServerError, "");
        var api = Api();
        var reader = new UsageReader(state, file, _time) { Api = api };

        reader.Read(await api.ReadAsync(default));

        var usage = state.Usage().ShouldNotBeNull();
        usage.FiveHour!.UsedPercent.ShouldBe(33);
        usage.Source.ShouldBe("status line");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private System.Net.HttpStatusCode _status = System.Net.HttpStatusCode.OK;
        private string _body = "";
        private Exception? _throw;

        public List<(string Url, string? Beta, string? Authorization)> Requests { get; } = [];

        public void Reply(System.Net.HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
            _throw = null;
        }

        public void Throw(Exception ex) => _throw = ex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.TryGetValues("anthropic-beta", out var beta) ? beta.Single() : null, request.Headers.Authorization?.ToString()));
            if (_throw is not null)
            {
                throw _throw;
            }
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    private sealed class CaptureLog : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
