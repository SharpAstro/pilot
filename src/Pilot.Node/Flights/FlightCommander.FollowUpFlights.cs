using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// What a landing does with the follow-ups the flight recorded in other repos (PIL1340): each gets a flight of its own, with no one touching it.
/// Local ones are made before the landing reads its consumers, connected to the landing flight, so the release and PackageWait path that
/// covers any connected flight covers them; ones only a fleet node holds are made on that node once the flight has landed.
/// </summary>
public sealed partial class FlightCommander
{
    /// <summary>The card kind raised for a follow-up on another node that needs a package this landing released: its wait cannot be set from here.</summary>
    public const string FollowUpPackageKind = "follow-up-package";

    [GeneratedRegex(@"already has (\S+?),", RegexOptions.CultureInvariant)]
    private static partial Regex AlreadyHas();

    private static bool Same(CrossRepoFollowUp a, CrossRepoFollowUp b) =>
        string.Equals(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase) && a.Issue == b.Issue;

    private string FollowUpPath(FlightRow flight, CrossRepoFollowUp followUp) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(flight.RepoPath)) ?? "", followUp.Repo[(followUp.Repo.LastIndexOf('/') + 1)..]);

    /// <summary>Says what went wrong with a follow-up's flight on stderr and on its row; the landing goes on, and the next try makes it.</summary>
    private async Task FollowUpFailedAsync(FlightRow landing, CrossRepoFollowUp followUp, string why)
    {
        await Error.WriteLineAsync($"follow-up: {landing.Code}'s {followUp.Repo}#{followUp.Issue.ToString(CultureInfo.InvariantCulture)} has no flight yet: {why}");
        SetFollowUp(landing.Code, followUp, f => f with { Problem = LegCommander.OneLine(why) });
    }

    private void SetFollowUp(string code, CrossRepoFollowUp followUp, Func<CrossRepoFollowUp, CrossRepoFollowUp> change) =>
        state.UpdateFlight(code, f => f with { RepoFollowUps = [.. f.RepoFollowUps.Select(x => Same(x, followUp) ? change(x) : x)] });

    /// <summary>
    /// Makes a flight on the issue of each follow-up of <paramref name="landing"/> that has none and whose repo is cloned here with no other
    /// node's label on the issue, connected to <paramref name="landing"/>. A flight already on the issue (here or on a peer) is recorded, never
    /// doubled. Every failure is said and left for the next try. Returns the codes of the flights made here for a follow-up needed as a package.
    /// </summary>
    private async Task<HashSet<string>> EnsureLocalFollowUpFlightsAsync(FlightRow landing, CancellationToken cancellationToken)
    {
        var packaged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var followUp in landing.RepoFollowUps.Where(f => f.Flight is null).ToList())
        {
            try
            {
                if (await EnsureLocalAsync(landing, followUp, cancellationToken) is { } made && followUp.Package)
                {
                    packaged.Add(made);
                }
            }
            catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or HttpRequestException or System.ComponentModel.Win32Exception)
            {
                await FollowUpFailedAsync(landing, followUp, ex.Message);
            }
        }
        // Those made on an earlier try and not yet released are forced the same.
        foreach (var f in state.Flights().First(f => f.Code == landing.Code).RepoFollowUps.Where(f => f is { Package: true, Flight: { } code } && f.Node == Machine))
        {
            packaged.Add(f.Flight!);
        }
        return packaged;
    }

    /// <summary>The code of the flight made here (or null when it is a peer's or none could be made).</summary>
    private async Task<string?> EnsureLocalAsync(FlightRow landing, CrossRepoFollowUp followUp, CancellationToken cancellationToken)
    {
        var path = FollowUpPath(landing, followUp);
        var name = followUp.Repo[(followUp.Repo.LastIndexOf('/') + 1)..];
        var item = new WorkItem(TrackerKind.GitHub, followUp.Issue);
        var live = state.Flights().FirstOrDefault(f => f.State != FlightState.Landed && f.Issue == followUp.Issue && f.Tracker == TrackerKind.GitHub
            && string.Equals(f.Repo, name, StringComparison.OrdinalIgnoreCase));
        if (live is not null)
        {
            SetFollowUp(landing.Code, followUp, f => f with { Flight = live.Code, Node = Machine, Problem = null });
            return null;
        }
        if (github is null || !github.IsCloned(path))
        {
            return null;
        }
        if (await FlownByPeerAsync(path, item, "", cancellationToken) is { } peerFlight)
        {
            SetFollowUp(landing.Code, followUp, f => f with { Flight = peerFlight.Code, Node = peerFlight.Node, Problem = null });
            return null;
        }
        if (await OtherNodeOfAsync(path, item, cancellationToken) is not null)
        {
            // Another node's label: a fleet node makes it once the flight has landed.
            return null;
        }
        var result = await CreateOneAtATimeAsync(new CommandRequest
        {
            Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(path), Who = "land", Args = [item.Display, followUp.Title],
        }, cancellationToken);
        if (result is not { Ok: true, Made: { Length: > 0 } code })
        {
            if (result.Error is { } error && AlreadyHas().Match(error) is { Success: true } has)
            {
                SetFollowUp(landing.Code, followUp, f => f with { Flight = has.Groups[1].Value, Node = Machine, Problem = null });
                return null;
            }
            throw new FlightException(result.Error ?? "the create made no flight");
        }
        // Recorded as soon as it exists, and connected to the landing flight so its landing releases it (and sets its package wait).
        SetFollowUp(landing.Code, followUp, f => f with { Flight = code, Node = Machine, Problem = null });
        if (landing.State == FlightState.Landed)
        {
            // Nothing to wait for any more, and the package wait cannot be read at a head that is gone.
            if (followUp.Package)
            {
                RaiseFollowUpPackageCard(landing, followUp, code, Machine, []);
            }
            await CommentFollowUpAsync(path, followUp, landing, code, Machine);
            return null;
        }
        if (state.Flights().FirstOrDefault(f => f.Code == code) is { } made)
        {
            state.PutFlight(Settle(made with { Connections = [.. made.Connections, landing.Code] }));
        }
        await CommentFollowUpAsync(path, followUp, landing, code, Machine);
        return code;
    }

    private async Task CommentFollowUpAsync(string path, CrossRepoFollowUp followUp, FlightRow landing, string code, string node)
    {
        if (github is null || !github.IsCloned(path))
        {
            return;
        }
        try
        {
            await github.CommentIssueAsync(path, followUp.Issue, $"Flight {code} was made for this on {node}, as {landing.Code} landed.", CancellationToken.None);
        }
        catch (FlightException ex)
        {
            await Error.WriteLineAsync($"follow-up: {code} was made, but {followUp.Repo}#{followUp.Issue.ToString(CultureInfo.InvariantCulture)} was not commented: {ex.Message}");
        }
    }

    /// <summary>
    /// After the landing: a follow-up still without a flight (its repo is not cloned here, or its issue carries another node's label) is made on a
    /// fleet node that holds the repo, through that node's own create-flight. A follow-up needed as a package gets a For-you card naming it, as the wait
    /// cannot be set on another node's flight from here. Every failure is said and left for the next try.
    /// </summary>
    private async Task EnsurePeerFollowUpFlightsAsync(string code, PackageWait[] released, CancellationToken cancellationToken)
    {
        var landing = state.Flights().FirstOrDefault(f => f.Code == code);
        if (landing is null)
        {
            return;
        }
        foreach (var followUp in landing.RepoFollowUps.Where(f => f.Flight is null).ToList())
        {
            try
            {
                var (made, node) = await MakeOnPeerAsync(landing, followUp, cancellationToken);
                SetFollowUp(code, followUp, f => f with { Flight = made, Node = node, Problem = null });
                var path = FollowUpPath(landing, followUp);
                await CommentFollowUpAsync(path, followUp, landing, made, node);
                if (followUp.Package)
                {
                    RaiseFollowUpPackageCard(landing, followUp, made, node, released);
                }
            }
            catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or HttpRequestException or System.ComponentModel.Win32Exception)
            {
                await FollowUpFailedAsync(landing, followUp, ex.Message);
            }
        }
    }

    /// <summary>A For-you card for a follow-up flight whose package wait this node cannot set: it names the package and the line to hold for. Never dropped silently.</summary>
    private void RaiseFollowUpPackageCard(FlightRow landing, CrossRepoFollowUp followUp, string made, string node, PackageWait[] released)
    {
        var named = released.Length > 0
            ? string.Join(", ", released.Select(w => $"{w.Id} {w.Line}{(w.Above is { } above ? $" above {above}" : "")}"))
            : $"whatever package {landing.Code} released (its version line is in its Directory.Build.props)";
        state.RaiseCard(CardBin.ForYou, FollowUpPackageKind, Verbs.FlightTarget(made), $"{made} on {node} needs {named} from {landing.Code}",
            $"{made} was made on {node} for {followUp.Repo}#{followUp.Issue.ToString(CultureInfo.InvariantCulture)}, which {landing.Code} recorded as needing a published package. Its wait cannot be set from here: hold {made} on {node} until {named} is on nuget.org.",
            [new CardChoice { Label = "I have held it", Verb = Verbs.Dismiss, Target = "" }]);
    }

    /// <summary>
    /// Makes the flights of a landed flight's follow-ups that still have none (a create that failed at the landing, a peer that was away): here
    /// when the repo is cloned here, else on a fleet node. One at a time with the flight verbs; a follow-up needed as a package is not given its
    /// wait (the landing's head is gone) but a For-you card. Said and left for the next try on any failure; never makes a second flight for an issue.
    /// </summary>
    public async Task EnsureFollowUpFlightsAsync(string code, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            var landing = Find(Verbs.FlightTarget(code));
            if (landing.State != FlightState.Landed || landing.RepoFollowUps.All(f => f.Flight is not null) || github is null)
            {
                return;
            }
            await EnsureLocalFollowUpFlightsAsync(landing, cancellationToken);
            await EnsurePeerFollowUpFlightsAsync(code, [], cancellationToken);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<(string Code, string Node)> MakeOnPeerAsync(FlightRow landing, CrossRepoFollowUp followUp, CancellationToken cancellationToken)
    {
        var item = new WorkItem(TrackerKind.GitHub, followUp.Issue);
        var peers = Fleet?.Peers() ?? [];
        if (peers.Count == 0)
        {
            throw new FlightException($"{followUp.Repo} is not made here (not cloned, or its issue is another node's) and this node has no peers");
        }
        var path = FollowUpPath(landing, followUp);
        var label = github is not null && github.IsCloned(path) ? await OtherNodeOfAsync(path, item, cancellationToken) : null;
        var now = time.GetUtcNow();
        var facts = await Task.WhenAll(peers.Select(p => ReadPeerAsync(p, followUp.Repo, now, cancellationToken)));
        var verdicts = FleetChoice.Judge(facts.Select(f => f.Facts), followUp.Repo)
            .OrderBy(v => label is not null && string.Equals(v.Name, label, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        var refused = new List<string>();
        foreach (var verdict in verdicts.Where(v => v.Reason is null))
        {
            var peer = facts.Single(f => ReferenceEquals(f.Facts, verdict.Facts)).Peer;
            CommandResult result;
            try
            {
                result = await peer.SendAsync(new CommandRequest
                {
                    Verb = Verbs.CreateFlight, Target = Verbs.RepoTarget(verdict.Facts.Checkout!), Who = "land", Args = [item.Display, followUp.Title],
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException)
            {
                refused.Add($"{verdict.Name}: unreachable ({ex.Message})");
                continue;
            }
            if (result is { Ok: true, Made: { Length: > 0 } made })
            {
                return (made, verdict.Name);
            }
            if (result.Error is { } error && AlreadyHas().Match(error) is { Success: true } has)
            {
                return (has.Groups[1].Value, verdict.Name);
            }
            refused.Add($"{verdict.Name}: refused it ({result.Error ?? "it made no flight"})");
        }
        var why = verdicts.Where(v => v.Reason is not null).Select(v => v.Said!).Concat(refused);
        throw new FlightException($"no node of the fleet takes {followUp.Repo}#{followUp.Issue.ToString(CultureInfo.InvariantCulture)}: {string.Join("; ", why)}");
    }
}
