using Microsoft.Extensions.Logging;
using Pilot.Contracts;
using Pilot.Gui;
using Pilot.Ui;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

public partial class FrameWatchTests
{
    private sealed class Capture : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Unchanged_build_asks_no_frame()
    {
        var watch = new FrameWatch(new Capture());

        watch.WorthAFrame(1, 7).ShouldBeTrue();
        watch.WorthAFrame(1, 7).ShouldBeFalse("the once-a-second look found the same text");
        watch.WorthAFrame(1, 7).ShouldBeFalse();
    }

    [Fact]
    public void Changed_build_asks_a_frame()
    {
        var watch = new FrameWatch(new Capture());
        watch.WorthAFrame(1, 7);

        watch.WorthAFrame(2, 7).ShouldBeTrue("a page switch asked for a build");
        watch.WorthAFrame(2, 8).ShouldBeTrue("the text changed");
        watch.Forget();
        watch.WorthAFrame(2, 8).ShouldBeTrue("a failed build is tried again");
    }

    private static long Text(DateTimeOffset at, DateTimeOffset now)
    {
        var row = new SessionRow
        {
            Id = "00000000", SessionId = "00000000-0000-0000-0000-000000000000", Name = "n", Kind = SessionKind.Background, Cwd = "/x",
            Role = SessionRole.Worker, ContextTokens = 0, ContextWindow = 0, State = SessionState.Idle, SpendWeighted = 0, Budget = 1,
            StartedAt = at, LastResponseAt = at,
        };
        var hash = new HashCode();
        hash.Add(SessionLine.From(row, now).ToDumpLine(), StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    [Fact]
    public void An_ago_that_is_not_on_the_minute_is_redrawn_when_it_turns_over()
    {
        // An event at :30 past the minute: "1m ago" shows from 12:01:30, "2m ago" from 12:02:30, not at :00.
        var at = T0.AddSeconds(30);
        var watch = new FrameWatch(new Capture());
        bool Frame(int secondsAfter) => watch.WorthAFrame(1, Text(at, at.AddSeconds(secondsAfter)));

        Frame(61).ShouldBeTrue();
        Frame(100).ShouldBeFalse("still 1m ago");
        Frame(119).ShouldBeFalse();
        Frame(120).ShouldBeTrue("2m ago at 12:02:30");
        Frame(150).ShouldBeFalse();
        Frame(180).ShouldBeTrue("3m ago at 12:03:30");
    }

    [Fact]
    public void Slow_frame_is_logged_once_with_page_and_summary()
    {
        var log = new Capture();
        var watch = new FrameWatch(log);

        watch.Frame(TimeSpan.FromMilliseconds(120), PilotPage.Flights, () => "3 sessions, 2 flights").ShouldBeTrue();

        var line = log.Lines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Warning);
        line.Message.ShouldContain("120");
        line.Message.ShouldContain("Flights");
        line.Message.ShouldContain("3 sessions, 2 flights");
    }

    [Fact]
    public void Fast_frame_is_silent()
    {
        var log = new Capture();
        var watch = new FrameWatch(log);

        watch.Frame(TimeSpan.FromMilliseconds(10), PilotPage.Flights, () => "x").ShouldBeFalse();

        log.Lines.ShouldBeEmpty();
    }

    private static FleetPages PagesWith(DateTimeOffset waitingSince, DateTimeOffset asOf)
    {
        var row = new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "push-it", Kind = SessionKind.Interactive, Cwd = "/r/pilot",
            Role = SessionRole.AdHoc, State = SessionState.Waiting, WaitingFor = "permission to use Bash", WaitingSince = waitingSince, StartedAt = waitingSince, ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0,
        };
        return new FleetPages(new SessionsScreen(SessionLine.Build([row], asOf), "test", asOf), new FlightsScreen([], "test", asOf), new JobsScreen([], "test", asOf), [], CardsScreen.From([]));
    }

