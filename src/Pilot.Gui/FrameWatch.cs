using Microsoft.Extensions.Logging;
using Pilot.Contracts;
using Pilot.Ui;

namespace Pilot.Gui;

/// <summary>
/// Keeps the window light: says whether a fresh page build is worth a frame (only when what the shown page draws moved), and
/// reports any frame that took longer than <see cref="SlowFrame"/>, the one slow-frame reporter.
/// <see cref="WorthAFrame"/> is for the one page-builder task; <see cref="Frame"/> keeps no state but a count, so the render thread may call it.
/// </summary>
internal sealed class FrameWatch(ILogger logger, TimeSpan? slowFrame = null)
{
    private (int Ask, long Shown)? _drawn;

    private long _frames;

    /// <summary>How many frames have been drawn (reported to <see cref="Frame"/>).</summary>
    public long Frames => Interlocked.Read(ref _frames);

    public TimeSpan SlowFrame { get; } = slowFrame ?? TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// What the header of every page shows that the builder reads and the render thread does not get from the pages: this node's
    /// maintenance and autopilot, the update light's release, and the node pills (each node's name, and when a down one was last heard).
    /// </summary>
    internal static long Header(bool maintenance, bool autopilot, string? newer, NodeFilter nodes)
    {
        var hash = new HashCode();
        hash.Add(maintenance);
        hash.Add(autopilot);
        hash.Add(newer, StringComparer.Ordinal);
        hash.Add(nodes.All);
        foreach (var choice in nodes.Choices)
        {
            hash.Add(choice);
            hash.Add(nodes.Down.TryGetValue(choice.Key, out var since) ? since ?? DateTimeOffset.MinValue : (DateTimeOffset?)null);
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// A fingerprint of what the window draws from the built pages on <paramref name="page"/>: the page's own text (every row's
    /// dump line, which carries its own "ago"), the cards panel and the rail, which every page has, and <paramref name="header"/>
    /// (<see cref="Header"/>). It moves exactly when some of that text does, whenever its second or minute turns over.
    /// Home's is whole: a node event that changes nothing Home draws (a busy worker's last response, on a node of hundreds of
    /// sessions) leaves it as it was. The other pages' are not, so <paramref name="version"/> (the feeds' combined version) is
    /// part of theirs, and any node event redraws them.
    /// </summary>
    internal static long Fingerprint(FleetPages pages, FleetScreen fleet, DateTimeOffset now, PilotPage page = PilotPage.Home, long version = 0, long header = 0)
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
        Panel(ref hash, pages.Cards, pages.PullRequests?.Row, pages.Ideas);
        if (page != PilotPage.Home)
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
            case PilotPage.Chat:
                foreach (var line in pages.Sessions.Lines)
                {
                    Add(line.ToDumpLine());
                    // The transcript banner's "waiting since", counted at render time from the row.
                    if (line.WaitingSince is { } since) Add(Waited.Say(now - since));
                }
                break;
            case PilotPage.Flights:
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
                // Settings' "checked Ns ago" is counted from the clock in seconds, so an open Settings page turns over each second.
                hash.Add(now.ToUnixTimeSeconds());
                break;
        }
        if (page is PilotPage.Home or PilotPage.Chat && pages.Summaries is { } shelf)
        {
            foreach (var row in shelf.Rows.Values) Add(SessionLine.Ago(now - row.MadeAt));
        }
        return hash.ToHashCode();
    }

    // The cards panel as CardsView draws it: the watchtower's line, the summary, each card's words and buttons (a PR's title
    // opens its row only while the node lists the PR) and the ideas it names, a node out of reach, and the handled lines.
    private static void Panel(ref HashCode hash, CardsScreen cards, PullRequestsRow? pullRequests, IdeasScreen? ideas)
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

    /// <summary>Forgets what was drawn, so the next build is worth a frame (a build that failed is tried again).</summary>
    public void Forget() => _drawn = null;

    /// <summary>Logs a frame that took longer than <see cref="SlowFrame"/>, with the page and what it drew (<paramref name="summary"/> is asked for only then); true when it logged.</summary>
    public bool Frame(TimeSpan took, object page, Func<string> summary)
    {
        Interlocked.Increment(ref _frames);
        if (took <= SlowFrame)
        {
            return false;
        }

        logger.LogWarning("Slow frame: {Milliseconds} ms on {Page}: {Summary}", (int)took.TotalMilliseconds, page, summary());
        return true;
    }
}
