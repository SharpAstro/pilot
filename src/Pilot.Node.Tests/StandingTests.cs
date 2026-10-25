using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Who may do what (#208), at the node: the person, an assistant, the lookout, a flight's session and a grant, one
/// test per row of the issue's table, and the autopilot setting only the person changes.
/// </summary>
public sealed class StandingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PastTheFence = "has not been reviewed";

    private static string Lookout(NodeHarness node) => $"{node.Options.Machine.ToLowerInvariant()}-watchtower";

    private static CommandRequest Command(string verb, string target, string who, bool agent = false, params string[] args) =>
        new() { Verb = verb, Target = target, Args = args, Who = who, Agent = agent };

    private static FlightRow Flight(string code, ReviewRow? review = null) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "/r", Issue = int.Parse(code[3..]), Branch = $"flight/{code.ToLowerInvariant()}", Worktree = $"/r/{code}", Base = "main",
        State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow, Review = review,
    };

    private static void Fly(NodeHarness node, string code, string worker, ReviewRow? review = null)
    {
        node.State.PutFlight(Flight(code, review));
        node.State.PutLeg(new LegRow { Flight = code, Name = "parser", Order = 1, Goal = "g", DoneWhen = "d", State = LegState.Flying, Worker = worker });
    }

    private static string? Saved(NodeHarness node) => NodeSettings.Read(node.Options.SettingsFile).Get(NodeSettings.AutopilotKey) is { } values ? string.Join(' ', values) : null;

    private static async Task<string?> Error(NodeHarness node, CommandRequest command) =>
        (await node.Client.SendAsync(command, Ct)).Response.ShouldNotBeNull().Error;

    [Fact]
    public async Task AGuiClickAndAHumanRunCliMayMergeAsBefore()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight("TIA1"));

        // The fence lets them through to the flight, which says why the merge cannot go yet.
        (await Error(node, Command(Verbs.Merge, "flight/TIA1", "gui", false, "cafe0000"))).ShouldNotBeNull().ShouldContain(PastTheFence);
        (await Error(node, Command(Verbs.Merge, "flight/TIA1", "cli", false, "cafe0000"))).ShouldNotBeNull().ShouldContain(PastTheFence);
    }

    [Fact]
    public async Task TheLookoutIsRefusedEveryYourOnlyVerbAndADismissedRingCardButMayDismissOthers()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var lookout = Lookout(node);
        node.State.PutFlight(Flight("TIA1"));

        foreach (var (verb, target, args) in new (string, string, string[])[]
        {
            (Verbs.Merge, "flight/TIA1", ["cafe0000"]),
            (Verbs.Land, "flight/TIA1", []),
            (Verbs.Set, Verbs.NodeTarget, ["maintenance", "on"]),
            (Verbs.Setup, Verbs.NodeTarget, ["firewall"]),
            (Verbs.AnswerGrant, Verbs.NodeTarget, ["x", "allow"]),
            (Verbs.RevokeGrant, Verbs.NodeTarget, ["x"]),
            (Verbs.Update, Verbs.NodeTarget, ["install"]),
            (Verbs.RestartWatchtower, Verbs.WatchtowerTarget, []),
            (Verbs.PostReview, "flight/TIA1", []),
            (Verbs.SyncRepos, Verbs.NodeTarget, []),
        })
        {
            (await Error(node, Command(verb, target, lookout, false, args))).ShouldBe($"{verb} is the controller's, not the watchtower's: leave its card open for them");
        }

        var ring = node.State.RaiseCard(CardBin.Ring, "pr-ready", "flight/TIA1", "TIA1's PR is ready");
        var forYou = node.State.RaiseCard(CardBin.ForYou, "landed", "flight/TIA1", "TIA1 landed");
        (await Error(node, Command(Verbs.Dismiss, Verbs.CardTarget(ring.Id), lookout))).ShouldNotBeNull().ShouldContain("dismiss is the controller's");
        (await Error(node, Command(Verbs.Dismiss, Verbs.CardTarget(forYou.Id), lookout))).ShouldBeNull();
        node.State.Cards(openOnly: true).ShouldHaveSingleItem().Id.ShouldBe(ring.Id);

        // An agent-run command named for the lookout is the lookout's still: the flag does not lift it.
        (await Error(node, Command(Verbs.Merge, "flight/TIA1", lookout, true, "cafe0000"))).ShouldNotBeNull().ShouldContain("not the watchtower's");
        // The person may dismiss the ring card.
        (await Error(node, Command(Verbs.Dismiss, Verbs.CardTarget(ring.Id), "gui"))).ShouldBeNull();
    }

    [Fact]
    public async Task AWorkersPlannersAndReviewersMergeIsRefusedWhateverAutopilotSays()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        const string reviewer = "du1-tia1-review";
        const string planner = "du1-tia1-plan";
        Fly(node, "TIA1", worker, new ReviewRow { State = ReviewState.Reviewing, Reviewer = reviewer, StartedAt = DateTimeOffset.UtcNow });
        node.State.PutSession(new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = planner, Kind = SessionKind.Background, Cwd = "/r/TIA1",
            Role = SessionRole.Planner, Flight = "TIA1", ContextTokens = 0, ContextWindow = 1, State = SessionState.Idle, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
        });

        foreach (var autopilot in new[] { "off", "on" })
        {
            (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", false, "autopilot", autopilot))).ShouldBeNull();
            foreach (var who in new[] { worker, reviewer, planner })
            {
                // From the CLI of its plane, the flag is set too: the session is known by its name, not by the flag.
                foreach (var agent in new[] { false, true })
                {
                    (await Error(node, Command(Verbs.Merge, "flight/TIA1", who, agent, "cafe0000"))).ShouldBe($"merge is the controller's, not a flight session's: leave it to them");
                    (await Error(node, Command(Verbs.Land, "flight/TIA1", who, agent))).ShouldNotBeNull().ShouldContain("land is the controller's");
                }
                (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, who, true, "maintenance", "on"))).ShouldNotBeNull().ShouldContain("set is the controller's");
            }
        }
    }

    [Fact]
    public async Task AFlightsSessionMayNotTargetAnotherFlightButHasItsOwn()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        const string worker = "du1-tia1-parser";
        Fly(node, "TIA1", worker);
        Fly(node, "TIA2", "du1-tia2-parser");

        foreach (var verb in new[] { Verbs.Check, Verbs.FinishLeg, Verbs.AddArtifact, Verbs.GoAround })
        {
            (await Error(node, Command(verb, "flight/TIA2/parser", worker, true, "1"))).ShouldBe("flight/TIA2/parser is not TIA1's: a flight's session acts on its own flight only");
        }
        (await Error(node, Command(Verbs.OpenPr, "flight/TIA2", worker, true))).ShouldNotBeNull().ShouldContain("not TIA1's");

        // Its own flight's leg is not stopped by the fence: the leg's own rules answer (this one has no checklist to tick).
        (await Error(node, Command(Verbs.Check, "flight/TIA1/parser", worker, true, "1"))).ShouldBe("TIA1/parser has no checklist");
        (await Error(node, Command(Verbs.Check, "flight/tia1/parser", worker, true, "2"))).ShouldNotBeNull().ShouldNotContain("own flight only");
    }

    [Fact]
    public async Task APrReviewsReviewerActsOnItsOwnPrOnly()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        node.State.PutFlight(Flight("TIA1"));
        node.State.PutPrReview(new PrReviewRow
        {
            Repo = "SharpAstro/tianwen", Number = 7, Url = "https://github.com/SharpAstro/tianwen/pull/7", Base = "main", Head = "cafe0000", ClonePath = "/r/tianwen",
            State = PrReviewState.Reviewing, Reviewer = "du1-review-tianwen-7", Worktree = "/r/review7", StartedAt = DateTimeOffset.UtcNow,
        });
        const string reviewer = "du1-review-tianwen-7";

        (await Error(node, Command(Verbs.PrVerdict, "pr/SharpAstro/tianwen#8", reviewer, true, "pass"))).ShouldNotBeNull().ShouldContain("not the PR this review is of");
        (await Error(node, Command(Verbs.Review, "flight/TIA1", reviewer, true, "pass", "x"))).ShouldNotBeNull().ShouldContain("not the PR this review is of");
        (await Error(node, Command(Verbs.Merge, "pr/SharpAstro/tianwen#7", reviewer, true))).ShouldNotBeNull().ShouldContain("merge is the controller's");
    }

    [Fact]
    public async Task AGrantsCommandsBehaveAsBeforeWhateverItsNameSays()
    {
        var port = FreePort();
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port);
        node.State.PutFlight(Flight("TIA1"));
        using var remote = NodeClient.Remote($"127.0.0.1:{port}");
        var ticket = (await remote.AskGrantAsync("se2-gui", Ct)).Response.ShouldNotBeNull();
        var card = node.State.Cards(openOnly: true).ShouldHaveSingleItem();
        (await node.Client.SendAsync(Command(Verbs.Answer, Verbs.CardTarget(card.Id), "gui", false, "Allow"), Ct)).Success.ShouldBeTrue();
        using var granted = NodeClient.Remote($"127.0.0.1:{port}", (await remote.GrantTicketAsync(ticket.Id, Ct)).Response.ShouldNotBeNull().Token);

        // A merge from the LAN reaches the flight, even named for the watchtower or a worker here.
        (await granted.SendAsync(Command(Verbs.Merge, "flight/TIA1", "tests", false, "cafe0000"), Ct)).Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain(PastTheFence);
        var named = await granted.SendAsync(Command(Verbs.Merge, "flight/TIA1", Lookout(node), false, "cafe0000"), Ct);
        named.Response.ShouldNotBeNull().Error.ShouldNotBeNull().ShouldContain(PastTheFence);
        named.Response.Who.ShouldBe($"se2-gui/{Lookout(node)}");
        // Never this machine's settings, grants or autopilot, as before.
        (await granted.SendAsync(Command(Verbs.Set, Verbs.NodeTarget, "tests", false, "autopilot", "on"), Ct)).Error.ShouldBe("set is for this machine only");
        (await granted.SendAsync(Command(Verbs.AnswerGrant, Verbs.NodeTarget, "tests", false, "x", "allow"), Ct)).Error.ShouldBe("answer-grant is for this machine only");
        Saved(node).ShouldBeNull();
    }

    [Fact]
    public async Task AutopilotIsOffByDefaultAndSavedOnlyWhenThePersonSetsIt()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var lookout = Lookout(node);
        Fly(node, "TIA1", "du1-tia1-parser");

        (await node.Client.GetNodeAsync(Ct)).Autopilot.ShouldBeFalse();
        var row = (await node.Client.GetSettingsAsync(Ct)).Response.ShouldNotBeNull().Settings.Single(s => s.Key == NodeSettings.AutopilotKey);
        (row.Value, row.Source).ShouldBe(("off", SettingSources.Default));
        row.Choices.ShouldBe(["on", "off"]);

        // An assistant (by its flag), the lookout and a flight's session are all refused, and nothing is saved.
        foreach (var (who, agent) in new[] { ("pilot-33", true), ("mcp", true), ("cli", true), (lookout, false), ("du1-tia1-parser", true) })
        {
            (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, who, agent, "autopilot", "on"))).ShouldNotBeNull().ShouldContain("autopilot is the person's to change");
            // Taking it out is changing it too.
            (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, who, agent, "autopilot"))).ShouldNotBeNull().ShouldContain("autopilot is the person's to change");
        }
        Saved(node).ShouldBeNull();
        (await node.Client.GetNodeAsync(Ct)).Autopilot.ShouldBeFalse();

        // The person: the GUI, or a CLI nobody's session runs. Saved, and in force at once.
        var set = (await node.Client.SendAsync(Command(Verbs.Set, Verbs.NodeTarget, "gui", false, "autopilot", "on"), Ct)).Response.ShouldNotBeNull();
        set.Error.ShouldBeNull();
        set.Way.ShouldNotBeNull().ShouldContain("in force now");
        Saved(node).ShouldBe("on");
        (await node.Client.GetNodeAsync(Ct)).Autopilot.ShouldBeTrue();
        (await node.Client.GetSettingsAsync(Ct)).Response.ShouldNotBeNull().Settings.Single(s => s.Key == NodeSettings.AutopilotKey).Value.ShouldBe("on");

        // An assistant cannot turn it off either.
        (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, "pilot-33", true, "autopilot", "off"))).ShouldNotBeNull().ShouldContain("autopilot is the person's to change");
        (await node.Client.GetNodeAsync(Ct)).Autopilot.ShouldBeTrue();
        (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, "cli", false, "autopilot", "off"))).ShouldBeNull();
        Saved(node).ShouldBe("off");
        (await node.Client.GetNodeAsync(Ct)).Autopilot.ShouldBeFalse();
        (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", false, "autopilot", "maybe"))).ShouldBe("autopilot takes on or off");
    }

    [Fact]
    public async Task ASavedAutopilotIsOnWhenTheNodeStarts()
    {
        await using var first = await NodeHarness.StartAsync(Ct);
        var models = first.App.Services.GetRequiredService<RoleModels>();
        models.Autopilot.ShouldBeFalse();
        models.Apply(NodeSettings.With(new NodeSettings(), NodeSettings.AutopilotKey, ["on"]).Settings.ShouldNotBeNull());
        models.Autopilot.ShouldBeTrue();
    }

    [Fact]
    public async Task PilotStatusPrintsTheNodesAutopilot()
    {
        await using var node = await NodeHarness.StartAsync(Ct);

        (await Status(node)).ShouldContain("autopilot  off");
        (await Error(node, Command(Verbs.Set, Verbs.NodeTarget, "gui", false, "autopilot", "on"))).ShouldBeNull();
        (await Status(node)).ShouldContain("autopilot  on");
    }

    [Fact]
    public async Task TheCliSendsTheAgentFlagWhenClaudeCodeIsInItsEnvironmentAndAnMcpClientAlways()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var before = Environment.GetEnvironmentVariable("CLAUDECODE");
        try
        {
            // A human at their own terminal: no CLAUDECODE, so the person's, and autopilot is theirs to set.
            Environment.SetEnvironmentVariable("CLAUDECODE", null);
            PilotCommand.IsAgent(bySession: true, agent: null).ShouldBeFalse();
            (await Cli(node, "settings", "set", "autopilot", "on")).ShouldContain("ok: set");
            (await node.Client.GetNodeAsync(Ct)).Autopilot.ShouldBeTrue();

            // A Claude session's tool: CLAUDECODE is set, so an assistant's, and it is refused.
            Environment.SetEnvironmentVariable("CLAUDECODE", "1");
            PilotCommand.IsAgent(bySession: true, agent: null).ShouldBeTrue();
            (await Cli(node, "settings", "set", "autopilot", "off")).ShouldContain("autopilot is the person's to change");
            (await node.Client.GetNodeAsync(Ct)).Autopilot.ShouldBeTrue();

            // The GUI never sets it, whatever its environment holds; an MCP client always does, whatever its does not.
            PilotCommand.IsAgent(bySession: false, agent: null).ShouldBeFalse();
            Environment.SetEnvironmentVariable("CLAUDECODE", null);
            PilotCommand.IsAgent(bySession: true, agent: true).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDECODE", before);
        }
    }

    private static async Task<string> Cli(NodeHarness node, params string[] args)
    {
        var output = new StringWriter();
        await PilotCommand.RunAsync(["--node-socket", node.SocketPath, .. args], node.Folder, output, output, Ct);
        return output.ToString();
    }

    private static Task<string> Status(NodeHarness node) => Cli(node, "status");

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
