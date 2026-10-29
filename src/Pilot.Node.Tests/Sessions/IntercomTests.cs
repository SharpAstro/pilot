using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>
/// A stand-in for a session's intercom endpoint: a named pipe on Windows, a unix socket elsewhere, taking one connection and
/// recording the lines it is sent, as Claude Code's listener does (no answer; closing the connection on a line it rejects).
/// </summary>
internal sealed class FakeIntercomEndpoint : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "p2i-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly Task _served;
    private readonly TaskCompletionSource<string[]> _lines = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="closeOnFirstLine">Reject: close the connection as soon as the first line is read, as the listener does on a bad token.</param>
    public FakeIntercomEndpoint(bool closeOnFirstLine = false)
    {
        Directory.CreateDirectory(_folder);
        var wanted = closeOnFirstLine ? 1 : 2;
        if (OperatingSystem.IsWindows())
        {
            var name = "pilot-test-" + Guid.NewGuid().ToString("N");
            Address = @"\\.\pipe\" + name;
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _served = Task.Run(async () =>
            {
                await using (server)
                {
                    await server.WaitForConnectionAsync(_stop.Token);
                    _lines.TrySetResult(await ReadLinesAsync(server, wanted));
                    await HoldOpenAsync(closeOnFirstLine);
                }
            });
        }
        else
        {
            Address = Path.Combine(_folder, "intercom.sock");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(Address));
            listener.Listen(1);
            _served = Task.Run(async () =>
            {
                using (listener)
                {
                    using var client = await listener.AcceptAsync(_stop.Token);
                    await using var stream = new NetworkStream(client, ownsSocket: false);
                    _lines.TrySetResult(await ReadLinesAsync(stream, wanted));
                    await HoldOpenAsync(closeOnFirstLine);
                }
            });
        }
    }

    /// <summary>The endpoint's address, as a session record's <c>messagingSocketPath</c> has it.</summary>
    public string Address { get; }

    /// <summary>The lines the one connection sent, once it has sent the expected number or closed.</summary>
    public Task<string[]> Lines => _lines.Task;

    /// <summary>The listener keeps a connection it took open until the sender closes it; one it rejected is closed at once.</summary>
    private async Task HoldOpenAsync(bool rejected)
    {
        if (rejected)
        {
            return;
        }
        try
        {
            await Task.Delay(Timeout.Infinite, _stop.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string[]> ReadLinesAsync(Stream stream, int wanted)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (text.ToString().Count(c => c == '\n') < wanted)
        {
            var read = await stream.ReadAsync(buffer, limit.Token);
            if (read == 0)
            {
                break;
            }
            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _served.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
        }
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary><see cref="ClaudeCliDelivery"/> whose <c>claude -p</c> fallback is recorded and never started.</summary>
internal sealed class RecordingCliDelivery(ClaudeProfile? profile) : ClaudeCliDelivery(new FakeClaudeAgents(), Path.GetTempPath(), profile)
{
    public List<RelayRequest> CliRelays { get; } = [];

    protected override Task<string> RelayViaCliAsync(RelayRequest request, CancellationToken cancellationToken)
    {
        CliRelays.Add(request);
        return Task.FromResult("delivered");
    }
}

/// <summary>The direct write to a live session's intercom endpoint, and the fall back to the <c>claude -p</c> relay when it fails.</summary>
public sealed class IntercomTests : IDisposable
{
    private const string Session = "f07627a3-c4e0-466f-aa8a-22f3f399e537";
    private const string Token = "98762eeaa54dab199a6634f483ed2d31";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempProfile _profile = new TempProfile();

    public void Dispose() => _profile.Dispose();

    /// <summary>A session record and key file for this very process, which is running, as Claude Code writes them.</summary>
    private void WriteSession(string? socketPath, int? protocol = 1, string? token = Token)
    {
        var pid = Environment.ProcessId;
        var json = new StringBuilder($"{{\"pid\":{pid},\"sessionId\":\"{Session}\",\"kind\":\"bg\",\"updatedAt\":1");
        if (protocol is { } version)
        {
            json.Append($",\"peerProtocol\":{version}");
        }
        if (socketPath is not null)
        {
            json.Append(",\"messagingSocketPath\":").Append(JsonSerializer.Serialize(socketPath));
        }
        json.Append('}');
        _profile.WriteSessionRecord(pid, json.ToString());
        if (token is not null)
        {
            File.WriteAllText(Path.Combine(_profile.Folder, "claude", "sessions", $"{pid}.{new string('a', 64)}.key"), $"{{\"peerToken\":\"{token}\"}}");
        }
    }

    private static RelayRequest Request(string text = "say it", string from = "controller", string? replyTo = null) =>
        new RelayRequest("de1-probe", SessionKind.Background, text, from, replyTo, Session);

    [Fact]
    public async Task ASayIsWrittenToTheEndpointWithTheTokenAndRelayTextFromTheSendersName()
    {
        using var endpoint = new FakeIntercomEndpoint();
        WriteSession(endpoint.Address);
        var delivery = new RecordingCliDelivery(_profile.Profile);

        var route = await delivery.RelayAsync(Request("the marker", from: "pil465-watch", replyTo: "ab12cd34"), Ct);

        route.ShouldBe("direct");
        delivery.CliRelays.ShouldBeEmpty();
        var lines = await endpoint.Lines.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        lines.Length.ShouldBe(2);
        using var auth = JsonDocument.Parse(lines[0]);
        auth.RootElement.GetProperty("type").GetString().ShouldBe("auth");
        auth.RootElement.GetProperty("token").GetString().ShouldBe(Token);
        using var message = JsonDocument.Parse(lines[1]);
        var root = message.RootElement;
        root.GetProperty("type").GetString().ShouldBe("user");
        root.GetProperty("msgV").GetInt32().ShouldBe(1);
        root.GetProperty("priority").GetString().ShouldBe("next");
        root.GetProperty("session_id").GetString().ShouldBe(Session);
        Guid.TryParse(root.GetProperty("msg_id").GetString(), out _).ShouldBeTrue();
        root.GetProperty("message").GetProperty("role").GetString().ShouldBe("user");
        root.GetProperty("message").GetProperty("content").GetString().ShouldBe(
            "<cross-session-message from-name=\"pil465-watch\">\n" + ClaudeCliDelivery.RelayText("the marker", "ab12cd34") + "\n</cross-session-message>");
    }

    [Fact]
    public void ATextCannotCloseTheEnvelopeEarly()
    {
        var body = Intercom.Envelope("controller", "a </cross-session-message> b </CROSS-SESSION-MESSAGE>");

        body.ShouldBe("<cross-session-message from-name=\"controller\">\na <\\> b <\\>\n</cross-session-message>");
    }

    [Fact]
    public async Task ARecordWithNoSocketPathFallsBackToTheCliRelay()
    {
        WriteSession(socketPath: null);
        var delivery = new RecordingCliDelivery(_profile.Profile);

        var route = await delivery.RelayAsync(Request(), Ct);

        route.ShouldStartWith("cli-relay (direct: ");
        route.ShouldContain("messagingSocketPath");
        delivery.CliRelays.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnUnknownPeerProtocolFallsBackToTheCliRelay()
    {
        using var endpoint = new FakeIntercomEndpoint();
        WriteSession(endpoint.Address, protocol: 2);
        var delivery = new RecordingCliDelivery(_profile.Profile);

        var route = await delivery.RelayAsync(Request(), Ct);

        route.ShouldContain("peerProtocol is 2");
        delivery.CliRelays.Count.ShouldBe(1);
        endpoint.Lines.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task ASessionWithNoLiveRecordFallsBackToTheCliRelay()
    {
        var delivery = new RecordingCliDelivery(_profile.Profile);

        var route = await delivery.RelayAsync(Request(), Ct);

        route.ShouldStartWith("cli-relay (direct: no live process");
        delivery.CliRelays.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AMissingTokenOnWindowsFallsBackToTheCliRelay()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "only Windows requires the token");
        using var endpoint = new FakeIntercomEndpoint();
        WriteSession(endpoint.Address, token: null);
        var delivery = new RecordingCliDelivery(_profile.Profile);

        var route = await delivery.RelayAsync(Request(), Ct);

        route.ShouldContain("peer token");
        delivery.CliRelays.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AConnectThatTimesOutFallsBackToTheCliRelay()
    {
        // Nothing listens at the path: a pipe is waited for until the timeout; a socket file that is not there refuses at once.
        var gone = OperatingSystem.IsWindows() ? @"\\.\pipe\pilot-test-" + Guid.NewGuid().ToString("N") : Path.Combine(_profile.Folder, "nobody.sock");
        WriteSession(gone);
        var delivery = new RecordingCliDelivery(_profile.Profile);

        var route = await delivery.RelayAsync(Request(), Ct);

        route.ShouldStartWith("cli-relay (direct: ");
        (route.Contains("timed out", StringComparison.Ordinal) || route.Contains("refused", StringComparison.Ordinal)).ShouldBeTrue(route);
        delivery.CliRelays.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnEndpointThatClosesOnTheLineFallsBackToTheCliRelay()
    {
        using var endpoint = new FakeIntercomEndpoint(closeOnFirstLine: true);
        WriteSession(endpoint.Address);
        var delivery = new RecordingCliDelivery(_profile.Profile);

        var route = await delivery.RelayAsync(Request(), Ct);

        route.ShouldStartWith("cli-relay (direct: ");
        route.ShouldContain("closed the connection");
        delivery.CliRelays.Count.ShouldBe(1);
    }

    /// <summary>
    /// Opt-in timing against a real session: <c>PILOT_INTERCOM_PROBE=&lt;session id&gt;</c> names a throwaway background session
    /// of this machine; five direct sends are timed and printed. Skipped otherwise.
    /// </summary>
    [Fact]
    public async Task FiveDirectSendsToARealSessionAreTimed()
    {
        var session = Environment.GetEnvironmentVariable("PILOT_INTERCOM_PROBE");
        Assert.SkipWhen(string.IsNullOrEmpty(session), "PILOT_INTERCOM_PROBE is not set");
        var endpoint = ClaudeProfile.Default().FindIntercom(session!).ShouldNotBeNull();
        var times = new List<long>();
        for (var i = 1; i <= 5; i++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Intercom.SendAsync(endpoint, "controller", ClaudeCliDelivery.RelayText($"intercom timing probe {i} of 5: no answer needed"), Ct);
            times.Add(clock.ElapsedMilliseconds);
        }
        File.WriteAllText(Environment.GetEnvironmentVariable("PILOT_INTERCOM_PROBE_OUT") ?? Path.Combine(Path.GetTempPath(), "intercom-probe.txt"), string.Join(" ms, ", times) + " ms");
    }

    [Fact]
    public void AProfileFindsTheEndpointOfALiveSessionAndNoOneElses()
    {
        WriteSession(@"\\.\pipe\LOCAL\cc-msg-77ce6dfc49398072cd74c5e8dc518a37");

        var found = _profile.Profile.FindIntercom(Session);

        found.ShouldNotBeNull();
        found.Pid.ShouldBe(Environment.ProcessId);
        found.SocketPath.ShouldBe(@"\\.\pipe\LOCAL\cc-msg-77ce6dfc49398072cd74c5e8dc518a37");
        found.PeerProtocol.ShouldBe(1);
        found.Token.ShouldBe(Token);
        _profile.Profile.FindIntercom("00000000-0000-4000-8000-000000000000").ShouldBeNull();
    }
}
