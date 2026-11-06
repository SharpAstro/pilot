using System.Net.WebSockets;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The planner's overlap check across nodes (<c>touches</c>, #375): the files its legs will change against every other flight not landed
/// on this node and on each reachable peer, by what the flight's plane changes (each node reads its own flights' planes and a peer is
/// asked with <see cref="Verbs.FlightChanges"/>, so a branch not pushed is seen; an older peer is read by its pushed branch), and against
/// every open PR, with a choice per match (<see cref="OverlapChoice"/>). A read: it takes no lock and changes nothing, so a slow peer or gh holds up this answer only.
/// </summary>
public sealed partial class LegCommander
{
    /// <summary>The most shared files a line names before it says how many more there are.</summary>
    private const int TouchesNamed = 8;

    /// <summary>One other flight or PR the files are compared with: whom it is and what it changes, or why that is not known.</summary>
    private sealed record Touched(string Label, string[]? Files, string? Unknown, string? FlightCode, bool OnPeer, string? Node = null, int? PrNumber = null,
        IReadOnlyDictionary<string, int>? Lines = null);

    /// <summary>
    /// <c>touches flight/&lt;code&gt; &lt;file…&gt;</c>: a line per flight and PR, the matches first (fold, connect), then those not known,
    /// then the parallel ones. A node that did not answer (or whose flight's changes cannot be read at all) and a PR whose files gh would not
    /// list are said as <c>unknown</c>, never left out; a flight whose branch is not pushed is not unknown, its plane is read.
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

