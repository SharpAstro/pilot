using System.Globalization;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The flight verbs: <c>create-flight</c>, <c>connect</c> and <c>land</c>. Git work takes seconds, so each runs off the
/// state's lock, one at a time (two creates must not claim one code), and puts its result on the stream when done.
/// </summary>
/// <param name="workers">Stops the flight's sessions before its plane goes; null in a test that starts none.</param>
/// <param name="github">Gives a flight made without a title its issue's, and the commit a landed PR merged as; null in a test that names its titles.</param>
/// <param name="packages">Reads what a landing flight releases, for the flights connected to it to wait for; null in a test that has none wait for a package.</param>
public sealed class FlightCommander(NodeState state, Planes planes, TimeProvider time, IWorkerLauncher? workers = null, IGitHub? github = null, PackageReleases? packages = null)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The settings in force: whether the node is in maintenance. A test's commander takes every flight.</summary>
    public RoleModels Models { get; set; } = RoleModels.Unset;

    /// <summary>Where the images its legs' artifacts name are kept; a landed flight's copies go. Null keeps none.</summary>
    public Attachments? Attachments { get; set; }

    /// <summary>The trackers a work item's title is read from; null reads GitHub issues only, through <c>github</c>, and refuses any other tracker's item given without a title.</summary>
    public Trackers? Trackers
    {
        get => _trackers ?? (github is null ? null : _trackers = new Trackers([new GitHubTracker(github)]));
        set => _trackers = value;
    }

    private Trackers? _trackers;

    /// <summary>The machine's prefix, as a refusal names it.</summary>
    public string Machine { get; set; } = "this node";

    /// <summary>The other nodes of the fleet, which a create-flight in maintenance is routed to; null where there are none (a test's commander).</summary>
    public IFleet? Fleet { get; set; }

    /// <summary>The quiet card a flight folded into another leaves in place of a landed one.</summary>
    public const string FoldedKind = "folded";

    /// <summary>The Handled card that says a flight went to another node, as this one is in maintenance.</summary>
    public const string RoutedKind = "routed";

    /// <summary>Whether <paramref name="verb"/> is one of these.</summary>
    public static bool Handles(string verb) => verb is Verbs.CreateFlight or Verbs.Connect or Verbs.Land or Verbs.FlyAnyway;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            string? error = null;
            string? made = null;
            string? way = null;
            try
            {
                switch (command.Verb)
                {
                    case Verbs.CreateFlight:
                        (made, way) = await CreateAsync(command, cancellationToken);
                        break;
                    case Verbs.Connect:
                        Connect(command);
                        break;
                    case Verbs.Land:
                        await LandAsync(command, cancellationToken);
                        break;
                    case Verbs.FlyAnyway:
                        FlyAnyway(command);
                        break;
                }
            }
            catch (FlightException ex)
            {
                error = ex.Message;
            }
            return state.Complete(command, error, way: way, made: made);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Makes the flight; returns its code, which the result carries as what it made, and where it went when it was made
    /// on another node (<see cref="CommandResult.Way"/>), else null.
    /// </summary>
    private async Task<(string Code, string? Way)> CreateAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        if (!command.Target.StartsWith(Verbs.RepoTargetPrefix, StringComparison.Ordinal) || command.Target.Length == Verbs.RepoTargetPrefix.Length)
        {
            throw new FlightException($"create-flight takes a repo target, {Verbs.RepoTargetPrefix}<path to its checkout>, not {command.Target}");
        }
        var args = command.Args ?? [];
        if (args.Length == 0)
        {
            throw new FlightException("create-flight needs the work item first (an issue number, sc-N or a Shortcut story URL), as in create-flight repo/<path> 1611 [title]");
        }
        // A bare number means the repo's tracker default (repo-trackers), keyed by the checkout's folder name.
        var givenPath = command.Target[Verbs.RepoTargetPrefix.Length..];
        var named = Path.GetFileName((await planes.MainCheckoutAsync(givenPath, cancellationToken) ?? givenPath).TrimEnd('/', '\\'));
        var (parsed, parseError) = WorkItem.Parse(args[0], Models.TrackerOf(named));
        if (parsed is not { } item)
        {
            throw new FlightException($"create-flight: {parseError}");
        }

        // Only a new flight is refused here: flying and held flights, legs, reviews and landings never come through here. In
        // maintenance a node takes none of its own, and sends this one to a node of the fleet that takes it.
        if (Models.Maintenance)
        {
            return await RouteAsync(command, item, cancellationToken);
        }

        var path = command.Target[Verbs.RepoTargetPrefix.Length..];
        var repoPath = await planes.MainCheckoutAsync(path, cancellationToken)
            ?? throw new FlightException($"{path} is no git checkout");
        var repo = Path.GetFileName(repoPath);
        var flights = state.Flights();
        var prefix = FlightCodes.PrefixFor(repo, flights);
        var code = FlightCodes.Next(prefix, item.Id, flights)
            ?? throw new FlightException($"{prefix}{item.Id} and its suffixes {FlightCodes.Suffixes} are all taken");

        var title = string.Join(' ', args.Skip(1)).Trim();
        if (title.Length == 0 && Trackers?.Of(item.Tracker) is { } tracker)
        {
            // No title given: the item's own, so the flight reads as what it is about. Asked before the plane is made, so a
            // refusal for want of one leaves nothing behind.
            title = (await tracker.TitleAsync(repoPath, item.Id, cancellationToken))?.Trim() ?? "";
            if (title.Length == 0 && tracker.MissingTitle(item) is { } missing)
            {
                throw new FlightException(missing);
            }
        }
        if (title.Length == 0 && item.Tracker != TrackerKind.GitHub && Trackers?.Of(item.Tracker) is null)
        {
            throw new FlightException($"{item.Display} needs a title: this node cannot read a {item.Tracker} item's, so give one as in create-flight repo/<path> {item.Display} <title>");
        }

        var plane = await planes.MakeAsync(repoPath, code, cancellationToken, item);
        state.PutFlight(new FlightRow
        {
            Code = code,
            Repo = repo,
            RepoPath = plane.RepoPath,
            Issue = item.Id,
            Tracker = item.Tracker,
            Title = title.Length > 0 ? title : null,
            Branch = plane.Branch,
            Worktree = plane.Worktree,
            Base = plane.Base,
            State = FlightState.Flying,
            CreatedAt = time.GetUtcNow(),
        });
        return (code, null);
    }

    /// <summary>
    /// A create-flight this node, in maintenance, does not take itself: made on the best willing node of the fleet instead
    /// (<see cref="FleetChoice"/>), through that node's own create-flight with its own checkout of the repo. Says where it went
    /// on a Handled card and in the result's way; with none willing, refuses naming each node and why it did not qualify.
    /// </summary>
    private async Task<(string Code, string? Way)> RouteAsync(CommandRequest command, WorkItem item, CancellationToken cancellationToken)
    {
        var refusal = $"{Machine} is in maintenance: it takes no new flights";
        var peers = Fleet?.Peers() ?? [];
        if (peers.Count == 0)
        {
            throw new FlightException(refusal);
        }

        // The repo is found on another node by its GitHub remote: the paths are each machine's own.
        var path = command.Target[Verbs.RepoTargetPrefix.Length..];
        var repoPath = await planes.MainCheckoutAsync(path, cancellationToken);
        var gitHub = repoPath is null ? null : RepoCatalog.GitHubOf(Path.Combine(repoPath, ".git"));
        var now = time.GetUtcNow();
        var facts = await Task.WhenAll(peers.Select(p => ReadPeerAsync(p, gitHub, now, cancellationToken)));
        var verdicts = FleetChoice.Judge(facts.Select(f => f.Facts), gitHub);
        var refused = new List<string>();
        foreach (var verdict in verdicts.Where(v => v.Reason is null))
        {
            var peer = facts.Single(f => ReferenceEquals(f.Facts, verdict.Facts)).Peer;
            var checkout = verdict.Facts.Checkout!;
            CommandResult result;
            try
            {
                // The item resolved here is sent explicitly (sc-N or #N), so the peer's own repo-trackers cannot read it differently.
                string[] sent = [item.Display, .. (command.Args ?? []).Skip(1)];
                result = await peer.SendAsync(command with { Target = Verbs.RepoTarget(checkout), Args = sent }, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException)
            {
                refused.Add($"{verdict.Name}: unreachable ({ex.Message})");
                continue;
            }
            if (result is not { Ok: true, Made: { Length: > 0 } code })
            {
                refused.Add($"{verdict.Name}: refused it ({result.Error ?? "it made no flight"})");
                continue;
            }
            var way = $"{code} is flying on {verdict.Name}: {Machine} is in maintenance";
            state.RaiseCard(CardBin.Handled, RoutedKind, Verbs.NodeTarget, way,
                $"{Machine} is in maintenance, so {item.Noun} of {gitHub ?? path} went to {verdict.Name}, which has {checkout}");
            return (code, way);
        }

        var why = verdicts.Where(v => v.Reason is not null).Select(v => $"{v.Name}: {v.Reason}").Concat(refused);
        throw new FlightException($"{refusal}, and no node of the fleet takes it either: {string.Join("; ", why)}");
    }

    /// <summary>The peer's facts as far as they can be read: a peer that cannot be reached, or refuses the grant held for it, is judged for that.</summary>
    private static async Task<(IFleetPeer Peer, FleetPeerFacts Facts)> ReadPeerAsync(IFleetPeer peer, string? gitHub, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!peer.HasGrant)
        {
            return (peer, new FleetPeerFacts(peer.Endpoint, Reached: false, Granted: false, false, false, null, null, 0));
        }
        var name = peer.Endpoint;
        try
        {
            var info = await peer.NodeAsync(cancellationToken);
            name = info.MachinePrefix.Length > 0 ? info.MachinePrefix : peer.Endpoint;
            if (await peer.ReposAsync(cancellationToken) is not { } repos)
            {
                return (peer, new FleetPeerFacts(name, Reached: true, Granted: false, info.Maintenance, info.AcceptsFleetFlights, null, null, 0));
            }
            var load = await peer.LoadAsync(cancellationToken);
            var checkout = gitHub is null ? null : repos.FirstOrDefault(r => string.Equals(r.GitHub, gitHub, StringComparison.OrdinalIgnoreCase))?.Path;
            return (peer, new FleetPeerFacts(name, Reached: true, Granted: true, info.Maintenance, info.AcceptsFleetFlights, checkout,
                FleetChoice.HeadroomOf(load.Usage, now), load.Flights.Count(f => f.State == FlightState.Flying)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or System.Net.WebSockets.WebSocketException)
        {
            return (peer, new FleetPeerFacts(name, Reached: false, Granted: true, false, false, null, null, 0));
        }
    }

    private void Connect(CommandRequest command)
    {
        var flight = Find(command.Target);
        var other = (command.Args ?? []) is [{ } code, ..] ? state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)) : null;
        if (other is null)
        {
            throw new FlightException($"connect needs the code of a flight {flight.Code} waits for, as in connect flight/{flight.Code} TIA1611");
        }
        if (other.Code == flight.Code || WaitsFor(other, flight.Code))
        {
            throw new FlightException($"{flight.Code} cannot wait for {other.Code}: {other.Code} already waits for {flight.Code}, so neither would leave");
        }
        if (flight.Connections.Contains(other.Code, StringComparer.Ordinal))
        {
            return;
        }
        state.PutFlight(Settle(flight with { Connections = [.. flight.Connections, other.Code] }));
    }

    /// <summary>
    /// Lands <paramref name="code"/> as <c>land</c> does, for a merge that has just put its work in: one at a time with
    /// the flight verbs. Throws <see cref="FlightException"/> with why it could not.
    /// </summary>
    /// <param name="mergedHead">The head the PR merged at: the plane may hold nothing past it.</param>
    public async Task LandAfterMergeAsync(string code, string mergedHead, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            await LandFlightAsync(Find(Verbs.FlightTarget(code)), cancellationToken, mergedHead);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Retires <paramref name="flight"/> as folded into <paramref name="other"/>: landed with nothing of its own, as <c>land</c>
    /// does (its sessions stopped, its clean plane removed, its code and branch kept), marked with the flight it went into, and a
    /// quiet card saying so instead of a landed one. One at a time with the flight verbs. Throws <see cref="FlightException"/>
    /// with why it could not, and then changes nothing.
    /// </summary>
    public async Task RetireFoldedAsync(FlightRow flight, string other, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            await LandFlightAsync(flight, cancellationToken, foldedInto: other);
        }
        finally
        {
            _one.Release();
        }
    }

    private Task LandAsync(CommandRequest command, CancellationToken cancellationToken) => LandFlightAsync(Find(command.Target), cancellationToken);

    private async Task LandFlightAsync(FlightRow flight, CancellationToken cancellationToken, string? mergedHead = null, string? foldedInto = null)
    {
        if (flight.State == FlightState.Landed)
        {
            return;
        }
        // Its sessions first: a worker inside its stop grace, or a reviewer, still has the plane open, and a folder held
        // open is not removed. Seen live: a flight landed a minute after its worker finished kept its plane.
        if (workers is not null)
        {
            var jobs = state.Legs(flight.Code).Select(l => l.WorkerJob).Append(flight.Review?.ReviewerJob).OfType<string>().Distinct();
            foreach (var job in jobs)
            {
                try
                {
                    await workers.StopAsync(job, cancellationToken);
                }
                catch (Exception ex) when (ex is FlightException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Gone already.
                }
            }
        }
        // Nothing of the flight's is left where nobody looks: no commit that is on no remote and not in main (TIA1658's
        // last read was one, until it was landed by hand). Such a plane is not landed; it says which commits.
        // After a merge, everything up to the merged head is in: only commits past it would be left behind.
        var mainRef = flight.Base.Length > 0 ? flight.Base : "origin/main";
        if (mergedHead is not null)
        {
            if (await planes.PastAsync(flight.Worktree, mergedHead, cancellationToken) is { Count: > 0 } past)
            {
                throw new FlightException($"{flight.Code} merged at {mergedHead[..Math.Min(8, mergedHead.Length)]}, but its plane holds {past.Count} commit{(past.Count == 1 ? "" : "s")} past it: {string.Join("; ", past)}. Land {(past.Count == 1 ? "it" : "them")} in a PR of {(past.Count == 1 ? "its" : "their")} own, then land the flight");
            }
        }
        else if (await planes.UnlandedAsync(flight.Worktree, mainRef, cancellationToken) is { Count: > 0 } loose)
        {
            throw new FlightException($"{flight.Code}'s plane holds {loose.Count} commit{(loose.Count == 1 ? "" : "s")} on no remote and not in {mainRef}: {string.Join("; ", loose)}. Push {(loose.Count == 1 ? "it" : "them")} (flight pr) or drop {(loose.Count == 1 ? "it" : "them")}, then land");
        }

        // What it releases is read before its plane goes, and before anything is changed: a land that cannot say what its
        // consumers wait for is refused, never half done. A folded flight released nothing.
        var consumers = state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Contains(flight.Code, StringComparer.Ordinal)).ToList();
        var released = foldedInto is null && packages is not null && consumers.Count > 0
            ? await MergeOfAsync(flight, cancellationToken) is var (mergeCommit, mergedAt)
                ? await packages.WaitsAsync(flight, consumers, mergedHead, mergeCommit, mergedAt, cancellationToken)
                : []
            : [];

        var leftover = await planes.RemoveAsync(flight.RepoPath, flight.Worktree, cancellationToken);
        if (leftover is not null && planes.Queue is { } queue)
        {
            // Held by a process: the flight lands all the same, and the folder is removed at the node's next start.
            queue.Add(flight.Worktree, RemovalQueue.PlaneKind, flight.Code, leftover);
            state.RaiseCard(CardBin.Handled, "plane-left", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed",
                $"left {flight.Worktree} for the next start: a process holds it");
        }
        else if (leftover is not null)
        {
            state.RaiseCard(CardBin.ForYou, "plane-left", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed, but its plane folder is still there",
                $"{leftover}; nothing of git's is in it, so delete it by hand once whatever holds it lets go");
        }
        Attachments?.Remove(flight.Code);
        state.PutFlight(flight with { State = FlightState.Landed, LandedAt = time.GetUtcNow(), FoldedInto = foldedInto ?? flight.FoldedInto });

        // Its cards leave with it, and a quiet one says it is in; a folded one says where its work went.
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), foldedInto is null ? $"{flight.Code} landed" : $"{flight.Code} folded into {foldedInto}");
        if (foldedInto is null)
        {
            state.RaiseCard(CardBin.ForYou, "landed", Verbs.FlightTarget(flight.Code), $"{flight.Code} landed",
                $"{flight.Title ?? flight.Item.Noun}{(flight.PullRequest is { } number ? $", PR #{number}" : "")}", link: flight.PullRequestUrl);
        }
        else
        {
            state.RaiseCard(CardBin.Handled, FoldedKind, Verbs.FlightTarget(flight.Code), $"{flight.Code} folded into {foldedInto}",
                $"{flight.Title ?? flight.Item.Noun} ({flight.Item.Display}) is now {foldedInto}'s: its PR closes it, and {foldedInto}'s planner plans its points");
        }

        // Flights that waited for it may leave now. A folded flight's issue is the other flight's work and has not landed, so
        // those that waited for it wait for the other flight instead (not for themselves, not twice, not in a cycle).
        foreach (var waitingRow in state.Flights().Where(f => f.State != FlightState.Landed && f.Connections.Contains(flight.Code, StringComparer.Ordinal)).ToList())
        {
            var waiting = waitingRow;
            if (foldedInto is not null)
            {
                var target = state.Flights().FirstOrDefault(f => string.Equals(f.Code, foldedInto, StringComparison.OrdinalIgnoreCase));
                var connections = waiting.Connections.Where(c => c != flight.Code).ToList();
                if (target is not null && target.Code != waiting.Code && !connections.Contains(target.Code, StringComparer.Ordinal) && !WaitsFor(target, waiting.Code))
                {
                    connections.Add(target.Code);
                }
                waiting = waiting with { Connections = [.. connections] };
            }
            else if (released.Length > 0)
            {
                // Landed with a release it uses: it flies once the package can be fetched, not before.
                var uses = new List<PackageWait>();
                foreach (var release in released.Where(r => !waiting.PackageWaits.Any(w => w.Id == r.Id && w.Producer == r.Producer)))
                {
                    if (await packages!.ConsumesAsync(waiting, flight, release.Id, cancellationToken))
                    {
                        uses.Add(release);
                    }
                }
                waiting = waiting with { PackageWaits = [.. waiting.PackageWaits, .. uses] };
            }
            state.PutFlight(Settle(waiting));
        }
    }

    /// <summary>The commit <paramref name="flight"/>'s PR merged as and when, as far as GitHub says; both null when it does not (the land then reads as of now).</summary>
    private async Task<(string? Commit, DateTimeOffset? At)> MergeOfAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (github is null)
        {
            return (null, null);
        }
        try
        {
            var pr = await github.PullRequestAsync(flight.RepoPath, flight.Branch, cancellationToken);
            return pr is { State: "MERGED" } && (flight.PullRequest is null || flight.PullRequest == pr.Number)
                ? (pr.MergeCommit.Length > 0 ? pr.MergeCommit : null, pr.MergedAt)
                : (null, null);
        }
        catch (FlightException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// <c>fly-anyway flight/&lt;code&gt;</c>: the way out of a wait for a package that is not coming (its publish failed, or the
    /// connection never needed it). Every package wait of the flight is dropped, its late card closed, and the flight flies when
    /// nothing else holds it.
    /// </summary>
    private void FlyAnyway(CommandRequest command)
    {
        var flight = Find(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed");
        }
        if (flight.PackageWaits.Length == 0)
        {
            throw new FlightException($"{flight.Code} waits for no package{(flight.State == FlightState.Held ? $", only for {FlightWaiting.For(flight)}" : "")}");
        }
        var dropped = string.Join(", ", flight.PackageWaits.Select(FlightWaiting.Text).Distinct(StringComparer.Ordinal));
        state.PutFlight(Settle(flight with { PackageWaits = [] }));
        state.CloseCardsAbout(Verbs.FlightTarget(flight.Code), $"{command.Who} let {flight.Code} fly without {dropped}", PackageWatch.LateKind);
    }

    /// <summary>
    /// Drops <paramref name="met"/> (package id and producer code) from the waits of <paramref name="code"/>, as the node found
    /// each package on nuget.org, and lets the flight go once nothing else holds it. One at a time with the flight verbs.
    /// </summary>
    public async Task DropPackageWaitsAsync(string code, IReadOnlyCollection<(string Id, string Producer)> met, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is not { } flight || flight.State == FlightState.Landed)
            {
                return;
            }
            var rest = flight.PackageWaits.Where(w => !met.Contains((w.Id, w.Producer))).ToArray();
            if (rest.Length == flight.PackageWaits.Length)
            {
                return;
            }
            state.PutFlight(Settle(flight with { PackageWaits = rest }));
            if (rest.Length == 0)
            {
                state.CloseCardsAbout(Verbs.FlightTarget(code), "its package is on nuget.org", PackageWatch.LateKind);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Marks the wait for <paramref name="id"/> of <paramref name="producer"/> on <paramref name="code"/> as said to be late, so no second card is raised for it.</summary>
    public async Task MarkPackageWaitLateAsync(string code, string id, string producer, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is { } flight)
            {
                state.PutFlight(flight with { PackageWaits = [.. flight.PackageWaits.Select(w => w.Id == id && w.Producer == producer ? w with { Escalated = true } : w)] });
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>A flight held while any flight it connects from has not landed or any package it waits for is not up, flying otherwise; a landed one stays landed.</summary>
    private FlightRow Settle(FlightRow flight)
    {
        if (flight.State == FlightState.Landed)
        {
            return flight;
        }
        var flights = state.Flights().ToDictionary(f => f.Code, StringComparer.Ordinal);
        var held = flight.PackageWaits.Length > 0 || flight.Connections.Any(c => !flights.TryGetValue(c, out var other) || other.State != FlightState.Landed);
        return flight with { State = held ? FlightState.Held : FlightState.Flying };
    }

    /// <summary>Whether <paramref name="flight"/> waits for <paramref name="code"/>, directly or through the flights it waits for.</summary>
    private bool WaitsFor(FlightRow flight, string code)
    {
        var flights = state.Flights().ToDictionary(f => f.Code, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(flight.Connections);
        while (queue.TryDequeue(out var next))
        {
            if (next == code)
            {
                return true;
            }
            if (seen.Add(next) && flights.TryGetValue(next, out var row))
            {
                foreach (var c in row.Connections)
                {
                    queue.Enqueue(c);
                }
            }
        }
        return false;
    }

    private FlightRow Find(string target)
    {
        if (!target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            throw new FlightException($"this verb takes a flight target, {Verbs.FlightTargetPrefix}<code>, not {target}");
        }
        var code = target[Verbs.FlightTargetPrefix.Length..];
        return state.Flights().FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new FlightException($"There is no flight {code}");
    }
}
