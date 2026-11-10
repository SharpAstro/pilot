using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The copy a node keeps of each granted peer's flights and legs (#894): fed by the reads of the peers, with when each was last heard,
/// kept through a peer going silent and through a restart, and the setting that says how long a silent peer waits before its flights
/// may be taken over.
/// </summary>
public sealed class FleetCopyTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2fc-" + Guid.NewGuid().ToString("N")[..8])).FullName;
    private readonly Clock _clock = new(Start);
    private readonly FakeFleet _fleet = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string File => FleetCopy.FileIn(_folder);

    private FlightCommander Commander(FleetCopy copy) =>
        new(new NodeState(new InMemoryJournal(), TimeProvider.System), new Planes(new GitCli()), _clock) { Machine = "DU1", Fleet = _fleet, Copy = copy };

    private static FlightRow Flight(string code) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "D:/src/tianwen", Issue = 1, Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"D:/src/tianwen/.claude/worktrees/{code.ToLowerInvariant()}", Base = "origin/main", State = FlightState.Flying, CreatedAt = Start,
    };

    private static LegRow Leg(string flight, string name, int order) => new()
    {
        Flight = flight, Name = name, Order = order, Goal = $"{name} goal", DoneWhen = $"{name} done", State = LegState.Planned,
    };

    [Fact]
    public async Task APeerReadIsCopiedWithWhenItWasHeard()
    {
        var peer = new FakePeer("DE1") { Flights = [Flight("PIL1")], Legs = [Leg("PIL1", "parser", 1), Leg("PIL1", "render", 2)] };
        _fleet.Peers.Add(peer);
        var copy = new FleetCopy(File, _clock);

        (await Commander(copy).CopyPeersAsync(Ct)).ShouldBe(1);

        var kept = copy.Of("DE1").ShouldNotBeNull();
        kept.LastHeard.ShouldBe(Start);
        kept.Flights.Select(f => f.Code).ShouldBe(["PIL1"]);
        kept.Legs.Select(l => l.Name).ShouldBe(["parser", "render"]);
        copy.Silence("DE1").ShouldBe(TimeSpan.Zero);
        copy.Of("SE1").ShouldBeNull();
    }

    [Fact]
    public async Task APeerThatStopsAnsweringKeepsItsRowsAndItsOldHeardAt()
    {
        var peer = new FakePeer("DE1") { Flights = [Flight("PIL1")], Legs = [Leg("PIL1", "parser", 1)] };
        _fleet.Peers.Add(peer);
        var copy = new FleetCopy(File, _clock);
        var commander = Commander(copy);
        await commander.CopyPeersAsync(Ct);

        peer.Unreachable = true;
        _clock.Now = Start.AddMinutes(90);
        (await commander.CopyPeersAsync(Ct)).ShouldBe(0);

        var kept = copy.Of("DE1").ShouldNotBeNull();
        kept.LastHeard.ShouldBe(Start);
        kept.Flights.Select(f => f.Code).ShouldBe(["PIL1"]);
        kept.Legs.Select(l => l.Name).ShouldBe(["parser"]);
        copy.Silence("DE1").ShouldBe(TimeSpan.FromMinutes(90));

        // Heard again: the copy is what it said now, heard at the new time.
        peer.Unreachable = false;
        peer.Flights = [Flight("PIL1"), Flight("PIL2")];
        await commander.CopyPeersAsync(Ct);
        copy.Of("DE1")!.Flights.Select(f => f.Code).ShouldBe(["PIL1", "PIL2"]);
        copy.Of("DE1")!.LastHeard.ShouldBe(Start.AddMinutes(90));
    }

    [Fact]
    public void TheCopySurvivesARestart()
    {
        var copy = new FleetCopy(File, _clock);
        copy.Record("DE1", new FleetLoad(null, [Flight("PIL1")], Legs: [Leg("PIL1", "parser", 1)]));
        _clock.Now = Start.AddMinutes(5);
        copy.Record("SE1", new FleetLoad(null, [Flight("TIA2")]));

        var restarted = new FleetCopy(File, _clock);

        restarted.Peers().Select(p => p.Peer).ShouldBe(["DE1", "SE1"]);
        var de1 = restarted.Of("DE1").ShouldNotBeNull();
        (de1.LastHeard, de1.Flights.Single().Code, de1.Flights.Single().Branch, de1.Legs.Single().Name).ShouldBe((Start, "PIL1", "flight/pil1", "parser"));
        restarted.Of("SE1")!.LastHeard.ShouldBe(Start.AddMinutes(5));
        restarted.Of("SE1")!.Legs.ShouldBeEmpty();
    }

    [Fact]
    public void AMissingOrCorruptFileIsAnEmptyCopyThatIsWrittenAfresh()
    {
        new FleetCopy(File, _clock).Peers().ShouldBeEmpty();

        System.IO.File.WriteAllText(File, "{ this is not json");
        var copy = new FleetCopy(File, _clock);
        copy.Peers().ShouldBeEmpty();

        copy.Record("DE1", new FleetLoad(null, [Flight("PIL1")]));
        new FleetCopy(File, _clock).Of("DE1").ShouldNotBeNull();
        System.IO.File.Exists(File + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void AdoptAfterIsReadFromSettingsAndDefaultsToAnHour()
    {
        NodeSettings.Keys.ShouldContain(NodeSettings.AdoptAfterKey);
        new RoleModels(null).AdoptAfter.ShouldBe(TimeSpan.FromHours(1));

        var saved = NodeSettings.With(new NodeSettings(), NodeSettings.AdoptAfterKey, ["30m"]).Settings.ShouldNotBeNull();
        saved.AdoptAfter.ShouldBe(TimeSpan.FromMinutes(30));
        saved.Get(NodeSettings.AdoptAfterKey).ShouldBe(["30m"]);
        new RoleModels(saved).AdoptAfter.ShouldBe(TimeSpan.FromMinutes(30));

        // Written to the settings file and read back.
        var file = Path.Combine(_folder, NodeSettings.FileName);
        saved.Write(file);
        NodeSettings.Read(file).AdoptAfter.ShouldBe(TimeSpan.FromMinutes(30));

        NodeSettings.With(saved, NodeSettings.AdoptAfterKey, ["2h"]).Settings!.AdoptAfter.ShouldBe(TimeSpan.FromHours(2));
        NodeSettings.With(saved, NodeSettings.AdoptAfterKey, ["default"]).Settings!.AdoptAfter.ShouldBeNull();
        NodeSettings.With(saved, NodeSettings.AdoptAfterKey, ["10s"]).Error.ShouldNotBeNull();
        NodeSettings.With(saved, NodeSettings.AdoptAfterKey, ["soon"]).Error.ShouldNotBeNull();
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeFleet : IFleet
    {
        public List<IFleetPeer> Peers { get; } = [];

        IReadOnlyList<IFleetPeer> IFleet.Peers() => [.. Peers];
    }

    private sealed class FakePeer(string name) : IFleetPeer
    {
        public string Name => name;

        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasGrant => true;

        public bool Unreachable { get; set; }

        public FlightRow[] Flights { get; set; } = [];

        public LegRow[] Legs { get; set; } = [];

        private void Reach()
        {
            if (Unreachable)
            {
                throw new HttpRequestException("connection refused");
            }
        }

        public Task<NodeInfo> NodeAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new NodeInfo
            {
                WireVersion = NodeWire.Version, Version = "0", MachineName = name, MachinePrefix = name, ProcessId = 1, StartedAt = DateTimeOffset.UtcNow,
                DataFolder = "/data", SocketPath = "/sock", Stream = "s",
            });
        }

        public Task<RepoInfo[]?> ReposAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FleetLoad> LoadAsync(CancellationToken cancellationToken)
        {
            Reach();
            return Task.FromResult(new FleetLoad(null, Flights, Legs: Legs));
        }

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
