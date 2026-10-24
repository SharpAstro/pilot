using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// An icon says it better than a word, but only to a reader who knows the mark: so every button that is only a drawn mark carries
/// the word as its tooltip, and the hand cursor every button has. Each page is rendered as the layout tests do, and each button
/// found in the arranged tree whose content is an icon and no text is held to both.
/// </summary>
public sealed class IconButtonTests
{
    private static readonly DateTimeOffset Now = Live.At.AddHours(2);

    private static FlightRow Flight() => new()
    {
        Code = "TIA1611", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1611, Title = "Planets on the GPU", Branch = "flight/tia1611",
        Worktree = "/r/tianwen/.claude/worktrees/tia1611", Base = "origin/main", State = FlightState.Flying, CreatedAt = Live.At,
    };

    private static LegRow[] Legs() =>
    [
        new() { Flight = "TIA1611", Name = "render", Order = 1, Goal = "Draw the planets.", DoneWhen = "The capture shows all eight.", State = LegState.Flying },
    ];

    private static NodeSummary Summary(string stream) => new()
    {
        Connected = true, Status = "live", Stream = stream, LiveSessions = 1,
        Node = new NodeInfo { WireVersion = 1, Version = "2.0.9", MachineName = "DESKTOP-7", MachinePrefix = "SE2", ProcessId = 1, StartedAt = Live.At, DataFolder = "/d", SocketPath = "/s", Stream = stream },
    };

    /// <summary>Every button of the tree that shows an icon and no text: its action, and what it carries.</summary>
    private static List<(string Action, string? Tooltip, CursorKind? Cursor)> IconOnlyButtons(Layout.Node tree)
    {
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 240, 80), CellMeasureContext.PixelAuthored);
        var found = new List<(string, string?, CursorKind?)>();
        for (var i = 0; i < arranged.Length; i++)
        {
            if (arranged[i].Node.Hit is not HitResult.ButtonHit button)
            {
                continue;
            }

            // The button's own content and what is arranged inside its rect, in the arranged order (parents before children).
            var bounds = arranged[i].Bounds;
            var inside = arranged.Skip(i).Where(a => a.Node is Layout.Node.Leaf && Contains(bounds, a.Bounds)).Select(a => ((Layout.Node.Leaf)a.Node).Content).ToList();
            var icon = inside.OfType<Layout.Content.Icon>().Any();
            var text = inside.OfType<Layout.Content.Text>().Any(t => t.Value.Trim().Length > 0);
            if (icon && !text)
            {
                found.Add((button.Action, arranged[i].Node.Tooltip, arranged[i].Node.Cursor));
            }
        }
        return found;
    }

    private static bool Contains(Rect<int> outer, Rect<int> inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    private static void ShouldBeToldAndPointedAt(string page, List<(string Action, string? Tooltip, CursorKind? Cursor)> buttons)
    {
        foreach (var (action, tooltip, cursor) in buttons)
        {
            tooltip.ShouldNotBeNullOrWhiteSpace($"{page}: the icon-only button {action} names no word");
            cursor.ShouldBe(CursorKind.Pointer, $"{page}: the icon-only button {action} has no hand cursor");
        }
    }

    [Fact]
    public void EveryIconOnlyButtonOnEveryPageHasATooltipAndTheHandCursor()
    {
        using var renderer = new RgbaImageRenderer(1280, 800);
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        var snapshot = SessionsCapture.LoadSnapshot(SessionsCapture.SamplePath);
        var sessions = SessionsCapture.ScreenFor(snapshot, "test");
        var jobs = new JobsScreen(JobLine.Build(snapshot.Jobs, snapshot.At), "test", snapshot.At);
        var flights = new FlightsScreen([FlightLine.Build([Flight()], [], Now, Legs()).Single()], "test", Now);
        var cards = CardsScreen.From([], []);
        var repos = new ReposScreen(RepoLine.Build(SessionsCapture.LoadRepos(SessionsCapture.ReposSamplePath), [], [], [], [], Now), "live", Now);
        var fleet = new FleetScreen([FleetLine.From("node.sock", here: true, canCommand: true, null, Summary("s1"))], Now);

        // The Flights header's New flight needs the wizard wired and a node that may be commanded.
        chrome.Flights.CanCommand = true;
        chrome.Flights.Wizard.LoadRepos = _ => Task.FromResult<RepoInfo[]>([]);

        var pages = new Dictionary<string, Layout.Node>
        {
            ["Home"] = chrome.Home.Tree(new HomeScreen(flights, sessions, jobs, cards)),
            ["Sessions"] = chrome.Sessions.Tree(sessions),
            ["Jobs"] = chrome.Jobs.Tree(jobs),
            ["Fleet"] = chrome.Fleet.Tree(fleet),
            ["Repos"] = chrome.Repos.Tree(repos),
            ["Settings"] = chrome.Settings.Tree(Now),
            ["Watchtower"] = chrome.Cards.Tree(cards),
        };
        foreach (var mode in Enum.GetValues<FlightsViewMode>())
        {
            chrome.Flights.Mode = mode;
            pages[$"Flights ({mode})"] = chrome.Flights.Tree(flights);
        }

        // The pages are not vacuous: the Flights header has its view switch and its New flight, and the Repos rows their folds.
        var all = new Dictionary<string, List<(string Action, string? Tooltip, CursorKind? Cursor)>>();
        foreach (var (page, tree) in pages)
        {
            var buttons = IconOnlyButtons(tree);
            all[page] = buttons;
            ShouldBeToldAndPointedAt(page, buttons);
        }

        all["Flights (Canvas)"].Select(b => b.Action).ShouldBe(["flights:view:grid", "flights:view:list", "flights:new"]);
        all["Flights (Canvas)"].Select(b => b.Tooltip).ShouldBe(["Grid", "List", "New flight"]);
        all["Repos"].ShouldContain(b => b.Action.EndsWith(":worktrees", StringComparison.Ordinal));
    }

    [Fact]
    public void AButtonWithoutATooltipIsFound()
    {
        // The test's own proof: a drawn mark that names no word is what it exists to catch.
        var bare = Layout.Builder.Icon(Layout.IconKind.Plus, 12f).WAuto().Clickable(new HitResult.ButtonHit("bare"), _ => { }, CursorKind.Pointer);
        var told = bare.WithTooltip("Add");

        IconOnlyButtons(bare).ShouldBe([("bare", null, CursorKind.Pointer)]);
        IconOnlyButtons(told).ShouldBe([("bare", "Add", CursorKind.Pointer)]);
        Should.Throw<Exception>(() => ShouldBeToldAndPointedAt("test", IconOnlyButtons(bare)));
    }
}
