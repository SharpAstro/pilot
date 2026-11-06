using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// Where a failure was seen: the repo and PR it hit, the flight if there is one, and the head and place it failed at.
/// </summary>
/// <param name="Repo">The repo's folder name, or <c>owner/name</c> (its last part is kept).</param>
/// <param name="RepoPath">A checkout of the repo to ask GitHub through; the issue is filed there.</param>
/// <param name="Pr">The PR it hit; null for a leg check that ran before its flight had one.</param>
/// <param name="Flight">The flight whose PR or leg it hit; null for a hand PR.</param>
/// <param name="Head">The head or queue entry it failed at.</param>
/// <param name="Where">A <see cref="FailureWhere"/>.</param>
/// <param name="PrUrl">The PR's page, to link the issue's page from; may be null.</param>
public sealed record FailureSite(string Repo, string RepoPath, int? Pr, string? Flight, string Head, string Where, string? PrUrl = null)
{
    /// <summary>The repo by its folder name, the key a signature is kept under.</summary>
    public string Name => Repo[(Repo.LastIndexOfAny(['/', '\\']) + 1)..];
}

/// <summary>
/// A failure in a test the PR does not touch is a bug with a cause to find, never chance: filed at once, from its first occurrence,
/// as an investigation (an issue of priority high carrying the run, the test, its assertion message and the PRs it hit, and a flight
/// started on it) and a For-you card. One investigation per signature (<see cref="FailureSignature"/>): a later occurrence comments
/// its run and PR on that issue. Looking never throws (bar a cancellation) and never stops the look that called it: a read GitHub
/// would not answer files nothing.
/// </summary>
public sealed class FailureInvestigations(NodeState state, IGitHub github, FailureLedger ledger, TimeProvider time)
{
    /// <summary>The For-you card that names an investigation made for a failure.</summary>
    public const string CardKind = "failure-investigation";

    // The most investigations one run opens: a run that fails dozens of tests has one cause behind most of them.
    private const int MostPerRun = 5;

    /// <summary>Where this says what it would have said on stderr; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>Makes the flight of an investigation (create-flight, as a person's would); the node sets it to its flights. Null makes none.</summary>
    public Func<CommandRequest, CancellationToken, Task<CommandResult>>? CreateFlight { get; set; }

    /// <summary>The failures kept so far.</summary>
    public FailureLedger Ledger => ledger;

