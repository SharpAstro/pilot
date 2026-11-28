using Microsoft.Extensions.Logging;
using Pilot.Contracts;
using Pilot.Ui;

namespace Pilot.Gui;

/// <summary>
/// Keeps the window light: says whether a fresh page build is worth a frame (only when what the shown page draws moved), and
/// reports any frame that took longer than <see cref="SlowFrame"/>, the one slow-frame reporter.
/// <see cref="WorthAFrame"/> is for the one page-builder task; <see cref="Frame"/> keeps no state but a count, so the render thread may call it.
/// </summary>
internal sealed class FrameWatch(ILogger logger, TimeSpan? slowFrame = null, TimeSpan? lineEvery = null)
{
    private (int Ask, long Shown)? _drawn;

    private long _frames;

    /// <summary>How many frames have been drawn (reported to <see cref="Frame"/>).</summary>
    public long Frames => Interlocked.Read(ref _frames);

    private long _builds;
    private long _buildTicks;

    /// <summary>How many page builds there were, and the time they took together (reported to <see cref="Built"/>), for the closing line.</summary>
    public (long Count, TimeSpan Took) Builds => (Interlocked.Read(ref _builds), TimeSpan.FromTicks(Interlocked.Read(ref _buildTicks)));

    /// <summary>Counts one page build that took <paramref name="took"/>, frame or not.</summary>
    public void Built(TimeSpan took)
    {
        Interlocked.Increment(ref _builds);
        Interlocked.Add(ref _buildTicks, took.Ticks);
    }

