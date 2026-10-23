using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Sessions;
using Pilot.Node.Tests.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Which sessions say-all picks, over rows made by hand.</summary>
public sealed class SayAllPickerTests
{
    private static SessionRow Row(string id, string name, SessionRole role = SessionRole.Worker, SessionState state = SessionState.Working,
        string? flight = null, string? repo = null, string cwd = "C:\\work\\other", bool archived = false) => new()
    {
        Id = id,
        SessionId = id + "-0000-4000-8000-000000000000",
        Name = name,
        Kind = SessionKind.Background,
        Cwd = cwd,
        Repo = repo,
        Role = role,
        Flight = flight,
        ContextTokens = 0,
        ContextWindow = 200_000,
        State = state,
        SpendWeighted = 0,
        StartedAt = DateTimeOffset.UnixEpoch,
        Archived = archived,
    };

    private static string[] Ids(IEnumerable<SessionRow> rows, SayAllFilter filter) => [.. SayAllFilter.Pick(rows, filter).Select(r => r.Id)];

    private static readonly SessionRow[] Listing =
    [
        Row("w1", "du1-pil1-node", flight: "PIL1", repo: "pilot"),
        Row("w2", "du1-pil1-gui", state: SessionState.Waiting, flight: "PIL1", repo: "pilot"),
        Row("w3", "du1-tia2-parser", flight: "TIA2", repo: "tianwen"),
        Row("p1", "du1-pil1-plan", SessionRole.Planner, flight: "PIL1", repo: "pilot"),
        Row("r1", "du1-pil1-review", SessionRole.Reviewer, flight: "PIL1", repo: "pilot"),
        Row("v1", "du1-pil1-node-validator", flight: "PIL1", repo: "pilot"),
        Row("t1", "du1-watchtower", SessionRole.Watchtower),
        Row("i1", "du1-pil1-idle", state: SessionState.Idle, flight: "PIL1", repo: "pilot"),
        Row("e1", "du1-pil1-ended", state: SessionState.Ended, flight: "PIL1", repo: "pilot"),
        Row("f1", "du1-pil1-failed", state: SessionState.Failed, flight: "PIL1", repo: "pilot"),
        Row("a1", "du1-pil1-archived", state: SessionState.Ended, flight: "PIL1", repo: "pilot", archived: true),
    ];

    [Fact]
    public void ByDefaultTheActiveOnesAreWorkingAndWaitingAndNeitherValidatorsNorTheWatchtower() =>
        Ids(Listing, new SayAllFilter()).ShouldBe(["w1", "w2", "w3", "p1", "r1"]);

    [Fact]
    public void AllAddsIdleButNeverEndedFailedOrArchived()
    {
        var picked = Ids(Listing, new SayAllFilter(All: true));
        picked.ShouldBe(["w1", "w2", "w3", "p1", "r1", "i1"]);
        picked.ShouldNotContain("e1");
        picked.ShouldNotContain("f1");
        picked.ShouldNotContain("a1");
    }

    [Fact]
    public void ARoleIsNamedByItsRoleNameAnyCase() =>
        Ids(Listing, new SayAllFilter(Roles: ["Worker", "planner"])).ShouldBe(["w1", "w2", "w3", "p1"]);

    [Fact]
    public void AValidatorIsPickedOnlyWhenTheRoleValidatorNamesIt_AndThenItIsNoWorker()
    {
        Ids(Listing, new SayAllFilter(Roles: ["validator"])).ShouldBe(["v1"]);
        Ids(Listing, new SayAllFilter(Roles: ["worker"])).ShouldNotContain("v1");
        Ids(Listing, new SayAllFilter(Roles: ["worker", "validator"])).ShouldContain("v1");
    }

    [Fact]
    public void TheWatchtowerIsPickedOnlyWhenTheRoleWatchtowerNamesIt()
    {
        Ids(Listing, new SayAllFilter()).ShouldNotContain("t1");
        Ids(Listing, new SayAllFilter(All: true, Roles: ["worker", "planner", "reviewer"])).ShouldNotContain("t1");
        Ids(Listing, new SayAllFilter(Roles: ["watchtower"])).ShouldBe(["t1"]);
    }