    /// <summary>
    /// A PR's failed run, read now (a PR's own checks, or with <paramref name="mergeGroup"/> its merge queue run) and looked into. Called
    /// before the failed jobs are run again, which replaces their logs.
    /// </summary>
    public async Task LookAtRunAsync(FailureSite site, bool mergeGroup, CancellationToken cancellationToken)
    {
        try
        {
            if (site.Pr is not { } number || await github.FailedRunAsync(site.RepoPath, number, mergeGroup, cancellationToken) is not { } run)
            {
                return;
            }
            await LookAsync(site, run.Id, run.Url, run.FailedJobs, run.Log, () => github.PullRequestFilesAsync(site.RepoPath, number, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Error.WriteLineAsync($"failures: {site.Name} #{site.Pr}: could not look into the failed run: {ex.Message}");
        }
    }

    /// <summary>A leg's check that failed, looked into from its output; <paramref name="changedFiles"/> says what the leg's work changes.</summary>
    public async Task LookAtCheckAsync(FailureSite site, string output, Func<Task<string[]>> changedFiles, CancellationToken cancellationToken)
    {
        try
        {
            await LookAsync(site, 0, null, [], output, changedFiles, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Error.WriteLineAsync($"failures: {site.Name}: could not look into the failed check: {ex.Message}");
        }
    }

    /// <summary>One failure found in a log, ready to be filed.</summary>
    private sealed record Found(string Test, string Message, string Signature);

    private async Task LookAsync(FailureSite site, long runId, string? runUrl, string[] jobs, string log, Func<Task<string[]>> changedFiles, CancellationToken cancellationToken)
    {
        var tests = FailureSignature.Tests(log);
        var found = new List<Found>();
        if (tests.Count > 0)
        {
            found.AddRange(tests.Select(t => new Found(t.Test, t.Message, FailureSignature.Of(t))));
        }
        else
        {
            // No failed test: a build the PR broke is its own to fix; any other error is the same failure when its line reads the same.
            if (FailureSignature.IsBuildError(log) || FailureSignature.ForError(log) is not { } signature)
            {
                return;
            }
            found.Add(new Found("", FailureSignature.ErrorText(log) ?? signature, signature));
        }
        string[] files;
        try
        {
            files = await changedFiles();
        }
        catch (Exception ex) when (ex is FlightException or IOException)
        {
            // Which files the PR changes is not known: nothing says the test is not the PR's.
            await Error.WriteLineAsync($"failures: {site.Name}: the changed files could not be read, so nothing was filed: {ex.Message}");
            return;
        }
        var untouched = found.Where(f => f.Test.Length == 0 || !FailureSignature.Touches(f.Test, files))
            .GroupBy(f => f.Signature, StringComparer.Ordinal).Select(g => g.First()).Take(MostPerRun).ToList();
        foreach (var failure in untouched)
        {
            if (ledger.Has(site.Name, site.Pr, site.Flight, site.Head, site.Where, failure.Signature))
            {
                continue;
            }
            await FileAsync(site, runId, runUrl, jobs, failure, cancellationToken);
        }
    }

    private async Task FileAsync(FailureSite site, long runId, string? runUrl, string[] jobs, Found failure, CancellationToken cancellationToken)
    {
        var existing = ledger.InvestigationOf(site.Name, failure.Signature);
        var run = runId > 0 ? $"run {runId}{(runUrl is { Length: > 0 } ? $" ({runUrl})" : "")}" : "its leg check";
        if (existing is not null)
        {
            try
            {
                await github.CommentIssueAsync(site.RepoPath, existing.Issue,
                    $"Seen again in {Place(site)}: {run}, {WhereWords(site.Where)}.\n\nAssertion message: {failure.Message}", cancellationToken);
            }
            catch (FlightException ex)
            {
                await Error.WriteLineAsync($"failures: could not comment on {site.Name}#{existing.Issue}: {ex.Message}");
                return;
            }
            Keep(site, runId, failure, existing.Investigation, existing.Issue);
            return;
        }
        var title = failure.Test.Length > 0
            ? $"{FailureSignature.ShortOf(FailureSignature.NameOf(failure.Test))} fails intermittently, cause unknown"
            : $"{LegCommander.OneLine(failure.Message)}: fails intermittently, cause unknown";
        IssueInfo issue;
        try
        {
            issue = await github.CreateIssueAsync(site.RepoPath, title, Body(site, run, jobs, failure), cancellationToken);
        }
        catch (FlightException ex)
        {
            await Error.WriteLineAsync($"failures: could not file the investigation of {title}: {ex.Message}");
            return;
        }
        var notes = new List<string>();
        try
        {
            await github.SetIssuePriorityAsync(site.RepoPath, issue.Number, IssuePriority.High, cancellationToken);
        }
        catch (FlightException ex)
        {
            notes.Add($"its priority could not be set to high: {ex.Message}");
        }
        var code = "";
        if (CreateFlight is { } create)
        {
            try
            {
                var made = await create(new CommandRequest
                {
                    Verb = Verbs.CreateFlight,
                    Target = Verbs.RepoTarget(site.RepoPath),
                    Args = [issue.Number.ToString(CultureInfo.InvariantCulture), title],
                    Who = "node",
                }, cancellationToken);
                if (made is { Ok: true, Made: { Length: > 0 } flight })
                {
                    code = flight;
                }
                else
                {
                    notes.Add($"its flight was refused: {made.Error ?? "no reason given"}");
                }
            }
            catch (FlightException ex)
            {
                notes.Add($"its flight was refused: {ex.Message}");
            }
        }
        Keep(site, runId, failure, code, issue.Number);
        var test = failure.Test.Length > 0 ? FailureSignature.ShortOf(FailureSignature.NameOf(failure.Test)) : LegCommander.OneLine(failure.Message);
        var subject = code.Length > 0 ? Verbs.FlightTarget(code) : $"{Verbs.RepoTargetPrefix}{site.RepoPath}";
        state.RaiseCard(CardBin.ForYou, CardKind, subject,
            $"{site.Name}#{issue.Number}: {test} fails intermittently in a PR that does not touch it, {(code.Length > 0 ? $"{code} investigates" : "no flight could be made for it")}",
            $"{Place(site)}: {run}, {WhereWords(site.Where)}.\n\nTest: {(failure.Test.Length > 0 ? failure.Test : "none, an error line")}\nAssertion message: {failure.Message}\n\nIt is a bug with a cause to find, filed as {site.Name}#{issue.Number} with priority high."
            + (notes.Count > 0 ? $"\n\nNot done: {string.Join("; ", notes)}." : ""),
            link: IssueLink(site, issue.Number));
    }

    private static string Body(FailureSite site, string run, string[] jobs, Found failure) =>
        $"""
        A test the PR does not touch failed. This is a bug with a cause to find.

        - Run: {run}
        - Seen: {WhereWords(site.Where)}{(jobs.Length > 0 ? $", in {string.Join(", ", jobs)}" : "")}
        - Test: {(failure.Test.Length > 0 ? failure.Test : "none, an error line")}
        - Assertion message: {failure.Message}
        - PRs hit: {Place(site)}

        Later occurrences of this failure are added to this issue as comments.
        """;

    private static string Place(FailureSite site) =>
        (site.Pr, site.Flight) switch
        {
            ({ } pr, { Length: > 0 } flight) => $"{site.Name}#{pr} ({flight})",
            ({ } pr, _) => $"{site.Name}#{pr}",
            (_, { Length: > 0 } flight) => $"{flight}'s work, before it had a PR",
            _ => site.Name,
        };

    private static string WhereWords(string where) => where switch
    {
        FailureWhere.Queue => "in the merge queue",
        FailureWhere.LegCheck => "in a leg check",
        _ => "in the PR's checks",
    };

    private static string? IssueLink(FailureSite site, int number) =>
        site.PrUrl is { } url && url.LastIndexOf("/pull/", StringComparison.Ordinal) is var at and > 0
            ? $"{url[..at]}/issues/{number.ToString(CultureInfo.InvariantCulture)}" : null;

    private void Keep(FailureSite site, long runId, Found failure, string investigation, int issue) =>
        ledger.Add(new FailureRecord(site.Name, site.Pr, site.Flight, site.Head, site.Where, runId, failure.Test, failure.Message, failure.Signature, investigation, issue, time.GetUtcNow()));
}
