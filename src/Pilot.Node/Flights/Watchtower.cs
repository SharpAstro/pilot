using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The node's one watchtower session, <c>&lt;machine&gt;-watchtower</c>, kept running while there is something to watch
/// (<see cref="Watching"/>: a flight not landed, a job, a ring): started with Remote Control on so
/// the controller reaches it from claude.ai or a phone, briefed from the node's state, and fed every Ring and For-you
/// card as it opens. One that ended or failed is started again; one the usage limit stopped is resumed once the limit
/// resets. A watchtower that cannot be started rings, never stays silent.
/// <para>
/// It keeps its conversation: what it did and told the controller is worth more than a fresh brief. Long idle, or grown
/// past its share of the window, it is compacted instead (<see cref="Compactor"/>), so it wakes on a summary rather than
/// re-reading a long conversation whose prompt cache has gone cold. Stalls are the node's to find (<see cref="LegWatch"/>),
/// so it is not sent on rounds to keep its cache warm.
/// </para>
/// <para>
/// With nothing to watch it costs nothing: none is started, and one idle <see cref="QuietFor"/> after the last thing to
/// watch is stopped. A round every 50 minutes with no flight in the air spent 3.5M tokens in a day on "quiet". The next
/// flight, job or ring starts a fresh one, briefed from the state: cheaper than waking an old conversation whose cache
/// has gone cold.
/// </para>
/// </summary>
/// <remarks>
/// Which cards it was told is kept in memory: a node restarted finds its watchtower by name and takes the open cards
/// as told, since that session saw them; a fresh one has them in its brief.
/// <para>
/// Which brief it was started with is kept in a file (<see cref="BriefFile"/>), as a fingerprint of the brief's fixed part
/// (<see cref="Briefs.WatchtowerFingerprint"/>), because a say resumes the session and so keeps the brief it had: a node
/// updated to a changed brief finds one still running on the old, and starts a fresh one in its place before telling it
/// anything (#22). One whose start the node has no record of is taken as briefed by an older node.
/// </para>
/// </remarks>
public sealed class Watchtower(
    NodeState state, IWorkerLauncher launcher, Func<CommandRequest, CancellationToken, Task<CommandResult>> run, NodeOptions options, TimeProvider time) : BackgroundService
{
    /// <summary>Where this says what it would have said on stderr: the node's log (pilot #416), nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    public const string Down = "watchtower-down";

    /// <summary>The model and effort the watchtower starts on; a test's watchtower passes none.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>What holds a start while the 5-hour window is spent: the node's one gate, shared with the leg sweep and the compactor.</summary>
    public StartGate Gate { get; set; } = new(state, time);

    public const string Stopped = "watchtower-stopped";

    /// <summary>The Handled card that says its start waits for the 5-hour window.</summary>
    public const string Waits = "watchtower-waits";

    /// <summary>What its own raises are: a card it raised is never said back to it.</summary>
    public const string RaisedKind = "watchtower";

    /// <summary>How often it looks.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a Ring card it was told may stay open before it is told once more, which wakes or starts it if it has gone.
    /// Told only once, an ended watchtower was never woken for a card it had heard of and left open: PIL338's review asked
    /// for changes and nobody sent the leg round until the person asked why nothing landed (SE1, 2026-10-27).
    /// </summary>
    public TimeSpan RetellAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long it is kept once there is nothing to watch, idle, before it is stopped.</summary>
    public TimeSpan QuietFor { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long after a failed start the next one is tried.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long after its limit resets a stopped watchtower is resumed.</summary>
    public TimeSpan ResetMargin { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a started watchtower has to show up in the listing before it counts as gone.</summary>
    public TimeSpan ShowUpGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long after its last reply an ended watchtower is resumed rather than replaced: inside the prompt cache's hour, with a
    /// margin. Resumed warm, its context is read from the cache; past it, the whole context is read again at full price, more
    /// than a fresh one's brief, so a fresh one starts instead.
    /// </summary>
    public TimeSpan CacheWarmFor { get; init; } = TimeSpan.FromMinutes(55);

    /// <summary>The share of its context window past which an ended watchtower is replaced, not resumed, warm or not.</summary>
    public double ResumeBelow { get; init; } = 0.75;

    /// <summary>How it runs the CLI, socket and all: <see cref="Briefs.Run"/>.</summary>
    public string Pilot { get; init; } = "pilot";

    /// <summary>Completes when the session tracker has had its first look: before it, a running watchtower looks missing.</summary>
    public Func<Task> Ready { get; init; } = () => Task.CompletedTask;

    /// <summary>Its session name, <c>du1-watchtower</c>.</summary>
    public string Name => $"{options.Machine.ToLowerInvariant()}-watchtower";

    /// <summary>Where the fingerprint of the brief the running watchtower was started with is kept, in the node's data folder.</summary>
    public string BriefFile => Path.Combine(options.DataFolder, "watchtower-brief");

    private readonly SemaphoreSlim _one = new(1, 1);
    private string? _briefed;
    private bool _briefedRead;
    // Each open card it was told, and when; a Ring card still open RetellAfter on is told once more (_retold).
    private readonly Dictionary<string, DateTimeOffset> _told = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retold = new(StringComparer.Ordinal);
    private string? _job;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _nextTry = DateTimeOffset.MinValue;
    private DateTimeOffset? _resumedAfter;

    // An ended watchtower a resuming say did not reach: replaced by a fresh one, not said to again.
    private string? _resumeRefused;
    private bool _adopted;
    // Rows this node has stopped as a second watchtower; the sweep leaves them alone until the tracker shows them ended.
    private readonly HashSet<string> _stopped = new(StringComparer.Ordinal);
    // When there was last something to watch; the node's start counts, so one running then is kept QuietFor first.
    private DateTimeOffset? _watchingAt;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            await Ready();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await LookAsync(stopping);
                await Task.Delay(Interval, time, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // As with the leg watch: a keeper that died on one bad look would be the silent derail itself.
                await Error.WriteLineAsync($"watchtower: one look failed, looking again: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Whether a row is a watchtower of this node, by id and not by name (pilot #517): its row id is a <see cref="Generation"/>'s
    /// job, or its session id (or an earlier one) is a generation's session. Renamed, it is still the node's. A background row
    /// of the watchtower's name counts too, which is how one an older node started is adopted into <see cref="Generations"/>.
    /// </summary>
    private bool IsWatchtower(SessionRow row)
    {
        if (row.Name == Name)
        {
            return true;
        }
        lock (_generationsLock)
        {
            _generations ??= LoadGenerations();
            return _generations.Any(g => g.Job == row.Id
                || g.SessionId is { } session && (row.SessionId == session || row.EarlierSessionIds?.Contains(session) == true));
        }
    }

    /// <summary>Whether <paramref name="id"/> is a row id or a session id of a watchtower the node has had, whatever it is called now.</summary>
    public bool KnowsId(string id)
    {
        lock (_generationsLock)
        {
            _generations ??= LoadGenerations();
            return _generations.Any(g => g.Job == id || g.SessionId == id);
        }
    }

    /// <summary>
    /// The watchtower's row now: the one it started, else the newest of its name, live first. One renamed is not current: it
    /// is still the node's, but stopped (<see cref="StopOthersAsync"/>), and cards go to the current one only.
    /// </summary>
    public SessionRow? Current()
    {
        var rows = state.Sessions().Where(r => IsWatchtower(r) && r.Name == Name).ToList();
        return rows.FirstOrDefault(r => r.Id == _job)
            ?? rows.OrderByDescending(Live).ThenByDescending(r => r.StartedAt).FirstOrDefault();
    }

    /// <summary>Where the watchtower sessions there have been are kept, in the node's data folder: a line each (<see cref="Generation"/>).</summary>
    public string SessionsFile => Path.Combine(options.DataFolder, "watchtower-sessions");

    /// <summary>One watchtower the node has had: the row it ran as, its latest Claude session (a say resumes it under a new id that holds the history), when it began and why.</summary>
    public sealed record Generation(string Job, string? SessionId, DateTimeOffset StartedAt, string? Why,
        DateTimeOffset? EndedAt = null, DateTimeOffset? LastReplyAt = null, string? EndedBecause = null);

    private readonly Lock _generationsLock = new();
    private List<Generation>? _generations;

    /// <summary>
    /// Every watchtower there has been, oldest first: each start the node made (kept in <see cref="SessionsFile"/>, so a restarted
    /// node knows them and the chat reads the earlier conversations after the rows are archived) and any row of the watchtower's
    /// name the node has seen, which brings in one an older node started. Each one's latest session id follows its row.
    /// </summary>
    public IReadOnlyList<Generation> Generations()
    {
        lock (_generationsLock)
        {
            _generations ??= LoadGenerations();
            var changed = false;
            foreach (var row in state.Sessions().Where(IsWatchtower))
            {
                var at = _generations.FindIndex(g => g.Job == row.Id);
                if (at < 0)
                {
                    _generations.Add(new Generation(row.Id, row.SessionId, row.StartedAt, null));
                    at = _generations.Count - 1;
                    changed = true;
                }
                else if (_generations[at].SessionId != row.SessionId)
                {
                    _generations[at] = _generations[at] with { SessionId = row.SessionId };
                    changed = true;
                }
                var known = _generations[at];
                if (row.State is SessionState.Ended or SessionState.Failed && known.EndedAt is null)
                {
                    // Noted as the row is first seen ended: when it ended, when it last replied and why.
                    _generations[at] = known with { EndedAt = time.GetUtcNow(), LastReplyAt = row.LastResponseAt, EndedBecause = row.EndedBecause };
                    changed = true;
                }
                else if (known.EndedAt is not null && row.EndedBecause is { } because && because != known.EndedBecause)
                {
                    _generations[at] = known with { EndedBecause = because };
                    changed = true;
                }
            }
            if (changed)
            {
                _generations = [.. _generations.OrderBy(g => g.StartedAt)];
                SaveGenerations(_generations);
            }
            return [.. _generations];
        }
    }

    private void RememberStart(string job, string why)
    {
        lock (_generationsLock)
        {
            _generations ??= LoadGenerations();
            _generations.RemoveAll(g => g.Job == job);
            _generations.Add(new Generation(job, null, time.GetUtcNow(), why));
            _generations = [.. _generations.OrderBy(g => g.StartedAt)];
            SaveGenerations(_generations);
        }
    }

    private List<Generation> LoadGenerations()
    {
        var found = new List<Generation>();
        try
        {
            foreach (var line in File.Exists(SessionsFile) ? File.ReadAllLines(SessionsFile) : [])
            {
                var parts = line.Split('\t');
                if (parts.Length >= 3 && DateTimeOffset.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                {
                    found.Add(new Generation(parts[0], parts[1].Length == 0 ? null : parts[1], at, parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null,
                        TimeAt(parts, 4), TimeAt(parts, 5), parts.Length > 6 && parts[6].Length > 0 ? parts[6] : null));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable counts as none kept: the rows of its name still bring back the sessions the node knows.
        }
        return found;
    }

    private static DateTimeOffset? TimeAt(string[] parts, int at) =>
        parts.Length > at && DateTimeOffset.TryParse(parts[at], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var found) ? found : null;

    private static string Stamp(DateTimeOffset? at) => at?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static string OneLine(string? text) => (text ?? "").ReplaceLineEndings(" ").Replace('\t', ' ');

    private void SaveGenerations(List<Generation> generations)
    {
        try
        {
            Directory.CreateDirectory(options.DataFolder);
            File.WriteAllLines(SessionsFile, generations.Select(g =>
                string.Join('\t', g.Job, g.SessionId ?? "", g.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), OneLine(g.Why),
                Stamp(g.EndedAt), Stamp(g.LastReplyAt), OneLine(g.EndedBecause))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory all the same; the rows of its name bring back what a restart loses.
        }
    }

    /// <summary>More watchtowers started in the last hour than this is churning.</summary>
    public int MaxStartsPerHour { get; set; } = 3;

    /// <summary>Watchtowers that end a median of less than this after their last reply, over the last hour, are churning.</summary>
    public TimeSpan ShortLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How many that replied and ended in the last hour it takes before their lifetime is judged.</summary>
    public int MinEndedToJudge { get; set; } = 2;

    /// <summary>How the watchtower is doing, from <see cref="Generations"/> and its row; null when the node keeps none.</summary>
    public WatchtowerHealth? Health()
    {
        if (!options.Watchtower)
        {
            return null;
        }
        var now = time.GetUtcNow();
        var all = Generations();
        var row = Current();
        var lastHour = all.Count(g => now - g.StartedAt <= TimeSpan.FromHours(1));
        var last24 = all.Count(g => now - g.StartedAt <= TimeSpan.FromHours(24));
        var lifetimes = all
            .Where(g => g.EndedAt is { } end && now - end <= TimeSpan.FromHours(1) && g.LastReplyAt is not null)
            .Select(g => g.EndedAt!.Value - g.LastReplyAt!.Value)
            .Order()
            .ToList();
        TimeSpan? typical = lifetimes.Count == 0 ? null
            : lifetimes.Count % 2 == 1 ? lifetimes[lifetimes.Count / 2] : (lifetimes[lifetimes.Count / 2 - 1] + lifetimes[lifetimes.Count / 2]) / 2;
        var lastEnded = all.Where(g => g.EndedAt is not null).OrderByDescending(g => g.EndedAt).FirstOrDefault()?.EndedBecause;
        var churning = lastHour > MaxStartsPerHour || (lifetimes.Count >= MinEndedToJudge && typical < ShortLifetime);

        var summary = $"{lastHour} watchtower{(lastHour == 1 ? "" : "s")} in the last hour ({last24} in 24 h)";
        if (typical is { } after)
        {
            var secs = Math.Max(0, (int)Math.Round(after.TotalSeconds));
            var mins = (int)Math.Round(after.TotalMinutes);
            var span = secs < 60 ? $"{secs} second{(secs == 1 ? "" : "s")}" : $"{mins} minute{(mins == 1 ? "" : "s")}";
            summary += $"; each ended about {span} after its reply";
        }
        if (lastEnded is { Length: > 0 })
        {
            summary += $"; the last ended because: {lastEnded}";
        }
        return new WatchtowerHealth
        {
            State = row?.State,
            Id = row?.Id,
            StartedAt = row?.StartedAt,
            ContextTokens = row?.ContextTokens ?? 0,
            ContextWindow = row?.ContextWindow ?? 0,
            StartsLastHour = lastHour,
            StartsLast24h = last24,
            TypicalLifetimeAfterReply = typical,
            LastEndedBecause = lastEnded,
            Churning = churning,
            Summary = summary,
        };
    }

    /// <summary>One For-you card while it churns, its text kept to the counts; closed once it calms down.</summary>
    private void KeepChurnCard(WatchtowerHealth? health)
    {
        if (health is not { Churning: true })
        {
            state.CloseCardsAbout(Verbs.WatchtowerTarget, "it calmed down", CardKinds.WatchtowerChurning);
            return;
        }
        const string title = "The watchtower keeps ending";
        var card = state.RaiseCard(CardBin.ForYou, CardKinds.WatchtowerChurning, Verbs.WatchtowerTarget, title, health.Summary, link: InAppLinks.Fleet);
        state.UpdateCard(card.Id, title, health.Summary, card.Choices, InAppLinks.Fleet);
    }

    private volatile bool _lastFromPerson;

    /// <summary>
    /// Notes who sent the latest message to the watchtower's session: the person (from the GUI chat, a TUI or a human's
    /// <c>pilot tell</c>) or the node (a card it says, a nudge). The watchtower's turn began with that message.
    /// </summary>
    public void NoteMessage(bool fromPerson, string? text = null)
    {
        _lastFromPerson = fromPerson;
        if (Fold(text) is not { Length: > 0 } folded)
        {
            return;
        }
        lock (_routed)
        {
            // A say the node routes reaches the session past the node too (resumed, it is a plain line), so what a non-person
            // said is kept to tell it from the person's; the person saying the same words again makes them theirs.
            _routed.Remove(folded);
            if (!fromPerson)
            {
                _routed.Add(folded);
                if (_routed.Count > MaxRouted)
                {
                    _routed.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>How many of the latest non-person messages routed to the watchtower are kept.</summary>
    private const int MaxRouted = 64;

    private readonly List<string> _routed = [];

    /// <summary>
    /// Whether the watchtower is acting on a message the person sent it: its latest message was theirs, and its session has not
    /// gone idle since. Then what it asks for in the person's name is an assistant's ask (the autopilot rule); on its own it
    /// is the lookout, who is never to do what is theirs.
    /// </summary>
    public bool ActingForPerson => _lastFromPerson && Current() is { State: SessionState.Working or SessionState.Waiting };

    /// <summary>
    /// The watchtower's transcripts, which are all the node has of what the person says to it on Remote Control: that goes to
    /// the session past the node. Null (a test's watchtower) reads none, so no word of the person's is ever found.
    /// </summary>
    public Sessions.TranscriptBook? Transcripts { get; set; }

    /// <summary>The shortest <c>--why</c> that can be matched to the person's message: a letter or two is in any message.</summary>
    private const int MinWordsLength = 6;

    /// <summary>
    /// The person's word for what the watchtower asks of a card: the words of the latest line that fed its current session's
    /// turn (<see cref="Sessions.TranscriptBook.LastPrompt"/>, meta lines included), when that line is the person's, its turn
    /// is still running, and <paramref name="command"/>'s <c>--why</c> quotes it or is quoted by it (whitespace and case
    /// folded). Null when there is none, and the watchtower stays fenced.
    /// </summary>
    public string? PersonsWord(CommandRequest command)
    {
        if (Transcripts is null || Fold(command.Why) is not { Length: > 0 } why
            || Current() is not { State: SessionState.Working or SessionState.Waiting, SessionId: { Length: > 0 } sessionId }
            || Transcripts.LastPrompt(sessionId) is not { } prompt || !IsPersons(prompt))
        {
            return null;
        }
        var said = Fold(prompt.Text);
        return said.Length > 0 && Quotes(said, why) ? command.Why!.Trim() : null;
    }

    /// <summary>
    /// Whether a line that fed the watchtower's turn is the person's: typed or resumed text (Remote Control, the GUI, a
    /// <c>tell</c>) or a cross-session message from the controller (the node relaying the person's words), and not the node's
    /// voice, another node's watchtower, or a say the node routed for someone else. A message from any other session, the
    /// node's own envelope, a task finishing, a scheduled wake, a hand-back, a compaction or a slash command is not; nor is
    /// the session's first prompt, which is always its brief: Claude Code records it as typed with no <c>[node] </c> in
    /// front, and a fresh watchtower works through the open cards it lists with no line of the node's after it (the review of
    /// PIL473).
    /// </summary>
    private bool IsPersons(Sessions.TranscriptBook.TurnPrompt prompt)
    {
        if (prompt.First)
        {
            return false;
        }
        var persons = prompt.Origin switch
        {
            "human" => true,
            "peer" => string.Equals(prompt.From, Sessions.ClaudeCliDelivery.ControllerName, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        var text = prompt.Text.TrimStart();
        if (!persons || text.StartsWith(NodeVoice.Prefix, StringComparison.Ordinal) || text.StartsWith(Sessions.SessionMessenger.FromWatchtowerPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var said = Fold(text);
        lock (_routed)
        {
            return !_routed.Any(r => said == r || r.Length >= MinWordsLength && said.Contains(r, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Whether <paramref name="why"/> is the person's message or quotes it: equal at any length (a one-word reply such as
    /// Allow), otherwise one holds the other on word boundaries and the shorter side is at least <see cref="MinWordsLength"/>
    /// long, so an ok or a yes never lets an unrelated reason through.
    /// </summary>
    private static bool Quotes(string said, string why)
    {
        if (said == why)
        {
            return true;
        }
        var (shorter, longer) = said.Length <= why.Length ? (said, why) : (why, said);
        if (shorter.Length < MinWordsLength)
        {
            return false;
        }
        for (var i = longer.IndexOf(shorter, StringComparison.Ordinal); i >= 0; i = longer.IndexOf(shorter, i + 1, StringComparison.Ordinal))
        {
            var before = i == 0 || !char.IsLetterOrDigit(longer[i - 1]);
            var after = i + shorter.Length == longer.Length || !char.IsLetterOrDigit(longer[i + shorter.Length]);
            if (before && after)
            {
                return true;
            }
        }
        return false;
    }

    private static string Fold(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool Live(SessionRow row) => row.State is SessionState.Working or SessionState.Waiting or SessionState.Idle;

    /// <summary>
    /// What there is to watch, in words, or null for nothing: a flight not landed, a job running or waiting, a ring open
    /// (other than about the watchtower itself).
    /// </summary>
    public string? Watching()
    {
        if (state.Flights().FirstOrDefault(f => f.State != FlightState.Landed) is { } flight)
        {
            return $"{flight.Code} is {(flight.State == FlightState.Held ? "held" : "in the air")}";
        }
        if (state.Jobs().FirstOrDefault(j => j.State is JobState.Running or JobState.Queued) is { } job)
        {
            return $"job {job.Id} is {(job.State == JobState.Running ? "running" : "waiting")}";
        }
        return state.Cards(openOnly: true).FirstOrDefault(c => c.Bin == CardBin.Ring && c.Subject != Verbs.WatchtowerTarget) is { } ring
            ? $"{ring.Id} rings"
            : null;
    }

    /// <summary>One look: keeps it running while there is something to watch, and tells it the cards it has not heard of.</summary>
    public async Task LookAsync(CancellationToken cancellationToken)
    {
        if (!options.Watchtower)
        {
            return;
        }
        await _one.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var row = Current();
            // Kept every look, so a row archived before anyone asked for the chat is still known by its session.
            Generations();
            KeepChurnCard(Health());
            var watching = Watching();
            if (watching is not null || _watchingAt is null)
            {
                _watchingAt = now;
            }
            if (!_adopted)
            {
                // A watchtower already running when the node starts saw the cards open now.
                _adopted = true;
                if (row is not null && Live(row))
                {
                    _job = row.Id;
                    TellAllOpen();
                }
            }
            // Every look: a watchtower of this node but the current, renamed or not, is stopped.
            await StopOthersAsync(row?.Id, null, cancellationToken);

            if (watching is null)
            {
                // Nothing to watch: none is started, and an idle one goes once it has been quiet long enough.
                if (row is { State: SessionState.Idle } && now - _watchingAt >= QuietFor)
                {
                    await StopQuietAsync(row, cancellationToken);
                }
                return;
            }

            if (row is { ApiError: { } error } && row.State != SessionState.Working)
            {
                await StoppedAsync(row, error, now, cancellationToken);
                return;
            }
            if (row is null || !Live(row))
            {
                if (_startedAt is { } started && now - started < ShowUpGrace && (row is null || row.Id != _job))
                {
                    return;
                }
                // One that ended (its turn over, or stopped when quiet) on the brief it has is woken only when there is something
                // to say to it: in place while its cache is warm, where the say resumes it; past that, a fresh one, below. Started
                // afresh after every ended turn instead, SE1 began 83 in a day (2026-10-25), each reading its brief again cold.
                if (row is { State: SessionState.Ended } && Briefed() == BriefFingerprint() && row.Id != _resumeRefused)
                {
                    if (Untold().Count == 0)
                    {
                        return;
                    }
                    if (Warm(row, now))
                    {
                        _job = row.Id;
                        if (await TellNewCardsAsync(row, cancellationToken) == false)
                        {
                            // Not delivered: the next look starts a fresh one rather than trying this one again.
                            _resumeRefused = row.Id;
                        }
                        return;
                    }
                }
                // Kept by the node by itself, so held while the 5-hour window is spent, and started by the next look once it has reset;
                // a restart asked for (restart-watchtower, or the card's Try now) is a person's, and goes ahead.
                if (now >= _nextTry && !Gate.Hold("watchtower", Models.StartLimit, Models.WeekLimit, Waits, Verbs.WatchtowerTarget, "The watchtower waits to start"))
                {
                    await StartAsync($"{watching}, and {(row is null ? "none was running" : $"the last one {row.State.ToString().ToLowerInvariant()}")}", cancellationToken);
                }
                return;
            }

            state.CloseCardsAbout(Verbs.WatchtowerTarget, "it is running", Down);
            if (Briefed() != BriefFingerprint())
            {
                // A say would resume it on its old brief: a fresh one is told the new. A start that failed is tried again
                // after the retry time, as any other start is, not stopped and started on every look.
                if (now >= _nextTry)
                {
                    await StartAsync("its brief changed since the node started it", cancellationToken, row);
                }
                return;
            }
            if (row.State == SessionState.Idle && Models.Model(RoleModels.Watchtower) is { } setting && Sessions.SessionModelCheck.ModelDiffers(setting, row.Model) && now >= _nextTry)
            {
                // Never mid-turn, and never by changing the model of the session: a fresh one on the setting.
                await StartAsync($"it ran on {row.Model}, the setting says {setting}", cancellationToken, row);
                return;
            }
            await TellNewCardsAsync(row, cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary><c>restart-watchtower</c>: a fresh watchtower in place of the one running, if any.</summary>
    public async Task<string?> RestartAsync(string why, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var row = Current();
            return await StartAsync(why, cancellationToken, row is not null && Live(row) ? row : null) ? null : "the watchtower did not start; its card says why";
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The target a <c>tell</c> goes to: the watchtower's row, or why there is none to tell.</summary>
    public (string? Target, string? Error) TellTarget()
    {
        var row = Current();
        return row is not null && (Live(row) || row.State == SessionState.Ended)
            ? (Verbs.SessionTarget(row.Id), null)
            : (null, options.Watchtower ? "the watchtower is not running; the node is starting one" : "this node keeps no watchtower (--no-watchtower)");
    }

    private async Task StoppedAsync(SessionRow row, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (row.ApiErrorResetsAt is not { } resets)
        {
            state.RaiseCard(CardBin.Ring, Stopped, Verbs.WatchtowerTarget, "The watchtower was stopped by the API", $"{error}, with no time it clears",
                [new CardChoice { Label = "Restart it", Verb = Verbs.RestartWatchtower, Target = Verbs.WatchtowerTarget, Args = ["stopped by the API"] }]);
            return;
        }
        if (now < resets + ResetMargin)
        {
            state.RaiseCard(CardBin.ForYou, Stopped, Verbs.WatchtowerTarget, $"The watchtower hit the usage limit; it resumes at {resets.ToLocalTime():HH:mm}",
                $"{error}; until then no card reaches it, so rings come straight to you");
            return;
        }
        if (_resumedAfter is { } done && done >= resets)
        {
            // Resumed once for this reset to no effect: a fresh one instead.
            await StartAsync("it stayed stopped after its limit reset", cancellationToken, row);
            return;
        }

        _resumedAfter = resets;
        var result = await run(new CommandRequest
        {
            Verb = Verbs.Say,
            Target = Verbs.SessionTarget(row.Id),
            Args = ["The usage limit that stopped you has reset. Carry on; `" + Pilot + " cards` shows what opened meanwhile."],
            Who = LegWatch.Who,
        }, cancellationToken);
        if (result.Ok)
        {
            state.CloseCardsAbout(Verbs.WatchtowerTarget, "resumed after the limit reset", Stopped);
            state.RaiseCard(CardBin.Handled, "watchtower-resumed", Verbs.WatchtowerTarget, "The watchtower resumed after its usage limit reset");
        }
    }

    /// <summary>
    /// The name the row this node started has now, when it is not <see cref="Name"/>: noted on the row as <c>renamed to
    /// &lt;name&gt;</c>: still the node's, so it is stopped, never left running. Null when it is under its own name, or gone.
    /// </summary>
    private string? NoteRename()
    {
        if (_job is not { } job || state.Sessions().FirstOrDefault(s => s.Id == job) is not { } row || row.Name == Name)
        {
            return null;
        }
        var because = $"renamed to {row.Name}";
        if (row.EndedBecause != because)
        {
            state.PutSession(row with { EndedBecause = because });
        }
        return row.Name;
    }

    /// <summary>Starts a fresh watchtower, stopping <paramref name="replacing"/> first; false, with a ring, when it could not.</summary>
    private async Task<bool> StartAsync(string why, CancellationToken cancellationToken, SessionRow? replacing = null)
    {
        if (!options.Watchtower)
        {
            return false;
        }
        if (NoteRename() is { } renamed)
        {
            why += $"; the one it started was renamed to {renamed}";
        }
        if (replacing is not null)
        {
            try
            {
                await launcher.StopAsync(replacing.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already: the fresh one starts all the same.
            }
            _stopped.Add(replacing.Id);
        }

        var folder = options.WatchtowerFolder;
        string fingerprint;
        try
        {
            Directory.CreateDirectory(folder);
            var brief = Briefs.Watchtower(Name, state.Flights(), state.Legs(), state.Cards(openOnly: true), Pilot, Models.Model(RoleModels.Watchtower), options.Machine);
            fingerprint = BriefFingerprint();
            _job = await launcher.StartAsync(new WorkerStart(Name, folder, null, brief, Model: Models.Model(RoleModels.Watchtower), Effort: Models.Effort(RoleModels.Watchtower), RemoteControl: true,
                // A background session's AskUserQuestion blocks until someone answers it in claude agents, and a say to the chat is read only between tool calls: it asks in an ask block instead.
                DisallowedTools: ["AskUserQuestion"]), cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _nextTry = time.GetUtcNow() + RetryAfter;
            var untrusted = ex.Message.Contains("not trusted", StringComparison.OrdinalIgnoreCase);
            state.RaiseCard(CardBin.Ring, Down, Verbs.WatchtowerTarget, "The watchtower could not be started",
                untrusted
                    ? $"Claude Code starts no background session in a folder never trusted. Run `claude` in {folder} once and accept the prompt (or start the node with --watchtower-folder <a trusted folder>); the node tries again every {RetryAfter.TotalMinutes:0} minutes."
                    : $"{ex.Message}; the node tries again in {RetryAfter.TotalMinutes:0} minutes",
                [new CardChoice { Label = "Try now", Verb = Verbs.RestartWatchtower, Target = Verbs.WatchtowerTarget, Args = ["tried again from its card"] }]);
            return false;
        }

        _startedAt = time.GetUtcNow();
        _resumedAfter = null;
        RememberStart(_job, why);
        KeepChurnCard(Health());
        RecordBrief(fingerprint);
        TellAllOpen();
        state.CloseCardsAbout(Verbs.WatchtowerTarget, "a fresh one started", Down, Stopped);
        state.RaiseCard(CardBin.Handled, "watchtower-started", Verbs.WatchtowerTarget, $"Started a fresh watchtower: {why}");
        await StopOthersAsync(_job, replacing?.Id, cancellationToken);
        return true;
    }

    /// <summary>
    /// One watchtower per node: stops every live background watchtower row but <paramref name="keep"/> (and the one just
    /// stopped, <paramref name="replaced"/>), and says which in one Handled line. An interactive session is someone's terminal, never stopped.
    /// </summary>
    private async Task StopOthersAsync(string? keep, string? replaced, CancellationToken cancellationToken)
    {
        // Known by id (IsWatchtower), whatever its name or role now: a renamed one is still the node's. Ones already stopped
        // wait for the tracker to show them ended, and are not stopped and reported again on every look.
        var others = state.Sessions()
            .Where(r => r.Kind == SessionKind.Background && IsWatchtower(r) && Live(r) && r.Id != keep && r.Id != replaced && !_stopped.Contains(r.Id))
            .OrderBy(r => r.StartedAt)
            .ToList();
        if (others.Count == 0)
        {
            return;
        }
        foreach (var other in others)
        {
            try
            {
                await launcher.StopAsync(other.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already: nothing to stop.
            }
            _stopped.Add(other.Id);
        }
        state.RaiseCard(CardBin.Handled, "watchtower-duplicate", Verbs.WatchtowerTarget,
            $"Stopped {(others.Count == 1 ? "another watchtower" : $"{others.Count} other watchtowers")} (one per node): {string.Join(", ", others.Select(r => $"{r.Name} ({r.Id})"))}");
    }

    /// <summary>Stops a watchtower with nothing left to watch, and says so; the next thing to watch starts a fresh one.</summary>
    private async Task StopQuietAsync(SessionRow row, CancellationToken cancellationToken)
    {
        try
        {
            await launcher.StopAsync(row.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Gone already: there is nothing to stop.
        }
        _stopped.Add(row.Id);
        _job = null;
        _startedAt = null;
        state.RaiseCard(CardBin.Handled, "watchtower-parked", Verbs.WatchtowerTarget,
            $"Stopped the watchtower: nothing to watch for {QuietFor.TotalMinutes:0} minutes; the next card resumes it within the hour, else starts a fresh one");
    }

    private string BriefFingerprint() => Briefs.WatchtowerFingerprint(Name, Pilot, Models.Model(RoleModels.Watchtower), options.Machine);

    /// <summary>The fingerprint of the brief the running watchtower was started with, or null when the node has no record of it.</summary>
    private string? Briefed()
    {
        if (!_briefedRead)
        {
            _briefedRead = true;
            try
            {
                _briefed = File.Exists(BriefFile) ? File.ReadAllText(BriefFile).Trim() : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable counts as no record: one fresh watchtower, rather than one on a brief nobody can vouch for.
            }
        }
        return _briefed;
    }

    private void RecordBrief(string fingerprint)
    {
        _briefed = fingerprint;
        _briefedRead = true;
        try
        {
            Directory.CreateDirectory(options.DataFolder);
            File.WriteAllText(BriefFile, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory all the same; a restarted node then finds no record and starts one afresh.
        }
    }

    /// <summary>Takes every open card as told: a fresh watchtower has them in its brief.</summary>
    private void TellAllOpen()
    {
        var now = time.GetUtcNow();
        foreach (var card in state.Cards(openOnly: true))
        {
            _told.TryAdd(card.Id, now);
        }
    }

    /// <summary>
    /// The open cards it has not heard of, oldest first: not its own, not Handled lines, not about itself; and once, a Ring
    /// card it was told <see cref="RetellAfter"/> ago that is still open.
    /// </summary>
    private List<CardRow> Untold()
    {
        var now = time.GetUtcNow();
        return state.Cards(openOnly: true)
            .Where(c => c.Bin != CardBin.Handled && c.Kind != RaisedKind && c.Subject != Verbs.WatchtowerTarget
                && (!_told.TryGetValue(c.Id, out var told) || c.Bin == CardBin.Ring && now - told >= RetellAfter && !_retold.Contains(c.Id)))
            .OrderBy(c => c.At)
            .ToList();
    }

    /// <summary>Whether an ended watchtower is resumed rather than replaced: its cache still warm, and room left in its window.</summary>
    private bool Warm(SessionRow row, DateTimeOffset now) =>
        row.LastResponseAt is { } last && now - last < CacheWarmFor
        && (row.ContextWindow <= 0 || row.ContextTokens < row.ContextWindow * ResumeBelow);

    /// <summary>
    /// Says the cards it has not heard of to it, in one message: null when there were none, else whether the say went through.
    /// </summary>
    private async Task<bool?> TellNewCardsAsync(SessionRow row, CancellationToken cancellationToken)
    {
        var fresh = Untold();
        if (fresh.Count == 0)
        {
            return null;
        }

        var text = (fresh.Count == 1 ? "A card opened:" : $"{fresh.Count} cards opened:") + "\n" + string.Join("\n", fresh.Select(Briefs.CardLine))
            + "\nThese are yours to see through: act on each (resolve a conflict, make a review's small fixes, redo a leg only for real work).";
        var result = await run(new CommandRequest { Verb = Verbs.Say, Target = Verbs.SessionTarget(row.Id), Args = [text], Who = LegWatch.Who }, cancellationToken);
        if (result.Ok)
        {
            var now = time.GetUtcNow();
            foreach (var card in fresh)
            {
                if (!_told.TryAdd(card.Id, now))
                {
                    _retold.Add(card.Id);
                }
            }
        }
        return result.Ok;
    }
}
