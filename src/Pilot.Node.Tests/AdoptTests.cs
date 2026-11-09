using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A node takes over a flight whose node has gone down (<c>adopt-flight</c>, #894), from its last copy of that node's flights and
/// legs: the plane from the pushed branch, the legs with a flying one planned again, the label and the comments, and a card for what
/// the old node had not pushed. Refused while the old node is still heard from, and with no copy to take it from. All in memory:
/// the modelled git, a fake peer and a clock the test moves.
/// </summary>
public sealed class AdoptTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private const string Code = "PIL900";
    private const string Branch = "flight/pil900";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "p2ad-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Clock _clock = new(Start);
    private readonly NodeState _state = new(new InMemoryJournal(), TimeProvider.System);
    private readonly FakeGitHub _github = new();
    private readonly FakeRepoGit _git;
    private readonly FleetCopy _copy;
    private readonly FakeFleet _fleet = new();
    private readonly FakePeer _de1 = new("DE1") { Unreachable = true };
    private readonly FlightCommander _flights;

    public AdoptTests()
    {
        _git = new FakeRepoGit(_root);
        _copy = new FleetCopy(null, _clock);
        _fleet.Peers.Add(_de1);
        var options = new NodeOptions(Path.Combine(_root, "node.sock"), Path.Combine(_root, "data"), MachinePrefix: "SE1", RepoRootsGiven: [_root]);
        _flights = new FlightCommander(_state, new Planes(_git), _clock, github: _github)
        {
            Machine = "SE1",
            Fleet = _fleet,
            Copy = _copy,
            Models = new RoleModels(null),
            Repos = new RepoCatalog(_state, options),
        };
        // The pushed branch of the flight, as DE1 left it before it went down.
        var sender = Path.Combine(_root, "de1-plane");
        Run(_root + "/tianwen", "worktree", "add", "-b", Branch, sender, "origin/main");
        File.WriteAllText(Path.Combine(sender, "parser.txt"), "the parser leg's work\n");
        Run(sender, "add", ".");
        Run(sender, "commit", "-m", "parser");
        Run(sender, "push", "origin", Branch);
        Run(_root + "/tianwen", "worktree", "remove", "--force", sender);
    }

    public void Dispose()
    {
        _git.Release();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        TestDirs.DeleteTree(_root);
    }

    private void Run(string cwd, params string[] args)
    {
        var run = _git.Run(args, cwd);
        run.Ok.ShouldBeTrue($"git {string.Join(' ', args)}: {run.Error}{run.Output}");
    }

    private string Repo => Path.Combine(_root, "tianwen");

    private static FlightRow Theirs(string code = Code, FlightState state = FlightState.Flying) => new()
    {
        Code = code, Repo = "tianwen", RepoPath = "D:/src/tianwen", Issue = 900, Title = "Planets", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"D:/src/tianwen/.claude/worktrees/{code.ToLowerInvariant()}", Base = "origin/main", State = state, CreatedAt = Start.AddHours(-3),
        PullRequest = 41, PullRequestUrl = "https://github.com/SharpAstro/tianwen/pull/41",
    };

    private static LegRow Leg(string name, int order, LegState state, Func<LegRow, LegRow>? with = null)
    {
        var leg = new LegRow { Flight = Code, Name = name, Order = order, Goal = $"{name} goal", DoneWhen = $"{name} done", State = state };
        return with is null ? leg : with(leg);
    }

    private LegRow[] ThreeLegs() =>
    [
        Leg("parser", 1, LegState.Done, l => l with { Report = "parser in", StartedAt = Start.AddHours(-2), FinishedAt = Start.AddHours(-1) }),
        Leg("render", 2, LegState.Flying, l => l with { Worker = "de1-pil900-render", WorkerJob = "ab12cd34", StartedAt = Start.AddMinutes(-30), AttemptModel = "opus" }),
        Leg("polish", 3, LegState.Planned),
    ];

    /// <summary>DE1 was heard once at <see cref="Start"/> with its flight and legs, and has said nothing since.</summary>
    private void HeardOnce(FlightRow[]? flights = null, LegRow[]? legs = null)
    {
        var now = _clock.Now;
        _clock.Now = Start;
        _copy.Record("DE1", new FleetLoad(null, flights ?? [Theirs()], Legs: legs ?? ThreeLegs()));
        _clock.Now = now;
    }

    private Task<CommandResult> Adopt(string code = Code, string from = "DE1") =>
        _flights.RunAsync(new CommandRequest { Verb = Verbs.AdoptFlight, Target = Verbs.FlightTarget(code), Args = [from], Who = "cli" }, Ct);

    [Fact]
    public async Task AFlightOfANodeSilentPastAdoptAfterIsTakenFromTheCopyWithItsPlaneLegsLabelCommentsAndAnAtRiskCard()
    {
        HeardOnce();
        _github.IssueLabels[900] = ["pilot node: DE1", "bug"];
        _clock.Now = Start.AddHours(2);

        var result = await Adopt();
        await _flights.WhenAdvancedAsync();

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe(Code);

        // The plane is made here from the branch DE1 pushed, with its work in it.
        var flight = _state.Flights().Single();
        flight.Code.ShouldBe(Code);
        Path.GetFullPath(flight.RepoPath).ShouldBe(Path.GetFullPath(Repo), StringCompareShould.IgnoreCase);
        Path.GetFullPath(flight.Worktree).ShouldBe(Path.GetFullPath(Path.Combine(Repo, ".claude", "worktrees", "pil900")), StringCompareShould.IgnoreCase);
        flight.Branch.ShouldBe(Branch);
        flight.State.ShouldBe(FlightState.Flying);
        flight.PullRequest.ShouldBe(41);
        File.ReadAllText(Path.Combine(flight.Worktree, "parser.txt")).ShouldBe("the parser leg's work\n");
        flight.MovedFrom.ShouldNotBeNull().Adopted.ShouldBeTrue();
        flight.MovedFrom.From.ShouldBe("DE1");
        flight.MovedFrom.AfterLeg.ShouldBe("parser");
        flight.MovedFrom.At.ShouldBe(Start.AddHours(2));
        flight.MovedFrom.Text.ShouldBe("adopted from DE1");
        result.Way.ShouldBe($"{Code} adopted from DE1");

        // The legs come from the copy; the one that was flying there is planned again, with nothing of the attempt there.
        var legs = _state.Legs(Code).OrderBy(l => l.Order).ToArray();
        legs.Select(l => (l.Name, l.State)).ShouldBe([("parser", LegState.Done), ("render", LegState.Planned), ("polish", LegState.Planned)]);
        legs[0].Report.ShouldBe("parser in");
        (legs[1].Worker, legs[1].WorkerJob, legs[1].StartedAt, legs[1].AttemptModel).ShouldBe((null, null, null, null));

        // The item flies here now.
        _github.IssueLabels[900].OrderBy(l => l, StringComparer.Ordinal).ShouldBe(["bug", "pilot node: SE1"]);

        // The issue and the PR say who adopted it from whom.
        _github.IssueComments.Select(c => c.Number).Order().ShouldBe([41, 900]);
        _github.IssueComments.ShouldAllBe(c => c.Body.Contains("SE1 adopted flight PIL900 from DE1", StringComparison.Ordinal));

        // A line that says it, and a card that says what is at risk.
        var cards = _state.Cards();
        cards.Single(c => c.Kind == FlightCommander.AdoptedKind).Bin.ShouldBe(CardBin.Handled);
        var risk = cards.Single(c => c.Kind == FlightCommander.AdoptAtRiskKind);
        risk.Bin.ShouldBe(CardBin.ForYou);
        risk.Subject.ShouldBe(Verbs.FlightTarget(Code));
        // Golden (#797): what happened in the title, what to do and why first, one short verb; the plane's branch is the detail's.
        risk.Title.ShouldBe("PIL900 was adopted; what DE1 had not pushed is not here");
        risk.Summary.ShouldBe("Look at DE1's plane when it is back and push what it holds, or hold PIL900 here until you have: its plane was made from what DE1 last pushed, so its unpushed work is not in it.");
        risk.Choices.Select(c => (c.Label, c.Verb, c.Target)).ShouldBe([(FlightCommander.AdoptHoldLabel, Verbs.Ground, Verbs.FlightTarget(Code))]);
        risk.Choices[0].Label.ShouldBe("Hold the flight here");
        cards.ShouldNotContain(c => c.Kind == "adopt-comment-refused");
    }

    [Fact]
    public async Task ANodeStillHeardFromWithinAdoptAfterRefusesAndSaysWhenItWasLastHeard()
    {
        HeardOnce();
        _clock.Now = Start.AddMinutes(20);

        var result = await Adopt();

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("DE1 was last heard from at");
        result.Error.ShouldContain("20 min ago");
        result.Error.ShouldContain("1 h 0 min or more");
        _state.Flights().ShouldBeEmpty();
        _state.Legs(Code).ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
        Directory.Exists(Path.Combine(Repo, ".claude", "worktrees", "pil900")).ShouldBeFalse();

        // The setting moves the line: a node silent for 20 minutes is adoptable once adopt-after says 15 minutes.
        _flights.Models.Apply(NodeSettings.With(new NodeSettings(), NodeSettings.AdoptAfterKey, ["15m"]).Settings!);
        (await Adopt()).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task ANodeThatAnswersNowRefusesWhateverTheCopySays()
    {
        HeardOnce();
        _clock.Now = Start.AddHours(5);
        _de1.Unreachable = false;

        var result = await Adopt();

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("DE1 answers now");
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task WithNoCopyOfTheNodeOrOfTheFlightItRefusesAndMakesNothing()
    {
        _clock.Now = Start.AddHours(5);

        var never = await Adopt();
        never.Ok.ShouldBeFalse();
        never.Error.ShouldNotBeNull().ShouldContain("has no copy of DE1's flights");

        HeardOnce([Theirs("PIL901")], []);
        var missing = await Adopt();
        missing.Ok.ShouldBeFalse();
        missing.Error.ShouldNotBeNull().ShouldContain("has no flight PIL900");

        _state.Flights().ShouldBeEmpty();
        _github.IssueComments.ShouldBeEmpty();
        _state.Cards().ShouldBeEmpty();
    }

    [Fact]
    public async Task ALandedFlightAndACodeThatIsAlreadyHereAreRefused()
    {
        _clock.Now = Start.AddHours(5);
        HeardOnce([Theirs(state: FlightState.Landed)], []);
        var landed = await Adopt();
        landed.Ok.ShouldBeFalse();
        landed.Error.ShouldNotBeNull().ShouldContain("is landed on DE1");

        HeardOnce();
        _state.PutFlight(Theirs() with { RepoPath = Repo, Worktree = Path.Combine(Repo, ".claude", "worktrees", "pil900"), State = FlightState.Landed });
        var here = await Adopt();
        here.Ok.ShouldBeFalse();
        here.Error.ShouldNotBeNull().ShouldContain("PIL900 is already here");
    }

    [Fact]
    public async Task WithNoCheckoutOfTheRepoItRefuses()
    {
        HeardOnce([Theirs() with { Repo = "elsewhere" }]);
        _clock.Now = Start.AddHours(5);

        var result = await Adopt();

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("has no checkout of elsewhere");
        _state.Flights().ShouldBeEmpty();
    }

    [Fact]
    public async Task ARefusedCommentOrLabelIsACardAndTheAdoptionStands()
    {
        HeardOnce();
        _clock.Now = Start.AddHours(2);
        _github.CommentFails = "gh: HTTP 502";
        _github.IssueLabelFails = "gh: HTTP 502";

        var result = await Adopt();
        await _flights.WhenAdvancedAsync();

        result.Ok.ShouldBeTrue(result.Error);
        _state.Flights().Single().MovedFrom!.Adopted.ShouldBeTrue();
        // The refusals are lines, not rings: the adoption stands and nothing the node can press would post them.
        var told = _state.Cards().Single(c => c.Kind == "adopt-comment-refused");
        told.Bin.ShouldBe(CardBin.Handled);
        told.Title.ShouldStartWith("PIL900 was adopted, but ");
        told.Title.ShouldEndWith(" was not told");
        told.Detail.ShouldNotBeNull().ShouldContain("The comment, to post by hand: SE1 adopted flight PIL900 from DE1");
        _state.Cards().Where(c => c.Kind == "node-label-refused").ShouldAllBe(c => c.Bin == CardBin.Handled);
        _state.Cards().Select(c => c.Kind).ShouldContain("node-label-refused");
        _state.Cards().Select(c => c.Kind).ShouldContain(FlightCommander.AdoptAtRiskKind);
    }

    [Fact]
    public void AdoptedIsOnTheWireAndAnOlderRowReadsAsNotAdopted()
    {
        var adopted = new FlightMoved { From = "DE1", AfterLeg = "parser", At = Start, Adopted = true };
        var json = System.Text.Json.JsonSerializer.Serialize(adopted, PilotJsonContext.Default.FlightMoved);
        System.Text.Json.JsonSerializer.Deserialize(json, PilotJsonContext.Default.FlightMoved)!.Adopted.ShouldBeTrue();

        var older = System.Text.Json.JsonSerializer.Deserialize("""{"from":"DE1","afterLeg":"parser","at":"2026-11-08T09:00:00+00:00"}""", PilotJsonContext.Default.FlightMoved)!;
        older.Adopted.ShouldBeFalse();
        older.Text.ShouldBe("moved from DE1 after leg parser");
        new FlightMoved { From = "DE1", At = Start, Adopted = true }.Text.ShouldBe("adopted from DE1");
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
        public string Endpoint => $"{name.ToLowerInvariant()}.local:47731";

        public bool HasGrant => true;

        public bool Unreachable { get; set; }

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
            return Task.FromResult(new FleetLoad(null, []));
        }

        public Task<CommandResult> SendAsync(CommandRequest command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
