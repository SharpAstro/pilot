using DIR.Lib;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>Flying a flight from the window alone: the actions a flight offers (a why asked under its button, Plan a leg said in the chat), and the watchtower's panel.</summary>
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

        // What needs words asks for them: a why under its button (Go around, Remove), the chat's box (Plan a leg); what does not is a verb at once.
        var actions = FlightAction.For(Line(Flight(), Leg("parser", 1, LegState.Flying)));
        actions[0].WhyPrompt.ShouldBe("why go around parser?");
        actions[0].ChatMessage.ShouldBeNull();
        FlightAction.For(Line(Flight(), Leg("parser", 1, LegState.Planned)))[0]
            .ShouldBe(new FlightAction("Start parser", Verbs.StartLeg, "flight/TIA1611/parser", []), new FlightActionComparer());
        FlightAction.For(Line(Flight(), Leg("parser", 1, LegState.Planned)))[1].WhyPrompt.ShouldBe("why remove parser?");
        FlightAction.For(Line(Flight())).Single().ChatMessage.ShouldBe("plan a leg on TIA1611: ");
        FlightAction.For(Line(Flight())).Single().WhyPrompt.ShouldBeNull();
        Labels(Line(Flight(), Leg("parser", 1, LegState.Flying), Leg("render", 2, LegState.Planned))).ShouldBe(["Go around parser", "Remove render", "Plan a leg"]);
    }

    [Fact]
    public void TheSelectedFlightShowsItsLegsArtifactsAndButtonsAndNoCommandBar()
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

        // No command bar: no field on the page until a why is asked, and nothing of the bar is left on the view.
        regions.Any(r => r.Result is HitResult.TextInputHit).ShouldBeFalse();
        foreach (var gone in new[] { "CommandInput", "CommandOutput", "OnCommand", "CommandShortcut", "Prefill" })
        {
            var members = typeof(FlightsView<RgbaImage>).GetMember(gone, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
            members.ShouldBeEmpty(gone);
        }
        AboutCard.Keys.ShouldNotContain(k => k.Keys == "Ctrl+K");

        // What an action said is one line, under the flights.
        chrome.Flights.Say("ok: plan-leg flight/TIA1611\nmore");
        chrome.Flights.Said.ShouldBe("ok: plan-leg flight/TIA1611 · more");
    }

    // The Flights page with TIA1611 selected, rendered, and its painted regions.
    private static (PilotChrome<RgbaImage> Chrome, List<FlightAction> Acted, Func<List<ClickableRegion>> Regions) Flying(
        RgbaImageRenderer renderer, FlightLine line, bool detailPage = false)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Flights.CanCommand = true;
        var acted = new List<FlightAction>();
        chrome.Flights.OnAction = acted.Add;
        var screen = new FlightsScreen([line], "test", Live.At);
        chrome.Show(PilotPage.Flights);
        chrome.Flights.ListCursor.MoveTo(0);
        if (detailPage)
        {
            chrome.Flights.OpenDetail(line.Code);
        }
        void Render() => chrome.Render(new SessionsScreen([], "test", Live.At), screen, new RectF32(0, 0, 1280, 800));
        List<ClickableRegion> Regions()
        {
            Render();
            var regions = new List<ClickableRegion>();
            chrome.Flights.CollectPaintedRegions(regions);
            return regions;
        }
        Render();
        return (chrome, acted, Regions);
    }

    private static void Press(List<ClickableRegion> regions, string action) =>
        regions.Single(r => r.Result is HitResult.ButtonHit hit && hit.Action == action).OnClick!.Invoke(InputModifier.None);

    private static bool AsksWhy(PilotChrome<RgbaImage> chrome, List<ClickableRegion> regions) =>
        regions.Any(r => r.Result is HitResult.TextInputHit hit && hit.Input == chrome.Flights.WhyInput);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GoAroundAsksWhyInAOneLineFieldUnderItsButtonAndEnterSendsTheVerbWithIt(bool detailPage)
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, acted, regions) =Flying(renderer, Line(Flight(), Leg("parser", 1, LegState.Flying)), detailPage);

        // Pressed, it runs nothing: it opens a field, with its question, in place.
        AsksWhy(chrome, regions()).ShouldBeFalse();
        Press(regions(), "flight:TIA1611:go-around");
        acted.ShouldBeEmpty();
        AsksWhy(chrome, regions()).ShouldBeTrue();
        chrome.Flights.WhyInput.Placeholder.ShouldBe("why go around parser?");

        // Enter with nothing typed keeps it open; with words it sends the verb with them as its argument, and the field goes.
        await chrome.Flights.WhyInput.OnCommit.ShouldNotBeNull()("   ");
        acted.ShouldBeEmpty();
        AsksWhy(chrome, regions()).ShouldBeTrue();
        await chrome.Flights.WhyInput.OnCommit.ShouldNotBeNull()(" it went in circles ");
        var sent = acted.ShouldHaveSingleItem();
        (sent.Verb, sent.Target).ShouldBe((Verbs.GoAround, "flight/TIA1611/parser"));
        sent.Args.ShouldBe(["it went in circles"]);
        AsksWhy(chrome, regions()).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveAsksWhyAndEscapeCancelsTheField(bool detailPage)
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, acted, regions) =Flying(renderer, Line(Flight(), Leg("parser", 1, LegState.Done), Leg("render", 2, LegState.Planned)), detailPage);

        Press(regions(), "flight:TIA1611:remove-leg");
        AsksWhy(chrome, regions()).ShouldBeTrue();
        chrome.Flights.WhyInput.Placeholder.ShouldBe("why remove render?");

        // Escape in the field is its cancel: nothing is sent and the field is gone.
        chrome.Flights.WhyInput.OnCancel.ShouldNotBeNull()();
        acted.ShouldBeEmpty();
        AsksWhy(chrome, regions()).ShouldBeFalse();

        // Asked again, and answered, the verb goes with the why to the leg's target.
        Press(regions(), "flight:TIA1611:remove-leg");
        await chrome.Flights.WhyInput.OnCommit.ShouldNotBeNull()("the render step is covered by parser");
        var sent = acted.ShouldHaveSingleItem();
        (sent.Verb, sent.Target).ShouldBe((Verbs.RemoveLeg, "flight/TIA1611/render"));
        sent.Args.ShouldBe(["the render step is covered by parser"]);
    }

    [Fact]
    public void TheWhyFieldGoesWhenTheActionIsNoLongerOpenToTheFlight()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, acted, regions) =Flying(renderer, Line(Flight(), Leg("parser", 1, LegState.Flying)));
        Press(regions(), "flight:TIA1611:go-around");
        AsksWhy(chrome, regions()).ShouldBeTrue();

        // The leg went done under it: its Go around is gone, and so is the field that was asking for it.
        var done = new FlightsScreen([Line(Flight(), Leg("parser", 1, LegState.Done))], "test", Live.At);
        chrome.Render(new SessionsScreen([], "test", Live.At), done, new RectF32(0, 0, 1280, 800));
        var after = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(after);
        AsksWhy(chrome, after).ShouldBeFalse();
        acted.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanALegPutsItsStartInTheChatsBoxAndOpensTheChat(bool detailPage)
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var (chrome, acted, regions) =Flying(renderer, Line(Flight(), Leg("parser", 1, LegState.Done)), detailPage);
        // Something already typed in the box gives way to it.
        chrome.Chat.Input.Text = "old";

        Press(regions(), "flight:TIA1611:plan-leg");

        chrome.Page.ShouldBe(PilotPage.Chat);
        chrome.Chat.Input.Text.ShouldBe("plan a leg on TIA1611: ");
        chrome.Chat.Input.CursorPos.ShouldBe("plan a leg on TIA1611: ".Length);
        chrome.Chat.Ui.Focus.Current.ShouldBe(chrome.Chat.Input);
        // Nothing was sent to a node: the person finishes the message and sends it as any other.
        acted.ShouldBeEmpty();
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
    [InlineData(ChoiceStatus.Conflicting, "Merge · conflicts with main")]
    public void AMergeSaysWhereItsChecksStand(string? status, string label)
    {
        CardsView<RgbaImage>.StatusLabel(new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/X", Status = status }).ShouldBe(label);
    }

    /// <summary>
    /// A button the node refused says why on its card, until a press goes through: a Merge on a PR GitHub would not merge
    /// was pressed over and over with the refusal seen only in a log (SE1, 2026-10-24).
    /// </summary>
    [Fact]
    public void ARefusedButtonSaysWhyOnItsCard()
    {
        using var renderer = new RgbaImageRenderer(460, 800);
        var cards = new CardsView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath };
        var card = new CardRow
        {
            Id = "c144", Bin = CardBin.Ring, Kind = "pr-ready", Subject = "flight/PIL173", Title = "PIL173's PR #195 is ready to merge",
            Choices = [new CardChoice { Label = "Merge", Verb = Verbs.Merge, Target = "flight/PIL173", Status = ChoiceStatus.Conflicting }],
            At = Live.At, State = CardState.Open,
        };

        cards.NoteRefused("c144", "#195 conflicts with its base, so GitHub cannot merge it: it is being rebased onto its base and reviewed afresh");
        cards.Render(CardsScreen.From([card], []), new RectF32(0, 0, 460, 800));

        cards.RefusedOn("c144").ShouldNotBeNull().ShouldContain("conflicts with its base");
        cards.NoteRefused("c144", null);
        cards.RefusedOn("c144").ShouldBeNull();
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
    }

    [Fact]
    public async Task ConnectRunsTheClisCommandAndSaysWhatItPrinted()
    {
        var gone = Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

        var said = await DashboardCommands.RunAsync("status", ["--node-socket", gone], "gui", Ct);

        said.ShouldContain("no node answers");
        (await DashboardCommands.RunAsync("flight launch", ["--node-socket", gone], "gui", Ct)).ShouldContain("pilot --help lists the commands");
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
