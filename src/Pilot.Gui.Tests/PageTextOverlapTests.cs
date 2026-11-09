using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// Every page's arranged tree, with sample data, at the widths and DPIs the issue names: no text is drawn over other text,
/// and none reaches past the scroll or the page that holds it (<see cref="TextOverlap"/>, the one check the live GUI reads too).
/// Every case runs: a fault is the page's own layout and is fixed, never skipped. (DIR.Lib's stack measure, #169, is in the
/// pinned package from 11.39.)
/// </summary>
public sealed class PageTextOverlapTests
{
    private static readonly int[] Widths = [500, 700, 900, 1300, 1900];
    private static readonly float[] Dpis = [1f, 1.25f, 1.5f];
    private const int Height = 900;

    /// <summary>The short window of the issue: a height that cuts the page's rows, at the widths and DPIs a small laptop screen has.</summary>
    private const int ShortHeight = 500;
    private static readonly int[] ShortWidths = [500, 700, 1400];
    private static readonly float[] ShortDpis = [1f, 1.5f];
    private static readonly string[] ShortStates = ["long", "many"];

    internal static readonly string[] Pages =
    [
        "home", "sessions", "flights table", "flights canvas", "flight page", "jobs", "fleet", "settings", "repos", "pull requests", "chat", "spend",
        "cards panel", "transcript", "wizard repos", "wizard issues",
    ];

    /// <summary>The Ideas board is not in <see cref="Pages"/> (the selectable-content check walks it by its own name); this sweep adds it.</summary>
    internal const string IdeasPage = "ideas";

    /// <summary>The cards panel with every card's detail fold opened (a click on each card's painted toggle), the detail long.</summary>
    internal const string CardDetailPage = "card detail";

    /// <summary>Every page the sweep walks: <see cref="Pages"/> and the two the issue adds.</summary>
    internal static readonly string[] SweptPages = [.. Pages, IdeasPage, CardDetailPage];

    internal static readonly string[] States = ["sample", "long", "many", "empty"];

    /// <summary>The window of card c10787: 2880 x 1814 at DPI 1.5, a 2352 wide page beside the rail and the cards panel.</summary>
    private const int BigWidth = 2880, BigHeight = 1814;
    private const float BigDpi = 1.5f;

    /// <summary>
    /// 18 pages x 4 states x (5 widths x 3 DPIs + the big window) = 1152 cases at a normal height, and the short height (500) at 500, 700 and 1400
    /// wide, DPI 1 and 1.5, in the "long" and "many" states only (the others have less to cut): 18 x 2 x 6 = 216 more, 1368 in all (it was
    /// 16 pages x 4 x 13 = 832).
    /// </summary>
    public static IEnumerable<TheoryDataRow<string, string, int, float, int>> Cases() =>
        CaseTuples().Select(c => Row(c.Page, c.State, c.Width, c.Dpi, c.Height));

    /// <summary>The cases as plain tuples: what <see cref="Cases"/> wraps for the theory, and what the allow-list's stale-entry test walks.</summary>
    internal static IEnumerable<(string Page, string State, int Width, float Dpi, int Height)> CaseTuples()
    {
        foreach (var page in SweptPages)
        {
            foreach (var state in States)
            {
                foreach (var width in Widths)
                {
                    foreach (var dpi in Dpis)
                    {
                        yield return (page, state, width, dpi, Height);
                    }
                }
                yield return (page, state, BigWidth, BigDpi, BigHeight);
                if (!ShortStates.Contains(state))
                {
                    continue;
                }
                foreach (var width in ShortWidths)
                {
                    foreach (var dpi in ShortDpis)
                    {
                        yield return (page, state, width, dpi, ShortHeight);
                    }
                }
            }
        }
    }

    private static TheoryDataRow<string, string, int, float, int> Row(string page, string state, int width, float dpi, int height) =>
        new(page, state, width, dpi, height) { TestDisplayName = Key(page, state, width, dpi, height) };

