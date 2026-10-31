using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The Pull requests page: every open PR the node read, grouped by repo, in fixed columns that line up across the groups, its
/// titles cut with an ellipsis and carried whole on hover, a group folding on a click anywhere on its header, the pills and the
/// repo search narrowing the list, a click selecting a row and a double-click opening it, the empty line and the stale source line.
/// </summary>
public sealed class PullRequestsPageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private const string LongTitle = "Rework the whole of the plate solver's catalogue loader so that a very long title still gets its ellipsis and keeps its whole text on hover (src/TianWen.Lib/Astrometry/Catalogs/VeryLongNamespaceName/ReallyLongFileName.cs)";

    private static PullRequestRow Pr(string repo, int number, string title, PullRequestKind kind, string label, string author = "sebgod", string? flight = null, string? review = null,
        bool needsMe = false, TimeSpan? age = null, string? failedUrl = null) => new()
    {
        Repo = repo, Number = number, Title = title, Url = $"https://github.com/{repo}/pull/{number}", Author = author, Kind = kind, Label = label, NeedsMe = needsMe,
        Flight = flight, Review = review, ReviewUrl = review is null ? null : $"https://github.com/{repo}/pull/{number}#pullrequestreview-1", FailedUrl = failedUrl,
        UpdatedAt = Now - (age ?? TimeSpan.FromHours(3)), CreatedAt = Now - TimeSpan.FromDays(2), Head = flight is null ? "feature/x" : $"flight/{flight.ToLowerInvariant()}", Base = "main",
    };

    /// <summary>Eight PRs in four repos: pilot (ready, running, queued), DIR.Lib (changes requested, behind), tianwen (a long title that failed, a conflict) and sebgod/notes (a draft).</summary>
    private static PullRequestsRow Row(DateTimeOffset? readAt = null, string? viewer = "sebgod") => new()
    {
        Orgs = ["SharpAstro", "sebgod"],
        ReadAt = readAt ?? Now - TimeSpan.FromSeconds(40),
        Viewer = viewer,
        PullRequests =
        [
            Pr("SharpAstro/tianwen", 1820, LongTitle, PullRequestKind.ChecksFailed, "Checks failed: build-and-test (ubuntu-latest, net10.0)", author: "a-contributor-with-a-very-long-login", failedUrl: "https://github.com/SharpAstro/tianwen/actions/runs/9"),
            Pr("SharpAstro/pilot", 261, "Checks while the next leg flies", PullRequestKind.ChecksRunning, "Checks running 4/7", flight: "PIL261", review: PullRequestReview.Reviewing),
            Pr("SharpAstro/pilot", 252, "Pull requests page: every open PR across the orgs", PullRequestKind.Ready, "Ready", needsMe: true, flight: "PIL252", review: PullRequestReview.Passed, age: TimeSpan.FromMinutes(5)),
            Pr("SharpAstro/tianwen", 1799, "Plate solving: a fallback when the catalogue is missing", PullRequestKind.Conflicts, "Conflicts", needsMe: true, flight: "TIA1799", review: PullRequestReview.Changes, age: TimeSpan.FromDays(4)),
            Pr("SharpAstro/pilot", 258, "Bump the lexer", PullRequestKind.InMergeQueue, "In merge queue #2", author: "dependabot[bot]"),
            Pr("SharpAstro/DIR.Lib", 301, "A text input that scrolls", PullRequestKind.Behind, "Behind", author: "ada"),
            Pr("SharpAstro/DIR.Lib", 300, "Tooltip anchors", PullRequestKind.ChangesRequested, "Changes requested", author: "ada", needsMe: true),
            Pr("sebgod/notes", 12, "Draft: notes on the dome", PullRequestKind.Draft, "Draft"),
        ],
    };

    private static PullRequestsScreen Screen(PullRequestsRow? row = null) => new(row ?? Row(), "live from the node on node.sock", Now);

    private static (PilotChrome<RgbaImage> Chrome, PullRequestsView<RgbaImage> Page) Chrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.PullRequests.OnRefresh = _ => Task.CompletedTask;
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        return (chrome, chrome.PullRequests);
    }

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(RgbaImageRenderer renderer, PullRequestsView<RgbaImage> page, PullRequestsScreen screen, float width = 1280f, float height = 900f) =>
        Layout.Engine.Arrange(page.Tree(screen, width), new Rect<float>(0, 0, width, height), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

    private static Rect<float> Where(ImmutableArray<Layout.ArrangedNode<float>> arranged, Func<Layout.Node, bool> which)
    {
        var found = arranged.Where(a => which(a.Node)).ToList();
        found.Count.ShouldBe(1);
        return found[0].Bounds;
    }

    private static Rect<float> Button(ImmutableArray<Layout.ArrangedNode<float>> arranged, string action) =>
        Where(arranged, n => n.Hit is HitResult.ButtonHit b && b.Action == action);

    private static bool Contains(Rect<float> r, float x, float y) => x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;

    // A click where the router would put it: the top-most thing with a hit that holds the point answers, as CellLayout.HitTest does.
    private static void ClickAt(ImmutableArray<Layout.ArrangedNode<float>> arranged, float x, float y, bool twice = false)
    {
        for (var i = arranged.Length - 1; i >= 0; i--)
        {
            if (arranged[i].Node.Hit is not null && Contains(arranged[i].Bounds, x, y))
            {
                (twice ? arranged[i].Node.OnDoubleClick : arranged[i].Node.OnClick)?.Invoke(InputModifier.None);
                return;
            }
        }
    }

    private static void ClickButton(ImmutableArray<Layout.ArrangedNode<float>> arranged, string action)
    {
        var bounds = Button(arranged, action);
        ClickAt(arranged, bounds.X + bounds.Width / 2f, bounds.Y + bounds.Height / 2f);
    }

    // The point in a PR's row clear of every link, chip and button: at its left edge, on the stripe.
    private static (float X, float Y) RowPoint(ImmutableArray<Layout.ArrangedNode<float>> arranged, int index)
    {
        var row = arranged.First(a => a.Node.Hit is HitResult.ListItemHit h && h.Index == index).Bounds;
        return (row.X + 2f, row.Y + row.Height / 2f);
    }

    private static string Dump(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        string.Join("\n", arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Select(t => t.Value));

    private static string[] Urls(PullRequestsView<RgbaImage> page) => [.. page.Rows.Select(r => r.Url)];

    private static int[] Numbers(PullRequestsView<RgbaImage> page) => [.. page.Rows.Select(r => r.Number)];

    // WCAG 2.x relative luminance and contrast ratio.
    private static double Luminance(RGBAColor32 c)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.Red) + 0.7152 * Linear(c.Green) + 0.0722 * Linear(c.Blue);
    }

    private static double Contrast(RGBAColor32 a, RGBAColor32 b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void TheRailListsPullRequestsBetweenReposAndBackgroundTasksOnItsOwnChord()
    {
        var pages = PilotChrome<RgbaImage>.Pages.Select(p => p.Page).ToList();
        pages.IndexOf(PilotPage.PullRequests).ShouldBe(pages.IndexOf(PilotPage.Repos) + 1);
        pages.IndexOf(PilotPage.Jobs).ShouldBe(pages.IndexOf(PilotPage.PullRequests) + 1);
        var entry = PilotChrome<RgbaImage>.Pages.Single(p => p.Page == PilotPage.PullRequests);
        entry.Shortcut.ShouldBe(new KeyChord(InputKey.D9, InputModifier.Ctrl));
        entry.Locked.ShouldBeNull();
        PilotChrome<RgbaImage>.Pages.Select(p => p.Shortcut).Distinct().Count().ShouldBe(PilotChrome<RgbaImage>.Pages.Count);
        PilotChrome<RgbaImage>.Pages.Select(p => p.Icon).Distinct().Count().ShouldBe(PilotChrome<RgbaImage>.Pages.Count);

        using var renderer = new RgbaImageRenderer(1, 1);
        var (chrome, page) = Chrome(renderer);
        chrome.Show(PilotPage.PullRequests);
        chrome.PageWidget.ShouldBeSameAs(page);
    }

    [Fact]
    public void TheTopBarSaysPullRequestsTheCountsAndHasFilterPillsAndARefreshAsTallAsTheQuestionMark()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, page) = Chrome(renderer);
        chrome.Nodes.Choices = [new NodeMark("a", "a", "SE1", true), new NodeMark("b", "b", "LE1", false)];
        var arranged = Arrange(renderer, page, Screen());

        var text = Dump(arranged);
        text.ShouldContain("pilot v2");
        text.ShouldContain("pull requests");
        text.ShouldContain("8 open · 1 ready · 1 checks running · 1 checks failed · 1 conflicts · 1 changes requested");
        Screen().Counts.ShouldBe("8 open · 1 ready · 1 checks running · 1 checks failed · 1 conflicts · 1 changes requested");

        // Refresh is the page's own button, in the middle of the bar: after the node pills and the counts, before Autopilot, as tall as the ?.
        var help = Button(arranged, "about:toggle");
        foreach (var action in new[] { "prs:refresh", "nodes:all", "nodes:b", AutopilotSwitch.Action })
        {
            (Button(arranged, action).Y, Button(arranged, action).Height).ShouldBe((help.Y, help.Height), action);
        }
        Button(arranged, "prs:refresh").X.ShouldBeGreaterThan(Button(arranged, "nodes:b").X);
        Button(arranged, "prs:refresh").X.ShouldBeLessThan(Button(arranged, AutopilotSwitch.Action).X);

        // The pills under it, on the row of the repo search: one height and one middle, the same height as the bar's buttons.
        var search = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput }).Bounds;
        foreach (var action in new[] { "prs:filter:mine", "prs:filter:flights", "prs:filter:needs-me" })
        {
            (Button(arranged, action).Y, Button(arranged, action).Height).ShouldBe((search.Y, search.Height), action);
        }
        search.Height.ShouldBe(help.Height);
        Button(arranged, "prs:filter:mine").X.ShouldBeLessThan(Button(arranged, "prs:filter:flights").X);
        Button(arranged, "prs:filter:needs-me").X.ShouldBeLessThan(search.X);
    }

    [Theory]
    [InlineData(800f)]
    [InlineData(1000f)]
    [InlineData(1280f)]
    public void TheBarsButtonsNeverLeaveTheWindowhoweverLongTheCountsAre(float width)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 900);
        var (chrome, page) = Chrome(renderer);
        chrome.Nodes.Choices = [new NodeMark("a", "a", "SE1", true), new NodeMark("b", "b", "LE1", false)];
        var arranged = Arrange(renderer, page, Screen(), width);

        foreach (var action in new[] { "prs:refresh", AutopilotSwitch.Action, "about:toggle", "nodes:b" })
        {
            var bounds = Button(arranged, action);
            bounds.X.ShouldBeGreaterThanOrEqualTo(0f, action);
            (bounds.X + bounds.Width).ShouldBeLessThanOrEqualTo(width, action);
        }
        // The counts shrink to fit and carry the whole line on hover.
        arranged.Select(a => a.Node.Tooltip).ShouldContain(Screen().Counts);
    }

    [Fact]
    public void TheCountsAreNotCutWhereTheBarHasRoom()
    {
        using var renderer = new RgbaImageRenderer(2000, 900);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen(), 2000f);

        var counts = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == Screen().Counts);
        var (measured, _) = renderer.MeasureText(Screen().Counts, SessionsCapture.FontPath, 15f);
        counts.Bounds.Width.ShouldBeGreaterThanOrEqualTo(measured);
    }

    [Fact]
    public void GroupsGoByNameEachWithItsRepoAsALinkAndItsCountAndARepoWithNoPRHasNone()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen());

        // The repos by name, whatever their PRs' states, so a group never moves on a refresh.
        var headers = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit b && b.Action.StartsWith("prs:group:", StringComparison.Ordinal)).Select(a => ((HitResult.ButtonHit)a.Node.Hit!).Action).ToList();
        headers.ShouldBe(["prs:group:sebgod/notes", "prs:group:SharpAstro/DIR.Lib", "prs:group:SharpAstro/pilot", "prs:group:SharpAstro/tianwen"]);
        Numbers(page).ShouldBe([12, 300, 301, 252, 261, 258, 1799, 1820]);

        var text = Dump(arranged);
        text.ShouldContain("SharpAstro/pilot");
        text.ShouldContain("3 PRs");
        text.ShouldContain("1 PR");

        // The name is a link out of the app, ↗ marked, to the repo.
        var opened = new List<string>();
        page.OnOpen = opened.Add;
        var link = Button(arranged, "prs:repo:SharpAstro/pilot");
        ClickAt(arranged, link.X + 2f, link.Y + link.Height / 2f);
        opened.ShouldBe(["https://github.com/SharpAstro/pilot"]);

        // A repo with none left by the pills has no header at all.
        page.Filter = PullRequestFilter.Flights;
        Arrange(renderer, page, Screen());
        Numbers(page).ShouldBe([252, 261, 1799]);
        var flightsOnly = Arrange(renderer, page, Screen());
        Dump(flightsOnly).ShouldNotContain("sebgod/notes");
        Dump(flightsOnly).ShouldNotContain("SharpAstro/DIR.Lib");
    }

    [Fact]
    public void ARowsChipTitleFlightAuthorReviewAndAgeAreWhatTheRowSays()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen());
        var text = Dump(arranged);

        foreach (var shown in new[] { "Ready", "Checks running 4/7", "In merge queue #2", "Conflicts", "Changes requested", "Behind", "Draft", "#252 Pull requests page: every open PR across the orgs", "✈ PIL252", "✈ TIA1799",
            "sebgod", "dependabot[bot]", "passed", "changes", "reviewing", "none", "updated 5m ago", "updated 3h ago", "updated 4d ago" })
        {
            text.ShouldContain(shown);
        }
        // The age's whole date is on hover, in UTC.
        arranged.Select(a => a.Node.Tooltip).ShouldContain("Updated 2026-10-28 11:55 UTC · opened 2026-10-26 12:00 UTC");

        // The review is a link to its comment; the flight chip opens the flight; a failed check's chip opens the run.
        var opened = new List<string>();
        var flights = new List<string?>();
        page.OnOpen = opened.Add;
        page.OnFlight = pr => flights.Add(pr.Flight);
        var review = Button(arranged, "pr:https://github.com/SharpAstro/pilot/pull/252:review");
        ClickAt(arranged, review.X + 2f, review.Y + review.Height / 2f);
        var flight = Button(arranged, "pr:https://github.com/SharpAstro/pilot/pull/252:flight");
        ClickAt(arranged, flight.X + 2f, flight.Y + flight.Height / 2f);
        var run = Button(arranged, "pr:https://github.com/SharpAstro/tianwen/pull/1820:run");
        ClickAt(arranged, run.X + 2f, run.Y + run.Height / 2f);
        var title = Button(arranged, "pr:https://github.com/SharpAstro/pilot/pull/258");
        ClickAt(arranged, title.X + 2f, title.Y + title.Height / 2f);
        opened.ShouldBe(["https://github.com/SharpAstro/pilot/pull/252#pullrequestreview-1", "https://github.com/SharpAstro/tianwen/actions/runs/9", "https://github.com/SharpAstro/pilot/pull/258"]);
        flights.ShouldBe(["PIL252"]);

        // A link that leaves the app ends in ↗: the title, the review, the repo.
        text.ShouldContain("#252 Pull requests page: every open PR across the orgs ↗");
        text.ShouldContain("passed ↗");
        text.ShouldContain("SharpAstro/pilot");
    }

    [Theory]
    [InlineData(1280f)]
    [InlineData(2000f)]
    public void EveryRowIsOneHeightAndTheColumnsLineUpAcrossTheGroups(float width)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 1100);
        var (_, page) = Chrome(renderer);
        var screen = Screen();
        var arranged = Arrange(renderer, page, screen, width, 1100f);
        var prs = screen.Row!.PullRequests;

        // One row height, every group's rows alike, and one height for each group's header.
        var rows = arranged.Where(a => a.Node.Hit is HitResult.ListItemHit).Select(a => a.Bounds).ToList();
        rows.Count.ShouldBe(8);
        rows.Select(r => r.Height).Distinct().ShouldBe([PullRequestsView<RgbaImage>.RowHeight]);
        rows.Select(r => r.X).Distinct().Count().ShouldBe(1);
        rows.Select(r => r.Width).Distinct().Count().ShouldBe(1);
        arranged.Where(a => a.Node.Hit is HitResult.ButtonHit b && b.Action.StartsWith("prs:group:", StringComparison.Ordinal)).Select(a => a.Bounds.Height).Distinct()
            .ShouldBe([PullRequestsView<RgbaImage>.GroupHeight]);

        // Each column starts at one x and is one wide, in every group: the chip, the title, the flight chip, the author, the review and the age.
        (float X, float Width)[] Column(Func<PullRequestRow, Func<Layout.Node, bool>> which) =>
            [.. prs.Select(pr => arranged.Where(a => which(pr)(a.Node)).Select(a => (a.Bounds.X, a.Bounds.Width)).ToList()).Where(c => c.Count > 0).Select(c => c[0]).Distinct()];

        Column(pr => n => n.Tooltip == pr.Label).Length.ShouldBe(1, "state chips");
        Column(pr => n => n.Hit is HitResult.ButtonHit b && b.Action == $"pr:{pr.Url}").Length.ShouldBe(1, "titles");
        Column(pr => n => n.Hit is HitResult.ButtonHit b && b.Action == $"pr:{pr.Url}:flight").Length.ShouldBe(1, "flight chips");
        Column(pr => n => n.Tooltip?.StartsWith($"Updated {pr.UpdatedAt!.Value.UtcDateTime:yyyy-MM-dd HH:mm}", StringComparison.Ordinal) == true).Length.ShouldBe(1, "ages");

        // The chip fills its column, so a column of them is one width however short a label is.
        var chips = prs.Select(pr => arranged.First(a => a.Node.Tooltip is { } t && t.StartsWith(pr.Label, StringComparison.Ordinal)).Bounds).ToList();
        chips.Select(c => c.Width).Distinct().Count().ShouldBe(1);
        chips.Select(c => c.Height).Distinct().ShouldBe([PullRequestsView<RgbaImage>.ChipHeight]);

        // Within a row the chip and the flight chip share one middle with the row's text: one height, one middle.
        foreach (var pr in prs)
        {
            var row = arranged.First(a => a.Node.Hit is HitResult.ListItemHit && arranged.Any(c => c.Node.Hit is HitResult.ButtonHit b && b.Action == $"pr:{pr.Url}" && Contains(a.Bounds, c.Bounds.X + 1, c.Bounds.Y + 1))).Bounds;
            var middle = row.Y + row.Height / 2f;
            var chip = arranged.First(a => a.Node.Tooltip is { } t && t.StartsWith(pr.Label, StringComparison.Ordinal)).Bounds;
            (chip.Y + chip.Height / 2f).ShouldBe(middle, 1.5f, pr.Label);
            if (pr.Flight is not null)
            {
                var flight = Button(arranged, $"pr:{pr.Url}:flight");
                flight.Height.ShouldBe(PullRequestsView<RgbaImage>.ChipHeight);
                (flight.Y + flight.Height / 2f).ShouldBe(middle, 1.5f, pr.Flight);
            }

            // The actions column is kept at the right, empty: nothing of the row's is in it.
            var title = Button(arranged, $"pr:{pr.Url}");
            (title.X + title.Width).ShouldBeLessThanOrEqualTo(row.X + row.Width - PullRequestsView<RgbaImage>.ActionsWidth);
        }

        // Nothing of the page lies past the window's right edge.
        foreach (var node in arranged.Where(a => a.Bounds.Width > 0f))
        {
            (node.Bounds.X + node.Bounds.Width).ShouldBeLessThanOrEqualTo(width + 0.5f);
        }
    }

    [Theory]
    [InlineData(1280f)]
    [InlineData(2000f)]
    public void ALongTitleIsCutWithAnEllipsisAndCarriesItsWholeTextOnHoverAndNothingIsCutSilently(float width)
    {
        using var renderer = new RgbaImageRenderer((uint)width, 1100);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen(), width, 1100f);

        // The long title is cut: its text is wider than the room its cell gives it, and the cell says it whole.
        var whole = $"#1820 {LongTitle}";
        var title = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == whole + Followable.Away);
        var text = (Layout.Content.Text)((Layout.Node.Leaf)title.Node).Content;
        var (measured, _) = renderer.MeasureText(text.Value, SessionsCapture.FontPath, text.FontSize);
        measured.ShouldBeGreaterThan(title.Bounds.Width);
        text.Trim.ShouldBe(TextTrim.End);
        arranged.Select(a => a.Node.Tooltip).OfType<string>().ShouldContain(t => t.StartsWith(whole, StringComparison.Ordinal));

        // And in general: any text wider than its box has the whole of it on the box or on what holds the box.
        var cut = 0;
        for (var i = 0; i < arranged.Length; i++)
        {
            if (arranged[i].Node is not Layout.Node.Leaf { Content: Layout.Content.Text { Value.Length: > 0 } leaf } || leaf.Trim == TextTrim.None)
            {
                continue;
            }
            var (wide, _) = renderer.MeasureText(leaf.Value, SessionsCapture.FontPath, leaf.FontSize);
            if (wide <= arranged[i].Bounds.Width + 0.5f)
            {
                continue;
            }
            cut++;
            // The text itself, then each ancestor up the pre-order list.
            var depth = arranged[i].Depth;
            // The ↗ that marks a link is not part of what the hover says: it says the title whole.
            var words = leaf.Value.Replace(Followable.Away, "").Trim();
            var carried = arranged[i].Node.Tooltip?.Contains(words, StringComparison.Ordinal) == true;
            for (var j = i - 1; j >= 0 && !carried && depth > 0; j--)
            {
                if (arranged[j].Depth < depth)
                {
                    depth = arranged[j].Depth;
                    carried = arranged[j].Node.Tooltip?.Contains(words, StringComparison.Ordinal) == true;
                }
            }
            carried.ShouldBeTrue($"'{leaf.Value}' is cut at {arranged[i].Bounds.Width} of {wide} units with no hover text");
        }
        cut.ShouldBeGreaterThan(0);

        // The long login and the long failed check are cut and carried whole as well.
        arranged.Select(a => a.Node.Tooltip).ShouldContain("a-contributor-with-a-very-long-login");
        arranged.Select(a => a.Node.Tooltip).ShouldContain("Checks failed: build-and-test (ubuntu-latest, net10.0) (opens the run ↗)");
    }

    [Fact]
    public void ANarrowWindowDropsTheOptionalColumnsAndKeepsTheChipTheTitleAndTheActionsColumn()
    {
        using var renderer = new RgbaImageRenderer(900, 900);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen(), 900f);
        var text = Dump(arranged);

        text.ShouldContain("Ready");
        text.ShouldContain("#252 Pull requests page");
        // The flight and the review are the last to go; the author is the first, and the age goes before them.
        text.ShouldContain("✈ PIL252");
        text.ShouldContain("passed");
        text.ShouldNotContain("updated 5m ago");
        text.ShouldNotContain("dependabot[bot]");
        var title = Button(arranged, "pr:https://github.com/SharpAstro/pilot/pull/252");
        title.Width.ShouldBeGreaterThanOrEqualTo(180f);

        // Wide, all of them are there, and the title has what is left.
        var wide = Arrange(renderer, page, Screen(), 2000f);
        Dump(wide).ShouldContain("updated 5m ago");
        Dump(wide).ShouldContain("dependabot[bot]");
        foreach (var node in arranged.Where(a => a.Bounds.Width > 0f))
        {
            (node.Bounds.X + node.Bounds.Width).ShouldBeLessThanOrEqualTo(900.5f);
        }
    }

    [Fact]
    public void AGroupFoldsOnAClickAnywhereOnItsHeaderAndTheLinkInItDoesNotFoldIt()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen());
        Numbers(page).ShouldBe([12, 300, 301, 252, 261, 258, 1799, 1820]);

        // Anywhere: the far right of the header, clear of the name.
        var header = Button(arranged, "prs:group:SharpAstro/pilot");
        ClickAt(arranged, header.X + header.Width - 30f, header.Y + header.Height / 2f);
        page.IsFolded("SharpAstro/pilot").ShouldBeTrue();
        arranged = Arrange(renderer, page, Screen());
        Numbers(page).ShouldBe([12, 300, 301, 1799, 1820]);
        Dump(arranged).ShouldNotContain("Checks running 4/7");
        // The header stays, and says how many it holds.
        Dump(arranged).ShouldContain("SharpAstro/pilot");
        Dump(arranged).ShouldContain("3 PRs");

        // The link in it opens the repo and leaves the fold as it is.
        var opened = new List<string>();
        page.OnOpen = opened.Add;
        var name = Button(arranged, "prs:repo:SharpAstro/pilot");
        ClickAt(arranged, name.X + 2f, name.Y + name.Height / 2f);
        opened.ShouldBe(["https://github.com/SharpAstro/pilot"]);
        page.IsFolded("SharpAstro/pilot").ShouldBeTrue();

        // The name itself folds nothing but the header's own area does: the middle of the header's text also folds.
        header = Button(arranged, "prs:group:SharpAstro/pilot");
        ClickAt(arranged, header.X + 4f, header.Y + header.Height / 2f);
        page.IsFolded("SharpAstro/pilot").ShouldBeFalse();
        arranged = Arrange(renderer, page, Screen());
        Numbers(page).ShouldBe([12, 300, 301, 252, 261, 258, 1799, 1820]);

        // The link is the name and its ↗ only, not the header's width: a click just past it folds, it does not open GitHub (the owner, 2026-10-29).
        name = Button(arranged, "prs:repo:SharpAstro/pilot");
        header = Button(arranged, "prs:group:SharpAstro/pilot");
        name.Width.ShouldBeLessThan(header.Width / 3f);
        // The name is drawn whole on a wide page: its text leaf is as wide as the name measures (a plain star inside the
        // content-capped stack was arranged 0 wide, and the bounds above could not tell).
        var (measured, _) = renderer.MeasureText("SharpAstro/pilot", SessionsCapture.FontPath, 14f);
        NameLeaf(arranged, "SharpAstro/pilot").Width.ShouldBeGreaterThanOrEqualTo(measured - 0.5f);
        ClickAt(arranged, name.X + name.Width + 40f, header.Y + header.Height / 2f);
        opened.Count.ShouldBe(1);
        page.IsFolded("SharpAstro/pilot").ShouldBeTrue();

        // On a narrow page it gives way, but is never squeezed away or wider than its header.
        var narrow = Arrange(renderer, page, Screen(), width: 520f);
        var squeezed = NameLeaf(narrow, "SharpAstro/pilot").Width;
        squeezed.ShouldBeGreaterThan(0f);
        squeezed.ShouldBeLessThanOrEqualTo(Button(narrow, "prs:group:SharpAstro/pilot").Width);
    }

    // The arranged bounds of a repo header's name text: the leaf whose text is exactly the repo's name.
    private static Rect<float> NameLeaf(ImmutableArray<Layout.ArrangedNode<float>> arranged, string repo)
    {
        var leaf = arranged.First(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: var v } } && v == repo);
        return leaf.Bounds;
    }

    [Fact]
    public void ThePillsNarrowTheListToMineFlightsOrWhatNeedsMeAndTheLitOneClickedAgainBringsThemBack()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var screen = Screen();

        Arrange(renderer, page, screen);
        page.Filter.ShouldBeNull();
        Numbers(page).Length.ShouldBe(8);

        void Pick(string action) => ClickButton(Arrange(renderer, page, screen), action);

        // Mine: the PRs the node's gh login wrote.
        Pick("prs:filter:mine");
        page.Filter.ShouldBe(PullRequestFilter.Mine);
        Arrange(renderer, page, screen);
        Numbers(page).ShouldBe([12, 252, 261, 1799]);
        Dump(Arrange(renderer, page, screen)).ShouldContain("showing 4 of 8");

        Pick("prs:filter:flights");
        Arrange(renderer, page, screen);
        Numbers(page).ShouldBe([252, 261, 1799]);

        Pick("prs:filter:needs-me");
        Arrange(renderer, page, screen);
        Numbers(page).ShouldBe([300, 252, 1799]);

        // The lit pill is the one chosen, drawn in the tint the node pills use, and its tooltip says how to go back.
        var arranged = Arrange(renderer, page, screen);
        arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "prs:filter:needs-me" }).Node.Background.ShouldBe(NodeFilter.Lit.Bg);
        arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "prs:filter:mine" }).Node.Background.ShouldNotBe(NodeFilter.Lit.Bg);
        arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "prs:filter:needs-me" }).Node.Tooltip.ShouldBe("Showing only Needs me; click again to show every pull request");
        arranged.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "prs:filter:mine" }).Node.Tooltip.ShouldBe("Show only Mine");

        // The lit pill clicked again is no pill: every PR is back.
        Pick("prs:filter:needs-me");
        page.Filter.ShouldBeNull();
        Arrange(renderer, page, screen);
        Numbers(page).Length.ShouldBe(8);
        Dump(Arrange(renderer, page, screen)).ShouldNotContain("showing");

        // A node that could not say who it reads as: Mine is whatever is no flight's.
        var unsure = Screen(Row(viewer: null));
        page.Filter = PullRequestFilter.Mine;
        Arrange(renderer, page, unsure);
        Numbers(page).ShouldBe([12, 300, 301, 258, 1820]);
    }

    [Fact]
    public void TheRepoSearchKeepsTheReposWhoseNameHoldsWhatIsTypedAndSaysWhenNoneDoes()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var screen = Screen();

        page.Search.Text = "tian";
        var arranged = Arrange(renderer, page, screen);
        Numbers(page).ShouldBe([1799, 1820]);
        Dump(arranged).ShouldContain("showing 2 of 8");
        Dump(arranged).ShouldNotContain("SharpAstro/pilot");

        // Case does not matter, and the org is part of the name.
        page.Search.Text = "SHARPASTRO/dir";
        Arrange(renderer, page, screen);
        Numbers(page).ShouldBe([300, 301]);

        // Together with a pill.
        page.Search.Text = "sharpastro";
        page.Filter = PullRequestFilter.NeedsMe;
        Arrange(renderer, page, screen);
        Numbers(page).ShouldBe([300, 252, 1799]);

        // None: it says so, names the pill, and the search box stays to be cleared.
        page.Filter = null;
        page.Search.Text = "zzz";
        arranged = Arrange(renderer, page, screen);
        page.Rows.ShouldBeEmpty();
        Dump(arranged).ShouldContain("No pull requests match \"zzz\".");
        arranged.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput }).ShouldBeTrue();

        page.Search.Text = "";
        Arrange(renderer, page, screen);
        Numbers(page).Length.ShouldBe(8);
    }

    [Fact]
    public void AClickSelectsARowADoubleClickOpensItsFlightOrThePRAndTheArrowsMove()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, page) = Chrome(renderer);
        chrome.LinkPullRequests();
        chrome.Show(PilotPage.PullRequests);
        var screen = Screen();
        var opened = new List<string>();
        page.OnOpen = opened.Add;
        var arranged = Arrange(renderer, page, screen);
        page.Selected.ShouldBe(0);

        // A click on the sixth row (#258, nobody's flight) selects it: the row, not any link in it.
        var (x, y) = RowPoint(arranged, 5);
        ClickAt(arranged, x, y);
        page.Selected.ShouldBe(5);
        page.SelectedUrl.ShouldBe("https://github.com/SharpAstro/pilot/pull/258");
        page.FocusFlight.ShouldBeNull();
        opened.ShouldBeEmpty();

        // Painted, the selected row is the one in the focus colour.
        arranged = Arrange(renderer, page, screen);
        var selected = arranged.First(a => a.Node.Hit is HitResult.ListItemHit { Index: 5 }).Node;
        selected.Background.ShouldBe(Palette.Standard.RowFocusBg);

        // A double-click on it opens the PR in the browser.
        ClickAt(arranged, x, y, twice: true);
        opened.ShouldBe(["https://github.com/SharpAstro/pilot/pull/258"]);

        // The arrows move the selection over the rows painted, and the selection follows by URL.
        page.Render(screen, new RectF32(0, 0, 1280, 900));
        page.HandleInput(new InputEvent.KeyDown(InputKey.Up, InputModifier.None)).ShouldBeTrue();
        page.Selected.ShouldBe(4);
        page.SelectedUrl.ShouldBe("https://github.com/SharpAstro/pilot/pull/261");
        page.FocusFlight.ShouldBe("PIL261");
        page.HandleInput(new InputEvent.KeyDown(InputKey.Down, InputModifier.None)).ShouldBeTrue();
        page.HandleInput(new InputEvent.KeyDown(InputKey.Down, InputModifier.None)).ShouldBeTrue();
        page.Selected.ShouldBe(6);
        page.SelectedUrl.ShouldBe("https://github.com/SharpAstro/tianwen/pull/1799");

        // The rows are read again with #252 gone: the groups keep their order, every row after it moves up one, and the
        // selection stays on its PR.
        var without252 = Screen(Row() with { PullRequests = [.. Row().PullRequests.Where(p => p.Number != 252)] });
        Arrange(renderer, page, without252);
        Numbers(page).ShouldBe([12, 300, 301, 261, 258, 1799, 1820]);
        page.SelectedUrl.ShouldBe("https://github.com/SharpAstro/tianwen/pull/1799");
        page.Selected.ShouldBe(5);
        Arrange(renderer, page, screen);

        // A double-click on a flight's row (#252), and Enter on it, open the flight's page.
        page.Select(3);
        ClickAt(arranged = Arrange(renderer, page, screen), x, arranged.First(a => a.Node.Hit is HitResult.ListItemHit { Index: 3 }).Bounds.Y + 20f, twice: true);
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.IsDetailOpen.ShouldBeTrue();
        chrome.Show(PilotPage.PullRequests);
        chrome.Flights.CloseDetail();
        Arrange(renderer, page, screen);
        page.Select(4);
        page.HandleInput(new InputEvent.KeyDown(InputKey.Enter, InputModifier.None)).ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Flights.OpenCode.ShouldBe("PIL261");

        // Enter on a PR that is no flight's opens it in the browser.
        chrome.Show(PilotPage.PullRequests);
        Arrange(renderer, page, screen);
        page.Select(5);
        page.HandleInput(new InputEvent.KeyDown(InputKey.Enter, InputModifier.None)).ShouldBeTrue();
        opened.Count.ShouldBe(2);
    }

    [Fact]
    public void SelectingAFlightsPRLightsItsCardsInThePanelAndBackReturnsToTheSelection()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, page) = Chrome(renderer);
        chrome.LinkPullRequests();
        var card = new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "stop", Subject = Verbs.FlightTarget("PIL252"), Title = "PIL252 is ready to merge", At = Now, State = CardState.Open };
        var cards = CardsScreen.From([card]);
        var sessions = new SessionsScreen([], "test", Now);
        var screen = Screen();

        chrome.Show(PilotPage.PullRequests);
        // The page's own frame finds its first row, and the next frame lights the panel for it.
        chrome.Render(sessions, null, cards, null, null, null, screen, new RectF32(0, 0, 1280, 900));
        chrome.Render(sessions, null, cards, null, null, null, screen, new RectF32(0, 0, 1280, 900));
        // The first row is #12, no flight's: it lights none.
        page.SelectedRow!.Number.ShouldBe(12);
        chrome.Cards.Focus.ShouldBeNull();

        // #252, a flight's: its cards are lit.
        page.Select(3);
        chrome.Render(sessions, null, cards, null, null, null, screen, new RectF32(0, 0, 1280, 900));
        chrome.Render(sessions, null, cards, null, null, null, screen, new RectF32(0, 0, 1280, 900));
        page.SelectedRow!.Number.ShouldBe(252);
        chrome.Cards.Focus.ShouldBe("PIL252");

        // The flight chip opens the flight's page, a move Back returns from, to the same selection.
        page.Select(4);
        chrome.Render(sessions, null, cards, null, null, null, screen, new RectF32(0, 0, 1280, 900));
        var chip = Button(Arrange(renderer, page, screen), "pr:https://github.com/SharpAstro/pilot/pull/261:flight");
        ClickAt(Arrange(renderer, page, screen), chip.X + 2f, chip.Y + chip.Height / 2f);
        chrome.Page.ShouldBe(PilotPage.Flights);
        chrome.Back().ShouldBeTrue();
        chrome.Page.ShouldBe(PilotPage.PullRequests);
        page.SelectedUrl.ShouldBe("https://github.com/SharpAstro/pilot/pull/261");
    }

    [Fact]
    public void WithNoOpenPRTheEmptyLineNamesTheOrgsAndBeforeTheFirstReadItSaysSo()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);

        var empty = Arrange(renderer, page, Screen(Row() with { PullRequests = [] }));
        // Each org of the read says so in its own group; the one line is for a read with no org to say it of.
        Dump(empty).ShouldContain("No open pull requests.");
        Dump(empty).ShouldContain("0 open");
        page.Rows.ShouldBeEmpty();
        page.Selected.ShouldBe(-1);
        var nowhere = Arrange(renderer, page, Screen(Row() with { PullRequests = [], Orgs = [] }));
        Dump(nowhere).ShouldContain("No open pull requests in any repo pilot works in.");
        nowhere.Any(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput }).ShouldBeFalse();

        var unread = Arrange(renderer, page, Screen(null) with { Row = null });
        Dump(unread).ShouldContain("The node has not read the pull requests yet.");
        Dump(unread).ShouldContain("pull requests not read yet");
    }

    [Fact]
    public void TheSourceLineSaysWhenGitHubWasReadAndTurnsAmberPastFiveMinutes()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);

        (string Value, RGBAColor32 Color) SourceLine(PullRequestsScreen screen)
        {
            var arranged = Arrange(renderer, page, screen);
            var line = arranged.Select(a => a.Node).OfType<Layout.Node.Leaf>().Select(l => l.Content).OfType<Layout.Content.Text>().Single(t => t.Value.StartsWith("live from the node", StringComparison.Ordinal));
            return (line.Value, line.Color);
        }

        var fresh = SourceLine(Screen(Row(readAt: Now - TimeSpan.FromSeconds(75))));
        fresh.Value.ShouldBe("live from the node on node.sock · read from GitHub 1m ago (2026-10-28 11:58:45 UTC)");
        fresh.Color.ShouldBe(Palette.Standard.Dim);
        Screen(Row(readAt: Now - TimeSpan.FromMinutes(5))).Stale.ShouldBeFalse();

        var stale = SourceLine(Screen(Row(readAt: Now - TimeSpan.FromMinutes(6))));
        stale.Value.ShouldBe("live from the node on node.sock · read from GitHub 6m ago (2026-10-28 11:54:00 UTC) · stale: over 5 minutes old, Refresh reads it now");
        stale.Color.ShouldBe(Palette.Standard.Held);
        // Amber alone says nothing: the words do too, and the amber is legible on the bar.
        Contrast(Palette.Standard.Held, Palette.Standard.HeaderBg).ShouldBeGreaterThanOrEqualTo(4.5);

        // An org GitHub would not read is said under the bar, and its PRs are the ones read before.
        var errored = Row() with { Errors = [new PullRequestsError { Org = "sebgod", Message = "GitHub said 502\nBad gateway" }] };
        Dump(Arrange(renderer, page, Screen(errored))).ShouldContain("sebgod: GitHub said 502 Bad gateway");
    }

    [Fact]
    public async Task RefreshAsksTheNodeShowsItRunningAndSaysWhyWhenItIsRefused()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var screen = Screen();
        var running = new TaskCompletionSource();
        var redraws = 0;
        page.OnRefresh = _ => running.Task;
        page.Changed = () => redraws++;

        ClickButton(Arrange(renderer, page, screen), "prs:refresh");
        page.Refreshing.ShouldBeTrue();
        var arranged = Arrange(renderer, page, screen);
        Dump(arranged).ShouldContain("Refreshing…");
        // The label changes, the button does not: nothing after it moves.
        Button(arranged, "prs:refresh").Width.ShouldBe(100f, 0.5f);
        // A second press while it runs does nothing.
        ClickButton(arranged, "prs:refresh");

        running.SetResult();
        for (var i = 0; i < 200 && page.Refreshing; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        page.Refreshing.ShouldBeFalse();
        redraws.ShouldBe(2);
        Dump(Arrange(renderer, page, screen)).ShouldContain("Refresh");

        page.OnRefresh = _ => Task.FromException(new InvalidOperationException("no org to read"));
        await page.RefreshAsync();
        page.RefreshError.ShouldBe("no org to read");
        Dump(Arrange(renderer, page, screen)).ShouldContain("refresh refused: no org to read");

        // No node to ask (a snapshot file): no button.
        page.OnRefresh = null;
        Arrange(renderer, page, screen).Any(a => a.Node.Hit is HitResult.ButtonHit { Action: "prs:refresh" }).ShouldBeFalse();
    }

    [Fact]
    public void EveryColourPairMeetsTheContrastFloorOnTheBackgroundItIsDrawnOn()
    {
        var day = Palette.Standard;
        foreach (var kind in Enum.GetValues<PullRequestKind>())
        {
            var (bg, fg) = PullRequestsView<RgbaImage>.ChipColors(day, kind);
            Contrast(fg, bg).ShouldBeGreaterThanOrEqualTo(4.5, $"{kind} chip");
        }
        // The text of a row on every background a row has: the table, the alternate, the hovered, the selected.
        foreach (var ground in new[] { day.Ground, day.RowAltBg, day.RowHoverBg, day.RowFocusBg, day.PanelBg })
        {
            foreach (var (name, color) in new[] { ("link", day.Link), ("body", day.Body), ("bright", day.Bright), ("passed", day.Flying), ("changes", day.Held) })
            {
                Contrast(color, ground).ShouldBeGreaterThanOrEqualTo(4.5, $"{name} on {ground}");
            }
        }
        Contrast(day.Link, day.Button).ShouldBeGreaterThanOrEqualTo(4.5);
        // The column titles, on the ground they are drawn on.
        Contrast(day.Dim, day.Ground).ShouldBeGreaterThanOrEqualTo(4.5);

        // Night is dim red on black and nothing else, so its floor is the 3:1 of large text and icons: what its own palette reaches.
        var night = Palette.Night;
        foreach (var kind in Enum.GetValues<PullRequestKind>())
        {
            var (bg, fg) = PullRequestsView<RgbaImage>.ChipColors(night, kind);
            Contrast(fg, bg).ShouldBeGreaterThanOrEqualTo(3.0, $"{kind} chip at night");
        }
        foreach (var ground in new[] { night.Ground, night.RowAltBg, night.RowHoverBg })
        {
            Contrast(night.Link, ground).ShouldBeGreaterThanOrEqualTo(3.0, $"night link on {ground}");
            // The row text and the column titles are night's bright red, not its body red, which is under 3:1 on black.
            Contrast(night.Bright, ground).ShouldBeGreaterThanOrEqualTo(3.0, $"night text on {ground}");
        }
    }

    [Fact]
    public void TheNightPaletteRepaintsThePageAndTheWindowPaintsBoth()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, page) = Chrome(renderer);
        chrome.Show(PilotPage.PullRequests);
        var sessions = new SessionsScreen([], "test", Now);
        chrome.Render(sessions, null, null, null, null, null, Screen(), new RectF32(0, 0, 1280, 900));
        var day = renderer.Surface.Pixels.ToArray();

        chrome.Night.On = true;
        chrome.Render(sessions, null, null, null, null, null, Screen(), new RectF32(0, 0, 1280, 900));
        page.Night!.Palette.ShouldBeSameAs(Palette.Night);
        renderer.Surface.Pixels.ToArray().ShouldNotBe(day);

        var regions = new List<ClickableRegion>();
        page.CollectPaintedRegions(regions);
        var buttons = regions.Select(r => r.Result).OfType<HitResult.ButtonHit>().Select(b => b.Action).ToList();
        buttons.ShouldContain("prs:refresh");
        buttons.ShouldContain("prs:filter:mine");
        buttons.ShouldContain("prs:group:SharpAstro/pilot");
        regions.Select(r => r.Result).OfType<HitResult.ListItemHit>().Count(h => h.ListId == PullRequestsView<RgbaImage>.ListId).ShouldBe(8);

        // And the day's again once the Night switch is off.
        chrome.Night.On = false;
        chrome.Render(sessions, null, null, null, null, null, Screen(), new RectF32(0, 0, 1280, 900));
        (page.Night!.Palette ?? Palette.Standard).ShouldBeSameAs(Palette.Standard);
    }

    [Fact]
    public async Task TheFeedKeepsTheNodesRowAndTheFleetMergesEachNodesReadIntoOnePerPR()
    {
        const string lan = "10.0.0.9:47731";
        var se1 = new FakeNode();
        var le1 = new FakeNode();
        var here = new NodeFeed(se1.Open, "node.sock", TimeSpan.FromMilliseconds(20));
        await using var hereOwner = here;
        await using var watch = new FleetWatch(endpoint => new NodeFeed(le1.Open, endpoint, TimeSpan.FromMilliseconds(20)));
        here.Start();
        watch.Know([(lan, "LE1")]);
        await Until(() => se1.OpenCount > 0 && le1.OpenCount > 0);
        here.PullRequests().ShouldBeNull();

        // SE1 read #1820 and #261 (as of 30 s ago), LE1 read #1820 and #252 three minutes ago.
        var older = Row(readAt: Now - TimeSpan.FromMinutes(3));
        var newer = Row(readAt: Now - TimeSpan.FromSeconds(30));
        se1.Send(Live.Snapshot(1, "s-se1") with { At = Now, PullRequests = newer });
        le1.Send(Live.Snapshot(1, "s-le1") with { At = Now, PullRequests = older with { PullRequests = [older.PullRequests[0], older.PullRequests[2]] } });
        await Until(() => here.PullRequests() is not null);

        // The stream's later row replaces the held one.
        se1.Send(new PullRequestsChangedEvent { Seq = 2, At = Now, Row = newer with { PullRequests = [.. newer.PullRequests.Take(2)] } });
        await Until(() => here.PullRequests()!.PullRequests.Length == 2);

        // The page of the fleet: one row a PR whatever node read it, as old as the oldest read.
        using var vessel = new RgbaImageRenderer(1, 1);
        var (chrome, _) = Chrome(vessel);
        FleetPages pages = watch.Pages("node.sock", here, chrome.Nodes, Now);
        await Until(() => (pages = watch.Pages("node.sock", here, chrome.Nodes, Now)).PullRequests!.Row!.PullRequests.Length == 3);
        var merged = pages.PullRequests!.Row!;
        merged.PullRequests.Select(p => p.Number).Order().ShouldBe([252, 261, 1820]);
        merged.ReadAt.ShouldBe(older.ReadAt);

        // And a node picked alone is that node's read only.
        chrome.Nodes.Toggle(chrome.Nodes.Choices.Single(c => c.Name == "LE1").Key);
        watch.Pages("node.sock", here, chrome.Nodes, Now).PullRequests!.Row!.PullRequests.Select(p => p.Number).Order().ShouldBe([252, 1820]);
        chrome.Nodes.ShowAll();
    }

    [Fact]
    public void ACaptureRendersThePageInTheWindowAtBothWidthsInBothPalettes()
    {
        // PILOT_TEST_CAPTURE_DIR keeps the frames as PNGs, for looking at; without it the frames are only painted.
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        var card = new CardRow { Id = "c1", Bin = CardBin.Ring, Kind = "stop", Subject = Verbs.FlightTarget("PIL252"), Title = "PIL252 is ready to merge", At = Now, State = CardState.Open };
        var card2 = new CardRow { Id = "c2", Bin = CardBin.Ring, Kind = "stop", Subject = Verbs.FlightTarget("TIA1799"), Title = "TIA1799's PR conflicts with main", At = Now, State = CardState.Open };
        foreach (var (width, height) in new[] { (1280, 800), (2000, 1000) })
        {
            foreach (var night in new[] { false, true })
            {
                using var renderer = new RgbaImageRenderer((uint)width, (uint)height);
                var (chrome, page) = Chrome(renderer);
                chrome.Show(PilotPage.PullRequests);
                chrome.Night.On = night;
                var sessions = new SessionsScreen([], "live from the node on node.sock", Now);
                // The stale read (an hour old) in one of the four: the amber source line.
                var screen = Screen(night && width == 1280 ? Row(readAt: Now - TimeSpan.FromMinutes(9)) : Row());
                chrome.Render(sessions, null, CardsScreen.From([card, card2]), null, null, null, screen, new RectF32(0, 0, width, height));
                // A moment later: the first row selected (a flight's) lights its card.
                page.Select(page.Rows.ToList().FindIndex(r => r.Flight == "PIL252"));
                chrome.Render(sessions, null, CardsScreen.From([card, card2]), null, null, null, screen, new RectF32(0, 0, width, height));
                chrome.Cards.Focus.ShouldBe("PIL252");
                if (dir is { Length: > 0 })
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"pull-requests-{width}-{(night ? "night" : "day")}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, width, height));
                }
            }
        }
    }

    [Fact]
    public void TheOtherStatesPaintToo_NothingOpenNothingReadYetAndASearchWithAFoldedGroup()
    {
        var dir = Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE_DIR");
        var sessions = new SessionsScreen([], "live from the node on node.sock", Now);

        void Paint(string name, PullRequestsScreen screen, Action<PullRequestsView<RgbaImage>>? setup = null)
        {
            using var renderer = new RgbaImageRenderer(1280, 640);
            var (chrome, page) = Chrome(renderer);
            chrome.Show(PilotPage.PullRequests);
            setup?.Invoke(page);
            chrome.Render(sessions, null, null, null, null, null, screen, new RectF32(0, 0, 1280, 640));
            chrome.Render(sessions, null, null, null, null, null, screen, new RectF32(0, 0, 1280, 640));
            if (dir is { Length: > 0 })
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"pull-requests-1280-{name}.png"), SharpAstro.Png.PngWriter.Encode(renderer.Surface.Pixels, 1280, 640));
            }
        }

        Paint("empty", Screen(Row() with { PullRequests = [] }));
        Paint("unread", new PullRequestsScreen(null, "live from the node on node.sock", Now));
        Paint("filtered", Screen(), page =>
        {
            page.Filter = PullRequestFilter.NeedsMe;
            page.Search.Text = "sharpastro";
            page.Toggle("SharpAstro/pilot");
        });
        Paint("no-match", Screen(), page => page.Search.Text = "zzz");
    }

    private const string BrokenMessage = "GitHub answered 502 Bad Gateway for the search of org broken-org, so its pull requests could not be read this time (and a very long message goes on from here, to be cut with an ellipsis and carried whole on hover)";

    /// <summary>The row of <see cref="Row"/> with two more orgs: <c>empty-org</c> with no PR, and <c>broken-org</c> whose read failed; and a rate-limit note.</summary>
    private static PullRequestsRow RowWithEmptyAndFailedOrgs() => Row() with
    {
        Orgs = ["SharpAstro", "sebgod", "empty-org", "broken-org"],
        Errors = [new PullRequestsError { Org = "broken-org", Message = BrokenMessage }],
        Note = "Rate-limited by GitHub until 14:05; reading again then",
    };

    private static string[] OrgHeaders(ImmutableArray<Layout.ArrangedNode<float>> arranged) =>
        [.. arranged.Where(a => a.Node.Hit is HitResult.ButtonHit b && b.Action.StartsWith("prs:org:", StringComparison.Ordinal)).Select(a => ((HitResult.ButtonHit)a.Node.Hit!).Action["prs:org:".Length..])];

    [Fact]
    public void AnOrgHeadsItsReposWithItsCountsAndTheOrgsGoByName()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen());

        // The orgs by name, whatever their PRs' states.
        OrgHeaders(arranged).ShouldBe(["sebgod", "SharpAstro"]);
        var text = Dump(arranged);
        text.ShouldContain("SharpAstro");
        text.ShouldContain("7 open · 1 ready · 1 checks running · 1 checks failed · 1 conflicts · 1 changes requested");
        text.ShouldContain("1 open");

        // An org's header is above its repos', and a taller row than a repo's; every org header is as tall as the next.
        var orgs = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit b && b.Action.StartsWith("prs:org:", StringComparison.Ordinal)).Select(a => a.Bounds).ToList();
        orgs.Select(o => o.Height).Distinct().ShouldBe([PullRequestsView<RgbaImage>.OrgHeight]);
        Button(arranged, "prs:org:SharpAstro").Y.ShouldBeLessThan(Button(arranged, "prs:group:SharpAstro/pilot").Y);
        Button(arranged, "prs:org:sebgod").Y.ShouldBeLessThan(Button(arranged, "prs:group:sebgod/notes").Y);
        Button(arranged, "prs:group:sebgod/notes").Y.ShouldBeLessThan(Button(arranged, "prs:org:SharpAstro").Y);
        Button(arranged, "prs:group:SharpAstro/pilot").X.ShouldBe(Button(arranged, "prs:org:SharpAstro").X);

        // The counts of an org are of all its PRs, whatever the pills show.
        page.Filter = PullRequestFilter.NeedsMe;
        arranged = Arrange(renderer, page, Screen());
        Numbers(page).ShouldBe([300, 252, 1799]);
        OrgHeaders(arranged).ShouldBe(["SharpAstro"]);
        Dump(arranged).ShouldContain("7 open · 1 ready");
    }

    [Fact]
    public void AnOrgFoldsOnAClickAnywhereOnItsHeaderItsReposKeepTheirFoldAndBothFoldsSurviveANewRead()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen());

        // A repo folded first, then its org.
        // At the caret by the header's left edge: the repo's name further along is a link to GitHub.
        var repoHeader = Button(arranged, "prs:group:SharpAstro/DIR.Lib");
        ClickAt(arranged, repoHeader.X + 8f, repoHeader.Y + repoHeader.Height / 2f);
        page.IsFolded("SharpAstro/DIR.Lib").ShouldBeTrue();
        arranged = Arrange(renderer, page, Screen());
        Numbers(page).ShouldBe([12, 252, 261, 258, 1799, 1820]);

        var header = Button(arranged, "prs:org:SharpAstro");
        ClickAt(arranged, header.X + header.Width - 30f, header.Y + header.Height / 2f);
        page.IsOrgFolded("SharpAstro").ShouldBeTrue();
        page.IsOrgFolded("sebgod").ShouldBeFalse();
        arranged = Arrange(renderer, page, Screen());
        // The org's repos are not listed, nor their rows, but the org's header says what it holds.
        Numbers(page).ShouldBe([12]);
        arranged.Any(a => a.Node.Hit is HitResult.ButtonHit { Action: "prs:group:SharpAstro/pilot" }).ShouldBeFalse();
        Dump(arranged).ShouldContain("7 open");
        Button(arranged, "prs:org:SharpAstro").Height.ShouldBe(PullRequestsView<RgbaImage>.OrgHeight);

        // A new read (the same PRs, read later) leaves both folds as they are.
        var later = Screen(Row(readAt: Now - TimeSpan.FromSeconds(5)));
        arranged = Arrange(renderer, page, later);
        page.IsOrgFolded("SharpAstro").ShouldBeTrue();
        page.IsFolded("SharpAstro/DIR.Lib").ShouldBeTrue();
        Numbers(page).ShouldBe([12]);

        // Unfolded, the org shows its repos as it left them: DIR.Lib still folded.
        ClickAt(arranged, header.X + 4f, header.Y + header.Height / 2f);
        page.IsOrgFolded("SharpAstro").ShouldBeFalse();
        Arrange(renderer, page, later);
        Numbers(page).ShouldBe([12, 252, 261, 258, 1799, 1820]);
    }

    [Fact]
    public void TheCapturesRowsDumpListsEachOrgWithItsCountsAboveItsReposAndSaysWhatAnEmptyOrFailedOrgSays()
    {
        var dump = SessionsCapture.PullRequestsDump(Screen(RowWithEmptyAndFailedOrgs())).Split('\n');

        // The orgs by name, sebgod before SharpAstro, each above its repos.
        dump[0].ShouldBe("sebgod  1 open");
        dump[1].ShouldBe("  sebgod/notes  1");
        var org = Array.IndexOf(dump, "SharpAstro  7 open · 1 ready · 1 checks running · 1 checks failed · 1 conflicts · 1 changes requested");
        var repo = Array.IndexOf(dump, "  SharpAstro/pilot  3");
        org.ShouldBeGreaterThan(1);
        repo.ShouldBeGreaterThan(org);
        dump[repo + 1].ShouldStartWith("    #252 Pull requests page: every open PR across the orgs | Ready | PIL252 | sebgod | review passed | updated 5m ago");
        // The failed org says why, not also that it has none; the empty one says it has none.
        dump[^4..].ShouldBe(["broken-org  0 open", $"  could not be read: {BrokenMessage}", "empty-org  0 open", "  no open pull requests"]);
        SessionsCapture.PullRequestsDump(new PullRequestsScreen(null, "x", Now)).ShouldBe("the node has not read the pull requests yet");
    }

    [Fact]
    public void AnOrgsHeaderReadsAsWellAsTheRowsDoInDayAndNight()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, page) = Chrome(renderer);
        var day = Arrange(renderer, page, Screen());
        var header = day.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "prs:org:SharpAstro" }).Node;
        header.Background.ShouldNotBeNull();
        // Day: body text on the org's blue at 4.5:1 or more, on its hover too.
        Contrast(Palette.Standard.Body, header.Background!.Value).ShouldBeGreaterThanOrEqualTo(4.5);
        Contrast(Palette.Standard.Body, Palette.Standard.ButtonHover).ShouldBeGreaterThanOrEqualTo(4.5);

        // Night: no darker than the red the rows' ink is read on, never the button red that takes it under 3:1.
        chrome.Night.On = true;
        page.Night = chrome.Night;
        var night = Arrange(renderer, page, Screen());
        var dark = night.Single(a => a.Node.Hit is HitResult.ButtonHit { Action: "prs:org:SharpAstro" }).Node.Background!.Value;
        dark.ShouldBe(Palette.Night.HeaderBg);
        Contrast(Palette.Night.Bright, dark).ShouldBeGreaterThanOrEqualTo(3.0);
    }

    [Fact]
    public void TheKeyboardSkipsTheRowsOfAFoldedOrgAndSelectingAFoldedRepoShowsItsOrgToo()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (chrome, page) = Chrome(renderer);
        chrome.Show(PilotPage.PullRequests);
        var screen = Screen();
        page.ToggleOrg("SharpAstro");
        Arrange(renderer, page, screen);
        page.Rows.Select(r => r.Number).ShouldBe([12]);
        page.Selected.ShouldBe(0);

        // Down from the only row listed stays on it: no row of the folded org is ever selected.
        page.HandleInput(new InputEvent.KeyDown(InputKey.Down, InputModifier.None));
        Arrange(renderer, page, screen);
        page.SelectedRow.ShouldNotBeNull().Number.ShouldBe(12);

        // A jump to a PR of the folded org (a card's link) opens the org and its repo.
        page.Toggle("SharpAstro/pilot");
        page.Reveal("SharpAstro/pilot", 261);
        Arrange(renderer, page, screen);
        page.IsOrgFolded("SharpAstro").ShouldBeFalse();
        page.IsFolded("SharpAstro/pilot").ShouldBeFalse();
        page.SelectedRow.ShouldNotBeNull().Number.ShouldBe(261);

        // ShowRepo does the same for a repo of a folded org.
        page.ToggleOrg("SharpAstro");
        page.ShowRepo("SharpAstro/tianwen");
        page.IsOrgFolded("SharpAstro").ShouldBeFalse();
        page.Filter.ShouldBeNull();
        Arrange(renderer, page, screen);
        Numbers(page).ShouldBe([1799, 1820]);
    }

    [Fact]
    public void AnOrgWithNoPRSaysSoAndAnOrgThatCouldNotBeReadShowsItsErrorWholeOnHoverAndTheRateLimitNoteIsAboveTheList()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var screen = Screen(RowWithEmptyAndFailedOrgs());
        var arranged = Arrange(renderer, page, screen);

        // Orgs with PRs first, then the empty and the failed one by name.
        OrgHeaders(arranged).ShouldBe(["sebgod", "SharpAstro", "broken-org", "empty-org"]);
        Numbers(page).ShouldBe([12, 300, 301, 252, 261, 258, 1799, 1820]);
        var text = Dump(arranged);
        text.ShouldContain("No open pull requests.");
        text.ShouldContain("0 open");
        text.ShouldContain($"Could not read broken-org: {BrokenMessage}");
        // Said once: the error is in the org's group, not also above the list.
        text.Split("GitHub answered 502").Length.ShouldBe(2);

        // The error is cut to the room it has and carries its whole text on hover.
        arranged.Select(a => a.Node.Tooltip).ShouldContain($"broken-org could not be read, so its PRs are the ones read before, or missing: {BrokenMessage}");
        arranged.Select(a => a.Node.Tooltip).ShouldContain("empty-org has no open pull request in the repos pilot works in");

        // Each is a group of its own under its header, as tall as its note says.
        var empty = Button(arranged, "prs:org:empty-org");
        var broken = Button(arranged, "prs:org:broken-org");
        broken.Y.ShouldBeLessThan(empty.Y);
        empty.Height.ShouldBe(PullRequestsView<RgbaImage>.OrgHeight);
        (empty.Y - (broken.Y + broken.Height)).ShouldBe(PullRequestsView<RgbaImage>.OrgNoteHeight);

        // The rate-limit note, above the list.
        text.ShouldContain("Rate-limited by GitHub until 14:05; reading again then");
        var note = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.StartsWith("Rate-limited", StringComparison.Ordinal)).Bounds;
        note.Y.ShouldBeLessThan(Button(arranged, "prs:org:SharpAstro").Y);

        // A pill or a search that leaves an org no PR does not say it has none; one whose read failed keeps its error.
        page.Filter = PullRequestFilter.Flights;
        arranged = Arrange(renderer, page, screen);
        OrgHeaders(arranged).ShouldBe(["SharpAstro", "broken-org"]);
        Dump(arranged).ShouldNotContain("No open pull requests.");
        Dump(arranged).ShouldContain("Could not read broken-org");

        // A folded org hides its note with the rest of its group.
        page.Filter = null;
        page.ToggleOrg("broken-org");
        arranged = Arrange(renderer, page, screen);
        Dump(arranged).ShouldNotContain("Could not read broken-org");
        Dump(arranged).ShouldContain("No open pull requests.");
    }

    [Fact]
    public void AnOrgThatHasNoPRAtAllIsStillShownWhenThePageIsOtherwiseEmpty()
    {
        using var renderer = new RgbaImageRenderer(1280, 900);
        var (_, page) = Chrome(renderer);
        var arranged = Arrange(renderer, page, Screen(Row() with { PullRequests = [] }));

        OrgHeaders(arranged).ShouldBe(["sebgod", "SharpAstro"]);
        Dump(arranged).Split("No open pull requests.").Length.ShouldBe(3);
        page.Rows.ShouldBeEmpty();

        // With no org to say it of, the page is the one line.
        arranged = Arrange(renderer, page, Screen(Row() with { PullRequests = [], Orgs = [] }));
        OrgHeaders(arranged).ShouldBeEmpty();
        Dump(arranged).ShouldContain("No open pull requests in any repo pilot works in.");
    }

    [Fact]
    public void ThePageArrangesInTerminalCellsToo()
    {
        using var renderer = new RgbaImageRenderer(1, 1);
        var (_, page) = Chrome(renderer);
        var text = CellLayout.Describe(Layout.Engine.Arrange(page.Tree(Screen(), 200 * 8f), new Rect<int>(0, 0, 200, 60), CellMeasureContext.PixelAuthored));

        text.ShouldContain("pull requests");
        text.ShouldContain("SharpAstro/pilot");
        text.ShouldContain("Checks running 4/7");
        text.ShouldContain("#252 Pull requests page: every open PR across the orgs");
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        condition().ShouldBeTrue();
    }

    private static void ClickTitle(RgbaImageRenderer renderer, PullRequestsView<RgbaImage> page, PullRequestsScreen screen, string title) =>
        ClickButton(Arrange(renderer, page, screen, 1600f), "pull requests:table:" + title);

    [Fact]
    public void AClickOnATitleSortsThePRsWithinEachRepoAndTheGroupsKeepTheirOrder()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var (chrome, page) = Chrome(renderer);
        chrome.Show(PilotPage.PullRequests);
        var screen = Screen();
        Arrange(renderer, page, screen, 1600f);
        var groups = page.Rows.Select(r => r.Repo).Distinct().ToList();
        string[] Repo(string repo) => [.. page.Rows.Where(r => r.Repo == repo).Select(r => r.Url[(r.Url.LastIndexOf('/') + 1)..])];
        Repo("SharpAstro/pilot").ShouldBe(["252", "261", "258"]);

        ClickTitle(renderer, page, screen, "Pull request");
        Arrange(renderer, page, screen, 1600f);
        Repo("SharpAstro/pilot").ShouldBe(["252", "258", "261"]);
        ClickTitle(renderer, page, screen, "Pull request");
        Arrange(renderer, page, screen, 1600f);
        Repo("SharpAstro/pilot").ShouldBe(["261", "258", "252"]);
        Repo("SharpAstro/DIR.Lib").ShouldBe(["301", "300"]);

        // The repos' groups are where they were, each repo's rows still together.
        page.Rows.Select(r => r.Repo).Distinct().ShouldBe(groups);
        page.Rows.Select(r => r.Repo).Where((repo, i) => i == 0 || repo != page.Rows[i - 1].Repo).Count().ShouldBe(groups.Count);

        // The age sorts by time, the newest first on a first click: #252 (5 minutes) leads its group.
        ClickTitle(renderer, page, screen, "Updated");
        Arrange(renderer, page, screen, 1600f);
        Repo("SharpAstro/pilot")[0].ShouldBe("252");
        page.Table.Sort.ShouldBe(new ListTable.SortState("Updated", Descending: true));
    }

    [Fact]
    public void TheSelectedPRStaysSelectedThroughASort()
    {
        using var renderer = new RgbaImageRenderer(1600, 900);
        var (chrome, page) = Chrome(renderer);
        chrome.Show(PilotPage.PullRequests);
        var screen = Screen();
        Arrange(renderer, page, screen, 1600f);
        page.Select(page.Rows.ToList().FindIndex(r => r.Number == 258));
        var before = page.Selected;

        ClickTitle(renderer, page, screen, "Pull request");
        Arrange(renderer, page, screen, 1600f);

        page.SelectedRow!.Number.ShouldBe(258);
        page.SelectedUrl.ShouldBe("https://github.com/SharpAstro/pilot/pull/258");
        page.Selected.ShouldNotBe(before);
    }
}
