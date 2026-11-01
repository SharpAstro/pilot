using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using Shouldly;
using Xunit;
using static Pilot.Gui.Tests.PageButtonFeedbackTests;
using PageTests = Pilot.Gui.Tests.PageButtonFeedbackTests;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Fleet, Repos, Ideas, Settings and Summary pages', the new-flight wizard's and the autopilot switch's buttons that ask the node
/// (issue 461, the owner 2026-10-29: "all buttons should always have a feedback in one form or the other"): each says it is working
/// on the frame it is pressed and takes no second press, shows the outcome when the node answers, and says a refusal in the node's
/// words where it was pressed. Every test's node answers late, through a <see cref="TaskCompletionSource{T}"/>.
/// </summary>
public sealed class OtherPagesButtonFeedbackTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    // A roomier grid than the shared helpers': a settings page and a fleet row's refusal need the room.
    private static readonly Rect<int> Grid = new(0, 0, 300, 240);

    private static ImmutableArray<Layout.ArrangedNode<int>> Arranged(Layout.Node tree) => Layout.Engine.Arrange(tree, Grid, CellMeasureContext.PixelAuthored);

    private static string Words(Layout.Node tree) => TestText.Flat(CellLayout.Describe(Arranged(tree)));

    private static bool Has(Layout.Node tree, string action) => Arranged(tree).Any(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action);

    private static void Click(Layout.Node tree, string action)
    {
        var arranged = Arranged(tree);
        var button = arranged.FirstOrDefault(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action);
        button.Node.ShouldNotBeNull($"no button {action} on the page: it is not there to press");
        CellLayout.HitTest(arranged, button.Bounds.X + 1, button.Bounds.Y);
    }

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer) =>
        new(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };

    // ---- Fleet ----

    private static NodeSummary Summary(string stream, bool connected = true) => new()
    {
        Connected = connected, Status = connected ? "live" : "waiting for the node on 10.0.0.9:47731; retrying every 2s", Stream = stream, LiveSessions = 3,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE2", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream },
    };

    [Fact]
    public async Task FleetConnectForgetAndMaintenanceSayWorkingAtOnceAndTheNodesWordsStandBesideTheRowWhenRefused()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var fleet = NewChrome(renderer).Fleet;
        var node = new LateNode();
        fleet.OnConnect = line => node.Ask($"connect {line.Where}");
        fleet.OnForget = line => node.Ask($"forget {line.Where}");
        fleet.OnMaintenance = (line, on) => node.Ask($"maintenance {(on ? "on" : "off")} {line.Where}");
        var screen = new FleetScreen(
        [
            FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("laptop")),
            FleetLine.From("10.0.0.9:47731", here: false, canCommand: false, null, Summary("desktop")),
            FleetLine.From("10.0.0.7:47731", here: false, canCommand: true, null, Summary("quiet", connected: false) with { Node = null }),
        ], Now);
        Layout.Node Tree() => fleet.Tree(screen);

        // Connect: the button reads "asking…" at once, takes no second press, and a refusal is said beside the row in the node's words.
        Click(Tree(), "fleet:10.0.0.9:47731:connect");
        node.Asked.ShouldBe(["connect 10.0.0.9:47731"]);
        Words(Tree()).ShouldContain("asking…");
        Has(Tree(), "fleet:10.0.0.9:47731:connect").ShouldBeFalse();
        node.Answer("the other machine has not allowed it");
        await Until(() => Has(Tree(), "fleet:10.0.0.9:47731:connect"));
        Words(Tree()).ShouldContain("the other machine has not allowed it");
        Words(Tree()).ShouldNotContain("asking…");

        // Pressed again, the old words go while the node thinks, and a taken press leaves none.
        Click(Tree(), "fleet:10.0.0.9:47731:connect");
        Words(Tree()).ShouldNotContain("the other machine has not allowed it");
        node.Answer(null);
        await Until(() => Has(Tree(), "fleet:10.0.0.9:47731:connect"));
        Words(Tree()).ShouldNotContain("asking…");

        // Forget: working on the row it was pressed in, and the other rows' buttons are not touched.
        Click(Tree(), "fleet:10.0.0.7:47731:forget");
        Words(Tree()).ShouldContain("forgetting…");
        Has(Tree(), "fleet:10.0.0.7:47731:forget").ShouldBeFalse();
        Has(Tree(), "fleet:10.0.0.9:47731:connect").ShouldBeTrue();
        node.Answer(null);
        await Until(() => Has(Tree(), "fleet:10.0.0.7:47731:forget"));

        // Maintenance: working, then refused in the node's words.
        Click(Tree(), "fleet:node.sock:maintenance");
        node.Asked[^1].ShouldBe("maintenance on node.sock");
        Words(Tree()).ShouldContain("putting into maintenance…");
        Has(Tree(), "fleet:node.sock:maintenance").ShouldBeFalse();
        node.Answer("only the person may");
        await Until(() => Has(Tree(), "fleet:node.sock:maintenance"));
        Words(Tree()).ShouldContain("only the person may");
    }

    // ---- Repos ----

    [Fact]
    public async Task ReposSyncReadsSyncingAtOnceTakesNoSecondPressShowsEachOutcomeAndSaysAnyRefusal()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var repos = NewChrome(renderer).Repos;
        var running = new List<TaskCompletionSource<RepoSyncOutcome[]>>();
        repos.SyncRepos = _ =>
        {
            var answer = new TaskCompletionSource<RepoSyncOutcome[]>();
            running.Add(answer);
            return answer.Task;
        };
        var screen = new ReposScreen([], "test", Now);
        Layout.Node Tree() => repos.Tree(screen);

        Words(Tree()).ShouldContain("Sync");
        Click(Tree(), "repos:sync");
        running.Count.ShouldBe(1);
        Words(Tree()).ShouldContain("Syncing…");
        Has(Tree(), "repos:sync").ShouldBeFalse();

        running[0].SetResult([new RepoSyncOutcome { Name = "orbit", Status = RepoSyncStatus.Updated, Detail = "main took 2 commits" }]);
        await Until(() => Has(Tree(), "repos:sync"));
        Words(Tree()).ShouldContain("orbit  updated  main took 2 commits");

        // Refused: the node's words, and the outcome list of the run before is gone.
        Click(Tree(), "repos:sync");
        running[1].SetException(new InvalidOperationException("watchtower: not yours"));
        await Until(() => Has(Tree(), "repos:sync"));
        Words(Tree()).ShouldContain("sync refused: watchtower: not yours");
        Words(Tree()).ShouldNotContain("orbit  updated");
    }

    // ---- Ideas ----

    private sealed class LateIdeas
    {
        public List<(string Verb, string Target, string[] Args)> Sent { get; } = [];
        private readonly Queue<TaskCompletionSource<IdeaReply>> _answers = new();

        public Task<IdeaReply> Send(string verb, string target, string[] args, CancellationToken _)
        {
            Sent.Add((verb, target, args));
            var answer = new TaskCompletionSource<IdeaReply>();
            _answers.Enqueue(answer);
            return answer.Task;
        }

        public void Answer(IdeaReply reply) => _answers.Dequeue().SetResult(reply);
    }

    private static IdeaRow Idea(int number, string title) => new()
    {
        Id = IdeaIds.Id(number),
        Repo = "pilot",
        Title = title,
        Body = "",
        State = IdeaState.Noted,
        UpdatedAt = Live.At,
        Age = "3h",
        Origin = "ada",
        Url = $"https://github.com/SharpAstro/pilot/issues/{number}",
    };

    private static string ActionWith(Layout.Node tree, string prefix) =>
        Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored)
            .Select(a => a.Node.Hit).OfType<HitResult.ButtonHit>().Select(b => b.Action).FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal)) ?? "";

    [Fact]
    public async Task IdeasPlanAndMoveSayWorkingOnTheRowTakeNoSecondPressAndSayTheNodesWordsWhereTheyWerePressed()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var ideas = NewChrome(renderer).Ideas;
        var node = new LateIdeas();
        ideas.Send = node.Send;
        var screen = new IdeasScreen([Idea(381, "Ideas pane")], [], ["pilot", "tianwen"], "live from the node", Live.At);
        Layout.Node Tree() => ideas.Tree(screen);

        // Drop: sent from its box (Enter); the row says "dropping…" at once and the page is working.
        Click(Tree(), ActionWith(Tree(), "ideas:drop:"));
        ideas.RowInput.Text = "a duplicate";
        var firstDrop = ideas.RowInput.OnCommit!("a duplicate");
        node.Sent.Select(s => s.Verb).ShouldBe([Verbs.DropIdea]);
        ideas.Working.ShouldBeTrue();
        Words(Tree()).ShouldContain("dropping…");

        // The press is with the node: no second one reaches it, whichever way it is made.
        await ideas.DropAsync(screen.Ideas[0], "a duplicate");
        node.Sent.Count.ShouldBe(1);

        // Refused: the node's words beside the row, and the row is no longer working.
        node.Answer(new IdeaReply(false, "no milestone is open"));
        await Until(() => !ideas.Working);
        Words(Tree()).ShouldNotContain("dropping…");
        Words(Tree()).ShouldContain("no milestone is open");
        ideas.Said.ShouldBe("I381 not dropped: no milestone is open");

        // Taken: the outcome is said at once and the row's words are gone.
        var again = ideas.DropAsync(screen.Ideas[0], "a duplicate");
        ideas.Working.ShouldBeTrue();
        ideas.Presses.RefusedOf("pilot I381").ShouldBeNull();
        node.Answer(new IdeaReply(true, Made: "PIL462"));
        await again;
        ideas.Said.ShouldBe("dropped I381");
        ideas.Working.ShouldBeFalse();

        // Move: working with the repo chosen, then refused in the node's words.
        Click(Tree(), ActionWith(Tree(), "ideas:move:"));
        var tianwen = ActionWith(Tree(), "ideas:move:pilot I381:tianwen");
        tianwen.ShouldNotBeEmpty();
        Click(Tree(), tianwen);
        Words(Tree()).ShouldContain("moving…");
        Has(Tree(), tianwen).ShouldBeFalse();
        node.Answer(new IdeaReply(false, "tianwen has no Ideas milestone"));
        await Until(() => !ideas.Working);
        Words(Tree()).ShouldContain("tianwen has no Ideas milestone");
    }

    [Fact]
    public async Task IdeasNoteIsWorkingAtOnceAndARefusedNoteIsSaidInTheNodesWords()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var ideas = NewChrome(renderer).Ideas;
        var node = new LateIdeas();
        ideas.Send = node.Send;
        var screen = new IdeasScreen([], [], ["pilot"], "live from the node", Live.At);

        ideas.NoteInput.InsertText("Scale status images");
        var noting = ideas.NoteAsync();
        ideas.Working.ShouldBeTrue();
        Words(ideas.Tree(screen)).ShouldContain("ideating…Scale status images");

        node.Answer(new IdeaReply(false, "gh is not signed in"));
        await noting;
        ideas.Working.ShouldBeFalse();
        ideas.Said.ShouldBe("not noted: gh is not signed in");
    }

    // ---- Settings ----

    private static SettingsPage Page() => new()
    {
        File = @"C:\Users\ada\AppData\Local\pilot2\settings.json",
        Settings = [new SettingRow { Key = "lan", Label = "On the LAN", Detail = "Listen on the LAN and announce it", Value = "on", Saved = "on", Source = SettingSources.File, Choices = ["on", "off"] }],
    };

    [Fact]
    public async Task SettingsChoiceFieldAndRevokeReadWorkingUntilTheNodeAnswersAndSayItsRefusal()
    {
        using var renderer = new RgbaImageRenderer(1280, 2000);
        var chrome = NewChrome(renderer);
        var settings = chrome.Settings;
        var node = new LateNode();
        settings.Load = _ => Task.FromResult(Page());
        settings.Send = async (verb, args, _) =>
        {
            var refused = await node.Ask($"{verb} {string.Join(' ', args)}");
            return refused is null ? new SettingsReply("saved") : new SettingsReply($"refused: {refused}", ReplyKind.Refused);
        };
        chrome.Show(PilotPage.Settings);
        chrome.Render(new SessionsScreen([], "test", Now), null, null, null, null, null, new RectF32(0, 0, 1280, 2000));
        await Until(() => settings.Page is not null);

        // Pressing a choice: the pill reads "off…", dimmed with no hit, and the second press is never sent.
        Presses(settings).PendingOf("settings:lan:off").ShouldBeNull();
        Click(settings.Tree(Now), "settings:lan:off");
        // The send runs on the thread pool, so it is waited for; the working state is already said.
        await Until(() => node.Asked.Count == 1);
        node.Asked.ShouldBe([$"{Verbs.Set} lan off"]);
        Words(settings.Tree(Now)).ShouldContain("off…");
        Has(settings.Tree(Now), "settings:lan:off").ShouldBeFalse();
        Presses(settings).PendingOf("settings:lan:off").ShouldNotBeNull();

        node.Answer("the node's command line gives lan, and wins");
        await Until(() => Presses(settings).PendingOf("settings:lan:off") is null);
        settings.Said.ShouldBe("refused: the node's command line gives lan, and wins");
        Words(settings.Tree(Now)).ShouldNotContain("off…");
        Has(settings.Tree(Now), "settings:lan:off").ShouldBeTrue();
    }

    private static Presses Presses(SettingsView<RgbaImage> settings) => settings.Presses;

    // ---- Summary ----

    [Fact]
    public async Task SummariseReadsSummarisingAtOnceTakesNoSecondPressAndSaysTheNodesRefusalOnTheCard()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var board = NewChrome(renderer).Summaries;
        var node = new LateNode();
        var tracked = new List<Task>();
        board.Send = ask => node.Ask($"summarise {ask.Scope}");
        board.Track = (run, _) => tracked.Add(run());
        var ask = new SummaryAsk(SummaryRow.FleetScope, new NodeMark("node.sock", "node.sock", "this node", true), "the flights in play");

        board.Press(ask);
        board.Running(ask).ShouldBeTrue();
        board.Press(ask);
        node.Asked.ShouldBe(["summarise " + SummaryRow.FleetScope]);

        node.Answer("one is being made already");
        await Task.WhenAll(tracked);
        board.Running(ask).ShouldBeFalse();
        board.Refused(ask).ShouldBe("one is being made already");

        // Pressed again, the old words go while the node thinks; taken, the card has none.
        board.Press(ask);
        board.Refused(ask).ShouldBeNull();
        node.Answer(null);
        await Task.WhenAll(tracked);
        board.Running(ask).ShouldBeFalse();
        board.Refused(ask).ShouldBeNull();
    }

    // ---- The new-flight wizard ----

    private static async Task<(PilotChrome<RgbaImage> Chrome, RgbaImageRenderer Renderer)> OpenWizardAtReviewAsync(TaskCompletionSource<string> create)
    {
        var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = NewChrome(renderer);
        chrome.Show(PilotPage.Flights);
        chrome.Flights.CanCommand = true;
        var wizard = chrome.Flights.Wizard;
        wizard.LoadRepos = _ => Task.FromResult<RepoInfo[]>([new() { Name = "orbit", Path = @"C:\src\orbit", GitHub = "SharpAstro/orbit" }]);
        wizard.LoadIssues = (_, _) => Task.FromResult<IssueInfo[]>([new() { Number = 204, Title = "Parser" }]);
        wizard.Create = _ => create.Task;
        wizard.Open(@"C:\src\orbit", atIssues: true);
        await Until(() => wizard.IssueResults.Count == 1);
        wizard.Pick(wizard.IssueResults[0]);
        return (chrome, renderer);
    }

    private static bool Painted(PilotChrome<RgbaImage> chrome, string action)
    {
        chrome.Render(new SessionsScreen([], "test", Now), new FlightsScreen([], "test", Now), new RectF32(0, 0, 1280, 800));
        var regions = new List<ClickableRegion>();
        chrome.Flights.CollectPaintedRegions(regions);
        return regions.Any(r => r.Result is HitResult.ButtonHit b && b.Action == action);
    }

    [Fact]
    public async Task TheWizardsCreateIsWorkingAtOnceTakesNoSecondPressAndASecondTryFollowsARefusalInTheNodesWords()
    {
        var create = new TaskCompletionSource<string>();
        var (chrome, renderer) = await OpenWizardAtReviewAsync(create);
        using var _ = renderer;
        var wizard = chrome.Flights.Wizard;
        wizard.Current.ShouldBe(NewFlightWizard.Step.Review);
        Painted(chrome, "wizard:create").ShouldBeTrue();

        var press = wizard.CreateAsync();
        // Working at once: the button is not there to press again, and a second Create is ignored.
        wizard.Presses.PendingOf("wizard:create").ShouldNotBeNull();
        Painted(chrome, "wizard:create").ShouldBeFalse();
        wizard.Status.ShouldBe("making 1 flight…");
        await wizard.CreateAsync();

        // Refused: the node's words in the status, back on the review with the button.
        create.SetException(new InvalidOperationException("the node is in maintenance"));
        await press;
        wizard.Status.ShouldBe("failed: the node is in maintenance");
        wizard.Current.ShouldBe(NewFlightWizard.Step.Review);
        wizard.IsOpen.ShouldBeTrue();
        Painted(chrome, "wizard:create").ShouldBeTrue();
    }

    [Fact]
    public async Task TheWizardsCreateIsTakenWhenTheNodeAnswersAndTheWizardCloses()
    {
        var create = new TaskCompletionSource<string>();
        var (chrome, renderer) = await OpenWizardAtReviewAsync(create);
        using var _ = renderer;
        var wizard = chrome.Flights.Wizard;

        var press = wizard.CreateAsync();
        wizard.Current.ShouldBe(NewFlightWizard.Step.Creating);
        create.SetResult("made ORB204; its planner is planning the legs");
        await press;

        wizard.IsOpen.ShouldBeFalse();
        wizard.Status.ShouldBe("made ORB204; its planner is planning the legs");
        wizard.Presses.IsTaken("wizard:create").ShouldBeTrue();
    }

    [Fact]
    public async Task TheWizardsCreateIssueIsWorkingAtOnceTakesNoSecondPressAndSaysTheNodesWords()
    {
        var create = new TaskCompletionSource<string>();
        var (chrome, renderer) = await OpenWizardAtReviewAsync(create);
        using var _ = renderer;
        var wizard = chrome.Flights.Wizard;
        var issue = new TaskCompletionSource<IssueInfo>();
        var asked = 0;
        wizard.CreateIssue = (_, _, _) =>
        {
            asked++;
            return issue.Task;
        };
        wizard.Back();
        wizard.Current.ShouldBe(NewFlightWizard.Step.Issues);
        wizard.BeginNewIssue();
        wizard.IssueTitle.Text = "Rings";

        Painted(chrome, "wizard:create-issue").ShouldBeTrue();
        var press = wizard.CreateIssueAsync();
        wizard.Presses.PendingOf("wizard:create-issue").ShouldNotBeNull();
        wizard.IssueStatus.ShouldBe("creating the issue…");
        Painted(chrome, "wizard:create-issue").ShouldBeFalse();
        await wizard.CreateIssueAsync();
        asked.ShouldBe(1);

        issue.SetException(new InvalidOperationException("gh is not signed in"));
        await press;
        wizard.IssueStatus.ShouldBe("failed: gh is not signed in");
        wizard.NewIssueOpen.ShouldBeTrue();
        Painted(chrome, "wizard:create-issue").ShouldBeTrue();

        // Taken: picked for the repo, and the form is gone.
        issue = new TaskCompletionSource<IssueInfo>();
        var second = wizard.CreateIssueAsync();
        issue.SetResult(new IssueInfo { Number = 300, Title = "Rings" });
        await second;
        wizard.NewIssueOpen.ShouldBeFalse();
        wizard.Picks.ShouldHaveSingleItem().Issue.Number.ShouldBe(300);
    }

    // ---- The autopilot switch ----

    [Fact]
    public async Task TheAutopilotSwitchShowsTheAskedStateDimmedUntilTheNodeTakesItThenTheTakenStateOrFlipsBackWithTheRefusal()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = NewChrome(renderer);
        var node = new LateNode();
        chrome.Autopilot.OnToggle = on => node.Ask($"autopilot {(on ? "on" : "off")}");
        chrome.Autopilot.On = false;
        Layout.Node Tree() => chrome.Fleet.Tree(new FleetScreen([], Now));

        // Pressed: the switch is lit (the asked state) at once and dimmed, with no hit, so no second press is taken.
        chrome.Autopilot.Shown.ShouldBeFalse();
        Click(Tree(), AutopilotSwitch.Action);
        node.Asked.ShouldBe(["autopilot on"]);
        chrome.Autopilot.Shown.ShouldBeTrue();
        Has(Tree(), AutopilotSwitch.Action).ShouldBeFalse();
        chrome.Autopilot.Tooltip.ShouldContain("Asking the node to turn autopilot on");

        // Taken: it keeps showing the asked state until the node's own look agrees, and clicks again, to turn it off.
        node.Answer(null);
        await Until(() => Has(Tree(), AutopilotSwitch.Action));
        chrome.Autopilot.Shown.ShouldBeTrue();
        chrome.Autopilot.On = true;
        chrome.Autopilot.Shown.ShouldBeTrue();
        chrome.Autopilot.Presses.IsTaken(AutopilotSwitch.Action).ShouldBeFalse();

        // Refused: the switch flips back to what the node says and its tooltip carries the node's words.
        Click(Tree(), AutopilotSwitch.Action);
        node.Asked[^1].ShouldBe("autopilot off");
        chrome.Autopilot.Shown.ShouldBeFalse();
        node.Answer("only the person may");
        await Until(() => Has(Tree(), AutopilotSwitch.Action));
        chrome.Autopilot.Shown.ShouldBeTrue();
        chrome.Autopilot.Refused.ShouldNotBeNull().ShouldContain("only the person may");
        chrome.Autopilot.Tooltip.ShouldContain("only the person may");
    }
}
