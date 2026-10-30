using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Repos page's PR links, which share one component (<c>PullRequestLink</c>): a repo row's PR opens its flight's page when it has
/// one, else its row on the Pull requests page when the node lists it, else GitHub; only the GitHub link ends in ↗; a worktree row's
/// "PR #n" opens the PR's row; a cut title carries its whole text as a tooltip; Back returns to the Repos page.
/// </summary>
public sealed class ReposPageLinksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private const string LongTitle = "Repos page: every PR link opens pilot's own page for it when there is one, else GitHub, and a title that is cut says all of it on hover";

    private static PullRequestStatus Status(int number, string? flight, string title = "A change") => new()
    {
        Number = number, Title = title, Head = flight is null ? $"feature/x{number}" : $"flight/{flight.ToLowerInvariant()}", Url = $"https://github.com/example-org/orbit/pull/{number}",
        Flight = flight, Checks = "3/3 passed", Labels = ["pilot node: SE1"],
    };

    private static PullRequestRow Listed(int number) => new()
    {
        Repo = "example-org/orbit", Number = number, Title = "A change", Url = $"https://github.com/example-org/orbit/pull/{number}", Author = "ada",
        Kind = PullRequestKind.Draft, Label = "Draft", UpdatedAt = Now, CreatedAt = Now, Head = "feature/x", Base = "main",
    };

    private static FlightRow Flight(string code) => new()
    {
        Code = code, Repo = "orbit", RepoPath = "/r/orbit", Issue = 1, Title = "A flight", Branch = $"flight/{code.ToLowerInvariant()}",
        Worktree = $"/r/orbit/.claude/worktrees/{code.ToLowerInvariant()}", Base = "origin/main", State = FlightState.Flying, CreatedAt = Now,
    };

    private static WorktreeStatus Worktree(string branch, string? flight = null) => new()
    {
        Path = $"/r/orbit/.claude/worktrees/{branch.Replace('/', '-')}", Branch = branch, Flight = flight, Files = 2, Added = 10, Removed = 1,
    };

    // Orbit has two PRs on its line (flight PIL1's, a listed one with no flight) and gadget one the node does not list; orbit's worktrees
    // carry the flight's PR and the listed one, gadget's the unlisted one.
    private static ReposScreen Screen(string title1 = "A change") => new(RepoLine.Build(
        [
            new RepoStatus
            {
                Name = "orbit", Path = "/r/orbit", GitHub = "example-org/orbit", Tracked = true, DefaultBranch = "main", Branch = "main", UpdatedAt = Now,
                PullRequests = [Status(101, "PIL1", title1), Status(102, null)],
                Worktrees = [Worktree("flight/pil1", "PIL1"), Worktree("feature/x102")],
            },
            new RepoStatus
            {
                Name = "gadget", Path = "/r/gadget", GitHub = "example-org/gadget", Tracked = true, DefaultBranch = "main", Branch = "main", UpdatedAt = Now,
                PullRequests = [Status(7, null) with { Url = "https://github.com/example-org/gadget/pull/7", Head = "feature/g" }],
                Worktrees = [Worktree("feature/g")],
            },
        ],
        [Flight("PIL1")], [], [], [], Now), "live", Now);

    private static PullRequestsScreen Prs() => new(new PullRequestsRow { Orgs = ["example-org"], ReadAt = Now, Viewer = "ada", PullRequests = [Listed(101), Listed(102)] }, "live", Now);

    private static PilotChrome<RgbaImage> Window(RgbaImageRenderer renderer, List<string> opened)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Autopilot.OnToggle = _ => { };
        chrome.LinkPullRequests();
        chrome.LinkRepos();
        chrome.Repos.OnOpen = opened.Add;
        return chrome;
    }

    private static void Draw(PilotChrome<RgbaImage> chrome, ReposScreen screen, float width = 1280f, float height = 700f)
    {
        var bounds = new RectF32(0, 0, width, height);
        chrome.Render(new SessionsScreen([], "test", Now), null, null, null, null, screen, Prs(), bounds);
        chrome.Render(new SessionsScreen([], "test", Now), null, null, null, null, screen, Prs(), bounds);
    }

    private static List<ClickableRegion> Regions(PixelWidgetBase<RgbaImage> widget)
    {
        var regions = new List<ClickableRegion>();
        widget.CollectPaintedRegions(regions);
        return regions;
    }

    private static void Click(PilotChrome<RgbaImage> chrome, string action) =>
        Regions(chrome.Repos).Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnClick.ShouldNotBeNull().Invoke(InputModifier.None);

    private static bool Has(PilotChrome<RgbaImage> chrome, string action) => Regions(chrome.Repos).Any(r => r.Result is HitResult.ButtonHit b && b.Action == action);

    private static (PilotChrome<RgbaImage> Chrome, ReposScreen Screen, List<string> Opened) Open(RgbaImageRenderer renderer, string title1 = "A change")
    {
        var opened = new List<string>();
        var chrome = Window(renderer, opened);
        var screen = Screen(title1);
        chrome.Show(PilotPage.Repos);
        chrome.Repos.ExpandAll(screen);
        Draw(chrome, screen);
        return (chrome, screen, opened);
    }

    [Fact]
    public void APRWithAFlightOpensTheFlightsPageOnItAndBackReturnsToTheReposPage()
    {
        using var renderer = new RgbaImageRenderer(1280, 700);
        var (chrome, _, opened) = Open(renderer);

        Click(chrome, "pr:101");

        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.OpenCode.ShouldBe("PIL1");
        opened.ShouldBeEmpty();
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Repos);
    }

    [Fact]
    public void AListedPRWithNoFlightOpensItsRowAndBackReturnsToTheReposPage()
    {
        using var renderer = new RgbaImageRenderer(1280, 700);
        var (chrome, screen, opened) = Open(renderer);

        Click(chrome, "pr:102");

        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Draw(chrome, screen);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Number.ShouldBe(102);
        opened.ShouldBeEmpty();
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Repos);
    }

    [Fact]
    public void AnUnlistedPRGoesToTheBrowserAndIsTheOnlyLinkWithTheAwayMark()
    {
        using var renderer = new RgbaImageRenderer(1280, 700);
        var (chrome, screen, opened) = Open(renderer);

        Click(chrome, "pr:7");
        opened.ShouldBe(["https://github.com/example-org/gadget/pull/7"]);
        chrome.Page.ShouldBe(PilotPage.Repos);

        // Only the browser link ends in ↗; an in-app link has a small ↗ of its own beside it, to GitHub.
        var tree = chrome.Repos.Tree(screen);
        var texts = TextsOf(renderer, tree);
        texts.ShouldContain(t => t.StartsWith("#7 ", StringComparison.Ordinal) && t.EndsWith(Followable.Away, StringComparison.Ordinal));
        texts.ShouldNotContain(t => t.StartsWith("#101 ", StringComparison.Ordinal) && t.EndsWith(Followable.Away, StringComparison.Ordinal));
        texts.ShouldNotContain(t => t.StartsWith("#102 ", StringComparison.Ordinal) && t.EndsWith(Followable.Away, StringComparison.Ordinal));
        texts.ShouldContain("PR #7 ↗");
        texts.ShouldNotContain("PR #101 ↗");
        texts.ShouldNotContain("PR #102 ↗");
        Has(chrome, "pr:101:browser").ShouldBeTrue();
        Has(chrome, "pr:102:browser").ShouldBeTrue();
        Has(chrome, "pr:7:browser").ShouldBeFalse();

        // The ↗ beside an in-app link opens GitHub, and does not leave the Repos page.
        Click(chrome, "pr:101:browser");
        opened.Last().ShouldBe("https://github.com/example-org/orbit/pull/101");
        chrome.Page.ShouldBe(PilotPage.Repos);
    }

    [Fact]
    public void AWorktreesPRNumberOpensTheRowOfAListedPRElseTheBrowserWithTheAwayMark()
    {
        using var renderer = new RgbaImageRenderer(1280, 700);
        var (chrome, screen, opened) = Open(renderer);
        var orbit = screen.Lines.Single(l => l.Name == "orbit").Key;
        var gadget = screen.Lines.Single(l => l.Name == "gadget").Key;

        // Its flight is the ✈ link beside it, so a PR with a flight still opens the PR's row, not the flight.
        Has(chrome, $"repo:{orbit}:pr:101").ShouldBeTrue();
        Click(chrome, $"repo:{orbit}:pr:102");
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        Draw(chrome, screen);
        chrome.PullRequests.SelectedRow.ShouldNotBeNull().Number.ShouldBe(102);
        chrome.Back().ShouldBeTrue();

        Draw(chrome, screen);
        Click(chrome, $"repo:{gadget}:pr:7");
        opened.ShouldBe(["https://github.com/example-org/gadget/pull/7"]);
        chrome.Page.ShouldBe(PilotPage.Repos);
        Click(chrome, $"repo:{orbit}:flight:PIL1");
        chrome.Page.ShouldBe(PilotPage.Flights);
    }

    [Fact]
    public void AHostWithNoRouteKeepsTheBrowserLinkWithTheAwayMark()
    {
        using var renderer = new RgbaImageRenderer(1280, 700);
        // The page on its own, as a host with neither page to open has it: the node lists both PRs, and no route is set.
        var repos = new ReposView<RgbaImage>(renderer) { FontPath = SessionsCapture.FontPath, OnOpen = _ => { }, PullRequests = Prs().Row };
        var screen = Screen();

        var texts = TextsOf(renderer, repos.Tree(screen));

        texts.ShouldContain(t => t.StartsWith("#101 ", StringComparison.Ordinal) && t.EndsWith(Followable.Away, StringComparison.Ordinal));
        texts.ShouldContain(t => t.StartsWith("#102 ", StringComparison.Ordinal) && t.EndsWith(Followable.Away, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryLinkKeepsTheCopyURLMenuAndACutTitleShowsItsWholeTextOnHover()
    {
        using var renderer = new RgbaImageRenderer(1280, 700);
        var (chrome, screen, _) = Open(renderer, LongTitle);
        chrome.Repos.CopyText = _ => { };
        Draw(chrome, screen);

        var whole = $"#101 {LongTitle}";
        var arranged = Layout.Engine.Arrange(chrome.Repos.Tree(screen), new Rect<float>(0, 0, 1280, 700), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));
        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(whole);
        // A short title says where its link goes instead.
        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain("Show example-org/orbit#102 on the Pull requests page");

        // Every PR link, and the ↗ beside an in-app one, has the menu on its right press.
        foreach (var action in new[] { "pr:101", "pr:101:browser", "pr:102", "pr:102:browser", "pr:7" })
        {
            Regions(chrome.Repos).Single(r => r.Result is HitResult.ButtonHit b && b.Action == action).OnContextMenu.ShouldNotBeNull(action);
        }
    }

    [Fact]
    public void ACaptureOfLongPRTitlesIsClearOfTextOverTextAtNarrowAndWideWidthsInBothPalettes()
    {
        // PILOT_TEST_CAPTURE_DIR keeps the frames as PNGs, for looking at; without it the frames are only checked.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        foreach (var (width, dpi) in new[] { (1300, 1f), (1900, 1f) })
        {
            foreach (var night in new[] { false, true })
            {
                using var renderer = new RgbaImageRenderer((uint)width, 700);
                var chrome = Window(renderer, []);
                chrome.DpiScale = dpi;
                chrome.Night.On = night;
                chrome.Repos.CopyText = _ => { };
                var screen = Screen(LongTitle);
                chrome.Show(PilotPage.Repos);
                chrome.Repos.ExpandAll(screen);
                Draw(chrome, screen, width, 700f);

                var shown = chrome.PageBounds ?? throw new InvalidOperationException("the chrome draws no page");
                chrome.Repos.Table.PageWidth = shown.Width / dpi;
                var ctx = new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, dpi);
                var arranged = Layout.Engine.Arrange(chrome.Repos.Tree(screen), new Rect<float>(shown.X, shown.Y, shown.Width, shown.Height), ctx);
                var faults = TextOverlap.Faults(arranged);
                faults.Select(f => f.Line).ShouldBeEmpty($"{width} wide, {(night ? "night" : "day")}");

                if (dir is { Length: > 0 })
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"repos-pr-links-{width}-{(night ? "night" : "day")}.png"),
                        SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, 700));
                }
            }
        }
    }

    // The text of every node the page draws, with the ↗ marks as they are drawn.
    private static List<string> TextsOf(RgbaImageRenderer renderer, Layout.Node tree) =>
        [.. Layout.Engine.Arrange(tree, new Rect<float>(0, 0, 1280, 700), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f))
            .Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value.Trim())];
}
