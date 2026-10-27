using System.Net.WebSockets;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The planner's overlap check across nodes (<c>touches</c>, #375): the files its legs will change against every other flight not landed
/// on this node and on each reachable peer, by the flight's branch as pushed, and against every open PR, with a choice per match
/// (<see cref="OverlapChoice"/>). A read: it takes no lock and changes nothing, so a slow peer or gh holds up this answer only.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The most shared files a line names before it says how many more there are.</summary>
    private const int TouchesNamed = 8;

    /// <summary>One other flight or PR the files are compared with: whom it is and what it changes, or why that is not known.</summary>
    private sealed record Touched(string Label, string[]? Files, string? Unknown, string? FlightCode, bool OnPeer);

    /// <summary>
    /// <c>touches flight/&lt;code&gt; &lt;file…&gt;</c>: a line per flight and PR, the matches first (fold, connect), then those not known,
    /// then the parallel ones. A flight whose branch is not pushed, a node that did not answer and a PR whose files gh would not list are
    /// said as <c>unknown</c>, never left out.
    /// </summary>
    private async Task<string> TouchesAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        var mine = OverlapChoice.Normalise(command.Args ?? []);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; it overlaps nothing now");
        }
        if (mine.Count == 0)
        {
            throw new FlightException($"touches needs the files {flight.Code}'s legs will change, as in touches flight/{flight.Code} src/Parser.cs src/Lexer.cs");
        }

        // Fetched first, so a branch pushed a moment ago is seen. A fetch that fails leaves what was fetched last, and the answer says so.
        string? stale = null;
        var fetch = await git.RunAsync(["fetch", "--prune", "--quiet", "origin"], flight.RepoPath, cancellationToken);
        if (fetch.ExitCode != 0)
        {
            stale = $"git fetch failed ({FirstLine(fetch.Error)}): pushed branches are as of the last fetch";
        }

        var others = new List<Touched>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { flight.Branch };

        foreach (var other in state.Flights().Where(f => f.Code != flight.Code && f.State != FlightState.Landed && FlightCommander.SamePath(f.RepoPath, flight.RepoPath)))
        {
            listed.Add(other.Branch);
            others.Add(await ChangedByAsync($"{other.Code} ({options.Machine})", other, local: true, onPeer: false, flight.RepoPath, cancellationToken));
        }

        var (peerFlights, peerNotes) = await PeerFlightsAsync(flight, cancellationToken);
        foreach (var (node, other) in peerFlights)
        {
            listed.Add(other.Branch);
            others.Add(await ChangedByAsync($"{other.Code} ({node})", other, local: false, onPeer: true, flight.RepoPath, cancellationToken));
        }
        others.AddRange(peerNotes.Select(n => new Touched(n.Label, null, n.Why, null, OnPeer: true)));

        // A flight's own PR is its branch, already compared: only the other PRs are.
        try
        {
            var prs = (await github.OpenPullRequestsAsync(flight.RepoPath, cancellationToken)).Where(p => !listed.Contains(p.Head)).ToArray();
            others.AddRange(await Task.WhenAll(prs.Select(p => ChangedByPrAsync(p, flight.RepoPath, cancellationToken))));
        }
        catch (FlightException ex)
        {
            others.Add(new Touched("open PRs", null, ex.Message, null, OnPeer: false));
        }

        var judged = others.Select(o => (Other: o, Judgement: o.Files is { } files ? OverlapChoice.Judge(mine, files) : null)).ToList();
        var lines = judged
            .OrderBy(j => j.Judgement is null ? 2 : j.Judgement.Pick == OverlapPick.Parallel ? 3 : j.Judgement.Pick == OverlapPick.Fold ? 0 : 1)
            .ThenBy(j => j.Other.Label, StringComparer.OrdinalIgnoreCase)
            .Select(j => TouchesLine(flight.Code, mine.Count, j.Other, j.Judgement))
            .ToList();
        if (lines.Count == 0)
        {
            lines.Add($"no other flight in the air and no open PR in {flight.Repo}: nothing to fold or connect, fly in parallel");
        }
        if (stale is not null)
        {
            lines.Add(stale);
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// What <paramref name="other"/> changes against its base, by its pushed branch (<c>origin/&lt;branch&gt;</c>). A flight of this node
    /// (<paramref name="local"/>) falls back to its local branch, which holds at least what it pushed; a peer's is not on this machine, so
    /// one not pushed is unknown.
    /// </summary>
    private async Task<Touched> ChangedByAsync(string label, FlightRow other, bool local, bool onPeer, string repoPath, CancellationToken cancellationToken)
    {
        List<string> tips = [$"origin/{other.Branch}"];
        if (local)
        {
            tips.Add($"refs/heads/{other.Branch}");
        }
        var tip = await ExistingRefAsync(tips, repoPath, cancellationToken);
        if (tip is null)
        {
            return new Touched(label, null, $"its branch {other.Branch} is not pushed, so what it changes is not known", other.Code, onPeer);
        }
        var baseRef = await ExistingRefAsync([other.Base, "origin/main"], repoPath, cancellationToken);
        if (baseRef is null)
        {
            return new Touched(label, null, $"its base {other.Base} is not in this clone, so what it changes is not known", other.Code, onPeer);
        }
        var diff = await git.RunAsync(["diff", "--name-only", $"{baseRef}...{tip}"], repoPath, cancellationToken);
        return diff.ExitCode == 0
            ? new Touched(label, [.. diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], null, other.Code, onPeer)
            : new Touched(label, null, $"git diff of {other.Branch} failed: {FirstLine(diff.Error)}", other.Code, onPeer);
    }

    private async Task<string?> ExistingRefAsync(IEnumerable<string> candidates, string repoPath, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            if ((await git.RunAsync(["rev-parse", "--verify", "--quiet", $"{candidate}^{{commit}}"], repoPath, cancellationToken)).ExitCode == 0)
            {
                return candidate;
            }
        }
        return null;
    }

    private async Task<Touched> ChangedByPrAsync(PullRequestStatus pr, string repoPath, CancellationToken cancellationToken)
    {
        var label = $"PR #{pr.Number} \"{pr.Title}\"";
        try
        {
            return new Touched(label, await github.PullRequestFilesAsync(repoPath, pr.Number, cancellationToken), null, null, OnPeer: false);
        }
        catch (FlightException ex)
        {
            return new Touched(label, null, ex.Message, null, OnPeer: false);
        }
    }

    /// <summary>
    /// The not-landed flights each reachable peer flies on this repo (matched by its GitHub remote, as paths are each machine's own), and a
    /// note for each peer whose flights could not be read: no grant held, grant refused, or it did not answer within
    /// <see cref="FlightCommander.PeerBudget"/>. A peer that does not hold the repo has nothing to say and no note.
    /// </summary>
    private async Task<(List<(string Node, FlightRow Flight)> Flights, List<(string Label, string Why)> Notes)> PeerFlightsAsync(FlightRow mine, CancellationToken cancellationToken)
    {
        var found = new List<(string, FlightRow)>();
        var notes = new List<(string, string)>();
        var peers = flights.Fleet?.Peers() ?? [];
        var gitHub = RepoCatalog.GitHubOf(Path.Combine(mine.RepoPath, ".git"));
        if (peers.Count == 0 || gitHub is null)
        {
            return (found, notes);
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(FlightCommander.PeerBudget);
        var reads = await Task.WhenAll(peers.Select(p => ReadPeerAsync(p, gitHub, mine.Branch, budget.Token, cancellationToken)));
        foreach (var (flights, note) in reads)
        {
            found.AddRange(flights);
            if (note is { } n)
            {
                notes.Add(n);
            }
        }
        return (found, notes);
    }

    private static async Task<(List<(string, FlightRow)> Flights, (string Label, string Why)? Note)> ReadPeerAsync(
        IFleetPeer peer, string gitHub, string ownBranch, CancellationToken budget, CancellationToken caller)
    {
        if (!peer.HasGrant)
        {
            return ([], (peer.Endpoint, "no grant is held for this node, so its flights are not known"));
        }
        try
        {
            if (await peer.ReposAsync(budget) is not { } repos)
            {
                return ([], (peer.Endpoint, "this node refuses the grant held for it, so its flights are not known"));
            }
            if (repos.FirstOrDefault(r => string.Equals(r.GitHub, gitHub, StringComparison.OrdinalIgnoreCase))?.Path is not { } checkout)
            {
                return ([], null);
            }
            var load = await peer.LoadAsync(budget);
            var theirs = load.Flights.Where(f => f.State != FlightState.Landed && FlightCommander.SamePath(f.RepoPath, checkout)
                && !string.Equals(f.Branch, ownBranch, StringComparison.OrdinalIgnoreCase)).ToList();
            if (theirs.Count == 0)
            {
                return ([], null);
            }
            var info = await peer.NodeAsync(budget);
            var node = info.MachinePrefix.Length > 0 ? info.MachinePrefix : peer.Endpoint;
            return ([.. theirs.Select(f => (node, f))], null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or WebSocketException && !caller.IsCancellationRequested)
        {
            return ([], (peer.Endpoint, $"this node did not answer ({FirstLine(ex.Message)}), so its flights are not known"));
        }
    }

    private static string TouchesLine(string code, int mine, Touched other, OverlapJudgement? judgement)
    {
        if (judgement is null)
        {
            return $"{other.Label}: unknown, {other.Unknown}";
        }
        var shared = judgement.Shared;
        var named = string.Join(", ", shared.Take(TouchesNamed)) + (shared.Length > TouchesNamed ? $" and {shared.Length - TouchesNamed} more" : "");
        return judgement.Pick switch
        {
            OverlapPick.Parallel => $"{other.Label}: parallel, no file in common",
            OverlapPick.Fold => $"{other.Label}: fold, both change {named} ({shared.Length} of your {mine} files); {Advice(code, other, shared, fold: true)}",
            _ => $"{other.Label}: connect, both change {named}; {Advice(code, other, shared, fold: false)}",
        };
    }

    // What the planner does with the line: a flight of this node can be folded into or connected to, a peer's flight connected to (never folded into); a PR neither.
    private static string Advice(string code, Touched other, string[] shared, bool fold)
    {
        var files = string.Join(' ', shared.Select(f => OverlapFileArg + f));
        if (other.FlightCode is { } flight && !other.OnPeer)
        {
            return fold
                ? $"when it is the same goal, flight overlap {code} {flight} {files} <why>; when it is another goal, flight connect {code} {flight}"
                : $"flight connect {code} {flight} so {code} branches off main once it has landed, or plan beside it when the shared files are only touched in passing";
        }
        return other.FlightCode is { } peerFlight
            ? $"it flies on another node, so it cannot be folded into from here: flight connect {code} {peerFlight} holds {code} until that node says it landed (or flight overlap {code} {peerFlight} {files} <why> to ask the controller whether to connect or plan in parallel), or plan beside it when the shared files are only touched in passing"
            : "an open PR is no flight to fold into or connect to: name it and the shared files in your planned summary, and plan the legs on those files to follow its merge";
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no reason given";
}
