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
    public void TheTitleNamesTheNodeAndNoNodePillIsOfferedWithSeveralNodes()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        chrome.Ideas.Nodes = new NodeFilter { Choices = [new NodeMark("here", "/sock", "DE1", Here: true), new NodeMark("se1", "http://se1", "SE1", Here: false)] };
        var screen = Sample();

        Texts(renderer, chrome, screen, 1280).Select(t => t.Text).ShouldContain("DE1's ideas (this machine)");
        Actions(Regions(chrome, screen)).ShouldNotContain(a => a.StartsWith("nodes:", StringComparison.Ordinal));
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

    private static async Task<(PilotChrome<RgbaImage> Chrome, List<(string Verb, string Target, string[] Args)> Sent, IdeasScreen Screen, IdeaRow Idea, RgbaImageRenderer Renderer)> OpenPlan(MilestoneProposal? proposal, bool ok = true, TaskCompletionSource? hold = null)
    {
        var renderer = new RgbaImageRenderer(1280, 1200);
        var (chrome, sent, tasks) = Chrome(renderer, ok ? new IdeaReply(true, Milestones: proposal) : new IdeaReply(false, "claude is offline"));
        var screen = Sample();
        var idea = screen.Ideas.First(i => i.Number == 381);
        // Claude's answer is held back until the test has seen the page waiting for it.
        var gate = hold ?? new TaskCompletionSource();
        var answer = chrome.Ideas.Send!;
        chrome.Ideas.Send = async (verb, target, args, token) =>
        {
            var reply = await answer(verb, target, args, token);
            if (verb == Verbs.ProposeMilestone)
            {
                await gate.Task;
            }
            return reply;
        };
        Draw(chrome, screen, 1280, 1200);
        chrome.Ideas.OpenOn(idea, IdeasView<RgbaImage>.Mode.Plan);
        Draw(chrome, screen, 1280, 1200);
        Spoken(renderer, chrome, screen, 1280).ShouldContain("Claude is choosing a milestone…");
        if (hold is not null)
        {
            return (chrome, sent, screen, idea, renderer);
        }
        gate.SetResult();
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
    public async Task TypingWhileClaudeIsStillChoosingOffersAndPlansNothing()
    {
        var hold = new TaskCompletionSource();
        var (chrome, sent, screen, idea, renderer) = await OpenPlan(Proposal(), hold: hold);
        using var _ = renderer;
        chrome.Ideas.RowInput.Text = "v2";
        var text = Spoken(renderer, chrome, screen, 1280);
        text.ShouldContain("Claude is choosing a milestone…");
        text.ShouldNotContain("new milestone: v2");
        Actions(Regions(chrome, screen)).ShouldNotContain("ideas:plan-go:pilot I381");
        var before = sent.Count;
        await chrome.Ideas.RowInput.OnCommit!("v2");
        sent.Count.ShouldBe(before);
        sent.Any(s => s.Verb == Verbs.PlanIdea).ShouldBeFalse();
        chrome.Ideas.Said.ShouldContain("still choosing");

        // The answer lands: the filter now narrows the open milestones.
        hold.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Draw(chrome, screen, 1280, 1200);
        text = Spoken(renderer, chrome, screen, 1280);
        text.ShouldContain("v2.3");
        text.ShouldContain("v2.2");
        text.ShouldNotContain("new milestone: v2");
        Actions(Regions(chrome, screen)).ShouldContain("ideas:plan-go:pilot I381");
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
        text.ShouldNotContain("● ");
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

    // ---- The board: Unsorted pinned, No milestone, every milestone ----

    private const string LongTitle = "A very long title for an issue that goes on well past the width of a narrow window, with a_path/that/has/no/spaces/at/all/in/it/to/break";

    private static IssueRow Issue(int number, string title, string? milestone, string repo = "pilot", string? flight = null, IssueReadiness? readiness = null) => new()
    {
        Repo = repo,
        Number = number,
        Title = title,
        Milestone = milestone,
        UpdatedAt = Updated.AddMinutes(number),
        Age = "2d",
        Author = "ada",
        Url = $"https://github.com/SharpAstro/{repo}/issues/{number}",
        Flight = flight,
        Readiness = readiness,
    };

    private static MilestoneRow Milestone(int number, string title, int open, int closed, DateTimeOffset? due = null, string repo = "pilot") =>
        new() { Repo = repo, Number = number, Title = title, OpenIssues = open, ClosedIssues = closed, DueOn = due };

    // Ideas (noted, one of them checked ready), milestones with and without issues, issues in none, one flown by a flight, long titles.
    private static IdeasScreen BoardScreen() => Screen(Sample().Ideas) with
    {
        Milestones =
        [
            Milestone(1, IdeaIds.Milestone, 3, 0),
            Milestone(2, "v2.3", 3, 5, new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero)),
            Milestone(3, "Rings", 0, 0),
        ],
        Issues =
        [
            Issue(381, "Ideas pane", IdeaIds.Milestone, readiness: new IssueReadiness(true, ["one goal", "tests can be named"], Live.At.AddMinutes(-5))),
            Issue(380, "Rail count", IdeaIds.Milestone),
            Issue(1829, "Tianwen night mode", IdeaIds.Milestone, repo: "tianwen"),
            Issue(12, "Resize the split " + LongTitle, "v2.3"),
            Issue(13, "Flown issue", "v2.3", flight: "PIL13"),
            Issue(14, "Checked issue", "v2.3", readiness: new IssueReadiness(false, ["two goals in one issue", "no test can be named"], Live.At.AddHours(-2))),
            Issue(20, "Loose issue " + LongTitle, null),
            Issue(21, "Another loose one", null),
        ],
    };

    private static ClickableRegion Region(List<ClickableRegion> regions, string action) =>
        regions.First(r => r.Result is HitResult.ButtonHit b && b.Action == action);

    [Fact]
    public void TheListIsTheDefaultViewOnTheSharedSwitchAndItsOtherViewsShowTheListForNow()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var screen = BoardScreen();
        chrome.Ideas.ViewMode.ShouldBe(IdeasViewMode.List);
        var regions = Regions(chrome, screen);
        Actions(regions).Where(a => a.StartsWith("ideas:view:", StringComparison.Ordinal)).ShouldBe(["ideas:view:list", "ideas:view:tree", "ideas:view:canvas"]);
        // One height and one middle with the Flights page's switch: the same component, so the same segments.
        Region(regions, "ideas:view:list").Height.ShouldBe(AboutCard.HeaderButton);
        Spoken(renderer, chrome, screen, 1280).ShouldNotContain("not built yet");

        Click(regions, "ideas:view:tree");
        chrome.Ideas.ViewMode.ShouldBe(IdeasViewMode.Tree);
        Spoken(renderer, chrome, screen, 1280).ShouldContain("Tree grid is not built yet: showing the list");
        Spoken(renderer, chrome, screen, 1280).ShouldContain("Unsorted (Ideas)");
    }

    [Fact]
    public void UnsortedIsPinnedOnTopThenNoMilestoneThenEveryMilestoneWithItsCountsAndProgress()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var screen = BoardScreen();
        var spoken = Spoken(renderer, chrome, screen, 1280);

        chrome.Ideas.Board.Sections.Select(s => s.Key).ShouldBe(["unsorted", "none", "m:pilot/v2.3", "m:pilot/Rings"]);
        // The Ideas milestone is the Unsorted section, not one of the milestones.
        chrome.Ideas.Board.Sections.ShouldNotContain(s => s.Title == IdeaIds.Milestone);
        var order = new[] { "Unsorted (Ideas)", "No milestone", "v2.3", "Rings" }.Select(t => spoken.IndexOf(t, StringComparison.Ordinal)).ToArray();
        order.ShouldAllBe(i => i >= 0);
        order.ShouldBe([.. order.Order()]);
        spoken.ShouldContain("3 noted");
        spoken.ShouldContain("2 open");
        spoken.ShouldContain("3 open · 5 closed");
        spoken.ShouldContain("due 5 Dec 2026");
        spoken.ShouldContain("0 open · 0 closed");
        chrome.Ideas.Board.Sections.Single(s => s.Key == "m:pilot/v2.3").Progress.ShouldBe(5f / 8f);
        // A milestone's issues sit under it; the issue of another list is not duplicated there.
        chrome.Ideas.Board.Sections.Single(s => s.Key == "m:pilot/v2.3").Items.Select(i => i.Number).ShouldBe([14, 13, 12]);
        chrome.Ideas.Board.Sections.Single(s => s.Key == "none").Items.Select(i => i.Number).ShouldBe([21, 20]);
        // The flown issue shows its flight code, as a link that opens the flight.
        var flown = new List<string>();
        chrome.Ideas.OnFlight = flown.Add;
        var regions = Regions(chrome, screen);
        spoken.ShouldContain("PIL13");
        Click(regions, "ideas:flight:pilot#13");
        flown.ShouldBe(["PIL13"]);
    }

    [Fact]
    public async Task ReadyAsksTheNodeShowsItsAnswerAndAPressWhileItRunsSendsNothing()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var (chrome, sent, tasks) = Chrome(renderer);
        var gate = new TaskCompletionSource();
        var calls = 0;
        chrome.Ideas.Send = async (verb, target, args, token) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return new IdeaReply(true, Readiness: new ReadinessResult(new IssueReadiness(false, ["two goals in one issue", "which GPU?"], Live.At)));
        };
        var screen = BoardScreen();
        var spoken = Spoken(renderer, chrome, screen, 1280);
        // What the node already knows is on the rows: ✓ ready on the checked idea, ✗ with the reasons on the checked issue.
        spoken.ShouldContain("✓ ready");
        spoken.ShouldContain("tests can be named");
        spoken.ShouldContain("✗ not ready");
        spoken.ShouldContain("two goals in one issue");
        spoken.ShouldContain("checked 5m ago");
        spoken.ShouldContain("Plan flight");

        var regions = Regions(chrome, screen);
        Actions(regions).ShouldContain("ideas:ready:pilot#12");
        Click(regions, "ideas:ready:pilot#12");
        Draw(chrome, screen);
        Spoken(renderer, chrome, screen, 1280).ShouldContain("checking whether it is ready to fly");
        // Pressed again while it runs: the button is gone, and asking again sends nothing more.
        Actions(Regions(chrome, screen)).ShouldNotContain("ideas:ready:pilot#12");
        await chrome.Ideas.CheckReadyAsync(chrome.Ideas.Board.Find("pilot#12")!);
        calls.ShouldBe(1);
        gate.SetResult();
        await Task.WhenAll(tasks);
        calls.ShouldBe(1);
        sent.Count.ShouldBe(0);

        Draw(chrome, screen);
        var after = Spoken(renderer, chrome, screen, 1280);
        after.ShouldContain("which GPU?");
        after.ShouldNotContain("checking whether it is ready to fly");
    }

    [Fact]
    public async Task EachItemButtonSendsItsVerbToTheNodeWithTheIssueAsTarget()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var (chrome, sent, tasks) = Chrome(renderer, new IdeaReply(true, Readiness: new ReadinessResult(new IssueReadiness(true, ["one goal"], Live.At)),
            Suggestions: new MilestoneSuggestions("pilot", [new ItemSuggestion(12, "x", "v2.3", null, "fits", Updated.AddMinutes(12))], [])));
        var chats = new List<string>();
        chrome.Ideas.OnChatAbout = chats.Add;
        var screen = BoardScreen();
        // The pinned pane short, so the milestone's issues are in view; made tall again for the idea's own buttons.
        chrome.Ideas.SplitExtent = 200f;
        var regions = Regions(chrome, screen);

        Click(regions, "ideas:chat:pilot#12");
        chats.Count.ShouldBe(1);
        chats[0].ShouldContain("Resize the split");
        chats[0].ShouldContain("https://github.com/SharpAstro/pilot/issues/12");

        Click(regions, "ideas:ready:pilot#12");
        await Task.WhenAll(tasks);
        (sent.Last().Verb, sent.Last().Target).ShouldBe((Verbs.CheckReady, "issue/pilot/#12"));
        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:suggest:pilot#12");
        await Task.WhenAll(tasks);
        (sent.Last().Verb, sent.Last().Target).ShouldBe((Verbs.SuggestMilestones, "issue/pilot/#12"));

        // A ready issue offers Plan flight, which starts the chat.
        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:ready:pilot#12");
        await Task.WhenAll(tasks);
        Actions(Regions(chrome, screen)).ShouldContain("ideas:plan-flight:pilot#12");
        Click(Regions(chrome, screen), "ideas:plan-flight:pilot#12");
        chats.Last().ShouldStartWith("Plan a flight for pilot #12");

        // An idea has them too, beside its own, on the same rows: its issue is the target.
        chrome.Ideas.SplitExtent = 900f;
        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:ready:pilot#380");
        await Task.WhenAll(tasks);
        sent.Last().Target.ShouldBe("issue/pilot/#380");
        foreach (var kept in new[] { "ideas:plan:pilot I380", "ideas:drop:pilot I380", "ideas:edit:pilot I380", "ideas:tidy:pilot I380", "ideas:move:pilot I380", "ideas:open:pilot I380", "ideas:chat:pilot#380" })
        {
            Actions(Regions(chrome, screen)).ShouldContain(kept);
        }
    }

    [Fact]
    public async Task ASectionsSuggestionsAreAcceptedOneByOneDeclinedOrAllAtOnceAndNothingMovesBefore()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var suggestions = new MilestoneSuggestions("pilot",
        [
            new ItemSuggestion(20, "Loose issue", "v2.3", null, "it touches the split", Updated.AddMinutes(20)),
            new ItemSuggestion(21, "Another loose one", null, "Observatory", "dome work", Updated.AddMinutes(21)),
        ], [new NewMilestoneSuggestion("Observatory", "Dome and roof control", [21])]);
        var (chrome, sent, tasks) = Chrome(renderer, new IdeaReply(true, Suggestions: suggestions));
        var screen = BoardScreen();

        Click(Regions(chrome, screen), "ideas:suggest-section:none");
        await Task.WhenAll(tasks);
        (sent.Last().Verb, sent.Last().Target).ShouldBe((Verbs.SuggestMilestones, "section/pilot/none"));
        var spoken = Spoken(renderer, chrome, screen, 1280);
        spoken.ShouldContain("Suggested: v2.3 (it touches the split)");
        spoken.ShouldContain("Suggested: new milestone Observatory (dome work)");
        spoken.ShouldContain("New milestone “Observatory”: Dome and roof control (#21)");
        // Nothing moved: the issues are still in No milestone, and no move was sent.
        chrome.Ideas.Board.Sections.Single(s => s.Key == "none").Items.Count.ShouldBe(2);
        sent.Any(s => s.Verb == Verbs.SetMilestone || s.Verb == Verbs.AcceptMilestones).ShouldBeFalse();

        // ✗ drops one suggestion and sends nothing; Accept all then takes the one that is left.
        var before = sent.Count;
        var regions = Regions(chrome, screen);
        Actions(regions).ShouldContain("ideas:accept:pilot#20");
        Actions(regions).ShouldContain("ideas:accept-all:none");
        Click(regions, "ideas:reject:pilot#21");
        sent.Count.ShouldBe(before);
        Actions(Regions(chrome, screen)).ShouldNotContain("ideas:accept:pilot#21");
        Actions(Regions(chrome, screen)).ShouldNotContain("ideas:accept-all:none");

        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:accept:pilot#20");
        await Task.WhenAll(tasks);
        (sent.Last().Verb, sent.Last().Target).ShouldBe((Verbs.AcceptMilestones, "issue/pilot/#20"));
        chrome.Ideas.Said.ShouldBe("the node moved #20 to v2.3");

        // Accept all sends the numbers still held (a declined one is not among them).
        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:suggest-section:none");
        await Task.WhenAll(tasks);
        tasks.Clear();
        Click(Regions(chrome, screen), "ideas:accept-all:none");
        await Task.WhenAll(tasks);
        (sent.Last().Verb, sent.Last().Target).ShouldBe((Verbs.AcceptMilestones, "repo/pilot"));
        sent.Last().Args.ShouldBe(["20", "21"]);
    }

    [Fact]
    public async Task TheUnsortedSectionIsSuggestedForOncePerRepoItHoldsIdeasOf()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var (chrome, sent, tasks) = Chrome(renderer, new IdeaReply(true, Suggestions: new MilestoneSuggestions("x", [], [], "no open issue is in Ideas")));
        var screen = BoardScreen();
        Click(Regions(chrome, screen), "ideas:suggest-section:unsorted");
        await Task.WhenAll(tasks);
        sent.Select(s => s.Target).Order().ShouldBe(["section/pilot/unsorted", "section/tianwen/unsorted"]);
        chrome.Ideas.Said.ShouldBe("no open issue is in Ideas");
    }

    [Fact]
    public async Task DraggingAnItemOntoAMilestoneAsksTheNodeAndTheRowStaysUntilTheNodeReadsItMoved()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var (chrome, sent, tasks) = Chrome(renderer);
        var screen = BoardScreen();
        chrome.Ideas.SplitExtent = 200f;
        var regions = Regions(chrome, screen);
        var grip = Region(regions, "ideas:drag:pilot#20");
        var press = new PointerPress(grip.X + 2, grip.Y + 2, MouseButton.Left, InputModifier.None, 1);
        var capture = grip.OnPress!(press).ShouldNotBeNull();
        chrome.Ideas.Dragging.ShouldBe("pilot#20");

        // Over the milestone's header: it lights, and says what dropping does.
        var target = Region(Regions(chrome, screen), "ideas:section:m:pilot/v2.3");
        capture.Move(new PointerMove(target.X + 20, target.Y + 4, MouseButton.Left, InputModifier.None));
        Draw(chrome, screen);
        Spoken(renderer, chrome, screen, 1280).ShouldContain("Drop to move it into v2.3");
        sent.ShouldBeEmpty();

        capture.Release(new PointerMove(target.X + 20, target.Y + 4, MouseButton.Left, InputModifier.None));
        await Task.WhenAll(tasks);
        (sent.Single().Verb, sent.Single().Target).ShouldBe((Verbs.SetMilestone, "issue/pilot/#20"));
        sent.Single().Args.ShouldBe(["v2.3", Updated.AddMinutes(20).ToString("O", System.Globalization.CultureInfo.InvariantCulture)]);
        chrome.Ideas.Dragging.ShouldBeNull();
        // The node's answer is shown, and the row is still where the node last put it: only a read of the repo moves it.
        chrome.Ideas.Said.ShouldBe("the node moved #20 to v2.3");
        chrome.Ideas.Board.SectionOf("pilot#20")!.Key.ShouldBe("none");
        var moved = screen with { Issues = [.. screen.Issues.Select(i => i.Number == 20 ? i with { Milestone = "v2.3" } : i)] };
        Draw(chrome, moved);
        chrome.Ideas.Board.SectionOf("pilot#20")!.Key.ShouldBe("m:pilot/v2.3");
    }

    [Fact]
    public async Task ADropThatTheNodeRefusesIsSaidAndADropOnNothingOrItsOwnSectionAsksNothing()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var (chrome, sent, tasks) = Chrome(renderer, new IdeaReply(false, "pilot has no open milestone Rings"));
        var screen = BoardScreen();
        chrome.Ideas.SplitExtent = 200f;
        var regions = Regions(chrome, screen);
        var grip = Region(regions, "ideas:drag:pilot#20");
        PointerPress Press() => new(grip.X + 2, grip.Y + 2, MouseButton.Left, InputModifier.None, 1);

        // Let go over the pinned section (above the handle) and over the item's own section: nothing is asked.
        var divider = Region(regions, SplitHitId);
        grip.OnPress!(Press())!.Release(new PointerMove(grip.X, divider.Y - 20, MouseButton.Left, InputModifier.None));
        var own = Region(Regions(chrome, screen), "ideas:section:none");
        grip.OnPress!(Press())!.Release(new PointerMove(own.X + 10, own.Y + 4, MouseButton.Left, InputModifier.None));
        // The right button does not lift an item.
        grip.OnPress!(Press() with { Button = MouseButton.Right }).ShouldBeNull();
        await Task.WhenAll(tasks);
        sent.ShouldBeEmpty();

        var rings = Region(Regions(chrome, screen), "ideas:section:m:pilot/Rings");
        grip.OnPress!(Press())!.Release(new PointerMove(rings.X + 10, rings.Y + 4, MouseButton.Left, InputModifier.None));
        await Task.WhenAll(tasks);
        sent.Single().Args[0].ShouldBe("Rings");
        chrome.Ideas.Said.ShouldBe("#20 not moved: pilot has no open milestone Rings");
        chrome.Ideas.Board.SectionOf("pilot#20")!.Key.ShouldBe("none");
    }

    [Fact]
    public async Task AnItemDroppedOnNoMilestoneIsTakenOutOfItsMilestoneAndAnotherReposMilestoneIsRefused()
    {
        using var renderer = new RgbaImageRenderer(1280, 1400);
        var (chrome, sent, tasks) = Chrome(renderer);
        var screen = BoardScreen() with { Milestones = [.. BoardScreen().Milestones, Milestone(9, "Night", 1, 0, repo: "tianwen")], Issues = [.. BoardScreen().Issues, Issue(5, "Night issue", "Night", repo: "tianwen")] };
        chrome.Ideas.SplitExtent = 200f;
        var grip = Region(Regions(chrome, screen), "ideas:drag:pilot#12");
        var none = Region(Regions(chrome, screen), "ideas:section:none");
        grip.OnPress!(new PointerPress(grip.X + 2, grip.Y + 2, MouseButton.Left, InputModifier.None, 1))!.Release(new PointerMove(none.X + 10, none.Y + 4, MouseButton.Left, InputModifier.None));
        await Task.WhenAll(tasks);
        (sent.Single().Verb, sent.Single().Target).ShouldBe((Verbs.SetMilestone, "issue/pilot/#12"));
        sent.Single().Args[0].ShouldBe(Verbs.NoMilestone);

        sent.Clear();
        var night = Region(Regions(chrome, screen), "ideas:section:m:tianwen/Night");
        grip.OnPress!(new PointerPress(grip.X + 2, grip.Y + 2, MouseButton.Left, InputModifier.None, 1))!.Release(new PointerMove(night.X + 10, night.Y + 4, MouseButton.Left, InputModifier.None));
        await Task.WhenAll(tasks);
        sent.ShouldBeEmpty();
        chrome.Ideas.Said.ShouldContain("is an issue of pilot");
    }

    private const string SplitHitId = IdeasView<RgbaImage>.SplitHit;

    [Fact]
    public void TheHandleResizesTheSplitAndTheSectionsFollowIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 1000);
        var (chrome, _, _) = Chrome(renderer);
        var screen = BoardScreen();
        var regions = Regions(chrome, screen);
        var handle = Region(regions, SplitHitId);
        var headerBefore = Region(regions, "ideas:section:none").Y;
        chrome.Ideas.SplitExtent.ShouldBeNull();

        var capture = handle.OnPress!(new PointerPress(handle.X + 10, handle.Y + 3, MouseButton.Left, InputModifier.None, 1)).ShouldNotBeNull();
        capture.Move(new PointerMove(handle.X + 10, handle.Y + 103, MouseButton.Left, InputModifier.None));
        capture.Release(new PointerMove(handle.X + 10, handle.Y + 103, MouseButton.Left, InputModifier.None));
        chrome.Ideas.SplitExtent.ShouldNotBeNull();
        var headerAfter = Region(Regions(chrome, screen), "ideas:section:none").Y;
        headerAfter.ShouldBeGreaterThan(headerBefore + 90f);
        headerAfter.ShouldBeLessThan(headerBefore + 110f);
    }

    [Theory]
    [InlineData(480f)]
    [InlineData(1280f)]
    public void TheBoardDrawsNoTextOverTextAtNarrowAndWideWidthsWithLongTitlesAndEverySuggestionOpen(float width)
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, _, _) = Chrome(renderer);
        var screen = BoardScreen() with { Issues = [.. BoardScreen().Issues.Select(i => i.Number == 381 ? i with { Title = LongTitle } : i)] };
        // Both panes shown whole, so every row is held against its neighbours.
        chrome.Ideas.SplitExtent = 2600f;
        chrome.Ideas.ShowFolded = true;
        var arranged = Layout.Engine.Arrange(chrome.Ideas.Tree(screen), new Rect<float>(0, 0, width, 6000), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        TextOverlap.Faults(arranged).Where(f => f.Bounds.Y > TopBar.Height).Select(f => f.Line).ShouldBeEmpty($"at {width} wide");
        foreach (var a in arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text } && a.Bounds.Y > TopBar.Height))
        {
            (a.Bounds.X + a.Bounds.Width).ShouldBeLessThanOrEqualTo(width + 0.5f);
        }
    }

    [Fact]
    public async Task CapturesOfTheBoardListAtNarrowAndWideSizesInBothPalettes()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        var suggestions = new MilestoneSuggestions("pilot",
        [
            new ItemSuggestion(20, "Loose issue", "v2.3", null, "it touches the split", Updated.AddMinutes(20)),
            new ItemSuggestion(21, "Another loose one", null, "Observatory", "dome work", Updated.AddMinutes(21)),
        ], [new NewMilestoneSuggestion("Observatory", "Dome and roof control", [21])]);
        foreach (var (size, width, height) in new[] { ("narrow", 480, 1100), ("wide", 1280, 1000) })
        {
            foreach (var night in new[] { false, true })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (chrome, _, tasks) = Chrome(renderer, new IdeaReply(true, Suggestions: suggestions));
                chrome.Night.On = night;
                var screen = BoardScreen();
                Click(Regions(chrome, screen), "ideas:suggest-section:none");
                await Task.WhenAll(tasks);
                chrome.Ideas.OpenOn(screen.Ideas.First(i => i.Number == 380), IdeasView<RgbaImage>.Mode.Drop);
                chrome.Ideas.Close();
                Draw(chrome, screen, width, height);
                if (dir is { Length: > 0 })
                {
                    File.WriteAllBytes(Path.Combine(dir, $"ideas-board-list-{size}-{(night ? "night" : "dark")}.png"), PngWriter.Encode(renderer.Surface.Pixels, width, height));
                }
            }
        }
    }
}
