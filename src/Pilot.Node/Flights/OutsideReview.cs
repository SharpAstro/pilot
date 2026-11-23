using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// An outside review job (a workflow that asks a model to review a PR) that ended before the model answered.
/// <paramref name="Reason"/> is the CLI's first sentence (for example "You've hit your individual spend limit"), <paramref name="Said"/> all of
/// what the CLI said, and <paramref name="ResetsAt"/> the next UTC instant the log says the limit resets, when it says one (pilot #1283).
/// </summary>
public sealed record NoModelReview(string Reason, string Said, DateTimeOffset? ResetsAt);

/// <summary>Reads a failed outside review run's log: a review that never reached a model is not a failed check of the PR.</summary>
public static partial class OutsideReview
{
    // The workflow's own words (chrome-review.yml's count step): the count is a number in the log, and the script text that quotes the
    // sentence carries "${turns:-0}" instead, so quoting the words never reads as the marker.
    [GeneratedRegex(@"the run ended before the model answered \(\d+ turn\(s\), no tokens used\)", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    [GeneratedRegex(@"the CLI said:\s*(?<said>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CliSaid();

    [GeneratedRegex(@"resets\s+(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<half>am|pm)\s*\(UTC\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reset();

    // gh run view --log-failed: "<job>\t<step>\t<timestamp>Z <text>".
    [GeneratedRegex(@"^(?<job>[^\t]*)\t[^\t]*\t(?<stamp>\d{4}-\d\d-\d\dT[\d:.]+Z) ?", RegexOptions.CultureInvariant)]
    private static partial Regex LinePrefix();

    /// <summary>
    /// The verdict of a failed run's <paramref name="log"/> as a whole: null unless every job its lines name never reached a model
    /// (<see cref="RunReview.Run"/> over <see cref="Jobs"/>); otherwise the first such job's reason and reset time.
    /// </summary>
    public static NoModelReview? Read(string? log, DateTimeOffset now) => Jobs(log, now).Run();

    /// <summary>
    /// Each job a failed run's <paramref name="log"/> names (its lines' first column, the job's name as its check shows it) with the job's own
    /// verdict: null unless one of that job's lines carries the no-model marker as its own output (not the script's text, not a diff line);
    /// otherwise the CLI's reason and the next reset time after the line's own time (<paramref name="now"/> when the line has none). A line with no job column belongs to the job "".
    /// </summary>
    public static RunReview Jobs(string? log, DateTimeOffset now)
    {
        var jobs = new Dictionary<string, NoModelReview?>(StringComparer.Ordinal);
        foreach (var raw in (log ?? "").Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            var prefix = LinePrefix().Match(text);
            if (!prefix.Success && text.Trim().Length == 0)
            {
                continue;
            }
            var job = prefix.Success ? prefix.Groups["job"].Value : "";
            var line = (prefix.Success ? text[prefix.Length..] : text).Trim();
            if (!jobs.TryGetValue(job, out var known))
            {
                jobs[job] = known = null;
            }
            if (known is not null || line.Length == 0 || line[0] is '+' or '-' or '>' || !Marker().IsMatch(line))
            {
                continue;
            }
            var said = CliSaid().Match(line) is { Success: true } m ? m.Groups["said"].Value : "";
            var reason = said.Length == 0 ? "the run ended before the model answered" : said.Split(" · ", 2)[0].Trim();
            // The reset is the next one after the line's own time, so a log read again later (after a node restart) names the same instant;
            // a line with no readable time falls back to the time of the read.
            var stamped = prefix.Success && DateTimeOffset.TryParse(prefix.Groups["stamp"].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : now;
            jobs[job] = new NoModelReview(reason, said, ResetAfter(said, stamped));
        }
        return new RunReview(jobs);
    }

    /// <summary>The next UTC instant after <paramref name="now"/> that <paramref name="text"/> names ("resets 10pm (UTC)"); null when absent or unparseable.</summary>
    public static DateTimeOffset? ResetAfter(string text, DateTimeOffset now)
    {
        var m = Reset().Match(text);
        if (!m.Success
            || !int.TryParse(m.Groups["hour"].Value, System.Globalization.CultureInfo.InvariantCulture, out var hour) || hour is < 1 or > 12
            || !int.TryParse(m.Groups["minute"].Success ? m.Groups["minute"].Value : "0", System.Globalization.CultureInfo.InvariantCulture, out var minute) || minute > 59)
        {
            return null;
        }
        hour = hour % 12 + (m.Groups["half"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        var utc = now.ToUniversalTime();
        var at = new DateTimeOffset(utc.Year, utc.Month, utc.Day, hour, minute, 0, TimeSpan.Zero);
        return at > utc ? at : at.AddDays(1);
    }
}

/// <summary>
/// What one attempt of a failed run's log says, job by job (<see cref="OutsideReview.Jobs"/>): each job its lines name, with its verdict (null
/// for a job whose lines say nothing of a model that never answered: a real failure, a review with findings).
/// </summary>
public sealed class RunReview(IReadOnlyDictionary<string, NoModelReview?> jobs)
{
    /// <summary>Each job the log names, with its verdict.</summary>
    public IReadOnlyDictionary<string, NoModelReview?> Jobs { get; } = jobs;

    /// <summary>The verdict of the failed job (or check) named <paramref name="job"/>: null when the log names it without the marker, or not at all.</summary>
    public NoModelReview? Job(string job) => Jobs.GetValueOrDefault(job);

    /// <summary>
    /// The verdict of the run: no-model only when it has a failed job and every one never reached a model, then the first one's verdict.
    /// The failed jobs are <paramref name="failedJobs"/> when the caller knows them (the run's jobs answer, which also names a job that
    /// failed without a failed step's log), else the jobs the log names. A no-model review beside a build that failed is a real failure.
    /// </summary>
    public NoModelReview? Run(IReadOnlyCollection<string>? failedJobs = null)
    {
        NoModelReview? first = null;
        foreach (var job in failedJobs ?? (IEnumerable<string>)Jobs.Keys)
        {
            if (Job(job) is not { } review)
            {
                return null;
            }
            first ??= review;
        }
        return first;
    }
}

/// <summary>What <see cref="OutsideReviews"/> knows of a failed job or run: <see cref="Known"/> false when gh could not say (ask again), else the verdict or null for a real failure.</summary>
public readonly record struct OutsideReviewAnswer(bool Known, NoModelReview? NoModel)
{
    public static OutsideReviewAnswer Unknown { get; } = new(false, null);
}

/// <summary>
/// The node's memory of which failed jobs never reached a model, keyed by run id, <c>run_attempt</c> and job. A completed attempt's log does not
/// change, so each run's log is read once per attempt (a rerun, by anyone, keeps the id and gets the next attempt and a new log), and what each
/// of its jobs said is kept. Both callers read the attempt from the same REST field, so one attempt is one read whichever path asks first, and
/// the failed jobs of one run share it. A read gh could not make is not kept and is asked again at the next look, and is never read as no-model.
/// </summary>
public sealed class OutsideReviews(IGitHub github, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // Per run and attempt, the verdict of each job its failed log names.
    private readonly Dictionary<(long RunId, int Attempt), RunReview> _runs = [];

    // Per job id (a check's /job/<id>): the attempt of its run it belongs to. A rerun makes new jobs with new ids, so the pair never changes.
    private readonly Dictionary<long, int> _attempts = [];
    private readonly Lock _lock = new();

    // When each entry above was last read or written, so a write can drop what has sat unused past Keep.
    private readonly Dictionary<(long RunId, int Attempt), DateTimeOffset> _runsUsed = [];
    private readonly Dictionary<long, DateTimeOffset> _attemptsUsed = [];

    /// <summary>
    /// How long an unused entry is kept. A day is past the 15 hours a no-reset run's reruns span, so an entry in use is never dropped; and a dropped
    /// entry costs only a read from gh again, never a wrong answer.
    /// </summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - Keep;
        foreach (var key in _runsUsed.Where(e => e.Value < cutoff).Select(e => e.Key).ToList())
        {
            _runsUsed.Remove(key);
            _runs.Remove(key);
        }
        foreach (var key in _attemptsUsed.Where(e => e.Value < cutoff).Select(e => e.Key).ToList())
        {
            _attemptsUsed.Remove(key);
            _attempts.Remove(key);
        }
    }

    /// <summary>What is known of attempt <paramref name="attempt"/> of run <paramref name="runId"/> without reading anything: false when its log was never read.</summary>
    public bool TryKnown(long runId, int attempt, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RunReview? review)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue((runId, attempt), out review))
            {
                _runsUsed[(runId, attempt)] = _time.GetUtcNow();
                return true;
            }
            return false;
        }
    }

    /// <summary>The verdict of failed job (or check) <paramref name="job"/> of attempt <paramref name="attempt"/> of run <paramref name="runId"/>, by its own lines of the run's log.</summary>
    public async Task<OutsideReviewAnswer> AskJobAsync(string repoPath, long runId, int attempt, string job, CancellationToken cancellationToken) =>
        await ReadAsync(repoPath, runId, attempt, cancellationToken) is { } review ? new OutsideReviewAnswer(true, review.Job(job)) : OutsideReviewAnswer.Unknown;

    /// <summary>The verdict of attempt <paramref name="attempt"/> of run <paramref name="runId"/> as a whole: <see cref="RunReview.Run"/> over <paramref name="failedJobs"/>.</summary>
    public async Task<OutsideReviewAnswer> AskRunAsync(string repoPath, long runId, int attempt, IReadOnlyCollection<string>? failedJobs, CancellationToken cancellationToken) =>
        await ReadAsync(repoPath, runId, attempt, cancellationToken) is { } review ? new OutsideReviewAnswer(true, review.Run(failedJobs)) : OutsideReviewAnswer.Unknown;

    private async Task<RunReview?> ReadAsync(string repoPath, long runId, int attempt, CancellationToken cancellationToken)
    {
        if (TryKnown(runId, attempt, out var known))
        {
            return known;
        }
        string? log;
        try
        {
            log = await github.RunFailedLogAsync(repoPath, runId, cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
        if (log is null)
        {
            return null;
        }
        var review = OutsideReview.Jobs(log, _time.GetUtcNow());
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            _runs[(runId, attempt)] = review;
            _runsUsed[(runId, attempt)] = now;
        }
        return review;
    }

    /// <summary>
    /// The <c>run_attempt</c> of run <paramref name="runId"/> that its failed check's job <paramref name="jobId"/> belongs to: asked of GitHub
    /// once per job id, since a job id names one attempt, and every look when the check names no job. Null when gh could not say; a gh
    /// failure is thrown as <see cref="IGitHub.RunAttemptAsync"/> throws it.
    /// </summary>
    public async Task<int?> AttemptAsync(string repoPath, long runId, long? jobId, CancellationToken cancellationToken)
    {
        if (jobId is { } job)
        {
            lock (_lock)
            {
                if (_attempts.TryGetValue(job, out var known))
                {
                    _attemptsUsed[job] = _time.GetUtcNow();
                    return known;
                }
            }
        }
        var attempt = await github.RunAttemptAsync(repoPath, runId, cancellationToken);
        if (attempt is { } asked && jobId is { } id)
        {
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                Prune(now);
                _attempts[id] = asked;
                _attemptsUsed[id] = now;
            }
        }
        return attempt;
    }

    /// <summary>Forgets what the log of <paramref name="runId"/> said, every attempt of it: a run run again keeps its id and gets a new log.</summary>
    public void Forget(long runId)
    {
        lock (_lock)
        {
            foreach (var key in _runs.Keys.Where(k => k.RunId == runId).ToList())
            {
                _runs.Remove(key);
                _runsUsed.Remove(key);
            }
        }
    }
}
