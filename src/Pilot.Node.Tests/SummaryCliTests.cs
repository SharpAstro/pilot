using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// <c>pilot summary</c> (#221) against a real node with the model faked: the kept summary printed with its age, tokens and cost,
/// one made when there is none or <c>--fresh</c> asks, one flight's, and the fleet's, whose other nodes' records are fetched from
/// each node. The MCP tool is the same command.
/// </summary>
public sealed class SummaryCliTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync()
    {
        _node = await NodeHarness.StartAsync(Ct);
        _node.State.PutFlight(Flight("TIA1611"));
        _node.State.PutLeg(Leg("TIA1611", "solver", 1, LegState.Flying));
    }

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private static FlightRow Flight(string code) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = 1611, Title = "Plate-solve the guide frame", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"/r/.claude/worktrees/{code}", Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(3),
    };

    private static LegRow Leg(string flight, string name, int order, LegState state) => new()
    {
        Flight = flight, Name = name, Order = order, Goal = "Make the thing", DoneWhen = $"{name} tests pass", State = state,
    };

    private async Task<(int Exit, string Output, string Error)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    private SummaryRow Keep(string scope, string markdown, TimeSpan age, long tokens, double? cost)
    {
        var row = new SummaryRow { Scope = scope, Markdown = markdown, MadeAt = DateTimeOffset.UtcNow - age, Tokens = tokens, Cost = cost };
        _node.State.PutSummary(row);
        return row;
    }

    private const string KeptText = "## Needs you\n- The guide worker waits on a permission prompt.\n\n## Where it is\nSolver is done; guide is flying.";

    [Fact]
    public async Task ASummaryKeptIsPrintedWithItsAgeTokensAndCost_AndNoCallIsMade()
    {
        Keep(SummaryRow.FleetScope, KeptText, TimeSpan.FromHours(3), 5_432, 0.0123);

        var (exit, output, error) = await Pilot("summary", "--output-format", "plain");

        exit.ShouldBe(0, output + error);
        output.ShouldContain("Needs you");
        output.ShouldContain("- The guide worker waits on a permission prompt.");
        output.ShouldContain("Where it is");
        output.ShouldContain("Solver is done; guide is flying.");
        output.ShouldNotContain("##");
        output.ShouldContain("made 3 h ago");
        output.ShouldContain("5,432 tokens");
        output.ShouldContain("$0.0123");
        _node.Summary.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithNoneKeptOneIsMadeAndPrinted_AndTheNextAskPrintsItWithoutAnotherCall()
    {
        _node.State.Summaries().ShouldBeEmpty();

        var (exit, output, error) = await Pilot("summary", "--output-format", "plain");

        exit.ShouldBe(0, output + error);
        _node.Summary.Prompts.ShouldHaveSingleItem().ShouldContain("Flight TIA1611");
        output.ShouldContain("Parser is flying.");
        output.ShouldContain("made just now");
        output.ShouldContain("1,234 tokens");
        output.ShouldContain("$0.0021");
        _node.State.Summaries().ShouldHaveSingleItem().Scope.ShouldBe(SummaryRow.FleetScope);

        var (again, text, _) = await Pilot("summary", "--output-format", "plain");

        again.ShouldBe(0);
        text.ShouldContain("Parser is flying.");
        _node.Summary.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task FreshMakesANewOneEvenWhenOneIsKept()
    {
        Keep(SummaryRow.FleetScope, KeptText, TimeSpan.FromHours(3), 5_432, 0.0123);
        _node.Summary.Answer = new SummaryAnswer("## Needs you\nNothing.\n\n## Where it is\nAll quiet.", 900, 0.0009);

        var (exit, output, error) = await Pilot("summary", "--fresh", "--output-format", "plain");

        exit.ShouldBe(0, output + error);
        _node.Summary.Prompts.ShouldHaveSingleItem();
        output.ShouldContain("All quiet.");
        output.ShouldNotContain("guide worker waits");
        output.ShouldContain("made just now");
        output.ShouldContain("900 tokens");
        _node.State.Summaries().ShouldHaveSingleItem().Markdown.ShouldContain("All quiet.");
    }

    [Fact]
    public async Task AFreshOneThatCannotBeMadeIsRefusedWithWhy_NotAnOldOneInItsPlace()
    {
        Keep(SummaryRow.FleetScope, KeptText, TimeSpan.FromHours(3), 5_432, 0.0123);
        _node.Summary.Fails = new InvalidOperationException("claude failed: overloaded");

        var (exit, output, _) = await Pilot("summary", "--fresh");

        exit.ShouldBe(1);
        output.ShouldContain("refused: summarise node");
        output.ShouldContain("overloaded");
        output.ShouldNotContain("guide worker waits");
    }

    [Fact]
    public async Task AFlightsSummaryIsItsOwn_AndAFlightThatIsNotThereIsRefused()
    {
        Keep(SummaryRow.FleetScope, KeptText, TimeSpan.FromMinutes(5), 100, null);

        var (exit, output, error) = await Pilot("summary", "tia1611", "--output-format", "plain");

        exit.ShouldBe(0, output + error);
        _node.Summary.Prompts.ShouldHaveSingleItem().ShouldContain("the flight TIA1611");
        output.ShouldContain("summary  flight TIA1611");
        output.ShouldContain("Parser is flying.");
        output.ShouldNotContain("guide worker waits");
        _node.State.Summaries().Select(s => s.Scope).ShouldBe([SummaryRow.FleetScope, "TIA1611"]);

        var (none, text, _) = await Pilot("summary", "NOPE1");

        none.ShouldBe(1);
        text.ShouldContain("there is no flight NOPE1");
        _node.Summary.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NothingInPlayIsAnsweredWithNoCallAndSaysSo()
    {
        _node.State.PutFlight(Flight("TIA1611") with { State = FlightState.Landed, LandedAt = DateTimeOffset.UtcNow });

        var (exit, output, error) = await Pilot("summary", "--output-format", "plain");

        exit.ShouldBe(0, output + error);
        _node.Summary.Prompts.ShouldBeEmpty();
        output.ShouldContain("No flights in play.");
        output.ShouldContain("no call was made");
    }

    [Fact]
    public async Task MarkdownKeepsTheTwoSectionsAndPutsTheAgeInItsLastLine()
    {
        Keep(SummaryRow.FleetScope, KeptText, TimeSpan.FromMinutes(12), 5_432, 0.0123);

        var (exit, output, error) = await Pilot("summary", "--output-format", "markdown");

        exit.ShouldBe(0, output + error);
        output.ShouldStartWith("# Summary: this node's flights in play");
        output.ShouldContain("## Needs you");
        output.ShouldContain("## Where it is");
        var last = output.TrimEnd().Split('\n').Last();
        last.ShouldStartWith("_made 12 min ago (");
        last.ShouldEndWith("UTC) · 5,432 tokens · $0.0123_");
    }

    [Fact]
    public async Task TheMcpToolIsTheSameCommand()
    {
        Keep(SummaryRow.FleetScope, KeptText, TimeSpan.FromHours(3), 5_432, 0.0123);
        var server = new McpServer(_node.Folder, node: null, socket: _node.SocketPath);
        var input = string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"summary","arguments":{}}}""") + "\n";
        var written = new StringWriter();

        await server.RunAsync(new StringReader(input), written, Ct);

        var replies = written.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        var tool = replies[1].GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "summary");
        var properties = tool.GetProperty("inputSchema").GetProperty("properties");
        properties.TryGetProperty("flight", out _).ShouldBeTrue();
        properties.GetProperty("fresh").GetProperty("type").GetString().ShouldBe("boolean");
        properties.GetProperty("fleet").GetProperty("type").GetString().ShouldBe("boolean");
        var result = replies[2].GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        text.ShouldContain("Solver is done; guide is flying.");
        text.ShouldContain("made 3 h ago");
        text.ShouldContain("5,432 tokens");
        _node.Summary.Prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARecordsReadAnswersWhatTheNodeHoldsWithNoCallAndKeepsNothing()
    {
        var sent = await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.Records, Target = Verbs.NodeTarget, Who = "tests" }, Ct);

        sent.Success.ShouldBeTrue(sent.Error);
        sent.Response!.Ok.ShouldBeTrue(sent.Response.Error);
        sent.Response.Made.ShouldNotBeNull().ShouldContain("Flight TIA1611: tianwen issue #1611");
        var one = await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.Records, Target = Verbs.FlightTarget("TIA1611"), Who = "tests" }, Ct);
        one.Response!.Made.ShouldNotBeNull().ShouldContain("Records of flight TIA1611");
        var gone = await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.Records, Target = Verbs.FlightTarget("NOPE1"), Who = "tests" }, Ct);
        gone.Response!.Ok.ShouldBeFalse();
        _node.Summary.Prompts.ShouldBeEmpty();
        _node.State.Summaries().ShouldBeEmpty();
    }

    /// <summary>A node of the fleet that answers <c>records node</c> with what it is given, or does not answer.</summary>
    private sealed class FakePeer(string name, string? records, bool unreachable = false, string? refuse = null) : IFleetPeer
    {
        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasGrant { get; init; } = true;

        public List<CommandRequest> Sent { get; } = [];

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken) =>
            unreachable
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(new NodeInfo
                {
                    WireVersion = NodeWire.Version, Version = "0", MachineName = name, MachinePrefix = name, ProcessId = 1, StartedAt = DateTimeOffset.UtcNow,
                    DataFolder = "/data", SocketPath = "/sock", Stream = "s",
                });

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => Task.FromResult<RepoInfo[]?>([]);

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new FleetLoad(null, []));

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken)
        {
            Sent.Add(command);
            return Task.FromResult(new CommandResult
            {
                Verb = command.Verb, Target = command.Target, Who = command.Who, Ok = refuse is null, Error = refuse, Made = refuse is null ? records : null, Seq = 1,
            });
        }
    }

    private sealed class FakeFleet(params IFleetPeer[] peers) : IFleet
    {
        public IReadOnlyList<IFleetPeer> Peers() => peers;
    }

    private const string PeerRecords = "Records of the node as of 2026-10-28 09:00 UTC: 1 flight in play.\n\nFlight DIR7: DIR.Lib issue #7 \"Cache the glyph atlas\".\n  State: flying.";

    private void Fleet(params IFleetPeer[] peers)
    {
        var summaries = (Summaries)_node.App.Services.GetService(typeof(Summaries))!;
        summaries.Fleet = new FakeFleet(peers);
        summaries.Machine = "SE1";
    }

    [Fact]
    public async Task AFleetSummaryCoversTheRecordsOfTheOtherNodesFetchedFromEachOne()
    {
        var de1 = new FakePeer("DE1", PeerRecords);
        var gone = new FakePeer("XX1", null, unreachable: true);
        var refusing = new FakePeer("YY1", null, refuse: "no grant here");
        var nothing = new FakePeer("ZZ1", null);
        var nogrant = new FakePeer("NG1", PeerRecords) { HasGrant = false };
        Fleet(de1, gone, refusing, nothing, nogrant);

        var (exit, output, error) = await Pilot("summary", "--fleet", "--output-format", "plain");

        exit.ShouldBe(0, output + error);
        var prompt = _node.Summary.Prompts.ShouldHaveSingleItem();
        // This node's own records, and the peer's exactly as it answered, each under its node.
        prompt.ShouldContain("=== Node SE1 (this node) ===");
        prompt.ShouldContain("Flight TIA1611: tianwen issue #1611");
        prompt.ShouldContain("=== Node DE1 ===");
        prompt.ShouldContain(PeerRecords);
        // A node that is not there, or says no, is said, not guessed at; one with nothing in play or no grant is left out.
        // (named by its endpoint, as its own prefix was never read.)
        prompt.ShouldContain("=== Node xx1.local:47731 ===");
        prompt.ShouldContain("could not be reached (connection refused)");
        prompt.ShouldContain("=== Node YY1 ===");
        prompt.ShouldContain("it refused (no grant here)");
        prompt.ShouldNotContain("ZZ1");
        prompt.ShouldNotContain("NG1");
        prompt.ShouldContain("on 2 nodes of a fleet");
        // Each node was asked for its records with the read, not for a summary.
        de1.Sent.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Verb.ShouldBe(Verbs.Records),
            r => r.Target.ShouldBe(Verbs.NodeTarget));
        nogrant.Sent.ShouldBeEmpty();
        output.ShouldContain("summary  every node's flights in play");
        output.ShouldContain("Parser is flying.");
        // Kept as its own scope, beside this node's.
        _node.State.Summaries().ShouldHaveSingleItem().Scope.ShouldBe(SummaryRow.AllNodesScope);
    }

    [Fact]
    public async Task TheFleetsKeptSummaryIsNotThisNodesAndTheOtherWayAround()
    {
        Fleet(new FakePeer("DE1", PeerRecords));
        Keep(SummaryRow.FleetScope, KeptText, TimeSpan.FromMinutes(5), 100, null);

        var (exit, output, error) = await Pilot("summary", "--fleet", "--output-format", "plain");

        exit.ShouldBe(0, output + error);
        _node.Summary.Prompts.ShouldHaveSingleItem();
        output.ShouldNotContain("guide worker waits");
        _node.State.Summaries().Select(s => s.Scope).ShouldBe([SummaryRow.FleetScope, SummaryRow.AllNodesScope]);

        var (second, text, _) = await Pilot("summary", "--output-format", "plain");

        second.ShouldBe(0);
        text.ShouldContain("guide worker waits");
        _node.Summary.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFleetWithNothingInPlayAnywhereIsAnsweredWithNoCall()
    {
        _node.State.PutFlight(Flight("TIA1611") with { State = FlightState.Landed, LandedAt = DateTimeOffset.UtcNow });
        Fleet(new FakePeer("DE1", null));

        var (exit, output, error) = await Pilot("summary", "--fleet", "--output-format", "plain");

        exit.ShouldBe(0, output + error);
        _node.Summary.Prompts.ShouldBeEmpty();
        output.ShouldContain("No flights in play.");
    }

    [Fact]
    public async Task FleetAndAFlightTogetherAreAUsageMistake()
    {
        var (exit, _, error) = await Pilot("summary", "TIA1611", "--fleet");

        exit.ShouldBe(2);
        error.ShouldContain("--fleet");
        _node.Summary.Prompts.ShouldBeEmpty();
    }
}