    internal static string Key(string page, string state, int width, float dpi, int height = Height) =>
        $"{page} | {state} | {width} wide | DPI {dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)}" + (height == Height ? "" : $" | {height} high");

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NoTextIsDrawnOverOtherTextOrPastItsClip(string page, string state, int width, float dpi, int height)
    {
        var key = Key(page, state, width, dpi, height);
        var faults = await FaultsOf(page, state, width, dpi, height);
        var dump = Environment.GetEnvironmentVariable("PILOT_TEXT_OVERLAP_DUMP");
        if (dump is { Length: > 0 })
        {
            // One line a fault, for classifying a run: the case, then the fault's own line.
            lock (DumpLock)
            {
                File.AppendAllLines(dump, faults.Select(f => $"{Key(page, state, width, dpi, height)}\t{f.Line}"));
            }
        }

        // With PILOT_TEXT_OVERLAP_CAPTURE set to a folder, every case with a fault (the allowed ones too) is written there as a PNG: what to look at to fix an allow-list entry.
        if (faults.Count > 0 && Environment.GetEnvironmentVariable("PILOT_TEXT_OVERLAP_CAPTURE") is { Length: > 0 } captureFolder)
        {
            await CaptureOf(page, state, width, dpi, height, key, captureFolder);
        }

        var failing = Failures(key, faults, TextOverlapAllowList.Covers);
        if (failing.Count == 0)
        {
            return;
        }
        // A picture to start the fix from: the page as the chrome paints it, beside the test's output.
        var capture = await CaptureOf(page, state, width, dpi, height, key, Path.Combine(AppContext.BaseDirectory, "text-overlap"));
        Assert.Fail(Message(key, failing, capture));
    }

    /// <summary>The faults of <paramref name="key"/>'s case that the allow-list (<paramref name="allowed"/>) does not cover: what fails the case.</summary>
    internal static IReadOnlyList<TextFault> Failures(string key, IReadOnlyList<TextFault> faults, Func<string, string, bool> allowed) =>
        [.. faults.Where(f => !allowed(key, f.Line))];

    /// <summary>One line a fault: the case (page, state, size), the two texts and their bounds (the fault's own line), and where the capture is.</summary>
    internal static string Message(string key, IReadOnlyList<TextFault> failing, string? capture) =>
        $"{key} [{failing.Count} faults]" + string.Concat(failing.Select(f => $"{Environment.NewLine}  {key}: {f.Line}" + (capture is null ? "" : $" (capture: {capture})")));

    /// <summary>Writes the page of a failing case as a PNG, as the chrome paints it, to <c>text-overlap/&lt;case&gt;.png</c> beside the test's output; the path.</summary>
    private static async Task<string> CaptureOf(string page, string state, int width, float dpi, int height, string key, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_')) + ".png");
        using var prepared = await Prepare(page, state, width, dpi, null, height);
        var data = prepared.Data;
        prepared.Chrome.Render(data.Sessions, data.Flights, CardsOf(data, page), data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend);
        File.WriteAllBytes(path, PngWriter.Encode(prepared.Renderer.Surface.Pixels, width, height));
        return path;
    }

    private static readonly Lock DumpLock = new();

    /// <summary>The check over <paramref name="page"/> in <paramref name="state"/>, arranged as the chrome arranges it: the page in the room the rail and the panel leave.</summary>
    internal static async Task<IReadOnlyList<TextFault>> FaultsOf(string page, string state, int width, float dpi, int height = Height)
    {
        using var prepared = await Prepare(page, state, width, dpi, null, height);
        var ctx = new PixelMeasureContext<RgbaImage>(prepared.Renderer, SessionsCapture.FontPath, dpi);
        return TextOverlap.Faults(Layout.Engine.Arrange(prepared.Tree, prepared.Rect, ctx));
    }

