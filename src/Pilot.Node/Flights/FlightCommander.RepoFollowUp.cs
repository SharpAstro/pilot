using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

public sealed partial class FlightCommander
{
    [GeneratedRegex(@"github\.com[:/]([^/\s]+)/([^/\s]+?)(?:\.git)?/?\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRemote();

    /// <summary>The heading of the PR body's list of the follow-ups a flight recorded in other repos (PIL1340).</summary>
    internal const string FollowUpsHeading = "### Follow-ups in other repos";

    /// <summary>
    /// <c>flight-follow-up flight/&lt;code&gt; &lt;repo&gt; &lt;title&gt; &lt;body&gt; [--package]</c> (PIL1340): opens an issue in a sibling repo for a finding this flight
    /// cannot do itself, records it on the flight as soon as it exists, and brings an open PR's body up to date. The same repo and title again files nothing.
    /// </summary>
    private async Task<string> FollowUpAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = Find(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed: a follow-up is recorded while a flight flies, so file it as an issue of its own and fly that");
        }
        if (github is null)
        {
            throw new FlightException("this node has no GitHub to open an issue with");
        }
        var named = command.Args.ElementAtOrDefault(0)?.Trim() ?? "";
        var title = command.Args.ElementAtOrDefault(1)?.Trim() ?? "";
        var text = command.Args.ElementAtOrDefault(2) ?? "";
        var package = command.Args.Skip(3).Contains(Verbs.PackageFlag, StringComparer.Ordinal);
        if (named.Length == 0 || title.Length == 0)
        {
            throw new FlightException("a follow-up needs the repo (owner/name) and a title");
        }

        // The flight's own repo is known by its GitHub name (its origin) and by its folder, and both are refused.
        var origin = await GitHubNameOfAsync(flight.RepoPath, cancellationToken);
        var name = named[(named.LastIndexOf('/') + 1)..];
        var repo = named.Contains('/') ? named : origin is { } own ? $"{own[..own.IndexOf('/')]}/{named}" : throw new FlightException($"name the repo as owner/name, not {named}");
        if (string.Equals(repo, origin, StringComparison.OrdinalIgnoreCase) || string.Equals(name, flight.Repo, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlightException($"{repo} is {flight.Code}'s own repo: a same-repo finding is done in the flight itself (a leg, or a fix on its PR), never filed as a follow-up");
        }

        var already = flight.RepoFollowUps.FirstOrDefault(f => string.Equals(f.Repo, repo, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Title, title, StringComparison.Ordinal));
        if (already is not null)
        {
            return $"{flight.Code} already recorded {already.Repo}#{already.Issue.ToString(CultureInfo.InvariantCulture)} for this: nothing filed";
        }

        var org = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(flight.RepoPath)) ?? "";
        var path = Path.Combine(org, name);
        if (!github.IsCloned(path))
        {
            throw new FlightException($"{repo} is not cloned beside {flight.Code}'s repo at {path}: clone it there, so the issue is opened from it");
        }
        var ours = origin ?? flight.Repo;
        var pr = flight.PullRequest is { } n ? $", its PR {ours}#{n.ToString(CultureInfo.InvariantCulture)}" : "";
        var issue = await github.CreateIssueWithLabelsAsync(path, title,
            $"Follow-up of flight {flight.Code} ({ours}#{flight.Issue.ToString(CultureInfo.InvariantCulture)}{pr}), which cannot make this change in its own repo{(package ? " and needs it as a published package" : "")}.\n\n{text}".TrimEnd(),
            [DeferredRoutes.FrameworkLabel], cancellationToken);

        // Recorded as soon as the issue exists, so a failure after it never opens it twice.
        var row = new CrossRepoFollowUp { Repo = repo, Issue = issue.Number, Title = title, Package = package, At = time.GetUtcNow() };
        var recorded = state.UpdateFlight(flight.Code, f => f with { RepoFollowUps = [.. f.RepoFollowUps, row] }) ?? flight with { RepoFollowUps = [.. flight.RepoFollowUps, row] };
        await RefreshFollowUpsInPrAsync(recorded, cancellationToken);
        return $"{flight.Code} recorded {repo}#{issue.Number.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The flight's own repo as <c>owner/name</c> from its origin; null where it has none or it is not on GitHub.</summary>
    private async Task<string?> GitHubNameOfAsync(string repoPath, CancellationToken cancellationToken)
    {
        var run = await planes.Git.RunAsync(["remote", "get-url", "origin"], repoPath, cancellationToken);
        return run.Ok && GitHubRemote().Match(run.Output.Trim()) is { Success: true } m ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
    }

    /// <summary>An open PR's body lists a follow-up recorded after it opened; GitHub's refusal is said on stderr and never refuses the record.</summary>
    private async Task RefreshFollowUpsInPrAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (flight.PullRequest is not { } number || github is null)
        {
            return;
        }
        try
        {
            if (await github.PullRequestBodyAsync(flight.RepoPath, number, cancellationToken) is { Length: > 0 } body && PrBodyWithFollowUps(body, flight.RepoFollowUps) is { } next)
            {
                await github.EditPullRequestBodyAsync(flight.RepoPath, number, next, cancellationToken);
            }
        }
        catch (FlightException ex)
        {
            await Error.WriteLineAsync($"follow-up: {flight.Code}'s PR #{number.ToString(CultureInfo.InvariantCulture)} body was not given the follow-up: {ex.Message}");
        }
    }

    /// <summary>The section of a PR body that lists <paramref name="followUps"/> (heading and one line each); empty for none.</summary>
    internal static string PrFollowUpsSection(IReadOnlyList<CrossRepoFollowUp> followUps) =>
        followUps.Count == 0 ? "" : $"{FollowUpsHeading}\n{string.Concat(followUps.Select(f => $"- {f.Line()}\n"))}";

    /// <summary>
    /// The body with its follow-ups section as <paramref name="followUps"/> reads now (added at the end when there is none, its list of
    /// lines replaced when there is); null when nothing differs. Every other line, a person's own included, is kept.
    /// </summary>
    internal static string? PrBodyWithFollowUps(string body, IReadOnlyList<CrossRepoFollowUp> followUps)
    {
        if (followUps.Count == 0)
        {
            return null;
        }
        var section = PrFollowUpsSection(followUps);
        var plain = body.Replace("\r\n", "\n");
        var lines = plain.Split('\n').ToList();
        var at = lines.FindIndex(l => l.TrimEnd() == FollowUpsHeading);
        if (at < 0)
        {
            return $"{plain.TrimEnd()}\n\n{section}";
        }
        var end = at + 1;
        while (end < lines.Count && lines[end].StartsWith("- ", StringComparison.Ordinal))
        {
            end++;
        }
        lines.RemoveRange(at, end - at);
        lines.InsertRange(at, section.TrimEnd('\n').Split('\n'));
        var next = string.Join('\n', lines);
        return next == plain ? null : next;
    }
}
