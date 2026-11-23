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
    [GeneratedRegex(@"^[^\t]*\t[^\t]*\t\d{4}-\d\d-\d\dT[\d:.]+Z ?", RegexOptions.CultureInvariant)]
    private static partial Regex LinePrefix();

    /// <summary>
    /// The verdict of a failed run's <paramref name="log"/>: null unless it carries the no-model marker on a line of its own output (not
    /// the script's text, not a diff line); otherwise the CLI's reason and the next reset time after <paramref name="now"/>.
    /// </summary>
    public static NoModelReview? Read(string? log, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(log))
        {
            return null;
        }
        foreach (var raw in log.Split('\n'))
        {
            var line = LinePrefix().Replace(raw.TrimEnd('\r'), "").Trim();
            if (line.Length == 0 || line[0] is '+' or '-' or '>' || !Marker().IsMatch(line))
            {
                continue;
            }
            var said = CliSaid().Match(line) is { Success: true } m ? m.Groups["said"].Value : "";
            var reason = said.Length == 0 ? "the run ended before the model answered" : said.Split(" · ", 2)[0].Trim();
            return new NoModelReview(reason, said, ResetAfter(said, now));
        }
        return null;
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

/// <summary>What <see cref="OutsideReviews"/> knows of a run: <see cref="Known"/> false when gh could not say (ask again), else the verdict or null for a real failure.</summary>
public readonly record struct OutsideReviewAnswer(bool Known, NoModelReview? NoModel)
{
    public static OutsideReviewAnswer Unknown { get; } = new(false, null);
}

/// <summary>
/// The node's memory of which failed runs never reached a model. A completed run's log does not change, so each run id's log is read once
/// and the answer kept; a read gh could not make is not kept and is asked again at the next look, and is never read as no-model.
/// </summary>
public sealed class OutsideReviews(IGitHub github, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<long, NoModelReview?> _answers = [];
    private readonly Lock _lock = new();

    public async Task<OutsideReviewAnswer> AskAsync(string repoPath, long runId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_answers.TryGetValue(runId, out var known))
            {
                return new OutsideReviewAnswer(true, known);
            }
        }
        string? log;
        try
        {
            log = await github.RunFailedLogAsync(repoPath, runId, cancellationToken);
        }
        catch (Exception ex) when (ex is FlightException or System.ComponentModel.Win32Exception or IOException)
        {
            return OutsideReviewAnswer.Unknown;
        }
        if (log is null)
        {
            return OutsideReviewAnswer.Unknown;
        }
        var verdict = OutsideReview.Read(log, _time.GetUtcNow());
        lock (_lock)
        {
            _answers[runId] = verdict;
        }
        return new OutsideReviewAnswer(true, verdict);
    }
}
