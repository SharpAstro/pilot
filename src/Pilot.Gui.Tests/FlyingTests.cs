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
        Labels(Line(Flight(), Leg("parser", 1, LegState.Planned), Leg("render", 2, LegState.Planned))).ShouldBe(["Start parser", "Remove parser", "Plan a leg"]);
        Labels(Line(Flight(), Leg("parser", 1, LegState.Done), Leg("render", 2, LegState.Flying))).ShouldBe(["Go around render", "Plan a leg"]);
        Labels(Line(Flight(), Leg("parser", 1, LegState.Done))).ShouldBe(["Open PR", "Plan a leg"]);
        Labels(Line(Flight(pr: 7, review: new ReviewRow { State = ReviewState.Changes, Reviewer = "r", StartedAt = Live.At }), Leg("parser", 1, LegState.Done)))
            .ShouldBe(["Review again", "Plan a leg"]);
        Labels(Line(Flight(FlightState.Held), Leg("parser", 1, LegState.Planned))).ShouldBe(["Remove parser", "Plan a leg"]);
        Labels(Line(Flight(FlightState.Landed), Leg("parser", 1, LegState.Done))).ShouldBeEmpty();

        // What needs words goes to the command bar, half written; what does not is a verb at once.
        var actions = FlightAction.For(Line(Flight(), Leg("parser", 1, LegState.Flying)));
        actions[0].Prefill.ShouldBe("leg go-around --leg TIA1611/parser ");
        FlightAction.For(Line(Flight(), Leg("parser", 1, LegState.Planned)))[0]
            .ShouldBe(new FlightAction("Start parser", Verbs.StartLeg, "flight/TIA1611/parser", []), new FlightActionComparer());
        FlightAction.For(Line(Flight(), Leg("parser", 1, LegState.Planned)))[1].Prefill.ShouldBe("leg remove TIA1611 parser ");
        Labels(Line(Flight(), Leg("parser", 1, LegState.Flying), Leg("render", 2, LegState.Planned))).ShouldBe(["Go around parser", "Remove render", "Plan a leg"]);
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

    [Fact]
    public void ACardAboutAPrOpensIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var opened = new List<string>();
        chrome.Cards.OnOpen = opened.Add;
        var card = new CardRow
        {
            Id = "c5", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight/DIR112", Title = "DIR112 landed", At = Live.At, State = CardState.Open,
            Link = "https://github.com/SharpAstro/DIR.Lib/pull/138",
        };

        chrome.Render(new SessionsScreen([], "test", Live.At), null, CardsScreen.From([card]), new RectF32(0, 0, 1280, 800));

        var regions = new List<ClickableRegion>();
        chrome.Cards.CollectPaintedRegions(regions);
        // The title is the link, and the only one: no button that could read as "raise a PR".
        regions.Any(r => r.Result is HitResult.ButtonHit { Action: "card:c5:open" }).ShouldBeFalse();
        regions.Single(r => r.Result is HitResult.ButtonHit { Action: "card:c5:title" }).OnClick!.Invoke(InputModifier.None);
        opened.ShouldBe(["https://github.com/SharpAstro/DIR.Lib/pull/138"]);
    }

    [Theory]
    [InlineData(null, "Merge")]
    [InlineData(ChoiceStatus.Passing, "Merge")]
    [InlineData(ChoiceStatus.Pending, "Merge · checks running")]
    [InlineData(ChoiceStatus.Failing, "Merge · checks failed")]
    public void AMergeSaysWhereItsChecksStand(string? status, string label)
    {
        CardsView<RgbaImage>.StatusLabel(new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/X", Status = status }).ShouldBe(label);
    }

    [Fact]
    public void TheLanLineSaysItIsDiscoveringWhatItHeardAndWhatWaits()
    {
        var nodes = new List<LanNode>();
        var started = Live.At;
        var lan = LanWatch.Over(() => nodes, started);

        lan.Line(started.AddSeconds(4)).ShouldBe("LAN: discovering… (4 s)");
        lan.Line(started.AddSeconds(30)).ShouldBe("LAN: no node announced itself yet (still listening)");
        nodes.Add(new LanNode("192.168.1.20:47731", "LP1 LAPTOP"));
        lan.Asking("192.168.1.20:47731", started.AddSeconds(30));
        lan.Line(started.AddSeconds(75)).ShouldBe("LAN: 1 node · waiting for 192.168.1.20:47731 to allow (45 s)");
        lan.Answered("192.168.1.20:47731");
        lan.Line(started.AddSeconds(80)).ShouldBe("LAN: 1 node");
        LanWatch.Over(() => [], started, () => "port 38821 is taken").Line(started).ShouldBe("LAN: discovery unavailable (port 38821 is taken)");

        DashboardCommands.ConnectTarget("connect 192.168.1.20:47731 --as se1-gui").ShouldBe("192.168.1.20:47731");
        DashboardCommands.ConnectTarget("pilot status").ShouldBeNull();
    }

    /// <summary>
    /// The nodes heard are the node pills' and the Fleet page's to show: the LAN line under the Flights header says only
    /// what waits (an ask) or what is wrong (discovering, unavailable), never the nodes again as chips.
    /// </summary>
    [Fact]
    public void TheLanLineShowsOnlyWhatWaitsNotTheNodesAgain()
    {
        var lan = LanWatch.Over(() => [new LanNode("192.168.1.20:47731", "LP1 LAPTOP")], Live.At);
        lan.Quiet.ShouldBeTrue();
        lan.Asking("192.168.1.20:47731", Live.At);
        lan.Quiet.ShouldBeFalse();
        lan.Answered("192.168.1.20:47731");
        lan.Quiet.ShouldBeTrue();
        LanWatch.Over(() => [], Live.At).Quiet.ShouldBeFalse("nothing heard yet is said");
        LanWatch.Over(() => [new LanNode("192.168.1.20:47731", "LP1")], Live.At, () => "port taken").Quiet.ShouldBeFalse();

        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.CanCommand = true;
        chrome.Flights.Lan = lan;
        chrome.Show(PilotPage.Flights);
        chrome.Render(new SessionsScreen([], "test", Live.At), new FlightsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions);
        regions.Any(r => r.Result is HitResult.ButtonHit { Action: "lan:192.168.1.20:47731" }).ShouldBeFalse();
    }

    [Fact]
    public async Task TheWizardMakesAFlightPerRepoInTheOrderTheyWerePickedWithTheirIssues()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var flights = chrome.Flights;
        flights.CanCommand = true;
        RepoInfo[] repos =
        [
            new() { Name = "tianwen", Path = @"C:\src\tianwen", GitHub = "SharpAstro/tianwen", Flights = 3 },
            new() { Name = "DIR.Lib", Path = @"C:\src\DIR.Lib", GitHub = "SharpAstro/DIR.Lib" },
        ];
        var issues = new Dictionary<string, IssueInfo[]>
        {
            [@"C:\src\tianwen"] = [new() { Number = 1611, Title = "GPU stacking" }],
            [@"C:\src\DIR.Lib"] = [new() { Number = 140, Title = "A compute pass" }, new() { Number = 141, Title = "Fonts" }],
        };
        IReadOnlyList<NewFlightPick>? made = null;
        flights.Wizard.LoadRepos = _ => Task.FromResult(repos);
        flights.Wizard.LoadIssues = (path, _) => Task.FromResult(issues[path]);
        flights.Wizard.Create = picks =>
        {
            made = picks;
            return Task.FromResult("made DIR140, TIA1611");
        };
        chrome.Show(PilotPage.Flights);

        void Click(string action)
        {
            chrome.Render(new SessionsScreen([], "test", Live.At), new FlightsScreen([], "test", Live.At), new RectF32(0, 0, 1280, 800));
            var regions = new List<ClickableRegion>();
            flights.CollectPaintedRegions(regions);
            var at = regions.FindIndex(r => r.Result is HitResult.ButtonHit hit && hit.Action == action);
            at.ShouldBeGreaterThanOrEqualTo(0, $"no {action} among {string.Join(", ", regions.Select(r => r.Result))}");
            regions[at].OnClick!.Invoke(InputModifier.None);
        }

        Click("flights:new");
        flights.Wizard.IsOpen.ShouldBeTrue();
        // The library first: the app's flight waits for it.
        Click("wizard:repo:DIR.Lib");
        Click("wizard:repo:tianwen");
        Click("wizard:next");
        Click("wizard:issue:140");
        Click("wizard:issue:1611");
        flights.Wizard.Current.ShouldBe(NewFlightWizard.Step.Review);
        Click("wizard:create");
        await Task.Yield();

        made.ShouldNotBeNull().Select(p => (p.Repo.Name, p.Issue.Number)).ShouldBe([("DIR.Lib", 140), ("tianwen", 1611)]);
        flights.Wizard.IsOpen.ShouldBeFalse();
        flights.Wizard.Status.ShouldBe("made DIR140, TIA1611");
    }

    [Fact]
    public void TheWizardIsFlownByTheKeyboardAlone()
    {
        var wizard = new NewFlightWizard
        {
            LoadRepos = _ => Task.FromResult<RepoInfo[]>([
                new() { Name = "AppShell", Path = @"C:\src\AppShell" },
                new() { Name = "DIR.Lib", Path = @"C:\src\DIR.Lib" },
                new() { Name = "Node.Lib", Path = @"C:\src\Node.Lib" },
                new() { Name = "tianwen", Path = @"C:\src\tianwen" }]),
            LoadIssues = (_, _) => Task.FromResult<IssueInfo[]>([new() { Number = 140, Title = "A compute pass" }, new() { Number = 141, Title = "Fonts" }]),
        };
        var focus = new TextInputFocus();
        var tracker = new BackgroundTaskTracker();
        TextInputInteraction.KeyContext Keys() => new(tracker, focus, () => { }, ActiveSearch: wizard.ActiveSearch);
        void Type(TextInputState input, string text)
        {
            foreach (var c in text)
            {
                TextInputInteraction.HandleText(input, c.ToString());
            }
        }
        void Press(InputKey key, InputModifier modifiers = InputModifier.None)
        {
            if (!(key is InputKey.Up or InputKey.Down && wizard.ActiveSearch?.HandleNavKey(key) == true))
            {
                TextInputInteraction.HandleKey(key, modifiers, Keys());
            }
        }

        wizard.Open();
        focus.Focus(wizard.RepoFilter);
        // "lib" leaves DIR.Lib and Node.Lib, the first highlighted; Down moves to Node.Lib, Enter chooses it.
        Type(wizard.RepoFilter, "lib");
        wizard.RepoResults.Select(r => r.Name).ShouldBe(["DIR.Lib", "Node.Lib"]);
        Press(InputKey.Down);
        Press(InputKey.Enter);
        wizard.Chosen.Select(r => r.Name).ShouldBe(["Node.Lib"]);
        wizard.GoOn();
        wizard.Current.ShouldBe(NewFlightWizard.Step.Issues);

        focus.Focus(wizard.IssueFilter);
        Press(InputKey.Down);
        Press(InputKey.Enter);
        wizard.Current.ShouldBe(NewFlightWizard.Step.Review);
        wizard.Picks.ShouldHaveSingleItem().Issue.Number.ShouldBe(141);

        // Escape goes back a step at a time, and out from the repos.
        wizard.Back();
        wizard.Current.ShouldBe(NewFlightWizard.Step.Issues);
        focus.Focus(wizard.IssueFilter);
        Press(InputKey.Escape);
        wizard.Current.ShouldBe(NewFlightWizard.Step.Repos);
        focus.Focus(wizard.RepoFilter);
        Press(InputKey.Escape);
        wizard.IsOpen.ShouldBeFalse();
    }

    /// <summary>Records compare their array by reference.</summary>
    private sealed class FlightActionComparer : IEqualityComparer<FlightAction>
    {
        public bool Equals(FlightAction? x, FlightAction? y) =>
            x is not null && y is not null && x with { Args = [] } == y with { Args = [] } && x.Args.SequenceEqual(y.Args);

        public int GetHashCode(FlightAction obj) => obj.Label.GetHashCode(StringComparison.Ordinal);
    }
}
