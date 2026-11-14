using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Layout = DIR.Lib.Layout;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// pilot-tui's page headers (#1044): every button and chip of the header takes exactly one row with its label on it, on every page
/// and at the terminal's real sizes, with the host's terminal mode on (the trees are the GUI's, arranged at 8 design units a column
/// and 16 a row, where the GUI's 28 high button is two rows).
/// </summary>
public sealed class TerminalHeaderTests
{
    // The tests turn the mode on through this; false shows what the base did.
    private static bool TerminalMode => true;

    private static readonly NodeMark Here = new("a", "a", "SE1", true);

    private static PilotChrome<RgbaImage> NewChrome(RgbaImageRenderer renderer)
    {
        var chrome = new PilotChrome<RgbaImage>(renderer, new SessionsView<RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Nodes.Choices = [Here, new("b", "b", "LE1", false), new("c", "c", "SE2", false), new("d", "d", "DE1", false)];
        chrome.Autopilot.OnToggle = _ => Task.FromResult<string?>(null);
        chrome.About.Newer = () => "2.0.9-beta.1";
        chrome.About.Restart = () => { };
        return chrome;
    }

    private static FlightsScreen Flights(DateTimeOffset now) => new(FlightLine.Build([new FlightRow
    {
        Code = "TIA1", Repo = "tianwen", RepoPath = "/r/tianwen", Issue = 1, Title = "t", Branch = "flight/tia1", Worktree = "/r/tianwen/.claude/worktrees/tia1",
        Base = "origin/main", State = FlightState.Flying, CreatedAt = now,
    }], [], now), "test", now);

    private static IEnumerable<(string Page, PilotPage Id, Layout.Node Tree)> Pages(PilotChrome<RgbaImage> chrome, int columns)
    {
        var now = Live.At;
        var sessions = new SessionsScreen(SessionLine.Order([SessionLine.From(Live.Row("aaaa0000", "worker", SessionState.Working), now)]), "test", now);
        yield return ("flights", PilotPage.Flights, chrome.Flights.Build(Flights(now)));
        yield return ("sessions", PilotPage.Sessions, chrome.Sessions.Tree(sessions));
        yield return ("home", PilotPage.Home, chrome.Home.Tree(new HomeScreen(Flights(now), sessions, new JobsScreen([], "test", now), CardsScreen.Empty, [])));
        yield return ("settings", PilotPage.Settings, chrome.Settings.Tree(now, columns * 8f));
        yield return ("repos", PilotPage.Repos, chrome.Repos.Tree(new ReposScreen([], "test", now)));
        yield return ("pull requests", PilotPage.PullRequests, chrome.PullRequests.Tree(new PullRequestsScreen(null, "test", now), columns * 8f));
        yield return ("chat", PilotPage.Chat, chrome.Chat.Tree());
    }

    [Theory]
    [InlineData(160, 44)]
    [InlineData(250, 50)]
    public void EveryHeaderButtonIsOneRowHighWithItsLabelOnThatRow(int columns, int rows)
    {
        using var terminal = HeaderMetrics.Use(TerminalMode);
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = NewChrome(renderer);
        chrome.Chat.Cells = true;
        chrome.Chat.CellColumns = TerminalFrame.PageColumns(columns, false);
        var bad = new List<string>();
        foreach (var (name, id, page) in Pages(chrome, columns))
        {
            var rail = TerminalRail.Tree(id, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
            var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
            var header = arranged.FirstOrDefault(a => a.Bounds.Y == 1 && a.Node.Background == Palette.Standard.HeaderBg && a.Bounds.Width > columns / 2);
            if (header.Node is null)
            {
                bad.Add($"{name}: no header found");
                continue;
            }
            var from = arranged.IndexOf(header);
            var buttons = 0;
            for (var i = from + 1; i < arranged.Length && arranged[i].Depth > header.Depth; i++)
            {
                var button = arranged[i];
                if (button.Node.Hit is not HitResult.ButtonHit hit || button.Node.Background is null)
                {
                    continue;
                }
                buttons++;
                if (button.Bounds.Height != 1 || button.Bounds.Y != 1)
                {
                    bad.Add($"{name} {columns}x{rows}: {hit.Action} is rows {button.Bounds.Y}..{button.Bounds.Y + button.Bounds.Height - 1} ({button.Bounds.Height} high)");
                    continue;
                }
                for (var j = i + 1; j < arranged.Length && arranged[j].Depth > button.Depth; j++)
                {
                    if (arranged[j].Node is Layout.Node.Leaf { Content: Layout.Content.Text } leaf && arranged[j].Bounds.Y != button.Bounds.Y)
                    {
                        bad.Add($"{name} {columns}x{rows}: the label of {hit.Action} is on row {arranged[j].Bounds.Y}, its button on {button.Bounds.Y}");
                    }
                }
            }
            buttons.ShouldBeGreaterThan(0, $"{name}: no header button found");
        }
        bad.ShouldBeEmpty(string.Join('\n', bad));
    }

    // The Flights header's buttons by row, at one terminal size.
    private static (SortedSet<int> Rows, int Right, int MaxRight) FlightsHeaderRows(int columns, int rows)
    {
        using var terminal = HeaderMetrics.Use(TerminalMode);
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = NewChrome(renderer);
        var page = chrome.Flights.Build(Flights(Live.At));
        var rail = TerminalRail.Tree(PilotPage.Flights, GlyphMode.Emoji, null, null, Live.At, _ => { }, null, columns);
        var arranged = Layout.Engine.Arrange(TerminalFrame.Tree(rail, page, null), new Rect<int>(0, 0, columns, rows), CellMeasureContext.PixelAuthored);
        var header = arranged.First(a => a.Bounds.Y == 1 && a.Node.Background == Palette.Standard.HeaderBg && a.Bounds.Width > columns / 2);
        var from = arranged.IndexOf(header);
        var set = new SortedSet<int>();
        var max = 0;
        for (var i = from + 1; i < arranged.Length && arranged[i].Depth > header.Depth; i++)
        {
            if (arranged[i].Node.Hit is HitResult.ButtonHit && arranged[i].Node.Background is not null)
            {
                set.Add(arranged[i].Bounds.Y);
                max = Math.Max(max, arranged[i].Bounds.X + arranged[i].Bounds.Width);
            }
        }
        return (set, header.Bounds.X + header.Bounds.Width, max);
    }

    [Fact]
    public void TheFlightsHeaderIsOneLineAt250Columns()
    {
        var (rows, _, _) = FlightsHeaderRows(250, 50);
        rows.Count.ShouldBe(1, $"the header's buttons stand on rows {string.Join(", ", rows)}");
    }

    [Fact]
    public void TheFlightsHeaderWrapsAt100ColumnsWithNothingPastThePage()
    {
        var (rows, right, maxRight) = FlightsHeaderRows(100, 30);
        rows.Count.ShouldBeGreaterThan(1);
        maxRight.ShouldBeLessThanOrEqualTo(right);
    }

    [Fact]
    public void ThePullRequestsSearchBoxIsBorderedByRoundedBoxCharactersRoundOneInputRow()
    {
        using var terminal = HeaderMetrics.Use(TerminalMode);
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = NewChrome(renderer);
        var pr = new PullRequestRow
        {
            Repo = "SharpAstro/pilot", Number = 1, Title = "t", Url = "https://github.com/SharpAstro/pilot/pull/1", Author = "a", Kind = PullRequestKind.Ready, Label = "Ready",
            UpdatedAt = Live.At, CreatedAt = Live.At, Head = "feature/x", Base = "main",
        };
        var row = new PullRequestsRow { Orgs = ["SharpAstro"], ReadAt = Live.At, Viewer = "a", PullRequests = [pr] };
        var page = chrome.PullRequests.Tree(new PullRequestsScreen(row, "test", Live.At), 200 * 8f);
        var arranged = Layout.Engine.Arrange(page, new Rect<int>(0, 0, 200, 40), CellMeasureContext.PixelAuthored);
        var input = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput }).Bounds;
        input.Height.ShouldBe(1);
        string TextAt(int y, char glyph) => arranged
            .Where(a => a.Bounds.Y == y && a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.Contains(glyph))
            .Select(a => ((Layout.Content.Text)((Layout.Node.Leaf)a.Node).Content).Value).FirstOrDefault() ?? "";
        TextAt(input.Y - 1, '╭').ShouldContain("╮");
        TextAt(input.Y + 1, '╰').ShouldContain("╯");
        // The toolbar is its content tall: the table's header stands on the row right under the box's bottom edge.
        var header = arranged.Where(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value == "Pull request").ToList();
        header.Count.ShouldBe(1);
        header[0].Bounds.Y.ShouldBe(input.Y + 2);
    }