    [Fact]
    public void A_waiting_banner_turning_over_from_1m_to_2m_asks_for_a_frame()
    {
        var watch = new FrameWatch(new Capture());
        var since = T0;
        var fleet = new FleetScreen([], T0);
        var at = (TimeSpan span) => FrameWatch.Fingerprint(PagesWith(since, T0 + span), fleet, T0 + span, PilotPage.Sessions);

        watch.WorthAFrame(0, at(TimeSpan.FromSeconds(61))).ShouldBeTrue();
        // The rest of that minute (and its seconds of ago) is the same banner: no frame.
        watch.WorthAFrame(0, at(TimeSpan.FromSeconds(61))).ShouldBeFalse();
        // The banner reads "since 2 m" now: a frame.
        watch.WorthAFrame(0, at(TimeSpan.FromSeconds(121))).ShouldBeTrue();
    }

    [Fact]
    public void A_last_response_turning_over_asks_a_frame_on_Sessions_but_not_on_Home()
    {
        var fleet = new FleetScreen([], T0);
        FleetPages Pages(DateTimeOffset now)
        {
            var row = new SessionRow
            {
                Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "push-it", Kind = SessionKind.Interactive, Cwd = "/r/pilot",
                Role = SessionRole.AdHoc, State = SessionState.Working, StartedAt = T0 - TimeSpan.FromHours(1), LastResponseAt = T0 - TimeSpan.FromSeconds(5), ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0,
            };
            return new FleetPages(new SessionsScreen(SessionLine.Build([row], now), "test", now), new FlightsScreen([], "test", now), new JobsScreen([], "test", now), [], CardsScreen.From([]));
        }
        long At(PilotPage page, int seconds) => FrameWatch.Fingerprint(Pages(T0 + TimeSpan.FromSeconds(seconds)), fleet, T0 + TimeSpan.FromSeconds(seconds), page);

        At(PilotPage.Home, 0).ShouldBe(At(PilotPage.Home, 1));
        At(PilotPage.Sessions, 0).ShouldNotBe(At(PilotPage.Sessions, 1));
    }

    private static FleetPages Busy(DateTimeOffset lastResponse, params CardRow[] cards)
    {
        var row = new SessionRow
        {
            Id = "a1b2c3d4", SessionId = "a1b2c3d4-0000-4000-8000-000000000000", Name = "push-it", Kind = SessionKind.Background, Cwd = "/r/pilot",
            Role = SessionRole.Worker, State = SessionState.Working, StartedAt = T0 - TimeSpan.FromHours(1), LastResponseAt = lastResponse, ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0,
        };
        return new FleetPages(new SessionsScreen(SessionLine.Build([row], T0), "test", T0), new FlightsScreen([], "test", T0), new JobsScreen([], "test", T0), [], CardsScreen.From(cards));
    }

    [Fact]
    public void A_node_event_that_changes_nothing_Home_draws_asks_no_frame_on_Home()
    {
        // A busy node: each response of a worker moves the feeds' version, and Home draws none of it (DE1, 2026-10-30: 588
        // sessions, Home redrawn 1.3 times a second while nothing on it changed).
        var watch = new FrameWatch(new Capture());
        var fleet = new FleetScreen([], T0);
        long Home(long version, FleetPages pages) => FrameWatch.Fingerprint(pages, fleet, T0, PilotPage.Home, version);
        var card = new CardRow { Id = "c1", Bin = CardBin.ForYou, Kind = "landed", Subject = "flight:PIL1", Title = "PIL1 landed", At = T0, State = CardState.Open };

        watch.WorthAFrame(0, Home(1, Busy(T0 - TimeSpan.FromSeconds(5)))).ShouldBeTrue();
        watch.WorthAFrame(0, Home(2, Busy(T0 - TimeSpan.FromSeconds(1)))).ShouldBeFalse("the version moved for a last response Home does not show");
        watch.WorthAFrame(0, Home(3, Busy(T0))).ShouldBeFalse();
        watch.WorthAFrame(0, Home(4, Busy(T0, card))).ShouldBeTrue("a card was raised, and the panel shows it");
        watch.WorthAFrame(0, Home(5, Busy(T0, card with { Detail = "the PR merged" }))).ShouldBeTrue("the card's words changed");

        // The Sessions page's fingerprint is not whole, so the version is part of it: the same event redraws it.
        var same = Busy(T0);
        FrameWatch.Fingerprint(same, fleet, T0, PilotPage.Sessions, 1).ShouldNotBe(FrameWatch.Fingerprint(same, fleet, T0, PilotPage.Sessions, 2));
    }

