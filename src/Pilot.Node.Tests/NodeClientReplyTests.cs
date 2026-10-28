using System.Net;
using System.Net.Sockets;
using System.Text;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node reply with no JSON in it (empty, or text) is a failed envelope that says the command may have run, never a throw.
/// </summary>
public sealed class NodeClientReplyTests
{
    private const string NoAnswer = "the node sent no answer; the command may have run: check pilot status";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Serves one raw HTTP reply on a loopback port and returns the client's envelope for a command.</summary>
    private static async Task<ResponseEnvelope<CommandResult>> SendAgainstAsync(int status, string reason, string body, string contentType = "text/plain")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync(Ct);
                var stream = tcp.GetStream();
                // Read the request head and its body so the client's write completes before we answer.
                var buffer = new byte[8192];
                var seen = new StringBuilder();
                while (!seen.ToString().Contains("\r\n\r\n"))
                {
                    var n = await stream.ReadAsync(buffer, Ct);
                    if (n == 0) break;
                    seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                var head = seen.ToString();
                var split = head.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                var length = 0;
                foreach (var line in head[..split].Split("\r\n"))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(line[15..].Trim());
                    }
                }
                var have = Encoding.UTF8.GetByteCount(head[split..]);
                while (have < length)
                {
                    var n = await stream.ReadAsync(buffer, Ct);
                    if (n == 0) break;
                    have += n;
                }
                var payload = Encoding.UTF8.GetBytes(body);
                var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(reply, Ct);
                await stream.WriteAsync(payload, Ct);
                await stream.FlushAsync(Ct);
            }, Ct);

            using var client = NodeClient.Remote($"127.0.0.1:{port}");
            var envelope = await client.SendAsync(new CommandRequest { Verb = Verbs.Say, Target = Verbs.NodeTarget, Args = ["hi"], Who = "test" }, Ct);
            await server;
            return envelope;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Empty200ReplyIsAFailedEnvelopeNotAThrow()
    {
        var envelope = await SendAgainstAsync(200, "OK", "");

        envelope.Success.ShouldBeFalse();
        envelope.StatusCode.ShouldBe(200);
        envelope.Error.ShouldContain("200");
        envelope.Error.ShouldContain(NoAnswer);
    }

    [Fact]
    public async Task Empty500ReplyIsAFailedEnvelopeNotAThrow()
    {
        var envelope = await SendAgainstAsync(500, "Internal Server Error", "");

        envelope.Success.ShouldBeFalse();
        envelope.StatusCode.ShouldBe(500);
        envelope.Error.ShouldContain("500");
        envelope.Error.ShouldContain(NoAnswer);
    }

    [Fact]
    public async Task NonJsonReplyIsAFailedEnvelopeWithAnExcerpt()
    {
        var envelope = await SendAgainstAsync(200, "OK", "<html>Bad gateway</html>", "text/html");

        envelope.Success.ShouldBeFalse();
        envelope.StatusCode.ShouldBe(200);
        envelope.Error.ShouldContain("200");
        envelope.Error.ShouldContain(NoAnswer);
        envelope.Error.ShouldContain("Bad gateway");
    }
}