    [Fact]
    public void ThePullRequestsSearchBoxFollowsANarrowPage()
    {
        using var terminal = HeaderMetrics.Use(TerminalMode);
        using var renderer = new RgbaImageRenderer(1, 1);
        var chrome = NewChrome(renderer);
        var pr = new PullRequestRow
        {
            Repo = "SharpAstro/pilot", Number = 1, Title = "t", Url = "https://github.com/SharpAstro/pilot/pull/1", Author = "a", Kind = PullRequestKind.Ready, Label = "Ready",
            UpdatedAt = Live.At, CreatedAt = Live.At, Head = "feature/x", Base = "main",
        };
        var row = new PullRequestsRow { Orgs = ["SharpAstro"], ReadAt = Live.At, Viewer = "a", PullRequests = [pr] };
        var page = chrome.PullRequests.Tree(new PullRequestsScreen(row, "test", Live.At), 50 * 8f);
        var arranged = Layout.Engine.Arrange(page, new Rect<int>(0, 0, 50, 40), CellMeasureContext.PixelAuthored);
        var input = arranged.Single(a => a.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput }).Bounds;
        (input.X + input.Width).ShouldBeLessThanOrEqualTo(50);
        var top = arranged.Single(a => a.Bounds.Y == input.Y - 1 && a.Node is Layout.Node.Leaf { Content: Layout.Content.Text t } && t.Value.StartsWith('╭'));
        (top.Bounds.X + top.Bounds.Width).ShouldBeLessThanOrEqualTo(50);
        top.Bounds.Height.ShouldBe(1);
    }
}