    [Fact]
    public void Home_s_last_pages_fingerprinted_later_move_only_when_a_clock_word_turns_over()
    {
        // The once-a-second look at Home fingerprints the last pages at the moment, without building them again.
        var fleet = new FleetScreen([], T0);
        var summary = new SummaryRow { Scope = SummaryRow.FleetScope, Markdown = "All calm.", MadeAt = T0 - TimeSpan.FromSeconds(200), Tokens = 10 };
        var pages = Busy(T0) with { Summaries = new SummaryShelf([], new Dictionary<(string Node, string Scope), SummaryRow> { [("", SummaryRow.FleetScope)] = summary }) };
        long At(int seconds) => FrameWatch.Fingerprint(pages, fleet, T0 + TimeSpan.FromSeconds(seconds), PilotPage.Home);

        At(1).ShouldBe(At(0), "the worker's last response is in the build, not counted again");
        At(30).ShouldBe(At(0), "made 3m ago still");
        At(40).ShouldNotBe(At(0), "made 4m ago from 240 s on");
    }

    [Fact]
    public void The_header_switches_and_node_pills_move_the_fingerprint()
    {
        var nodes = new NodeFilter();
        var off = FrameWatch.Header(false, false, null, nodes);

        FrameWatch.Header(false, false, null, nodes).ShouldBe(off);
        FrameWatch.Header(false, true, null, nodes).ShouldNotBe(off, "the autopilot switched on");
        FrameWatch.Header(true, false, null, nodes).ShouldNotBe(off, "the node went into maintenance");
        FrameWatch.Header(false, false, "2.4.1", nodes).ShouldNotBe(off, "the update light came on");
        nodes.Choices = [new NodeMark("here", "here", "DE1", true), new NodeMark("se1:7313", "se1:7313", "SE1", false)];
        var two = FrameWatch.Header(false, false, null, nodes);
        two.ShouldNotBe(off, "a second node's pill");
        nodes.Down = new Dictionary<string, DateTimeOffset?> { ["se1:7313"] = T0 };
        FrameWatch.Header(false, false, null, nodes).ShouldNotBe(two, "SE1 went down: its pill says when it was last heard");
    }

    [Fact]
    public void The_update_light_s_phase_moves_the_header_each_second_only_while_it_shows()
    {
        var nodes = new NodeFilter();
        var ms = 0L;
        var about = new AboutCard { Clock = () => ms };
        long At(long clock, string? shown)
        {
            ms = clock;
            return FrameWatch.Header(false, false, "2.4.1", nodes, FrameWatch.Light(about, shown));
        }

        At(0, "2.4.1").ShouldNotBe(At(1000, "2.4.1"), "lit, then unlit a second later");
        At(1000, "2.4.1").ShouldNotBe(At(2000, "2.4.1"));
        At(0, "2.4.1").ShouldBe(At(500, "2.4.1"), "the same phase within its second");
        At(0, null).ShouldBe(At(1000, null), "no light shows (a window run from a plane): nothing pulses, no frame");
    }

    [Fact]
    public void The_LAN_row_s_seconds_move_the_Flights_page_and_nothing_else()
    {
        var pages = Busy(T0);
        var fleet = new FleetScreen([], T0);
        var lan = LanWatch.Over(() => [], T0);
        long At(PilotPage page, int seconds) =>
            FrameWatch.Fingerprint(pages, fleet, T0 + TimeSpan.FromSeconds(seconds), page, lan: FrameWatch.LanLine(lan, T0 + TimeSpan.FromSeconds(seconds)));

        At(PilotPage.Flights, 3).ShouldNotBe(At(PilotPage.Flights, 4), "LAN: discovering (3 s), then (4 s)");
        At(PilotPage.Jobs, 3).ShouldBe(At(PilotPage.Jobs, 4));
        // Once a node is heard and no ask waits, the row is not drawn.
        FrameWatch.LanLine(LanWatch.Over(() => [new LanNode("10.0.0.9:7313", "SE1")], T0), T0 + TimeSpan.FromSeconds(3)).ShouldBeNull();
    }

