using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node in maintenance (#177): it takes no new flight, and says so, while what is already in the air carries on. The
/// setting is live, published on the wire, and the one setting a LAN grant may change.
/// </summary>
public sealed class MaintenanceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2m-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly RoleModels _models = new(null);
    private readonly FlightCommander _flights;
    private readonly LegCommander _legs;

    public MaintenanceTests()
    {
        Directory.CreateDirectory(Repo);
        Git(Repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(Repo, "README.md"), "hello\n");
        Git(Repo, "add", ".");
        Git(Repo, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "first");
        var git = new GitCli();
        _flights = new FlightCommander(_state, new Planes(git), TimeProvider.System) { Models = _models, Machine = "DU1" };
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "DU1");
        _legs = new LegCommander(_state, new FakeWorkers(), new FakeChecks(), new FakeGitHub(), git, new Planes(git), _flights, options, TimeProvider.System);
    }

    private string Repo => Path.Combine(_root, "tianwen");

    public void Dispose()
    {
        try
        {
            Git(Repo, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    private static void Maintenance(RoleModels models, bool on) =>
        models.Apply(NodeSettings.With(new NodeSettings(), NodeSettings.MaintenanceKey, [on ? "on" : "off"]).Settings!);

    private Task<CommandResult> Flight(string verb, string target, params string[] args) =>
        _flights.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    private Task<CommandResult> Leg(string verb, string target, params string[] args) =>
        _legs.RunAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = "tests" }, Ct);

    [Fact]
    public void BothSettingsAreSwitchesThatDefaultToOffAndReadBack()
    {
        var models = new RoleModels(null);
        (models.Maintenance, models.AcceptsFleetFlights).ShouldBe((false, false));
        NodeSettings.Keys.ShouldContain(NodeSettings.MaintenanceKey);
        NodeSettings.Keys.ShouldContain(NodeSettings.AcceptFleetFlightsKey);

        var set = NodeSettings.With(NodeSettings.With(new NodeSettings(), "maintenance", ["on"]).Settings!, "accept-fleet-flights", ["yes"]).Settings!;
        set.Get("maintenance").ShouldBe(["on"]);
        set.Get("accept-fleet-flights").ShouldBe(["on"]);
        NodeSettings.With(set, "maintenance", ["sometimes"]).Error.ShouldBe("maintenance takes on or off");
        NodeSettings.With(set, "accept-fleet-flights", ["sometimes"]).Error.ShouldBe("accept-fleet-flights takes on or off");

        var file = Path.Combine(_root, NodeSettings.FileName);
        set.Write(file);
        File.ReadAllText(file).ShouldContain("\"maintenance\": true");
        var read = NodeSettings.Read(file);
        (read.Maintenance, read.AcceptFleetFlights).ShouldBe((true, true));
        NodeSettings.With(read, "maintenance", ["default"]).Settings!.Maintenance.ShouldBeNull();
    }

    [Fact]
    public async Task MaintenanceSetOnANodeIsInForceAtOnceAndPublishedOnTheWire()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        var info = await node.Client.GetNodeAsync(Ct);
        (info.Maintenance, info.AcceptsFleetFlights).ShouldBe((false, false));

        var result = (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["maintenance", "on"], Who = "tests" }, Ct)).Response.ShouldNotBeNull();

        result.Way.ShouldBe("maintenance saved as on, and in force now");
        node.App.Services.GetRequiredService<RoleModels>().Maintenance.ShouldBeTrue();
        NodeSettings.Read(node.Options.SettingsFile).Maintenance.ShouldBe(true);
        (await node.Client.GetNodeAsync(Ct)).Maintenance.ShouldBeTrue();
        var row = (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == "maintenance");
        (row.Value, row.Source, row.Pending).ShouldBe(("on", SettingSources.File, false));
        // The Settings page offers both as on / off rows.
        row.Label.ShouldBe("Maintenance");
        row.Choices.ShouldBe(["on", "off"]);
        (await node.Client.GetSettingsAsync(Ct)).Response!.Settings.Single(s => s.Key == "accept-fleet-flights").Choices.ShouldBe(["on", "off"]);

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["accept-fleet-flights", "on"], Who = "tests" }, Ct)).Response!.Way
            .ShouldBe("accept-fleet-flights saved as on, and in force now");
        (await node.Client.GetNodeAsync(Ct)).AcceptsFleetFlights.ShouldBeTrue();

        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["maintenance", "off"], Who = "tests" }, Ct)).Success.ShouldBeTrue();
        (await node.Client.GetNodeAsync(Ct)).Maintenance.ShouldBeFalse();
    }

    [Fact]
    public async Task ACreateFlightIsRefusedWithTheReasonWhileInMaintenanceAndTakenOnceItIsOff()
    {
        Maintenance(_models, on: true);

        var refused = await Flight(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1");

        refused.Error.ShouldBe("DU1 is in maintenance: it takes no new flights");
        _state.Flights().ShouldBeEmpty();

        Maintenance(_models, on: false);
        (await Flight(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1")).Ok.ShouldBeTrue();
        _state.Flights().ShouldHaveSingleItem().Code.ShouldBe("TIA1");
    }

    [Fact]
    public async Task AFlightAlreadyFlyingStillAdvancesItsLegsWhileInMaintenance()
    {
        (await Flight(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1", "Planets")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=parser", "goal=Parse", "done-when=Tests pass", "item=Write it")).Ok.ShouldBeTrue();
        (await Leg(Verbs.PlanLeg, "flight/TIA1", "name=render", "goal=Draw", "done-when=Shown")).Ok.ShouldBeTrue();
        Maintenance(_models, on: true);

        (await Leg(Verbs.StartLeg, "flight/TIA1/parser")).Ok.ShouldBeTrue();
        (await Leg(Verbs.Check, "flight/TIA1/parser", "1")).Ok.ShouldBeTrue();
        (await Leg(Verbs.FinishLeg, "flight/TIA1/parser", "done", "parser in")).Ok.ShouldBeTrue();

        _state.Legs("TIA1").Single(l => l.Name == "parser").State.ShouldBe(LegState.Done);
        (await Leg(Verbs.StartLeg, "flight/TIA1/render")).Ok.ShouldBeTrue();
        _state.Flights().Single().State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task AHeldFlightStillSettlesWhenWhatItWaitsForLandsWhileInMaintenance()
    {
        (await Flight(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "1")).Ok.ShouldBeTrue();
        (await Flight(Verbs.CreateFlight, Verbs.RepoTarget(Repo), "2")).Ok.ShouldBeTrue();
        (await Flight(Verbs.Connect, Verbs.FlightTarget("TIA2"), "TIA1")).Ok.ShouldBeTrue();
        _state.Flights().Single(f => f.Code == "TIA2").State.ShouldBe(FlightState.Held);
        Maintenance(_models, on: true);

        (await Flight(Verbs.Land, Verbs.FlightTarget("TIA1"))).Ok.ShouldBeTrue();

        _state.Flights().Single(f => f.Code == "TIA1").State.ShouldBe(FlightState.Landed);
        _state.Flights().Single(f => f.Code == "TIA2").State.ShouldBe(FlightState.Flying);
    }

    [Fact]
    public async Task OnARunningNodeACreateFlightIsRefusedNamingMaintenance()
    {
        await using var node = await NodeHarness.StartAsync(Ct);
        await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = ["maintenance", "on"], Who = "tests" }, Ct);

        var refused = await node.Client.SendAsync(new CommandRequest { Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(Repo), Args = ["1"], Who = "tests" }, Ct);

        refused.Success.ShouldBeFalse();
        refused.Response!.Error.ShouldBe($"{node.Options.Machine} is in maintenance: it takes no new flights");
        node.State.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task ALanGrantMayPutANodeInMaintenanceAndChangeNothingElse()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await using var node = await NodeHarness.StartAsync(Ct, lanPort: port);
        using var remote = NodeClient.Remote($"127.0.0.1:{port}");
        var ticket = (await remote.AskGrantAsync("se2-gui", Ct)).Response.ShouldNotBeNull();
        var card = node.State.Cards(openOnly: true).ShouldHaveSingleItem();
        (await node.Client.SendAsync(new CommandRequest { Verb = Verbs.Answer, Target = Verbs.CardTarget(card.Id), Args = [card.Choices[0].Label], Who = "controller" }, Ct)).Success.ShouldBeTrue();
        using var granted = NodeClient.Remote($"127.0.0.1:{port}", (await remote.GrantTicketAsync(ticket.Id, Ct)).Response!.Token);
        CommandRequest Set(params string[] args) => new() { Verb = Verbs.Set, Target = Verbs.NodeTarget, Args = args, Who = "tests" };

        (await granted.SendAsync(Set("maintenance", "on"), Ct)).Success.ShouldBeTrue();

        (await remote.GetNodeAsync(Ct)).Maintenance.ShouldBeTrue();
        (await granted.SendAsync(Set("maintenance", "off"), Ct)).Success.ShouldBeTrue();
        (await remote.GetNodeAsync(Ct)).Maintenance.ShouldBeFalse();
        // Every other setting, and a maintenance that is no plain on or off, stays this machine's.
        (await granted.SendAsync(Set("lan", "off"), Ct)).Error.ShouldBe("set is for this machine only");
        (await granted.SendAsync(Set("accept-fleet-flights", "on"), Ct)).Error.ShouldBe("set is for this machine only");
        (await granted.SendAsync(Set("maintenance", "default"), Ct)).Error.ShouldBe("set is for this machine only");
        (await granted.SendAsync(Set("maintenance", "on", "lan", "off"), Ct)).Error.ShouldBe("set is for this machine only");
        (await granted.SendAsync(new CommandRequest { Verb = Verbs.Setup, Target = Verbs.NodeTarget, Args = ["maintenance", "on"], Who = "tests" }, Ct))
            .Error.ShouldBe("setup is for this machine only");
        (await remote.GetNodeAsync(Ct)).Maintenance.ShouldBeFalse();
    }
}
