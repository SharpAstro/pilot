using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Flying a flight from the window alone: the actions a flight offers, the command bar, and the watchtower's panel.</summary>
public sealed class FlyingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FlightRow Flight(FlightState state = FlightState.Flying, int? pr = null, ReviewRow? review = null) => new()
    {
        Code = "TIA1611",
        Repo = "tianwen",
        RepoPath = "/r/tianwen",
        Issue = 1611,
        Title = "Planets on the GPU",
        Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611",
        Base = "origin/main",
        State = state,
        CreatedAt = Live.At,
        PullRequest = pr,
        PullRequestUrl = pr is null ? null : $"https://github.com/x/tianwen/pull/{pr}",
        Review = review,
    };

    private static LegRow Leg(string name, int order, LegState state, params Artifact[] artifacts) =>
        new() { Flight = "TIA1611", Name = name, Order = order, Goal = "g", DoneWhen = "d", State = state, Artifacts = artifacts };

    private static FlightLine Line(FlightRow flight, params LegRow[] legs) => FlightLine.Build([flight], [], Live.At, legs).Single();

    private static string[] Labels(FlightLine line) => [.. FlightAction.For(line).Select(a => a.Label)];

    [Fact]
    public void AFlightOffersWhatItsStageAllows()
    {
        Labels(Line(Flight())).ShouldBe(["Plan a leg"]);
        Labels(Line(Flight(), Leg("parser", 1, LegState.Planned), Leg("render", 2, LegState.Planned))).ShouldBe(["Start parser", "Plan a leg"]);
        Labels(Line(Flight(), Leg("parser", 1, LegState.Done), Leg("render", 2, LegState.Flying))).ShouldBe(["Go around render", "Plan a leg"]);
        Labels(Line(Flight(), Leg("parser", 1, LegState.Done))).ShouldBe(["Open PR", "Plan a leg"]);
        Labels(Line(Flight(pr: 7, review: new ReviewRow { State = ReviewState.Changes, Reviewer = "r", StartedAt = Live.At }), Leg("parser", 1, LegState.Done)))
            .ShouldBe(["Review again", "Plan a leg"]);
        Labels(Line(Flight(FlightState.Held), Leg("parser", 1, LegState.Planned))).ShouldBe(["Plan a leg"]);
        Labels(Line(Flight(FlightState.Landed), Leg("parser", 1, LegState.Done))).ShouldBeEmpty();

        // What needs words goes to the command bar, half written; what does not is a verb at once.
        var actions = FlightAction.For(Line(Flight(), Leg("parser", 1, LegState.Flying)));
        actions[0].Prefill.ShouldBe("leg go-around --leg TIA1611/parser ");
        FlightAction.For(Line(Flight(), Leg("parser", 1, LegState.Planned)))[0]
            .ShouldBe(new FlightAction("Start parser", Verbs.StartLeg, "flight/TIA1611/parser", []), new FlightActionComparer());
    }

    [Fact]
    public void TheSelectedFlightShowsItsLegsArtifactsButtonsAndTheCommandBar()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.CanCommand = true;
        var opened = new List<string>();
        chrome.Flights.OnOpen = opened.Add;
        var capture = new Artifact { Title = "The planets", Location = "/r/capture.png", At = Live.At };
        var screen = new FlightsScreen([Line(Flight(), Leg("parser", 1, LegState.Done, capture), Leg("render", 2, LegState.Planned))], "test", Live.At);

        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", Live.At), screen, new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        buttons.ShouldContain("flight:TIA1611:start-leg");
        buttons.ShouldContain("flight:TIA1611:plan-leg");
        buttons.ShouldContain("artifact:TIA1611:The planets");

        // Plan a leg needs words: it half-writes the command bar instead of running anything.
        chrome.Flights.Prefill("leg plan TIA1611 docs ");
        chrome.Flights.CommandInput.Text.ShouldBe("leg plan TIA1611 docs ");
        chrome.Flights.Say("ok: plan-leg flight/TIA1611\nmore");
        chrome.Flights.CommandOutput.ShouldBe(["ok: plan-leg flight/TIA1611", "more"]);
    }

    [Fact]
    public async Task TheCommandBarRunsTheClisCommandsAndSaysWhatTheyPrinted()
    {
        var gone = Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

        var said = await DashboardCommands.RunAsync("pilot status", ["--node-socket", gone], "gui", Ct);

        said.ShouldContain("no node answers");
        (await DashboardCommands.RunAsync("flight launch", ["--node-socket", gone], "gui", Ct)).ShouldContain("pilot --help lists the commands");
    }

    [Fact]
    public void TheRightPanelLeadsWithTheNodesWatchtowerAndItsRemoteControlLink()
    {
        SessionRow Row(string id, string name, SessionKind kind) => new()
        {
            Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = kind, Cwd = "/w", Role = SessionRole.Watchtower,
            ContextTokens = 50_000, ContextWindow = 200_000, State = SessionState.Idle, SpendWeighted = 0, StartedAt = Live.At,
            RemoteControlUrl = "https://claude.ai/code/session_x",
        };
        var screen = CardsScreen.From([], [Row("aaaa0001", "surface-watchtower", SessionKind.Interactive), Row("bbbb0002", "se1-watchtower", SessionKind.Background)]);

        screen.Watchtower.ShouldNotBeNull().Name.ShouldBe("se1-watchtower");
        screen.HasAny.ShouldBeTrue();

        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Cards.CanTell = true;
        chrome.Render(new SessionsScreen([], "test", Live.At), null, screen, new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ShouldContain("watchtower:rc");
    }

    /// <summary>Records compare their array by reference.</summary>
    private sealed class FlightActionComparer : IEqualityComparer<FlightAction>
    {
        public bool Equals(FlightAction? x, FlightAction? y) =>
            x is not null && y is not null && x with { Args = [] } == y with { Args = [] } && x.Args.SequenceEqual(y.Args);

        public int GetHashCode(FlightAction obj) => obj.Label.GetHashCode(StringComparison.Ordinal);
    }
}