    [Fact]
    public void The_Ideas_page_turns_over_each_second_as_Settings_does()
    {
        // An idea's "checked 12s ago" is counted from a readiness the Ideas view holds itself, which the pages do not carry.
        var pages = Busy(T0);
        var fleet = new FleetScreen([], T0);
        long At(PilotPage page, int seconds) => FrameWatch.Fingerprint(pages, fleet, T0 + TimeSpan.FromSeconds(seconds), page);

        At(PilotPage.Ideas, 0).ShouldNotBe(At(PilotPage.Ideas, 1));
        At(PilotPage.Settings, 0).ShouldNotBe(At(PilotPage.Settings, 1));
        At(PilotPage.Repos, 0).ShouldBe(At(PilotPage.Repos, 1), "a page with no clock text of its own stays as it was");
    }

    /// <summary>Every text the chrome draws, in order; nothing is painted.</summary>
    private sealed class DrawnText(uint width, uint height) : DIR.Lib.RgbaImageRenderer(width, height)
    {
        public List<string> Texts { get; } = [];

        public override void DrawText(ReadOnlySpan<char> text, string fontFamily, float fontSize, DIR.Lib.RGBAColor32 fontColor, in DIR.Lib.RectInt layout,
            DIR.Lib.TextAlign horizAlignment = DIR.Lib.TextAlign.Center, DIR.Lib.TextAlign vertAlignment = DIR.Lib.TextAlign.Near) => Texts.Add(text.ToString());
    }

    public static TheoryData<string> Pages() =>
    [
        .. Enum.GetValues<PilotPage>().Where(p => p != PilotPage.Cards).Select(p => p.ToString()),
        "flight page",
        "Flights with the LAN row",
    ];

    // The moments the same rows are drawn at: a second on; across the summaries' minute (made 200 s before PageData.Now, so their
    // "made 3m ago" turns at +40 s, where nothing else does); across the rows' minute (they sit on whole minutes of PageData.Now);
    // a minute and a second on; and an hour on.
    private static readonly int[] Moments = [0, 1, 39, 40, 41, 59, 60, 61, 119, 120, 3600];

    /// <summary>
    /// Builds publish only when the fingerprint moves, so every text a page draws that turns over with the clock alone (the
    /// rows unchanged, the feeds' version the same) has to move its page's fingerprint, or the window shows it frozen. Each page
    /// is drawn by the chrome, as the window draws it, from the same rows at moments a second, a minute and an hour apart; where
    /// what it draws differs, so must the fingerprint. The "as of" in a page's source line is left out: it says when the shown
    /// pages were built, and an idle page is not built again for it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pages))]
    public void Every_text_a_page_draws_from_the_clock_moves_its_fingerprint(string page)
    {
        var shown = page is "flight page" or "Flights with the LAN row" ? PilotPage.Flights : Enum.Parse<PilotPage>(page);
        // The LAN row turns over each second for its first ten, which would hide any other text's turn on the Flights page.
        var drawn = Moments.Select(s => Draw(page, shown, PageData.Now + TimeSpan.FromSeconds(s), lan: page == "Flights with the LAN row")).ToList();
        drawn.ShouldAllBe(d => d.Text.Length > 0, $"{page} draws no text");
        for (var i = 1; i < drawn.Count; i++)
        {
            if (drawn[i].Text != drawn[i - 1].Text)
            {
                drawn[i].Fingerprint.ShouldNotBe(drawn[i - 1].Fingerprint,
                    $"{page} from +{Moments[i - 1]} s to +{Moments[i]} s draws {Differ(drawn[i - 1].Text, drawn[i].Text)} with the same fingerprint");
            }
        }
    }