    [Fact]
    public void AFlightMatchesInAnyCaseAndSeveralFlightsAreAnyOfThem()
    {
        Ids(Listing, new SayAllFilter(Flights: ["tia2"])).ShouldBe(["w3"]);
        Ids(Listing, new SayAllFilter(Flights: ["TIA2", "pil1"])).ShouldBe(["w1", "w2", "w3", "p1", "r1"]);
        Ids(Listing, new SayAllFilter(Flights: ["NONE1"])).ShouldBeEmpty();
    }

    [Fact]
    public void ARepoIsMatchedByNameInAnyCase() =>
        Ids(Listing, new SayAllFilter(Repos: ["PILOT"])).ShouldBe(["w1", "w2", "p1", "r1"]);

    [Fact]
    public void ARepoIsMatchedByCheckoutPathForAFolderInOrUnderIt()
    {
        var rows = new[]
        {
            Row("c1", "one", cwd: "C:\\src\\pilot"),
            Row("c2", "two", cwd: "C:\\src\\pilot\\.claude\\worktrees\\pil1"),
            Row("c3", "three", cwd: "C:\\src\\pilot-extras"),
            Row("c4", "four", cwd: "/home/u/pilot"),
        };

        Ids(rows, new SayAllFilter(Repos: ["C:/src/pilot/"])).ShouldBe(["c1", "c2"]);
        Ids(rows, new SayAllFilter(Repos: ["/home/u/pilot"])).ShouldBe(["c4"]);
        if (OperatingSystem.IsWindows())
        {
            Ids(rows, new SayAllFilter(Repos: ["c:\\SRC\\Pilot"])).ShouldBe(["c1", "c2"]);
        }
    }

    [Fact]
    public void RoleFlightAndRepoCombineEachNarrowingTheOthers()
    {
        Ids(Listing, new SayAllFilter(Roles: ["worker"], Flights: ["PIL1"], Repos: ["pilot"])).ShouldBe(["w1", "w2"]);
        Ids(Listing, new SayAllFilter(All: true, Roles: ["worker", "planner"], Repos: ["pilot"])).ShouldBe(["w1", "w2", "p1", "i1"]);
        Ids(Listing, new SayAllFilter(Roles: ["planner"], Flights: ["TIA2"])).ShouldBeEmpty();
        Ids(Listing, new SayAllFilter(Roles: ["validator"], Repos: ["tianwen"])).ShouldBeEmpty();
    }

    [Fact]
    public void ARoleValueMustNameARole()
    {
        SayAllFilter.NormalizeRole("VALIDATOR").ShouldBe("validator");
        SayAllFilter.NormalizeRole("adhoc").ShouldBe("adHoc");
        SayAllFilter.NormalizeRole("captain").ShouldBeNull();
    }
}

