using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The one top bar (#231): every page, Home included, shows <b>pilot v2</b> and its name, the node pills, Autopilot, the update
/// light and ?, in one height on one baseline and nothing clipped at 1280 px; the page supplies only its counts and its own
/// buttons, which stand in the middle of the bar.
/// </summary>
public sealed class TopBarTests
{
    private const float Width = 1280f;

    private static readonly NodeMark Here = new("a", "a", "SE1", true);
    private static readonly NodeMark Other = new("b", "b", "LE1", false);

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Nodes.Choices = [Here, Other];
        chrome.Autopilot.OnToggle = _ => { };
        chrome.About.Newer = () => "2.0.9-beta.1";
        chrome.About.Restart = () => { };
        return chrome;
    }

    private static FlightsScreen Flights(DateTimeOffset now) => new(FlightLine.Build([new FlightRow
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = "t", Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = now,
    }], [], now), "test", now);

    /// <summary>Every page's tree, as the window paints it.</summary>
    private static IEnumerable<(string Page, Layout.Node Tree)> Pages(PilotChrome<RgbaImage> chrome)
    {
        var now = Live.At;
        var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), now)]), "test", now);
        var fleet = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, new NodeSummary { Connected = true, Status = "live", Stream = "s", LiveSessions = 1 })], now);
        yield return ("home", chrome.Home.Tree(new HomeScreen(Flights(now), sessions, new JobsScreen([], "test", now), CardsScreen.Empty)));
        yield return ("sessions", chrome.Sessions.Tree(sessions));
        yield return ("flights", chrome.Flights.Build(Flights(now)));
        yield return ("background tasks", chrome.Jobs.Tree(new JobsScreen([], "test", now)));
        yield return ("fleet", chrome.Fleet.Tree(fleet));
        yield return ("repos", chrome.Repos.Tree(new ReposScreen([], "test", now)));
        yield return ("pull requests", chrome.PullRequests.Tree(new PullRequestsScreen(null, "test", now)));
        yield return ("settings", chrome.Settings.Tree(now));
        yield return ("chat", chrome.Chat.Tree());
    }

    private static ImmutableArray<Layout.ArrangedNode<float>> Arrange(RgbaImageRenderer renderer, Layout.Node tree) =>
        Layout.Engine.Arrange(tree, new Rect<float>(0, 0, Width, 800), new PixelMeasureContext<RgbaImage>(renderer, SessionsCapture.FontPath, 1f));

    private static Rect<float> Button(ImmutableArray<Layout.ArrangedNode<float>> arranged, string action)
    {
        var found = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action).ToList();
        found.Count.ShouldBe(1, action);
        return found[0].Bounds;
    }

    private static Rect<float> Text(ImmutableArray<Layout.ArrangedNode<float>> arranged, string value)
    {
        var found = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == value).ToList();
        found.Count.ShouldBe(1, value);
        return found[0].Bounds;
    }

    /// <summary>The same bar on every page: its name, the node pills, Autopilot, the update light and ? present, one height, one baseline.</summary>
    [Fact]
    public void EveryPageShowsTheSameBar()
    {
        using var renderer = new RgbaImageRenderer((int)Width, 800);
        var chrome = NewChrome(renderer);

        Rect<float>? brand = null;
        Rect<float>? help = null;
        foreach (var (page, tree) in Pages(chrome))
        {
            var arranged = Arrange(renderer, tree);

            // The brand and the page's name share a baseline, and the brand stands in the same place on every page.
            var pilot = Text(arranged, "pilot v2");
            // The Chat page is the fleet chat, and shows its nodes as one choice rather than the window's filter.
            var chatPage = page == "chat";
            // The Settings page names whose node it is, and offers no node pills: its settings are this machine's only.
            var settingsPage = page == "settings";
            var name = Text(arranged, chatPage ? chrome.Chat.Title : settingsPage ? $"{chrome.Settings.Owner}'s settings (this machine)" : page);
            (name.Y + name.Height).ShouldBe(pilot.Y + pilot.Height, page);
            brand ??= pilot;
            (pilot.X, pilot.Y, pilot.Width, pilot.Height).ShouldBe((brand.Value.X, brand.Value.Y, brand.Value.Width, brand.Value.Height), page);

            // The buttons every page carries, as tall as the ? and on its row.
            var question = Button(arranged, "about:toggle");
            help ??= question;
            (question.X, question.Y, question.Width, question.Height).ShouldBe((help.Value.X, help.Value.Y, help.Value.Width, help.Value.Height), page);
            foreach (var action in chatPage
                ? new[] { "chat:node:a", "chat:node:b", AutopilotSwitch.Action, "about:restart" }
                : settingsPage
                    ? new[] { AutopilotSwitch.Action, "about:restart" }
                    : new[] { "nodes:all", "nodes:a", "nodes:b", AutopilotSwitch.Action, "about:restart" })
            {
                var bounds = arranged.Where(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action).Select(a => a.Bounds).ToList();
                bounds.Count.ShouldBe(1, $"{page}: {action}");
                (bounds[0].Y, bounds[0].Height).ShouldBe((question.Y, question.Height), $"{page}: {action}");
            }

            // Autopilot, then the update light, then ? at the right edge, in that order.
            var autopilot = Button(arranged, AutopilotSwitch.Action);
            var light = Button(arranged, "about:restart");
            autopilot.X.ShouldBeLessThan(light.X, page);
            light.X.ShouldBeLessThan(question.X, page);
            (question.X + question.Width).ShouldBeLessThanOrEqualTo(Width, page);

            // The node pills stand left of the page's own things, after its name.
            if (settingsPage)
            {
                arranged.Any(a => a.Node.Hit is HitResult.ButtonHit b && b.Action.StartsWith("nodes:", StringComparison.Ordinal)).ShouldBeFalse(page);
                continue;
            }
            Button(arranged, chatPage ? "chat:node:a" : "nodes:all").X.ShouldBeGreaterThan(name.X + name.Width, page);
            Button(arranged, chatPage ? "chat:node:b" : "nodes:b").X.ShouldBeLessThan(autopilot.X, page);
        }
    }

    /// <summary>
    /// The ? card hangs under the bar's row: it covers neither the ? nor anything beside it, so the ? itself closes it again
    /// (it once covered the ?, whose click landed on the card's × instead).
    /// </summary>
    [Fact]
    public void TheHelpCardHangsUnderTheBarAndLeavesTheQuestionMarkClear()
    {
        using var renderer = new RgbaImageRenderer((int)Width, 800);
        var chrome = NewChrome(renderer);
        chrome.Repos.SyncRepos = _ => Task.FromResult<RepoSyncOutcome[]>([]);
        chrome.About.Toggle();

        foreach (var (page, tree) in Pages(chrome))
        {
            var arranged = Arrange(renderer, tree);
            var question = Button(arranged, "about:toggle");
            var close = Button(arranged, "about:close");
            var title = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text { Value: "pilot" } }).Select(a => a.Bounds).Single();
            title.Y.ShouldBeGreaterThanOrEqualTo(question.Y + question.Height, page);
            close.Y.ShouldBeGreaterThanOrEqualTo(question.Y + question.Height, page);
            // Nothing painted after the ? (the card is) lies over its middle; the clear spacer above the card paints nothing.
            var at = arranged.ToList().FindIndex(a => a.Node.Hit is HitResult.ButtonHit { Action: "about:toggle" });
            var (x, y) = (question.X + question.Width / 2f, question.Y + question.Height / 2f);
            arranged.Skip(at + 1).Where(a => a.Node is Layout.Node.Leaf leaf && a.Bounds.Width > 0f
                    && (leaf.Hit is not null || leaf.Background is not null || leaf.Content is not Layout.Content.Box { Color.Alpha: 0 })
                    && x >= a.Bounds.X && x <= a.Bounds.X + a.Bounds.Width && y >= a.Bounds.Y && y <= a.Bounds.Y + a.Bounds.Height)
                .ShouldBeEmpty(page);
        }
    }

    /// <summary>Nothing in the bar's rows is cut off at 1280 px wide: every box in them lies inside the window.</summary>
    [Fact]
    public void NothingInTheBarIsClippedAt1280()
    {
        using var renderer = new RgbaImageRenderer((int)Width, 800);
        var chrome = NewChrome(renderer);
        chrome.Repos.SyncRepos = _ => Task.FromResult<RepoSyncOutcome[]>([]);

        foreach (var (page, tree) in Pages(chrome))
        {
            var arranged = Arrange(renderer, tree);
            var help = Button(arranged, "about:toggle");
            var bottom = help.Y + help.Height;
            var bar = arranged.Where(a => a.Node is Layout.Node.Leaf && a.Bounds.Y + a.Bounds.Height <= bottom + 1f && a.Bounds.Height > 0f).ToList();
            // Settings offers no node pills, so its bar holds fewer boxes.
            bar.Count.ShouldBeGreaterThanOrEqualTo(page == "settings" ? 5 : 8, page);
            foreach (var item in bar)
            {
                item.Bounds.X.ShouldBeGreaterThanOrEqualTo(0f, page);
                (item.Bounds.X + item.Bounds.Width).ShouldBeLessThanOrEqualTo(Width, page);
                item.Bounds.Height.ShouldBeLessThanOrEqualTo(TopBar.RowHeight, page);
            }

            // Whatever the page puts in the bar's row is on its one row: the pills' middle is the ?'s middle.
            foreach (var pill in arranged.Where(a => a.Node.Hit is HitResult.ButtonHit { Action: var action } && (action.StartsWith("nodes:", StringComparison.Ordinal) || action.StartsWith("play:", StringComparison.Ordinal))))
            {
                (pill.Bounds.Y + pill.Bounds.Height / 2f).ShouldBe(help.Y + help.Height / 2f, 0.6f, page);
            }
        }
    }

    /// <summary>The page's own buttons stand in the middle of the bar: after its counts, before Autopilot, as tall as the ?.</summary>
    [Fact]
    public void ThePagesButtonsStandInItsSlot()
    {
        using var renderer = new RgbaImageRenderer((int)Width, 800);
        var chrome = NewChrome(renderer);
        chrome.Repos.SyncRepos = _ => Task.FromResult<RepoSyncOutcome[]>([]);
        chrome.Repos.Tracker = new BackgroundTaskTracker();

        var arranged = Arrange(renderer, chrome.Repos.Tree(new ReposScreen([], "test", Live.At)));
        var question = Button(arranged, "about:toggle");
        var sync = Button(arranged, "repos:sync");
        var all = Button(arranged, "repos:all");
        var autopilot = Button(arranged, AutopilotSwitch.Action);

        foreach (var button in new[] { sync, all })
        {
            (button.Y, button.Height).ShouldBe((question.Y, question.Height));
            button.X.ShouldBeGreaterThan(Button(arranged, "nodes:b").X);
            (button.X + button.Width).ShouldBeLessThan(autopilot.X);
        }
        sync.X.ShouldBeLessThan(all.X);
    }

    /// <summary>The In play pills sit beside the node pills on the pages that use them, and on no other.</summary>
    [Fact]
    public void InPlayPillsAreOnlyOnThePagesThatUseThem()
    {
        using var renderer = new RgbaImageRenderer((int)Width, 800);
        var chrome = NewChrome(renderer);

        foreach (var (page, tree) in Pages(chrome))
        {
            var has = Arrange(renderer, tree).Any(a => a.Node.Hit is HitResult.ButtonHit { Action: "play:in-play" });
            has.ShouldBe(page is "sessions" or "flights" or "background tasks", page);
        }
    }

    /// <summary>Home's node pills filter its content: a click on a node leaves only that node's things, All nodes brings the rest back.</summary>
    [Fact]
    public void HomesNodePillsFilterItsContent()
    {
        using var renderer = new RgbaImageRenderer((int)Width, 800);
        var chrome = NewChrome(renderer);
        var now = Live.At;
        var flights = new FlightsScreen([.. Flights(now).Lines.Select(l => l with { Node = Here }), .. Flights(now).Lines.Select(l => l with { Code = "ORB9", Node = Other })], "test", now);
        var sessions = new SessionsScreen(SessionLine.Order([
            SessionLine.From(Live.Row("aaaa0000", "worker-here", SessionState.Working), now) with { Node = Here },
            SessionLine.From(Live.Row("bbbb0000", "worker-there", SessionState.Working), now) with { Node = Other }]), "test", now);
        var screen = new HomeScreen(flights, sessions, new JobsScreen([], "test", now), CardsScreen.Empty);

        string Dump() => CellLayout.Describe(Layout.Engine.Arrange(chrome.Home.Tree(screen), new Rect<int>(0, 0, 260, 60), CellMeasureContext.PixelAuthored));
        void Click(string action)
        {
            var arranged = Layout.Engine.Arrange(chrome.Home.Tree(screen), new Rect<int>(0, 0, 260, 60), CellMeasureContext.PixelAuthored);
            var pill = arranged.First(a => a.Node.Hit is HitResult.ButtonHit b && b.Action == action);
            CellLayout.HitTest(arranged, pill.Bounds.X + 1, pill.Bounds.Y);
        }

        Dump().ShouldContain("SE1 · TIA1");
        Dump().ShouldContain("LE1 · ORB9");
        Dump().ShouldContain("LE1 · worker-there");

        Click("nodes:b");
        var only = Dump();
        only.ShouldContain("LE1 · ORB9");
        only.ShouldContain("LE1 · worker-there");
        only.ShouldNotContain("SE1 · TIA1");
        only.ShouldNotContain("SE1 · worker-here");

        Click("nodes:all");
        Dump().ShouldContain("SE1 · TIA1");
        Dump().ShouldContain("LE1 · ORB9");
    }
}
