using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Pilot.Ui;
using SharpAstro.Png;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Ideas page: the rail's count of noted ideas, a one-line box that notes an idea with the page the person was on, the
/// repo question and the merge question as inline choices, each row's buttons as verbs on the node, planned and dropped folded
/// below, and long Markdown bodies that wrap at a narrow width.
/// </summary>
public sealed class IdeasPaneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Updated = new(2026, 10, 27, 9, 30, 0, TimeSpan.Zero);

    private static IdeaRow Idea(int number, string title, string body = "", IdeaState state = IdeaState.Noted, string repo = "pilot", string age = "3h", string origin = "ada") => new()
    {
        Id = IdeaIds.Id(number),
        Repo = repo,
        Title = title,
        Body = body,
        State = state,
        UpdatedAt = Updated.AddMinutes(number),
        Age = age,
        Origin = origin,
        Url = $"https://github.com/SharpAstro/{repo}/issues/{number}",
    };

    private static IdeasScreen Screen(IEnumerable<IdeaRow> ideas, IEnumerable<CardRow>? asks = null, params string[] repos) =>
        new([.. ideas], [.. asks ?? []], repos.Length > 0 ? repos : ["pilot", "tianwen", "DIR.Lib"], "live from the node", Live.At);

    private static IdeasScreen Sample() => Screen(
    [
        Idea(381, "Ideas pane", "Show **ideas** in a pane.\n\n- a list\n- a box"),
        Idea(380, "Rail count", "", age: "1d"),
        Idea(1829, "Tianwen night mode", "Keep the red.", repo: "tianwen"),
        Idea(370, "Old plan", "", IdeaState.Planned, age: "9d"),
        Idea(371, "Not now", "", IdeaState.Dropped, age: "12d"),
    ]);

    private static (PilotChrome<RgbaImage> Chrome, List<(string Verb, string Target, string[] Args)> Sent, List<Task> Tasks) Chrome(RgbaImageRenderer renderer, IdeaReply? reply = null)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var sent = new List<(string, string, string[])>();
        var tasks = new List<Task>();
        chrome.Ideas.Send = (verb, target, args, _) =>
        {
            lock (sent)
            {
                sent.Add((verb, target, args));
            }
            return Task.FromResult(reply ?? new IdeaReply(true));
        };
        // A click's task is the test's to await, not left running.
        chrome.Ideas.Track = (run, _) => tasks.Add(run());
        chrome.Show(PilotPage.Ideas);
        return (chrome, sent, tasks);
    }

    private static void Draw(PilotChrome<RgbaImage> chrome, IdeasScreen screen, float width = 1280, float height = 1400) =>
        chrome.Render(new SessionsScreen([], "test", Live.At), null, null, null, null, null, new RectF32(0, 0, width, height), null, screen);

    private static List<ClickableRegion> Regions(PilotChrome<RgbaImage> chrome, IdeasScreen screen)
    {
        Draw(chrome, screen);
        var regions = new List<ClickableRegion>();
        chrome.Ideas.CollectPaintedRegions(regions);
        return regions;
    }

    private static void Click(List<ClickableRegion> regions, string action) =>
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick!.Invoke(InputModifier.None);

    private static IEnumerable<string> Actions(List<ClickableRegion> regions) =>
        regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action);

    private static List<(string Text, RectF32 Bounds)> Texts(RgbaImageRenderer renderer, PilotChrome<RgbaImage> chrome, IdeasScreen screen, float width)
    {
        var arranged = Layout.Engine.Arrange(chrome.Ideas.Tree(screen), new Rect<float>(0, 0, width, 6000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        return [.. arranged
            .Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text })
            .Select(a => (((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value, new RectF32(a.Bounds.X, a.Bounds.Y, a.Bounds.Width, a.Bounds.Height)))];
    }

    private static string Spoken(RgbaImageRenderer renderer, PilotChrome<RgbaImage> chrome, IdeasScreen screen, float width = 1000) =>
        // A sentence is a leaf per word, each ending in its space: joined as they are, a sentence reads as it was written.
        string.Concat(Texts(renderer, chrome, screen, width).Select(t => t.Text));

    [Fact]
    public void TheRailShowsTheCountOfNotedIdeasAndSaysItOnHover()
    {
        IdeasScreen screen = Sample();
        screen.Noted.ShouldBe(3);
        PilotChrome<RgbaImage>.IdeasGlyph(screen).ShouldBe(("\U0001F4A1" + "3", "3 noted"));
        PilotChrome<RgbaImage>.IdeasGlyph(IdeasScreen.Empty).ShouldBe(("\U0001F4A1", null));
        PilotChrome<RgbaImage>.IdeasGlyph(null).Said.ShouldBeNull();
        PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.Ideas).Shortcut.ShouldBe(new KeyChord(InputKey.D0, InputModifier.Ctrl));

        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        Draw(chrome, screen);
        chrome.Page.ShouldBe(PilotPage.Ideas);
    }

    [Fact]
    public async Task EnterNotesTheIdeaWithThePageThePersonWasOnAndNoRepoPicker()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, _) = Chrome(renderer, new IdeaReply(true, Made: "pilot I382"));
        // Coming from a flight's page: that is the context the node is given.
        chrome.Show(PilotPage.Flights);
        chrome.Flights.OpenDetail("PIL381");
        chrome.Show(PilotPage.Ideas);
        chrome.Ideas.Context.ShouldNotBeNull().ShouldContain("Flights");

        var screen = Sample();
        Draw(chrome, screen);
        chrome.Ideas.NoteInput.InsertText("Cache the sky chart tiles");
        await chrome.Ideas.NoteInput.OnCommit!("Cache the sky chart tiles");

        sent.Count.ShouldBe(1);
        sent[0].Verb.ShouldBe(Verbs.NoteIdea);
        sent[0].Target.ShouldBe(Verbs.RepoTarget(Verbs.AnyRepo));
        sent[0].Args[0].ShouldBe("Cache the sky chart tiles");
        sent[0].Args[1].ShouldBe(chrome.Ideas.Context);
        chrome.Ideas.NoteInput.Text.ShouldBe("");
        // Filed in the shown repo: its bare id.
        chrome.Ideas.Said.ShouldBe("noted I382");
        // No repo picker: the box and its hint, nothing that names a repo to choose.
        Actions(Regions(chrome, screen)).ShouldNotContain(a => a.StartsWith("ideas:repo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIdeaBeingNotedIsInTheListAtOnceAsIdeatingAndTheNextOneIsNotedAlongside()
    {
        // The owner (2026-10-29): the box stayed typable while one was noted, and "working…" did not say whether a second would
        // queue; better the idea in the list at once, said to be "ideating" until it is filed.
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var answer = new TaskCompletionSource<IdeaReply>();
        var sent = 0;
        chrome.Ideas.Send = (_, _, _, _) =>
        {
            Interlocked.Increment(ref sent);
            return answer.Task;
        };
        var screen = Sample();
        Draw(chrome, screen);

        chrome.Ideas.NoteInput.InsertText("Scale status images to the terminal's width, up to three times");
        var first = chrome.Ideas.NoteInput.OnCommit!("");
        chrome.Ideas.NoteInput.InsertText("A second idea");
        var second = chrome.Ideas.NoteInput.OnCommit!("");

        // Both went to the node at once: the second did not wait for the first.
        sent.ShouldBe(2);
        var line = Spoken(renderer, chrome, screen, 1280);
        // Both in the list, the newest first, each "ideating…" in place of an id; the box keeps its hint, not "working…".
        line.ShouldContain("ideating…A second idea");
        line.ShouldContain("ideating…Scale status images to the terminal's width, up to three times");
        line.IndexOf("A second idea", StringComparison.Ordinal).ShouldBeLessThan(line.IndexOf("Scale status images", StringComparison.Ordinal));
        line.ShouldNotContain("working…");
        line.ShouldContain("Ctrl+Enter notes it · Enter starts a line · Claude picks the repo");

        answer.SetResult(new IdeaReply(true, Made: "pilot I448"));
        await Task.WhenAll(first, second);
        Spoken(renderer, chrome, screen, 1280).ShouldNotContain("ideating…");
        // Each idea says its repo first on its second line, whichever repo the page shows (the owner, 2026-10-29).
        var spoken = Spoken(renderer, chrome, screen, 1280);
        spoken.ShouldContain("pilot · ");
        spoken.ShouldContain("tianwen · ");
    }

    [Fact]
    public async Task AMultiLineIdeaIsNotedWhole()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, _) = Chrome(renderer, new IdeaReply(true, Made: "pilot I382"));
        Draw(chrome, Sample());
        chrome.Ideas.NoteInput.InsertText("Cache tiles\nKeyed by chart centre and zoom.\n\n- evict by age");
        chrome.Ideas.NoteInput.LineCount.ShouldBe(4);
        await chrome.Ideas.NoteInput.OnCommit!("");

        sent[0].Args[0].ShouldBe("Cache tiles\nKeyed by chart centre and zoom.\n\n- evict by age");
        chrome.Ideas.NoteInput.Text.ShouldBe("");
    }

    [Fact]
    public async Task AnIdeaFiledInAnotherRepoIsNamedWithItsRepo()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer, new IdeaReply(true, Made: "tianwen I1829"));
        Draw(chrome, Sample());
        chrome.Ideas.NoteInput.InsertText("Dither the preview");
        await chrome.Ideas.NoteInput.OnCommit!("");
        chrome.Ideas.Said.ShouldBe("noted tianwen I1829");
        chrome.Ideas.Name(Idea(1829, "x", repo: "tianwen")).ShouldBe("tianwen I1829");
        chrome.Ideas.Name(Idea(381, "x")).ShouldBe("I381");
        var spoken = Spoken(renderer, chrome, Sample());
        spoken.ShouldContain("tianwen I1829");
        spoken.ShouldContain("I381");
        spoken.ShouldNotContain("pilot I381");
    }

    [Fact]
    public async Task ARefusedNoteIsSaidInRedAndTheBoxIsStillEmptied()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer, new IdeaReply(false, "gh is not signed in"));
        Draw(chrome, Sample());
        chrome.Ideas.NoteInput.InsertText("x");
        await chrome.Ideas.NoteInput.OnCommit!("");
        chrome.Ideas.Said.ShouldBe("not noted: gh is not signed in");
        Spoken(renderer, chrome, Sample()).ShouldContain("not noted: gh is not signed in");
    }

    [Fact]
    public void TheRepoQuestionShowsInlineAsFileInButtonsAndAnAnswerIsTheCardsChoice()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var card = new CardRow
        {
            Id = "c7", Bin = CardBin.ForYou, Kind = CardKinds.IdeaRepo, Subject = "node", Title = "Where does “Dither the preview” belong?",
            Choices = [new CardChoice { Label = "File in tianwen", Verb = "x", Target = "x" }, new CardChoice { Label = "File in DIR.Lib", Verb = "x", Target = "x" }],
            At = Live.At, State = CardState.Open,
        };
        var answered = new List<(string Id, int Number)>();
        chrome.Ideas.OnAnswer = (c, n) => answered.Add((c.Id, n));
        var screen = Screen([], [card]);

        var regions = Regions(chrome, screen);
        Actions(regions).ShouldContain("ideas:ask:c7:1");
        Actions(regions).ShouldContain("ideas:ask:c7:2");
        var spoken = Spoken(renderer, chrome, screen);
        spoken.ShouldContain("File in tianwen");
        spoken.ShouldContain("File in DIR.Lib");

        Click(regions, "ideas:ask:c7:2");
        answered.ShouldBe([("c7", 2)]);
    }

    [Fact]
    public void TheMergeQuestionShowsBothVersionsWithTheMergeAsTheFirstChoice()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var card = new CardRow
        {
            Id = "c9", Bin = CardBin.ForYou, Kind = CardKinds.IdeaConflict, Subject = "idea/pilot/I381", Title = "I381: Ideas pane",
            Detail = "Yours:\nShow ideas in a pane\n\nOn GitHub:\nShow ideas in a rail pane",
            Choices =
            [
                new CardChoice { Label = "Keep the merge", Verb = "x", Target = "x" },
                new CardChoice { Label = "Keep mine", Verb = "x", Target = "x" },
                new CardChoice { Label = "Keep GitHub's", Verb = "x", Target = "x" },
            ],
            At = Live.At, State = CardState.Open,
        };
        var answered = new List<int>();
        chrome.Ideas.OnAnswer = (_, n) => answered.Add(n);
        var screen = Screen([Idea(381, "Ideas pane")], [card]);
        var spoken = Spoken(renderer, chrome, screen);
        spoken.ShouldContain("Show ideas in a pane");
        spoken.ShouldContain("Show ideas in a rail pane");

        var regions = Regions(chrome, screen);
        Actions(regions).Where(a => a.StartsWith("ideas:ask:c9", StringComparison.Ordinal)).ShouldBe(["ideas:ask:c9:1", "ideas:ask:c9:2", "ideas:ask:c9:3"]);
        Click(regions, "ideas:ask:c9:1");
        answered.ShouldBe([1]);
    }

    [Fact]
    public async Task EachRowsButtonsAreVerbsOnTheNode()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, sent, tasks) = Chrome(renderer);
        var opened = new List<string>();
        chrome.Ideas.OnOpen = opened.Add;
        var screen = Sample();
        var idea = screen.Ideas.First(i => i.Number == 381);

        var regions = Regions(chrome, screen);
        foreach (var verb in new[] { "plan", "drop", "edit", "tidy", "move" })
        {
            Actions(regions).ShouldContain($"ideas:{verb}:pilot I381");
        }
        Actions(regions).ShouldContain("ideas:open:pilot I381");
        Click(regions, "ideas:open:pilot I381");
        opened.ShouldBe([idea.Url]);

        // Plan asks the node where it belongs; no 'next open milestone' is left (the owner, 2026-10-29: "not sure what it wants from me").
        Click(regions, "ideas:plan:pilot I381");
        chrome.Ideas.OpenAction.ShouldBe(("pilot I381", IdeasView<RgbaImage>.Mode.Plan));
        await Task.WhenAll(tasks);
        sent.Last().Verb.ShouldBe(Verbs.ProposeMilestone);
        var said = Spoken(renderer, chrome, screen, 1280);
        said.ShouldContain("Plan makes I381 a flight: its issue goes into a milestone and a planner starts on it.");
        said.ShouldNotContain("next open");
        Actions(Regions(chrome, screen)).ShouldNotContain("ideas:plan-next:pilot I381");

        // Drop asks why: nothing goes without one.
        var before = sent.Count;
        chrome.Ideas.OpenOn(idea, IdeasView<RgbaImage>.Mode.Drop);
        Draw(chrome, screen);
        await chrome.Ideas.RowInput.OnCommit!("  ");
        sent.Count.ShouldBe(before);
        chrome.Ideas.OpenAction.ShouldBe(("pilot I381", IdeasView<RgbaImage>.Mode.Drop));
        chrome.Ideas.Said.ShouldBe("say why it is dropped");
        await chrome.Ideas.RowInput.OnCommit!("duplicate of I12");
        (sent.Last().Verb, sent.Last().Target).ShouldBe((Verbs.DropIdea, "idea/pilot/I381"));
        sent.Last().Args.ShouldBe(["duplicate of I12"]);

        // Edit: the multi-line box holds the whole idea (title, then body), and the save is from the row's updated_at.
        chrome.Ideas.OpenOn(idea, IdeasView<RgbaImage>.Mode.Edit);
        Draw(chrome, screen);
        chrome.Ideas.EditInput.Text.ShouldBe("Ideas pane\n" + idea.Body);
        chrome.Ideas.EditInput.IsMultiLine.ShouldBeTrue();
        Actions(Regions(chrome, screen)).ShouldContain("ideas:edit-github:pilot I381");
        chrome.Ideas.EditInput.SetCaret(0, 10);
        chrome.Ideas.EditInput.InsertText(" in the GUI");
        await chrome.Ideas.EditInput.OnCommit!(chrome.Ideas.EditInput.Text);
        var edit = sent.Last();
        edit.Verb.ShouldBe(Verbs.EditIdea);
        edit.Target.ShouldBe("idea/pilot/I381");
        edit.Args[0].ShouldBe(idea.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        edit.Args[1].ShouldBe("Ideas pane in the GUI\n" + idea.Body);

        // Move to repo: the node's other repos.
        chrome.Ideas.OpenOn(idea, IdeasView<RgbaImage>.Mode.Move);
        regions = Regions(chrome, screen);
        Actions(regions).ShouldNotContain("ideas:move:pilot I381:pilot");
        tasks.Clear();
        Click(regions, "ideas:move:pilot I381:tianwen");
        await Task.WhenAll(tasks);
        (sent.Last().Verb, sent.Last().Target).ShouldBe((Verbs.MoveIdea, "idea/pilot/I381"));
        sent.Last().Args.ShouldBe(["tianwen"]);
    }

    private static MilestoneProposal Proposal(string? title = "v2.3", string? newTitle = null, string? newDescription = null) => new(
        [new MilestoneChoice("v2.2", "", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), 2), new MilestoneChoice("v2.3", "", new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero), 7), new MilestoneChoice("Rings", "", null, 0)],
        title, newTitle, newDescription, "it touches the pane v2.3 reworks");

    private static async Task<(PilotChrome<RgbaImage> Chrome, List<(string Verb, string Target, string[] Args)> Sent, IdeasScreen Screen, IdeaRow Idea, RgbaImageRenderer Renderer)> OpenPlan(MilestoneProposal? proposal, bool ok = true)
    {
        var renderer = new RgbaImageRenderer(1280, 1200);
        var (chrome, sent, tasks) = Chrome(renderer, ok ? new IdeaReply(true, Milestones: proposal) : new IdeaReply(false, "claude is offline"));
        var screen = Sample();
        var idea = screen.Ideas.First(i => i.Number == 381);
        Draw(chrome, screen, 1280, 1200);
        chrome.Ideas.OpenOn(idea, IdeasView<RgbaImage>.Mode.Plan);
        Draw(chrome, screen, 1280, 1200);
        Spoken(renderer, chrome, screen, 1280).ShouldContain("Claude is choosing a milestone…");
        await Task.WhenAll(tasks);
        Draw(chrome, screen, 1280, 1200);
        sent.Last().Verb.ShouldBe(Verbs.ProposeMilestone);
        return (chrome, sent, screen, idea, renderer);
    }

    [Fact]
    public async Task PlanListsTheOpenMilestonesWithTheProposalFirstMarkedAndChosen()
    {
        var (chrome, sent, screen, idea, renderer) = await OpenPlan(Proposal());
        using var _ = renderer;
        var text = Spoken(renderer, chrome, screen, 1280);
        var proposed = text.IndexOf("● v2.3 · proposed · due 5 Dec 2026 · 7 open", StringComparison.Ordinal);
        proposed.ShouldBeGreaterThanOrEqualTo(0);
        text.IndexOf("v2.2 · due 1 Nov 2026 · 2 open", StringComparison.Ordinal).ShouldBeGreaterThan(proposed);
        text.ShouldContain("it touches the pane v2.3 reworks");
        text.ShouldContain("Rings · 0 open");

        Click(Regions(chrome, screen), "ideas:plan-go:pilot I381");
        await Task.Yield();
        sent.Last().Verb.ShouldBe(Verbs.PlanIdea);
        sent.Last().Args.ShouldBe(["v2.3"]);

        // Enter plans the one chosen, after another pick.
        chrome.Ideas.OpenOn(idea, IdeasView<RgbaImage>.Mode.Plan);
        Draw(chrome, screen, 1280, 1200);
        Click(Regions(chrome, screen), "ideas:milestone:pilot I381:m:Rings");
        await chrome.Ideas.RowInput.OnCommit!("");
        sent.Last().Args.ShouldBe(["Rings"]);
    }

    [Fact]
    public async Task TypingFiltersTheListIgnoringCaseAndOffersTheTypedTextAsANewMilestone()
    {
        var (chrome, sent, screen, idea, renderer) = await OpenPlan(Proposal());
        using var _ = renderer;
        chrome.Ideas.RowInput.Text = "RING";
        var text = Spoken(renderer, chrome, screen, 1280);
        text.ShouldContain("Rings");
        text.ShouldNotContain("v2.2");
        text.ShouldNotContain("new milestone: RING");

        chrome.Ideas.RowInput.Text = "Lunar phases";
        text = Spoken(renderer, chrome, screen, 1280);
        text.ShouldContain("new milestone: Lunar phases");
        text.ShouldNotContain("Rings");
        await chrome.Ideas.RowInput.OnCommit!("Lunar phases");
        sent.Last().Verb.ShouldBe(Verbs.PlanIdea);
        sent.Last().Args.ShouldBe([Verbs.NewMilestoneArg, "Lunar phases"]);
    }

    [Fact]
    public async Task ASuggestedNewMilestoneShowsItsDescriptionAndIsMadeOnlyWhenPlanned()
    {
        var suggested = Proposal(title: null, newTitle: "Observatory", newDescription: "Dome and roof control");
        var (chrome, sent, screen, idea, renderer) = await OpenPlan(suggested);
        var text = Spoken(renderer, chrome, screen, 1280);
        text.ShouldContain("● new milestone: Observatory · proposed");
        text.ShouldContain("Dome and roof control");
        await chrome.Ideas.RowInput.OnCommit!("");
        sent.Last().Args.ShouldBe([Verbs.NewMilestoneArg, "Observatory", "Dome and roof control"]);
        renderer.Dispose();

        // Declined: another pick goes, and no new milestone with it.
        (chrome, sent, screen, idea, renderer) = await OpenPlan(suggested);
        using var _ = renderer;
        Click(Regions(chrome, screen), "ideas:milestone:pilot I381:m:v2.2");
        Click(Regions(chrome, screen), "ideas:plan-go:pilot I381");
        await Task.Yield();
        sent.Last().Args.ShouldBe(["v2.2"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithNoProposalNothingIsChosenAndPlanWaitsForAPick(bool offline)
    {
        var (chrome, sent, screen, idea, renderer) = await OpenPlan(offline ? null : new MilestoneProposal([new MilestoneChoice("v2.3", "", null, 1)], Note: "Claude gave no answer"), ok: !offline);
        using var _ = renderer;
        var text = Spoken(renderer, chrome, screen, 1280);
        text.ShouldNotContain("●  ");
        text.ShouldNotContain("● v2.3");
        var actions = Actions(Regions(chrome, screen)).ToList();
        actions.ShouldNotContain("ideas:plan-go:pilot I381");
        if (!offline)
        {
            actions.ShouldContain("ideas:milestone:pilot I381:m:v2.3");
        }
        var before = sent.Count;
        await chrome.Ideas.RowInput.OnCommit!("");
        sent.Count.ShouldBe(before);
        chrome.Ideas.Said.ShouldContain("pick a milestone");

        if (!offline)
        {
            Click(Regions(chrome, screen), "ideas:milestone:pilot I381:m:v2.3");
            Actions(Regions(chrome, screen)).ShouldContain("ideas:plan-go:pilot I381");
        }
    }

    [Fact]
    public async Task APlanOpenedAfterATidyStartsFromItsMilestoneProposalAndTheTidyShowsIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 1200);
        var tidy = new IdeaTidyProposal("Ideas pane", "Show ideas.", "ideas pane", "show ideas", Updated.AddHours(1)) { Milestone = Proposal() };
        var (chrome, sent, tasks) = Chrome(renderer, new IdeaReply(true, Tidy: tidy));
        var screen = Sample();
        Click(Regions(chrome, screen), "ideas:tidy:pilot I381");
        await Task.WhenAll(tasks);
        Draw(chrome, screen, 1280, 1200);
        Spoken(renderer, chrome, screen, 1280).ShouldContain("v2.3");

        var asked = sent.Count;
        Click(Regions(chrome, screen), "ideas:plan:pilot I381");
        await Task.WhenAll(tasks);
        sent.Count.ShouldBe(asked);
        Draw(chrome, screen, 1280, 1200);
        Spoken(renderer, chrome, screen, 1280).ShouldContain("● v2.3 · proposed");
    }

    [Fact]
    public async Task ThePlanListDrawsNoTextOverText()
    {
        var (chrome, _, screen, _, renderer) = await OpenPlan(Proposal(newTitle: "Observatory", newDescription: "Dome and roof control, the weather station and the safety monitor"));
        using var _r = renderer;
        foreach (var width in new[] { 480f, 1280f })
        {
            var arranged = Layout.Engine.Arrange(chrome.Ideas.Tree(screen), new Rect<float>(0, 0, width, 6000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
            TextOverlap.Faults(arranged).Where(f => f.Bounds.Y > TopBar.Height).Select(f => f.Line).ShouldBeEmpty($"at {width} wide");
        }
    }

    [Fact]
    public async Task TidyShowsADiffWithSaveDiscardAndEditAndSavesFromTheVersionClaudeRead()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var read = Updated.AddHours(1);
        var proposal = new IdeaTidyProposal("Ideas pane", "Show ideas in a pane.\n\n- a list", "ideas pane", "show ideas in a pane", read);
        var (chrome, sent, tasks) = Chrome(renderer, new IdeaReply(true, Tidy: proposal));
        var screen = Sample();
        var idea = screen.Ideas.First(i => i.Number == 381);

        Click(Regions(chrome, screen), "ideas:tidy:pilot I381");
        await Task.WhenAll(tasks);
        sent.Last().Verb.ShouldBe(Verbs.TidyIdea);
        var regions = Regions(chrome, screen);
        Actions(regions).ShouldContain("ideas:tidy-save:pilot I381");
        Actions(regions).ShouldContain("ideas:tidy-discard:pilot I381");
        Actions(regions).ShouldContain("ideas:tidy-edit:pilot I381");
        var spoken = Spoken(renderer, chrome, screen);
        spoken.ShouldContain("ideas pane");
        spoken.ShouldContain("- a list");

        // Discard sends nothing.
        var before = sent.Count;
        Click(regions, "ideas:tidy-discard:pilot I381");
        sent.Count.ShouldBe(before);
        chrome.Ideas.OpenAction.ShouldBeNull();

        // Save goes through the up-to-date path with the updated_at Claude read.
        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:tidy:pilot I381");
        await Task.WhenAll(tasks);
        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:tidy-save:pilot I381");
        await Task.WhenAll(tasks);
        sent.Last().Verb.ShouldBe(Verbs.SaveTidyIdea);
        sent.Last().Args[0].ShouldBe(read.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        sent.Last().Args[1].ShouldBe("Ideas pane\nShow ideas in a pane.\n\n- a list");

        // Edit takes the proposal's title into the box and keeps its body.
        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:tidy:pilot I381");
        await Task.WhenAll(tasks);
        Click(Regions(chrome, screen), "ideas:tidy-edit:pilot I381");
        chrome.Ideas.OpenAction.ShouldBe(("pilot I381", IdeasView<RgbaImage>.Mode.Edit));
        chrome.Ideas.EditInput.Text.ShouldBe("Ideas pane\nShow ideas in a pane.\n\n- a list");
        chrome.Ideas.EditInput.SetCaret(0, 10);
        chrome.Ideas.EditInput.InsertText("!");
        await chrome.Ideas.EditInput.OnCommit!(chrome.Ideas.EditInput.Text);
        sent.Last().Verb.ShouldBe(Verbs.EditIdea);
        sent.Last().Args[0].ShouldBe(read.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        sent.Last().Args[1].ShouldBe("Ideas pane!\nShow ideas in a pane.\n\n- a list");
        _ = idea;
    }

    [Fact]
    public void ThePlannedAndDroppedAreFoldedBelowUntilOpened()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var screen = Sample();

        var spoken = Spoken(renderer, chrome, screen);
        spoken.ShouldContain("Planned and dropped (2)");
        spoken.ShouldNotContain("Old plan");
        spoken.ShouldNotContain("Not now");
        // The header counts all three states.
        spoken.ShouldContain("3 noted · 1 planned · 1 dropped");

        Click(Regions(chrome, screen), "ideas:folded");
        spoken = Spoken(renderer, chrome, screen);
        spoken.ShouldContain("Old plan");
        spoken.ShouldContain("Not now");
        Actions(Regions(chrome, screen)).ShouldContain("ideas:open:pilot I370");
        // Still reached by id: their ids are on their rows.
        spoken.ShouldContain("I370");
        spoken.ShouldContain("I371");
    }

    private const string Long = "This idea is a long one: the pane should render **everything** the person wrote, wrapped at the width of the window, including `a_very_long_identifier_name_that_does_not_break` and a link https://github.com/SharpAstro/pilot/issues/381#issuecomment-123456789012345 that is far wider than a phone.\n\n"
        + "1. first step with enough words to need a second line when the window is narrow\n2. second step\n\n- bullet one with a long explanation that runs on and on past the edge of a narrow window\n- bullet two\n\n> a quoted remark that is also long enough to need wrapping at a narrow width";

    [Fact]
    public void ALongMarkdownBodyWrapsInsideTheWindowAtANarrowWidth()
    {
        using var renderer = new RgbaImageRenderer(420, 900);
        var (chrome, _, _) = Chrome(renderer);
        var screen = Screen([Idea(381, "A long idea with a title that must wrap too when the window is this narrow", Long)]);
        const float width = 360f;

        var texts = Texts(renderer, chrome, screen, width);
        texts.ShouldContain(t => t.Text.Contains("everything", StringComparison.Ordinal));
        // The page's body: the top bar is every page's own, whose counts line is as wide as it is at any narrow width.
        foreach (var (text, bounds) in texts.Where(t => t.Bounds.Y >= TopBar.Height))
        {
            (bounds.X + bounds.Width).ShouldBeLessThanOrEqualTo(width + 0.5f, $"“{text}” runs past the window");
            bounds.X.ShouldBeGreaterThanOrEqualTo(0f);
        }
    }

    [Fact]
    public async Task CapturesOfTheQuestionsAndTheOpenActionsInBothPalettes()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        var read = Updated.AddHours(1);
        var proposal = new IdeaTidyProposal("Ideas pane", "Show **ideas** in a pane.\n\n- a list\n- a box", "ideas pane", "show ideas in a pane\n- a list", read);
        var repoCard = new CardRow
        {
            Id = "c7", Bin = CardBin.ForYou, Kind = CardKinds.IdeaRepo, Subject = "node", Title = "Where does “Dither the preview” belong?",
            Choices = [new CardChoice { Label = "File in tianwen", Verb = "x", Target = "x" }, new CardChoice { Label = "File in DIR.Lib", Verb = "x", Target = "x" }],
            At = Live.At, State = CardState.Open,
        };
        var mergeCard = new CardRow
        {
            Id = "c9", Bin = CardBin.ForYou, Kind = CardKinds.IdeaConflict, Subject = "idea/pilot/I380", Title = "I380: Rail count",
            Detail = "Yours:\nShow the count on the rail\n\nOn GitHub:\nShow the count of noted ideas on the rail",
            Choices = [new CardChoice { Label = "Keep the merge", Verb = "x", Target = "x" }, new CardChoice { Label = "Keep mine", Verb = "x", Target = "x" }, new CardChoice { Label = "Keep GitHub's", Verb = "x", Target = "x" }],
            At = Live.At, State = CardState.Open,
        };
        var screen = Screen(Sample().Ideas, [repoCard, mergeCard]);
        foreach (var night in new[] { false, true })
        {
            using var renderer = new RgbaImageRenderer(1280, 1000);
            var (chrome, _, tasks) = Chrome(renderer, new IdeaReply(true, Tidy: proposal));
            chrome.Night.On = night;
            Draw(chrome, screen, 1280, 1000);
            var idea = screen.Ideas.First(i => i.Number == 381);
            chrome.Ideas.OpenOn(idea, IdeasView<RgbaImage>.Mode.Tidy);
            await Task.WhenAll(tasks);
            Draw(chrome, screen, 1280, 1000);
            if (dir is { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(dir, $"ideas-tidy-diff-wide-{(night ? "night" : "dark")}.png"), PngWriter.Encode(renderer.Surface.Pixels, 1280, 1000));
            }
            chrome.Ideas.OpenOn(screen.Ideas.First(i => i.Number == 380), IdeasView<RgbaImage>.Mode.Plan);
            Draw(chrome, screen, 1280, 1000);
            if (dir is { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(dir, $"ideas-questions-and-actions-wide-{(night ? "night" : "dark")}.png"), PngWriter.Encode(renderer.Surface.Pixels, 1280, 1000));
            }
        }
    }

    [Fact]
    public void CapturesOfAMultiLineIdeaInTheNoteBoxAndTheEditBoxNarrowAndWide()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        const string typed = "Cache the sky chart tiles\nKeyed by chart centre and zoom, so a pan\nre-uses what it already drew.\n\n- evict by age\n- cap at 200 MB";
        foreach (var (size, width, height) in new[] { ("narrow", 480, 900), ("wide", 1280, 900) })
        {
            using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
            var (chrome, _, _) = Chrome(renderer);
            var screen = Sample();
            chrome.Ideas.NoteInput.InsertText(typed);
            Draw(chrome, screen, width, height);
            // The box grew to the lines typed, and the caret is on the last one.
            chrome.Ideas.NoteInput.LineCount.ShouldBe(6);
            chrome.Ideas.NoteInput.Line.ShouldBe(5);
            if (dir is { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(dir, $"ideas-multiline-box-{size}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }

            chrome.Ideas.OpenOn(screen.Ideas.First(i => i.Number == 381), IdeasView<RgbaImage>.Mode.Edit);
            Draw(chrome, screen, width, height);
            if (dir is { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(dir, $"ideas-multiline-edit-{size}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
            }
        }
    }

    [Fact]
    public void CapturesOfTheEmptyManyAndLongPagesAtNarrowAndWideSizesInBothPalettes()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        var many = Screen(Enumerable.Range(1, 14).Select(n => Idea(n, $"Idea number {n}: a title of some length", n % 3 == 0 ? "" : "A body with **bold**, `code` and a list:\n\n- one\n- two", age: $"{n}h", repo: n % 5 == 0 ? "tianwen" : "pilot"))
            .Append(Idea(90, "Planned one", "", IdeaState.Planned)).Append(Idea(91, "Dropped one", "", IdeaState.Dropped)));
        var long1 = Screen([Idea(381, "A long idea with a title that must wrap too when the window is narrow", Long), Idea(380, "Another", Long, age: "2d")]);
        foreach (var (name, screen) in new[] { ("empty", IdeasScreen.Empty), ("many", many), ("long", long1) })
        {
            foreach (var (size, width, height) in new[] { ("narrow", 480, 900), ("wide", 1280, 900) })
            {
                foreach (var night in new[] { false, true })
                {
                    using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                    var (chrome, _, _) = Chrome(renderer);
                    chrome.Night.On = night;
                    // The folded section, open, so the capture shows it.
                    chrome.Ideas.ShowFolded = name == "many";
                    Draw(chrome, screen, width, height);
                    chrome.Ideas.Night.ShouldBeSameAs(chrome.Night);
                    (chrome.Night.Palette is not null).ShouldBe(night);
                    if (dir is { Length: > 0 })
                    {
                        File.WriteAllBytes(Path.Combine(dir, $"ideas-{name}-{size}-{(night ? "night" : "dark")}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
                    }
                }
            }
        }
    }
}