    /// <summary>
    /// The page's setup in <paramref name="state"/> (its chrome, a frame, its tree: <c>prepare</c>) and then its arrange, three times (<c>arrange</c>, the
    /// warm one last), in milliseconds: what <see cref="PageFrameTimeTests"/> records for the views a fix touched.
    /// </summary>
    internal static async Task<(double Prepare, double[] Arrange)> TimedArrange(string page, string state, int width, float dpi, int height)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var prepared = await Prepare(page, state, width, dpi, null, height);
        var prepare = watch.Elapsed.TotalMilliseconds;
        var ctx = new PixelMeasureContext<RgbaImage>(prepared.Renderer, SessionsCapture.FontPath, dpi);
        var arrange = new double[3];
        for (var i = 0; i < arrange.Length; i++)
        {
            watch.Restart();
            Layout.Engine.Arrange(prepared.Tree, prepared.Rect, ctx);
            arrange[i] = watch.Elapsed.TotalMilliseconds;
        }
        return (prepare, arrange);
    }

    internal static async Task<System.Collections.Immutable.ImmutableArray<Layout.ArrangedNode<float>>> ArrangedOf(string page, string state, int width, float dpi, Action<PilotChrome<RgbaImage>>? setup = null, int height = Height)
    {
        using var prepared = await Prepare(page, state, width, dpi, setup, height);
        var ctx = new PixelMeasureContext<RgbaImage>(prepared.Renderer, SessionsCapture.FontPath, dpi);
        return Layout.Engine.Arrange(prepared.Tree, prepared.Rect, ctx);
    }

    /// <summary>A page set up in a state: its chrome, the page's tree and the room the chrome leaves it.</summary>
    private sealed record Prepared(RgbaImageRenderer Renderer, PilotChrome<RgbaImage> Chrome, PageData Data, Layout.Node Tree, Rect<float> Rect) : IDisposable
    {
        public void Dispose() => Renderer.Dispose();
    }

    private static PilotPage PageOf(string page) => page switch
    {
        "home" => PilotPage.Home,
        "sessions" or "transcript" => PilotPage.Sessions,
        "flights table" or "flights canvas" or "flight page" or "wizard repos" or "wizard issues" => PilotPage.Flights,
        "jobs" => PilotPage.Jobs,
        "fleet" => PilotPage.Fleet,
        "settings" => PilotPage.Settings,
        "repos" => PilotPage.Repos,
        "pull requests" => PilotPage.PullRequests,
        "chat" => PilotPage.Chat,
        "ideas" => PilotPage.Ideas,
        "spend" => PilotPage.Spend,
        _ => PilotPage.Home,
    };

    private static IdeaRow Idea(int number, string title, string body, IdeaState state = IdeaState.Noted) => new()
    {
        Id = IdeaIds.Id(number), Repo = "pilot", Title = title, Body = body, State = state, UpdatedAt = PageData.Now.AddMinutes(number),
        Age = "3h", Origin = "ada", Url = $"https://github.com/SharpAstro/pilot/issues/{number}", Images = IdeaImages.In(body),
    };

    private static IdeasScreen IdeasSample() => new(
        [Idea(381, "Ideas pane", "Show **ideas** in a pane.\n\n- a list\n- a box"), Idea(380, "Rail count", ""), Idea(370, "Old plan", "", IdeaState.Planned)],
        [], ["pilot", "tianwen"], "live from the node", PageData.Now);

    /// <summary>The Ideas board in a state: the sample's three ideas, ideas with titles and bodies far past a column ("long"), sixty of every state ("many"), or none.</summary>
    private static IdeasScreen IdeasFor(string state)
    {
        var states = Enum.GetValues<IdeaState>();
        return state switch
        {
            "sample" => IdeasSample(),
            "long" => new IdeasScreen(
                [.. Enumerable.Range(0, 6).Select(i => Idea(381 - i, $"{PageData.LongTitle} {i}",
                    $"{PageData.LongReport}\n\n- **Path:** {PageData.LongPath}\n- **Word:** {PageData.LongWord}\n- {PageData.LongTitle}", states[i % states.Length]))],
                [], ["pilot", PageData.LongWord], "live from the node on " + PageData.LongPath, PageData.Now),
            "many" => new IdeasScreen(
                [.. Enumerable.Range(0, 60).Select(i => Idea(500 - i, $"Idea {i}: an idea with a title of some length", i % 3 == 0 ? "" : "Show **ideas** in a pane.\n\n- a list\n- a box", states[i % states.Length]))],
                [], ["pilot", "tianwen"], "live from the node", PageData.Now),
            "empty" => new IdeasScreen([], [], ["pilot"], "live from the node", PageData.Now),
            _ => throw new ArgumentException($"no state '{state}'", nameof(state)),
        };
    }

    /// <summary>The cards the page is drawn from: those with a long detail folded under a summary for "card detail", the plain ones otherwise.</summary>
    private static CardsScreen CardsOf(PageData data, string page) => page == CardDetailPage ? data.CardsWithDetail : data.Cards;

    private static async Task<Prepared> Prepare(string page, string state, int width, float dpi, Action<PilotChrome<RgbaImage>>? setup = null, int height = Height)
    {
        var renderer = new RgbaImageRenderer((uint)width, (uint)height);
        var data = PageData.For(state);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath, DpiScale = dpi };
        chrome.Play.ShowEverything();
        chrome.Flights.CanCommand = true;
        chrome.Jobs.CanCommand = true;
        chrome.Cards.CanTell = true;
        chrome.Nodes.Choices = data.Nodes;
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.About.Newer = () => "2.0.9-beta.1";
        chrome.About.Restart = () => { };
        setup?.Invoke(chrome);
        // The Settings page reads its rows from the node: the sample page (the role ranges among them) answers, so the sweep sees a range row (#770).
        if (page == "settings" && chrome.Settings.Load is null && PageData.SettingsFor(state) is { } settingsPage)
        {
            chrome.Settings.Load = _ => Task.FromResult(settingsPage);
        }
        chrome.Show(PageOf(page));

        // The room the chrome gives the page is the chrome's own: one frame arranges the window (rail, page and the cards panel, which
        // it shows beside the page, drops for want of room, or cues on the rail) and says where each leaf went.
        var cards = CardsOf(data, page);
        var panelPage = page is "cards panel" or CardDetailPage;
        void Frame() => chrome.Render(data.Sessions, data.Flights, cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, height), data.Machines, null, data.Spend);
        Frame();
        if (panelPage && chrome.CardsDropped)
        {
            // A window too narrow for the panel beside the page shows it through the rail's cue, in the page's place.
            chrome.ToggleCards();
            Frame();
        }
        // Where the chrome draws no panel (none waiting, so nothing for the rail to cue either) there is nothing to check.
        var panelDrawn = chrome.CardsBounds is not null;
        if (page == CardDetailPage && panelDrawn)
        {
            OpenEveryDetail(chrome.Cards);
            Frame();
        }
        var shown = panelPage
            ? chrome.CardsBounds ?? chrome.PageBounds ?? throw new InvalidOperationException($"the chrome draws no page at {width} wide, DPI {dpi}")
            : chrome.PageBounds ?? throw new InvalidOperationException($"the chrome draws no page at {width} wide, DPI {dpi}");
        var pageWidth = shown.Width;
        Rect<float> PageRect() => new(shown.X, shown.Y, shown.Width, shown.Height);
        // What Render hands the Flights page from its bounds: the room it has, in design units.
        chrome.Flights.PageWidth = pageWidth / dpi;
        chrome.Sessions.Table.PageWidth = chrome.Flights.Table.PageWidth = chrome.Jobs.Table.PageWidth = chrome.Repos.Table.PageWidth = chrome.Fleet.Table.PageWidth = pageWidth / dpi;

        Layout.Node tree;
        Rect<float> rect = PageRect();
        switch (page)
        {
            case "home":
                tree = chrome.Home.Tree(new HomeScreen(data.Flights, data.Sessions, data.Jobs, cards, data.Machines));
                break;
            case "sessions":
                tree = chrome.Sessions.Tree(data.Sessions);
                break;
            case "flights table":
                chrome.Flights.Mode = FlightsViewMode.List;
                tree = chrome.Flights.Tree(data.Flights);
                break;
            case "flights canvas":
                chrome.Flights.Mode = FlightsViewMode.Canvas;
                tree = chrome.Flights.Tree(data.Flights);
                break;
            case "flight page":
                chrome.Flights.Spend = data.FlightSpend;
                chrome.Flights.OpenDetail(data.Flights.Lines.Count > 0 ? data.Flights.Lines[0].Code : "NONE1");
                tree = chrome.Flights.Tree(data.Flights);
                break;
            case "jobs":
                tree = chrome.Jobs.Tree(data.Jobs);
                break;
            case "fleet":
                tree = chrome.Fleet.Tree(data.Fleet);
                break;
            case "settings":
                // The frame above started the read; the sample answers at once, so this wait is a few turns of the loop at most (the bound RoleRangeRowsTests keeps).
                for (var i = 0; i < 200 && chrome.Settings.Load is not null && chrome.Settings.Page is null; i++)
                {
                    await Task.Delay(25, TestContext.Current.CancellationToken);
                }
                tree = chrome.Settings.Tree(PageData.Now);
                break;
            case "repos":
                chrome.Repos.ExpandAll(data.Repos);
                tree = chrome.Repos.Tree(data.Repos);
                break;
            case "pull requests":
                tree = chrome.PullRequests.Tree(data.PullRequests, pageWidth / dpi);
                break;
            case "spend":
                tree = chrome.Spend.Tree(data.Spend);
                break;
            case "chat":
                tree = await OpenChat(chrome, data);
                break;
            case "ideas":
                tree = chrome.Ideas.Tree(IdeasFor(state));
                break;
            case "cards panel":
            case CardDetailPage:
                // As the chrome drew it: beside the page, or in the page's place where the rail's cue opened it.
                tree = panelDrawn ? chrome.Cards.Tree(cards) : Layout.Builder.Spacer();
                break;
            case "transcript":
                tree = await OpenTranscript(chrome, data);
                break;
            case "wizard repos":
                tree = await OpenWizard(chrome, data, toIssues: false);
                break;
            case "wizard issues":
                tree = await OpenWizard(chrome, data, toIssues: true);
                break;
            default:
                throw new ArgumentException($"no page '{page}'", nameof(page));
        }

        return new Prepared(renderer, chrome, data, tree, rect);
    }

    /// <summary>
    /// Opens the detail fold of every card by a click on its painted toggle (<c>card:&lt;id&gt;:detail</c>), as a person does, so no change to the view
    /// is needed. A painted region is where the card is drawn, so a card scrolled out of the panel has none to click: the walk goes on while a click opens one.
    /// </summary>
    private static void OpenEveryDetail(CardsView<RgbaImage> view)
    {
        for (var round = 0; round < 100; round++)
        {
            var regions = new List<ClickableRegion>();
            view.CollectPaintedRegions(regions);
            var opened = false;
            foreach (var region in regions)
            {
                if (region.Result is HitResult.ButtonHit { Action: var action } && action.StartsWith("card:", StringComparison.Ordinal) && action.EndsWith(":detail", StringComparison.Ordinal)
                    && !view.DetailOpenOn(action["card:".Length..^":detail".Length]))
                {
                    region.OnClick!.Invoke(InputModifier.None);
                    opened = true;
                }
            }
            if (!opened)
            {
                return;
            }
        }
    }

    /// <summary>
    /// With PILOT_TEST_CAPTURE set to a folder, every page in the "long" state as a PNG at a narrow window and a wide one, as the
    /// chrome paints it: what a person sees of the text the check above holds apart.
    /// </summary>
    [Fact]
    public async Task CapturesOfEveryPageAtNarrowAndWideWindows()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } capture)
        {
            return;
        }
        Directory.CreateDirectory(capture);
        foreach (var (width, dpi) in new[] { (700, 1.5f), (1900, 1f) })
        {
            foreach (var page in Pages.Where(p => p != "cards panel"))
            {
                using var prepared = await Prepare(page, Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_STATE") is { Length: > 0 } state ? state : "long", width, dpi);
                var chrome = prepared.Chrome;
                var data = prepared.Data;
                chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new RectF32(0, 0, width, Height), data.Machines, null, data.Spend);
                File.WriteAllBytes(Path.Combine(capture, $"{page.Replace(' ', '-')}-{width}.png"), PngWriter.Encode(prepared.Renderer.Surface.Pixels, width, Height));
            }
        }
    }

    private static async Task<Layout.Node> OpenChat(PilotChrome<RgbaImage> chrome, PageData data)
    {
        var chat = chrome.Chat;
        var feed = data.Chat;
        // A watchtower mid-turn when the feed has anything to show: the reply so far, two lines waiting behind it with the first one's Answer
        // now, and the last twenty turns' timings under the box, so the sweep and the frame time cover what PIL855 added.
        var live = feed.Length > 0;
        var lengthen = data.State is "long" or "many";
        var tail = live ? StreamingChatTests.Tail(lengthen) : null;
        ChatPage Page(TranscriptEntry[] entries) => (tail ?? new ChatPage { Entries = [], Total = 0 }) with { Entries = entries, Total = feed.Length };
        chat.Load = (after, _) => Task.FromResult(Page(after < 0 ? feed : []));
        chat.OnSend = _ => Task.FromResult<string?>(null);
        chat.OnAnswerNow = _ => Task.FromResult<string?>(null);
        chat.Open();
        for (var i = 0; i < 500 && chat.Entries.Count < feed.Length; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        chat.Close();
        chat.Entries.Count.ShouldBe(feed.Length);
        return chat.Tree();
    }

    private static async Task<Layout.Node> OpenTranscript(PilotChrome<RgbaImage> chrome, PageData data)
    {
        var entries = data.Transcript;
        var view = chrome.Sessions;
        view.Transcript.Load = (_, _, after, _) => Task.FromResult(after < 0
            ? new TranscriptPage { Total = entries.Length, Entries = entries }
            : new TranscriptPage { Total = entries.Length, Entries = [] });
        var line = data.Sessions.Lines.Count > 0 ? data.Sessions.Lines[0] : SessionLine.From(PageData.Row("aaaa0000", "worker", SessionState.Working), PageData.Now);
        view.Transcript.Open(line);
        for (var i = 0; i < 100 && view.Transcript.Entries.Count < entries.Length; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        view.Transcript.Entries.Count.ShouldBe(entries.Length);
        return view.Tree(data.Sessions);
    }

    private static async Task<Layout.Node> OpenWizard(PilotChrome<RgbaImage> chrome, PageData data, bool toIssues)
    {
        var wizard = chrome.Flights.Wizard;
        wizard.LoadRepos = _ => Task.FromResult(data.WizardRepos);
        wizard.LoadIssues = (_, _) => Task.FromResult(data.WizardIssues);
        wizard.Open();
        for (var i = 0; i < 100 && wizard.RepoResults.Count < data.WizardRepos.Length; i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        if (toIssues && data.WizardRepos.Length > 0)
        {
            wizard.Toggle(data.WizardRepos[0]);
            wizard.ToIssues();
            for (var i = 0; i < 100 && wizard.IssueResults.Count < data.WizardIssues.Length; i++)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
        }
        else if (data.WizardRepos.Length > 1)
        {
            wizard.Toggle(data.WizardRepos[1]);
        }
        return chrome.Flights.Tree(data.Flights);
    }
}