    [Fact]
    public void The_clock_alone_changes_what_some_pages_draw()
    {
        // The table above is not empty: a session's "3m ago", a running job's seconds, the LAN row's and a summary's "made" do turn over.
        string At(PilotPage page, int seconds) => Draw(page.ToString(), page, PageData.Now + TimeSpan.FromSeconds(seconds)).Text;

        At(PilotPage.Sessions, 59).ShouldNotBe(At(PilotPage.Sessions, 60));
        At(PilotPage.Jobs, 0).ShouldNotBe(At(PilotPage.Jobs, 1));
        Draw("LAN", PilotPage.Flights, PageData.Now, lan: true).Text.ShouldNotBe(Draw("LAN", PilotPage.Flights, PageData.Now + TimeSpan.FromSeconds(1), lan: true).Text);
        // The summary card's "made 3m ago" turns at +40 s, on Home and on a flight's page.
        At(PilotPage.Home, 39).ShouldNotBe(At(PilotPage.Home, 40));
        Draw("flight page", PilotPage.Flights, PageData.Now + TimeSpan.FromSeconds(39)).Text.ShouldNotBe(Draw("flight page", PilotPage.Flights, PageData.Now + TimeSpan.FromSeconds(40)).Text);
    }

    private static string Differ(string a, string b)
    {
        var from = 0;
        while (from < a.Length && from < b.Length && a[from] == b[from]) from++;
        string Around(string s) => s.Substring(Math.Max(0, from - 40), Math.Min(s.Length - Math.Max(0, from - 40), 100)).ReplaceLineEndings(" | ");
        return $"'{Around(a)}' then '{Around(b)}'";
    }

    private static (string Text, long Fingerprint) Draw(string page, PilotPage shown, DateTimeOffset at, bool lan = false)
    {
        var data = PageData.At(at);
        var here = data.Nodes[0];
        // The summary card on Home (every node's, as two nodes are shown) and on the first flight's page, made 200 s before Now.
        var made = PageData.Now - TimeSpan.FromSeconds(200);
        var summaries = new SummaryShelf(data.Nodes, new Dictionary<(string Node, string Scope), SummaryRow>
        {
            [(here.Key, SummaryRow.AllNodesScope)] = new() { Scope = SummaryRow.AllNodesScope, Markdown = "All calm.", MadeAt = made, Tokens = 10 },
            [(here.Key, data.Flights.Lines[0].Code)] = new() { Scope = data.Flights.Lines[0].Code, Markdown = "On its way.", MadeAt = made, Tokens = 10 },
        });
        var ideas = new IdeasScreen([], [], [], "test", at);
        var pages = new FleetPages(data.Sessions, data.Flights, data.Jobs, [.. data.Jobs.Lines.Select(l => l.Row)], data.Cards, data.Repos, summaries, data.Machines, data.PullRequests, ideas);

        using var renderer = new DrawnText(1600, 900);
        var chrome = new PilotChrome<DIR.Lib.RgbaImage>(renderer, new SessionsView<DIR.Lib.RgbaImage>(renderer)) { FontPath = SessionsCapture.FontPath };
        chrome.Play.ShowEverything();
        chrome.Nodes.Choices = data.Nodes;
        chrome.Summaries.Shelf = summaries;
        chrome.Flights.Jobs = pages.JobRows;
        // Discovering since Now: "LAN: discovering (N s)" for ten seconds, then still listening. Without it, a node is heard and
        // the row is not drawn.
        chrome.Flights.Lan = lan ? LanWatch.Over(() => [], PageData.Now) : LanWatch.Over(() => [new LanNode("10.0.0.9:47731", "LE1")], PageData.Now);
        chrome.Show(shown);
        if (page == "flight page")
        {
            chrome.Flights.OpenDetail(data.Flights.Lines[0].Code);
        }
        chrome.Render(data.Sessions, data.Flights, data.Cards, data.Jobs, data.Fleet, data.Repos, data.PullRequests, new DIR.Lib.RectF32(0, 0, 1600, 900), data.Machines, ideas);

        var text = AsOf().Replace(string.Join("\n", renderer.Texts), "as of");
        var header = FrameWatch.Header(false, false, null, chrome.Nodes);
        return (text, FrameWatch.Fingerprint(pages, data.Fleet, at, shown, version: 1, header, FrameWatch.LanLine(chrome.Flights.Lan, at)));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"as of\s+\d{4}-\d\d-\d\d\s+\d\d:\d\d:\d\d\s+UTC")]
    private static partial System.Text.RegularExpressions.Regex AsOf();
}