    public TimeSpan SlowFrame { get; } = slowFrame ?? TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// What the header of every page shows that the builder reads and the render thread does not get from the pages: this node's
    /// maintenance and autopilot, the update light's release and, while the light shows, its phase (<paramref name="light"/>,
    /// <see cref="Light"/>), and the node pills (each node's name, and when a down one was last heard).
    /// </summary>
    internal static long Header(bool maintenance, bool autopilot, string? newer, NodeFilter nodes, bool? light = null)
    {
        var hash = new HashCode();
        hash.Add(maintenance);
        hash.Add(autopilot);
        hash.Add(newer, StringComparer.Ordinal);
        hash.Add(light);
        hash.Add(nodes.All);
        foreach (var choice in nodes.Choices)
        {
            hash.Add(choice);
            hash.Add(nodes.Down.TryGetValue(choice.Key, out var since) ? since ?? DateTimeOffset.MinValue : (DateTimeOffset?)null);
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// The update light's phase while it shows <paramref name="shown"/> (what <see cref="AboutCard.Newer"/> last answered the
    /// render thread), null while no light shows: lit and unlit a second each, so a frame a second is asked only while it pulses.
    /// </summary>
    internal static bool? Light(AboutCard about, string? shown) => shown is null ? null : about.Lit;

    /// <summary>
    /// What the Flights page's LAN row says (<see cref="LanWatch.Line"/>), counted in seconds while it discovers or an ask waits;
    /// null while the row is not drawn.
    /// </summary>
    internal static string? LanLine(LanWatch? lan, DateTimeOffset now) => lan is { Quiet: false } ? lan.Line(now) : null;

    /// <summary>
    /// A fingerprint of what the window draws from the built pages on <paramref name="page"/>: the page's own text (every row's
    /// dump line, which carries its own "ago"), the cards panel and the rail, which every page has, and <paramref name="header"/>
    /// (<see cref="Header"/>). It moves exactly when some of that text does, whenever its second or minute turns over.
    /// Home's and Ideas' are whole (<see cref="Whole"/>): a node event that changes nothing the page draws (a busy worker's last
    /// response, on a node of hundreds of sessions) leaves it as it was. The other pages' are not, so <paramref name="version"/>
    /// (the feeds' combined version) is part of theirs, and any node event redraws them. <paramref name="lan"/> is the Flights page's
    /// LAN row (<see cref="LanLine"/>); <paramref name="told"/> is when the readiness answers the Ideas view holds itself were checked.
    /// </summary>
    internal static long Fingerprint(FleetPages pages, FleetScreen fleet, DateTimeOffset now, PilotPage page = PilotPage.Home, long version = 0, long header = 0, string? lan = null,
        IReadOnlyList<DateTimeOffset>? told = null)
    {
        var hash = new HashCode();
        void Add(string? line) => hash.Add(line, StringComparer.Ordinal);
        hash.Add(header);
        // The rail's tasks and ideas glyphs and their hover, and the cards panel: beside every page.
        var (glyph, said) = PilotChrome<DIR.Lib.RgbaImage>.JobsGlyph(pages.Jobs, now);
        Add(glyph);
        Add(said);
        (glyph, said) = PilotChrome<DIR.Lib.RgbaImage>.IdeasGlyph(pages.Ideas);
        Add(glyph);
        Add(said);
        Panel(ref hash, pages.Cards, pages.PullRequests?.Row, pages.Ideas, now);
        if (!Whole(page))
        {
            hash.Add(version);
        }
        // Only the shown page's rows: a row under a minute old on a page that is not drawn must not keep the window redrawing.
        switch (page)
        {
            case PilotPage.Home:
                // Counts, chips (flights, live sessions, running jobs), failed moves and each machine's figures, as a dump writes
                // them; a gauge's bar moves with its figure. A chip's brief is hover text, read from the newest build by the frame
                // the hover asks. The header's source (the "as of" is the frame's own), and which sessions there are, whose
                // transcript a card opens, in any order: a worker turning from working to idle moves its row, not a chip.
                Add(new HomeScreen(pages.Flights, pages.Sessions, pages.Jobs, pages.Cards, pages.Machines).ToDump());
                Add(pages.Flights.Source);
                var sessions = 0;
                foreach (var line in pages.Sessions.Lines) sessions = unchecked(sessions + StringComparer.Ordinal.GetHashCode(line.Key));
                hash.Add(sessions);
                // The summary card's row (its "made ago" is added below).
                if (pages.Summaries is { } summaries)
                {
                    foreach (var mark in summaries.Marks) hash.Add(mark);
                    foreach (var ((node, scope), row) in summaries.Rows)
                    {
                        Add(node);
                        Add(scope);
                        hash.Add(row);
                    }
                }
                break;
            case PilotPage.Sessions:
                foreach (var line in pages.Sessions.Lines)
                {
                    Add(line.ToDumpLine());
                    // The transcript banner's "waiting since", counted at render time from the row.
                    if (line.WaitingSince is { } since) Add(Waited.Say(now - since));
                }
                break;
            case PilotPage.Flights:
                // The LAN row's seconds while it discovers or an ask waits (the summary card of a flight's detail is added below).
                Add(lan);
                foreach (var line in pages.Flights.Lines)
                {
                    Add(line.ToDumpLine());
                    // The flight detail's artifacts, each with its own "ago".
                    foreach (var leg in line.LegRows)
                    {
                        foreach (var artifact in leg.Artifacts) Add(SessionLine.Ago(now - artifact.At));
                    }
                }
                break;
            case PilotPage.Jobs:
                foreach (var line in pages.Jobs.Lines) Add(line.ToDumpLine());
                // Who holds the queue, "held for 12m 4s": counted in seconds.
                Add(pages.Jobs.Holder);
                break;
            case PilotPage.Fleet:
                foreach (var line in fleet.Lines) Add(line.ToDumpLine());
                break;
            case PilotPage.Repos:
                if (pages.Repos is { } repos)
                {
                    foreach (var line in repos.Lines) Add(line.ToDumpLine());
                }
                break;
            case PilotPage.PullRequests:
                if (pages.PullRequests?.Row is { } read)
                {
                    // The pull requests' "read ago" and each row's "updated ago".
                    Add(PullRequestsRow.Age(read.ReadAt, now));
                    foreach (var pr in read.PullRequests) Add(PullRequestsRow.Age(pr.UpdatedAt ?? pr.CreatedAt, now));
                }
                break;
            case PilotPage.Settings:
                // Settings' "checked Ns ago" is counted from the clock in seconds: an open Settings page turns over each second.
                hash.Add(now.ToUnixTimeSeconds());
                break;
            case PilotPage.Ideas:
                Ideas(ref hash, pages.Ideas, now, told);
                break;
        }
        // The summary card's "made 3m ago": on Home, and on a flight's detail on the Flights page.
        if (page is PilotPage.Home or PilotPage.Flights && pages.Summaries is { } shelf)
        {
            foreach (var row in shelf.Rows.Values) Add(SessionLine.Ago(now - row.MadeAt));
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// Whether the fingerprint of <paramref name="page"/> holds everything it draws by value, so the feeds' version is no part of it.
    /// Chat's is whole too: the chat draws nothing from the pages but the header, the rail and the panel, and its conversation asks
    /// for its own frames. It used to count every session row and the version, so every node event drew it in full (129 to 738 ms a
    /// frame on DE1, 2026-11-28, pilot #1443).
    /// </summary>
    internal static bool Whole(PilotPage page) => page is PilotPage.Home or PilotPage.Ideas or PilotPage.Chat;

    // What the Ideas page draws from the pages, by value. No record holding an array or a list is added whole: its generated hash
    // takes the array by reference, new on every read of the feed. The source's "as of" is the frame's own and stays out. The only
    // clock text is a readiness row's "checked 12s ago" (IdeaIds.Age), counted here at the granularity it shows.
    private static void Ideas(ref HashCode hash, IdeasScreen? screen, DateTimeOffset now, IReadOnlyList<DateTimeOffset>? told)
    {
        if (told is not null)
        {
            foreach (var at in told) hash.Add(IdeaIds.Age(at, now), StringComparer.Ordinal);
        }
        if (screen is null)
        {
            hash.Add(-1);
            return;
        }
        hash.Add(screen.Source, StringComparer.Ordinal);
        foreach (var idea in screen.Ideas)
        {
            hash.Add(idea.Id, StringComparer.Ordinal);
            hash.Add(idea.Repo, StringComparer.Ordinal);
            hash.Add(idea.Title, StringComparer.Ordinal);
            hash.Add(idea.Body, StringComparer.Ordinal);
            hash.Add(idea.State);
            hash.Add(idea.Age, StringComparer.Ordinal);
            hash.Add(idea.Origin, StringComparer.Ordinal);
            hash.Add(idea.Url, StringComparer.Ordinal);
            hash.Add(idea.Pinned);
            hash.Add(idea.NeedsDesign);
            hash.Add(idea.Design, StringComparer.Ordinal);
            hash.Add(idea.DesignVersion);
            hash.Add(idea.Priority);
            hash.Add(idea.UpdatedAt);
            foreach (var design in idea.Designs)
            {
                hash.Add(design.Id, StringComparer.Ordinal);
                hash.Add(design.Title, StringComparer.Ordinal);
                hash.Add(design.Version);
            }
            foreach (var image in idea.Images)
            {
                hash.Add(image.Alt, StringComparer.Ordinal);
                hash.Add(image.Url, StringComparer.Ordinal);
                hash.Add(image.Copy, StringComparer.Ordinal);
            }
        }
        foreach (var ask in screen.Asks)
        {
            hash.Add(ask.Id, StringComparer.Ordinal);
            hash.Add(ask.Bin);
            hash.Add(ask.State);
            hash.Add(ask.Kind, StringComparer.Ordinal);
            hash.Add(ask.Subject, StringComparer.Ordinal);
            hash.Add(ask.Title, StringComparer.Ordinal);
            hash.Add(ask.Summary, StringComparer.Ordinal);
            hash.Add(ask.Detail, StringComparer.Ordinal);
            hash.Add(ask.Link, StringComparer.Ordinal);
            foreach (var choice in ask.Choices)
            {
                hash.Add(choice.Label, StringComparer.Ordinal);
                hash.Add(choice.Verb, StringComparer.Ordinal);
                hash.Add(choice.Status, StringComparer.Ordinal);
                hash.Add(choice.Note, StringComparer.Ordinal);
            }
        }
        foreach (var repo in screen.Repos) hash.Add(repo, StringComparer.Ordinal);
        foreach (var milestone in screen.Milestones)
        {
            hash.Add(milestone.Repo, StringComparer.Ordinal);
            hash.Add(milestone.Number);
            hash.Add(milestone.Title, StringComparer.Ordinal);
            hash.Add(milestone.Description, StringComparer.Ordinal);
            hash.Add(milestone.DueOn);
            hash.Add(milestone.OpenIssues);
            hash.Add(milestone.ClosedIssues);
        }
        foreach (var issue in screen.Issues)
        {
            hash.Add(issue.Repo, StringComparer.Ordinal);
            hash.Add(issue.Number);
            hash.Add(issue.Title, StringComparer.Ordinal);
            hash.Add(issue.Body, StringComparer.Ordinal);
            hash.Add(issue.Milestone, StringComparer.Ordinal);
            hash.Add(issue.Age, StringComparer.Ordinal);
            hash.Add(issue.Author, StringComparer.Ordinal);
            hash.Add(issue.Url, StringComparer.Ordinal);
            hash.Add(issue.Flight, StringComparer.Ordinal);
            hash.Add(issue.Pinned);
            hash.Add(issue.Priority);
            hash.Add(issue.UpdatedAt);
            if (issue.Readiness is { } readiness)
            {
                hash.Add(readiness.Ready);
                hash.Add(readiness.CheckedAt);
                foreach (var reason in readiness.Reasons) hash.Add(reason, StringComparer.Ordinal);
                hash.Add(IdeaIds.Age(readiness.CheckedAt, now), StringComparer.Ordinal);
            }
            else
            {
                hash.Add(-1);
            }
        }
        foreach (var move in screen.Moves)
        {
            hash.Add(move.Repo, StringComparer.Ordinal);
            hash.Add(move.Id, StringComparer.Ordinal);
            hash.Add(move.NewRepo, StringComparer.Ordinal);
            hash.Add(move.NewId, StringComparer.Ordinal);
        }
        foreach (var flight in screen.Flights)
        {
            hash.Add(flight.Code, StringComparer.Ordinal);
            hash.Add(flight.Repo, StringComparer.Ordinal);
            hash.Add(flight.FromIdea, StringComparer.Ordinal);
            hash.Add(flight.Tracker);
            hash.Add(flight.Issue);
            hash.Add(flight.State);
            foreach (var design in flight.Designs)
            {
                hash.Add(design.Id, StringComparer.Ordinal);
                hash.Add(design.Version);
            }
        }
    }

    // The cards panel as CardsView draws it: the watchtower's line, the summary, each card's words and buttons (a PR's title
    // opens its row only while the node lists the PR) and the ideas it names, a node out of reach, and the handled lines.
    private static void Panel(ref HashCode hash, CardsScreen cards, PullRequestsRow? pullRequests, IdeasScreen? ideas, DateTimeOffset now)
    {
        if (cards.Watchtower is { } tower)
        {
            hash.Add(tower.Name, StringComparer.Ordinal);
            hash.Add(tower.State);
            hash.Add(tower.ApiError, StringComparer.Ordinal);
            hash.Add(tower.ContextWindow > 0 ? $"{100.0 * tower.ContextTokens / tower.ContextWindow:0}" : null, StringComparer.Ordinal);
            hash.Add(tower.RemoteControlUrl, StringComparer.Ordinal);
        }
        hash.Add(cards.Summary, StringComparer.Ordinal);
        foreach (var card in cards.Open.Concat(cards.Merged).Concat(cards.Handled))
        {
            hash.Add(cards.NodeOf(card));
            hash.Add(card.Id, StringComparer.Ordinal);
            hash.Add(card.Bin);
            hash.Add(card.State);
            hash.Add(card.Kind, StringComparer.Ordinal);
            hash.Add(card.Subject, StringComparer.Ordinal);
            hash.Add(card.Title, StringComparer.Ordinal);
            hash.Add(card.Summary, StringComparer.Ordinal);
            hash.Add(card.Detail, StringComparer.Ordinal);
            hash.Add(card.Link, StringComparer.Ordinal);
            // The row's "stale 5 h" (#736) is counted from the clock, so it moves the fingerprint as the hour turns.
            hash.Add(CardsView<DIR.Lib.RgbaImage>.StaleMark(card, now, CardsView<DIR.Lib.RgbaImage>.DefaultStaleCardAfter), StringComparer.Ordinal);
            foreach (var choice in card.Choices)
            {
                hash.Add(choice.Label, StringComparer.Ordinal);
                hash.Add(choice.Verb, StringComparer.Ordinal);
                hash.Add(choice.Status, StringComparer.Ordinal);
                hash.Add(choice.Note, StringComparer.Ordinal);
            }
            hash.Add(PullRequestLinks.Of(card) is { } pr && PullRequestLinks.Lists(pullRequests, pr));
            foreach (var link in (ideas ?? IdeasScreen.Empty).LinksIn($"{card.Title}\n{card.Detail}")) hash.Add(link);
        }
        foreach (var held in cards.Held)
        {
            hash.Add(held.Line, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// True when a build for the page ask <paramref name="ask"/>, showing <paramref name="shown"/> (<see cref="Fingerprint"/>),
    /// differs from what was last drawn; remembers it as the drawn one. Decided on what the build shows, so an "ago" is redrawn
    /// the moment it turns over, and a node event that changes nothing on the page draws nothing.
    /// </summary>
    public bool WorthAFrame(int ask, long shown)
    {
        var current = (ask, shown);
        if (_drawn == current)
        {
            return false;
        }

        _drawn = current;
        return true;
    }

    /// <summary>Whether <paramref name="shown"/> for the page ask <paramref name="ask"/> is what was last drawn; remembers nothing.</summary>
    public bool Drawn(int ask, long shown) => _drawn == (ask, shown);

    /// <summary>Forgets what was drawn, so the next build is worth a frame (a build that failed is tried again).</summary>
    public void Forget() => _drawn = null;

    // The frame line's window (pilot #1443), as tianwen's GUI writes one every five seconds: what the frames took, what the render
    // thread allocated, the collections, the page builds and the process's processor time, so a change to the frame schedule is read
    // from gui.log and not guessed. Render thread only, but for the builds, which the builder counts (Interlocked).
    private readonly TimeSpan _lineEvery = lineEvery ?? TimeSpan.FromSeconds(5);
    private readonly List<double> _paints = new(512);
    private long _lineFrom = System.Diagnostics.Stopwatch.GetTimestamp();
    private long _lineAllocated = GC.GetAllocatedBytesForCurrentThread();
    private int _lineGen0 = GC.CollectionCount(0);
    private int _lineGen2 = GC.CollectionCount(2);
    private TimeSpan _linePaused = GC.GetTotalPauseDuration();
    private TimeSpan _lineCpu = Environment.CpuUsage.TotalTime;
    private long _lineBuilds;
    private long _lineBuildTicks;

    // What asked for frames since the last line, by cause (FrameCause): asks, not frames, since several asks before a frame are one.
    private readonly long[] _asks = new long[Enum.GetValues<FrameCause>().Length];

    /// <summary>Counts one ask for a frame, for the frame line: which of the window's causes keeps a page drawing.</summary>
    public void Asked(FrameCause cause) => Interlocked.Increment(ref _asks[(int)cause]);

    /// <summary>
    /// Counts one frame of the render thread, logs it when it took longer than <see cref="SlowFrame"/> (with the page and what it
    /// drew; <paramref name="summary"/> is asked for only then), and once five seconds (<c>lineEvery</c>) has passed since the last line,
    /// writes the frame line for the frames since. True when it logged the frame as slow.
    /// </summary>
    public bool Frame(TimeSpan took, object page, Func<string> summary)
    {
        Interlocked.Increment(ref _frames);
        _paints.Add(took.TotalMilliseconds);
        var slow = took > SlowFrame;
        if (slow)
        {
            logger.LogWarning("Slow frame: {Milliseconds} ms on {Page}: {Summary}", (int)took.TotalMilliseconds, page, summary());
        }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_lineFrom) >= _lineEvery)
        {
            WriteLine(page);
        }
        return slow;
    }

    // The window's line, then a fresh window. Written at a frame, so a window nothing was drawn in writes no line: an idle window logs nothing.
    private void WriteLine(object page)
    {
        var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_lineFrom).TotalSeconds;
        _paints.Sort();
        double At(double share) => _paints[Math.Min(_paints.Count - 1, (int)(share * _paints.Count))];
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var cpu = Environment.CpuUsage.TotalTime;
        var (builds, buildTicks) = (Interlocked.Read(ref _builds), Interlocked.Read(ref _buildTicks));
        logger.LogInformation(
            "Frames in {Seconds:0.#} s on {Page}: {Frames}, paint median {Median:0.#} ms, 90th {P90:0.#} ms, most {Most:0.#} ms, {KbAFrame:0} KB a frame on the render thread; {Builds} page builds, {BuildMs:0} ms; gen0 {Gen0}, gen2 {Gen2}, paused {PausedMs:0} ms; the process {Cores:0.###} cores; asked by {Asks}",
            seconds, page, _paints.Count, At(0.5), At(0.9), _paints[^1], (allocated - _lineAllocated) / 1024.0 / _paints.Count,
            builds - _lineBuilds, TimeSpan.FromTicks(buildTicks - _lineBuildTicks).TotalMilliseconds,
            GC.CollectionCount(0) - _lineGen0, GC.CollectionCount(2) - _lineGen2, (GC.GetTotalPauseDuration() - _linePaused).TotalMilliseconds,
            (cpu - _lineCpu).TotalSeconds / seconds, AsksSinceLine());
        _paints.Clear();
        _lineFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        _lineAllocated = allocated;
        _lineGen0 = GC.CollectionCount(0);
        _lineGen2 = GC.CollectionCount(2);
        _linePaused = GC.GetTotalPauseDuration();
        _lineCpu = cpu;
        (_lineBuilds, _lineBuildTicks) = (builds, buildTicks);
    }

    // The asks since the last line, as "build 5, change 3, glyphs 12", the causes that asked; and a fresh count.
    private string AsksSinceLine()
    {
        var said = new List<string>();
        foreach (var cause in Enum.GetValues<FrameCause>())
        {
            if (Interlocked.Exchange(ref _asks[(int)cause], 0) is var n and > 0)
            {
                said.Add($"{cause.ToString().ToLowerInvariant()} {n}");
            }
        }
        return said.Count == 0 ? "nothing" : string.Join(", ", said);
    }
}

/// <summary>What asked pilot-gui's window for a frame, as the frame line counts it.</summary>
internal enum FrameCause
{
    /// <summary>A page build that showed something new.</summary>
    Build,

    /// <summary>A change asked for one (a view's Changed, a read come back, a press): <c>Redraw</c>.</summary>
    Change,

    /// <summary>The font atlas still rasterising glyphs.</summary>
    Glyphs,

    /// <summary>A tooltip falling due.</summary>
    Tooltip,

    /// <summary>A toast fading.</summary>
    Fade,

    /// <summary>Another launch handing this window a link.</summary>
    Handoff,
}