        var (others, stale) = await GatherOthersAsync(flight, cancellationToken);

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
        await RecordPlannedAsync(flight.Code, mine, cancellationToken);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Keeps the files the planner named on the flight (the union with any named before), for the check at a leg's end. The state is
    /// written under the lock, as the other verbs do, though the read above took none.
    /// </summary>
    private async Task RecordPlannedAsync(string code, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            if (state.Flights().FirstOrDefault(f => f.Code == code) is { } now)
            {
                state.PutFlight(now with { PlannedFiles = [.. OverlapChoice.Normalise([.. now.PlannedFiles ?? [], .. files])] });
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// What every other flight not landed (this node's and the peers') and every open PR that is no flight's change, each as a
    /// <see cref="Touched"/>, after a fetch; <c>Stale</c> says so when the fetch failed. Shared by the planner's <c>touches</c> and the check at a leg's end.
    /// </summary>
    private async Task<(List<Touched> Others, string? Stale)> GatherOthersAsync(FlightRow flight, CancellationToken cancellationToken)
    {
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
        foreach (var (other, touched) in peerFlights)
        {
            listed.Add(other.Branch);
            others.Add(touched);
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
        return (others, stale);
    }

    /// <summary>
    /// <c>flight-changes flight/&lt;code&gt;</c>: what this node's own flight changes against its base, read from its plane (see
    /// <see cref="PlaneChangesAsync"/>), as a line per file, <c>&lt;added plus removed lines&gt;&lt;TAB&gt;&lt;path&gt;</c>. A flight that has landed or
    /// whose plane is gone is refused, saying why. A read: it changes nothing.
    /// </summary>
    private async Task<string> FlightChangesAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var flight = FlightOf(command.Target);
        if (flight.State == FlightState.Landed)
        {
            throw new FlightException($"{flight.Code} has landed; it changes nothing now");
        }
        var changes = await PlaneChangesAsync(flight, cancellationToken);
        return string.Join('\n', changes.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => $"{c.Value}\t{c.Key}"));
    }

    /// <summary>
    /// The files <paramref name="flight"/>'s plane changes against its base and the added plus removed lines of each: what is committed
    /// since the base, what is changed and not committed, and the files git does not track yet (each counted by its lines). Throws
    /// <see cref="FlightException"/> saying why when the plane is gone or git would not say.
    /// </summary>
    private async Task<Dictionary<string, int>> PlaneChangesAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(flight.Worktree))
        {
            throw new FlightException($"the plane of {flight.Code} is gone ({flight.Worktree})");
        }
        var baseRef = await ExistingRefAsync([flight.Base, "origin/main"], flight.Worktree, cancellationToken)
            ?? throw new FlightException($"the base {flight.Base} of {flight.Code} is not in its plane");
        var fork = await git.RunAsync(["merge-base", baseRef, "HEAD"], flight.Worktree, cancellationToken);
        if (!fork.Ok)
        {
            throw new FlightException($"git merge-base in the plane of {flight.Code} failed: {fork.Why}");
        }
        // The working tree against where the plane left its base: committed, staged and unstaged changes together.
        var diff = await git.RunAsync(["diff", "--numstat", "--no-renames", fork.Output.Trim()], flight.Worktree, cancellationToken);
        if (!diff.Ok)
        {
            throw new FlightException($"git diff in the plane of {flight.Code} failed: {diff.Why}");
        }
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AddNumstat(map, diff.Output);
        var untracked = await git.RunAsync(["ls-files", "--others", "--exclude-standard", "-z"], flight.Worktree, cancellationToken);
        if (!untracked.Ok)
        {
            throw new FlightException($"git ls-files in the plane of {flight.Code} failed: {untracked.Why}");
        }
        foreach (var path in untracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = path.Replace('\\', '/');
            map[key] = map.GetValueOrDefault(key) + UntrackedLines(Path.Combine(flight.Worktree, path));
        }
        return map;
    }

    /// <summary>The lines of a file git does not track yet; one that cannot be read, or is too large to read here, counts as 1 so it is still named.</summary>
    private static int UntrackedLines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length > 2_000_000 ? 1 : Math.Max(1, File.ReadLines(path).Count());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    /// <summary>
    /// What <paramref name="other"/> changes against its base, by its pushed branch (<c>origin/&lt;branch&gt;</c>). A flight of this node
    /// (<paramref name="local"/>) falls back to its local branch, which holds at least what it pushed; a peer's is not on this machine, so
    /// one not pushed is unknown. Used for a flight of this node whose plane is gone, and for a peer that did not know
    /// <see cref="Verbs.FlightChanges"/> (an older node).
    /// </summary>
    private async Task<Touched> ChangedByAsync(string label, FlightRow other, bool local, bool onPeer, string repoPath, CancellationToken cancellationToken)
    {
        if (local)
        {
            // This node's own flight is read from its plane, which holds what is not committed or pushed yet; a plane that is gone is read by its branch.
            try
            {
                var changes = await PlaneChangesAsync(other, cancellationToken);
                return new Touched(label, [.. changes.Keys], null, other.Code, onPeer, Lines: changes);
            }
            catch (FlightException)
            {
            }
        }
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
        if (diff.ExitCode != 0)
        {
            return new Touched(label, null, $"git diff of {other.Branch} failed: {FirstLine(diff.Error)}", other.Code, onPeer);
        }
        var lines = await LineCountsAsync($"{baseRef}...{tip}", repoPath, cancellationToken);
        return new Touched(label, [.. diff.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], null, other.Code, onPeer, Lines: lines);
    }

    /// <summary>The flight's own changes per file (added plus removed lines), <c>Base...HEAD</c> in its plane; null when git would not say.</summary>
    private async Task<Dictionary<string, int>?> OwnLinesAsync(FlightRow flight, CancellationToken cancellationToken) =>
        await LineCountsAsync($"{flight.Base}...HEAD", flight.Worktree, cancellationToken);

    /// <summary>Per-file added plus removed lines of <c>git diff --numstat</c> over a range; a binary file ('-') counts 0. Null on failure, never an error.</summary>
    private async Task<Dictionary<string, int>?> LineCountsAsync(string range, string workDir, CancellationToken cancellationToken)
    {
        try
        {
            var result = await git.RunAsync(["diff", "--numstat", "--no-renames", range], workDir, cancellationToken);
            if (result.ExitCode != 0)
            {
                return null;
            }
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            AddNumstat(map, result.Output);
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Adds the lines of each file of <c>git diff --numstat</c> output to <paramref name="map"/>; a binary file ('-') counts 0.</summary>
    private static void AddNumstat(Dictionary<string, int> map, string numstat)
    {
        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cols = line.Split('\t', 3);
            if (cols.Length < 3)
            {
                continue;
            }
            var n = (int.TryParse(cols[0], out var added) ? added : 0) + (int.TryParse(cols[1], out var removed) ? removed : 0);
            var path = cols[2].Replace('\\', '/');
            map[path] = map.GetValueOrDefault(path) + n;
        }
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
            return new Touched(label, await github.PullRequestFilesAsync(repoPath, pr.Number, cancellationToken), null, null, OnPeer: false, PrNumber: pr.Number);
        }
        catch (FlightException ex)
        {
            return new Touched(label, null, ex.Message, null, OnPeer: false, PrNumber: pr.Number);
        }
    }

    /// <summary>
    /// The not-landed flights each reachable peer flies on this repo (matched by its GitHub remote, as paths are each machine's own), each
    /// with what it changes as that peer says it (<see cref="ChangedByPeerAsync"/>), and a note for each peer whose flights could not be
    /// read: no grant held, grant refused, or it did not answer within <see cref="FlightCommander.PeerBudget"/>. A peer that does not hold
    /// the repo has nothing to say and no note. The peers, and the flights of a peer, are asked in parallel under the one budget.
    /// </summary>
    private async Task<(List<(FlightRow Flight, Touched Touched)> Flights, List<(string Label, string Why)> Notes)> PeerFlightsAsync(FlightRow mine, CancellationToken cancellationToken)
    {
        var found = new List<(FlightRow, Touched)>();
        var notes = new List<(string, string)>();
        var peers = flights.Fleet?.Peers() ?? [];
        var gitHub = RepoCatalog.GitHubOf(Path.Combine(mine.RepoPath, ".git"));
        if (peers.Count == 0 || gitHub is null)
        {
            return (found, notes);
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(FlightCommander.PeerBudget);
        var reads = await Task.WhenAll(peers.Select(p => ReadPeerAsync(p, gitHub, mine, budget.Token, cancellationToken)));
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

    private async Task<(List<(FlightRow, Touched)> Flights, (string Label, string Why)? Note)> ReadPeerAsync(
        IFleetPeer peer, string gitHub, FlightRow mine, CancellationToken budget, CancellationToken caller)
    {
        var ownBranch = mine.Branch;
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
            var touched = await Task.WhenAll(theirs.Select(f => ChangedByPeerAsync(peer, node, f, mine.RepoPath, budget, caller)));
            return ([.. theirs.Zip(touched)], null);
        }
        catch (Exception ex) when (DidNotAnswer(ex, caller))
        {
            return ([], (peer.Endpoint, $"this node did not answer ({FirstLine(ex.Message)}), so its flights are not known"));
        }
    }

    private static bool DidNotAnswer(Exception ex, CancellationToken caller) =>
        ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or WebSocketException && !caller.IsCancellationRequested;

    /// <summary>
    /// What a flight of a peer changes, as its node says it (<see cref="Verbs.FlightChanges"/>: read from its plane, so a branch not
    /// pushed is no matter). A node that refuses the verb (an older one, or a plane it no longer has) is read by the flight's pushed branch
    /// as before. Unknown only when the node did not answer, saying why.
    /// </summary>
    private async Task<Touched> ChangedByPeerAsync(IFleetPeer peer, string node, FlightRow other, string repoPath, CancellationToken budget, CancellationToken caller)
    {
        var label = $"{other.Code} ({node})";
        CommandResult answer;
        try
        {
            answer = await peer.SendAsync(new CommandRequest { Verb = Verbs.FlightChanges, Target = Verbs.FlightTarget(other.Code), Who = options.Machine }, budget);
        }
        catch (Exception ex) when (DidNotAnswer(ex, caller))
        {
            return new Touched(label, null, $"{node} did not answer ({FirstLine(ex.Message)}), so what it changes is not known", other.Code, OnPeer: true, node);
        }
        if (!answer.Ok)
        {
            return (await ChangedByAsync(label, other, local: false, onPeer: true, repoPath, caller)) with { Node = node };
        }
        var lines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (answer.Made ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cols = line.Split('\t', 2);
            if (cols.Length == 2)
            {
                lines[cols[1].Replace('\\', '/')] = int.TryParse(cols[0], out var n) ? n : 0;
            }
        }
        return new Touched(label, [.. lines.Keys], null, other.Code, OnPeer: true, node, Lines: lines);
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
                ? $"when it is the same goal, flight overlap {code} {flight} {Verbs.OverlapPickArg}{Verbs.OverlapPickFold} {files} <why>; when it is another goal, flight connect {code} {flight}"
                : $"flight connect {code} {flight} so {code} branches off main once it has landed, or plan beside it when the shared files are only touched in passing";
        }
        return other.FlightCode is { } peerFlight
            ? $"it flies on another node, so it cannot be folded into from here: flight connect {code} {peerFlight} holds {code} until that node says it landed (or flight overlap {code} {peerFlight} {files} <why> to ask the controller whether to connect or plan in parallel), or plan beside it when the shared files are only touched in passing"
            : "an open PR is no flight to fold into or connect to: name it and the shared files in your planned summary, and plan the legs on those files to follow its merge";
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no reason given";
}