/// <summary>say-all against a real node whose sessions the test lists: dry runs, the real thing, and its exit codes.</summary>
public sealed class SayAllCliTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync()
    {
        _node = await NodeHarness.StartAsync(Ct);
        _node.Agents.Json = AgentRows.List(
            AgentRows.Interactive(AgentRows.SessionA, 4242, "du1-pil9-alpha", status: "busy"),
            AgentRows.Background(AgentRows.SessionB, "du1-pil9-alpha-validator", "working", pid: 4343, status: "busy"),
            AgentRows.Interactive(AgentRows.SessionC, 4444, "plain-chat"));
        // The node lists them within a poll.
        for (var i = 0; i < 100 && (await _node.Client.GetSessionsAsync(Ct)).Length < 3; i++)
        {
            await Task.Delay(50, Ct);
        }
    }

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private async Task<(int Exit, string Output, string Error)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task SayAllDryRunListsWhoWouldBeToldAndSendsNothing()
    {
        var (exit, output, _) = await Pilot("say-all", "--dry-run", "--flight", "PIL9", "rebase", "on", "main");

        exit.ShouldBe(0);
        output.ShouldContain("would say session/a1b2c3d4");
        output.ShouldNotContain("b2c3d4e5");
        _node.Delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task SayAllTellsEachPickedSessionThroughSayAndSkipsValidators()
    {
        var (exit, output, error) = await Pilot("say-all", "--flight", "PIL9", "rebase", "on", "main");

        exit.ShouldBe(0, output + error);
        output.ShouldContain("ok session/a1b2c3d4 (relayed)");
        output.ShouldNotContain("b2c3d4e5");
        _node.Delivery.Calls.ShouldBe(["relay du1-pil9-alpha"]);
        _node.Delivery.Relays.Single().Text.ShouldContain("rebase on main");
    }

    [Fact]
    public async Task SayAllWithRoleValidatorNamesTheValidatorAndOnlyIt()
    {
        var (exit, output, _) = await Pilot("say-all", "--role", "validator", "stand", "by");

        exit.ShouldBe(0);
        output.ShouldContain("ok session/b2c3d4e5");
        _node.Delivery.Calls.ShouldBe(["relay du1-pil9-alpha-validator"]);
    }

    [Fact]
    public async Task SayAllWithAllTellsTheIdleOnesToo()
    {
        var (exit, _, _) = await Pilot("say-all", "--all", "hello");

        exit.ShouldBe(0);
        _node.Delivery.Calls.Order().ShouldBe(["relay du1-pil9-alpha", "relay plain-chat"]);
    }

    [Fact]
    public async Task SayAllExitsOneWhenAnySayFailedAndSaysWhy()
    {
        _node.Delivery.RelayFailure = new SessionDeliveryException("the intercom did not deliver it");

        var (exit, output, _) = await Pilot("say-all", "--flight", "PIL9", "hello");

        exit.ShouldBe(1);
        output.ShouldContain("refused session/a1b2c3d4");
        output.ShouldContain("the intercom did not deliver it");
    }

    [Fact]
    public async Task SayAllWithNoSessionPickedSaysSoAndIsDone()
    {
        var (exit, output, _) = await Pilot("say-all", "--flight", "NONE1", "hello");

        exit.ShouldBe(0);
        output.ShouldContain("No session matches");
        _node.Delivery.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("--role", "captain")]
    [InlineData("--active", "--all")]
    public async Task SayAllWithAnUnknownRoleOrBothActiveAndAllIsUsage(params string[] options)
    {
        var (exit, _, error) = await Pilot(["say-all", .. options, "hello"]);

        exit.ShouldBe(2);
        error.ShouldStartWith("pilot: ");
        _node.Delivery.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task SayAllIsAToolOfPilotMcpWithItsOptionsInTheSchema()
    {
        var server = new McpServer(_node.Folder, node: null, socket: _node.SocketPath);
        var output = new StringWriter();
        await server.RunAsync(new StringReader("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""" + "\n"), output, Ct);
        var tools = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement
            .GetProperty("result").GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        tools.ShouldContainKey("say-all");
        var schema = tools["say-all"].GetProperty("inputSchema");
        schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ShouldBe(["text"]);
        var properties = schema.GetProperty("properties");
        properties.GetProperty("role").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("flight").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("repo").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("all").GetProperty("type").GetString().ShouldBe("boolean");
        properties.GetProperty("active").GetProperty("type").GetString().ShouldBe("boolean");
        properties.GetProperty("dry-run").GetProperty("type").GetString().ShouldBe("boolean");

        // And a call runs it: the arrays spelled back as repeated options.
        var call = new StringWriter();
        await server.RunAsync(new StringReader("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"say-all","arguments":{"text":["hi","all"],"flight":["PIL9"],"role":["worker","reviewer"],"dry-run":true}}}""" + "\n"), call, Ct);
        var result = JsonDocument.Parse(call.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeFalse();
        result.GetProperty("content")[0].GetProperty("text").GetString()!.ShouldContain("would say session/a1b2c3d4");
        _node.Delivery.Calls.ShouldBeEmpty();
    }
}
