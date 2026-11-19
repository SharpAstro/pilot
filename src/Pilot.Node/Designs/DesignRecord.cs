using System.Globalization;
using System.Text;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Designs;

/// <summary>
/// GitHub as the system of record for what built a design (PIL1185). A flight's PR carries a Designs section, written when it opens and kept
/// current on later pushes; each design's issue is told once that the PR builds it; when the PR merges the node closes each design issue it
/// built (lock labels off) unless another unlanded flight carries the design or the build was of an older version, which gets a comment only.
/// What was told is kept on the design (<see cref="DesignRow.Record"/>), so a push or a retry after a failed call never says a thing twice.
/// A failure is the node's log and a retry by <see cref="SweepAsync"/>; it never blocks the PR or the land.
/// </summary>
public sealed class DesignRecord(DesignStore store, RepoCatalog repos, NodeState state, IGitHub github)
{
    /// <summary>Opens the Designs section's block in a PR body.</summary>
    public const string SectionStart = "<!-- pilot:designs -->";

    /// <summary>Closes it.</summary>
    public const string SectionEnd = "<!-- /pilot:designs -->";

    /// <summary>Where a failed call is told: the node's log; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>The kind of the card raised once for a design whose files are over a limit and so not committed (PIL1208).</summary>
    public const string OversizeKind = "design-oversize";

    /// <summary>The git that places a built design's files in a plane (PIL1208); null places nothing.</summary>
    public IGit? Git { get; set; }

    private readonly SemaphoreSlim _one = new(1, 1);

    private RepoInfo? RepoOf(string? gitHub) =>
        gitHub is { Length: > 0 } ? repos.Repos().FirstOrDefault(r => r.GitHub is not null && string.Equals(r.GitHub, gitHub, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>The Designs section for <paramref name="flight"/>, block markers included; empty when it builds no design.</summary>
    public string Section(FlightRow flight)
    {
        if (flight.Designs.Length == 0)
        {
            return "";
        }
        var text = new StringBuilder();
        text.AppendLine(SectionStart);
        text.AppendLine("## Designs");
        text.AppendLine();
        foreach (var d in flight.Designs)
        {
            var design = store.Find(d.Id);
            var line = new StringBuilder($"- **{d.Id}** {design?.Title ?? d.Id}, built as v{d.Version.ToString(CultureInfo.InvariantCulture)}");
            line.Append(d.Sha256 is { Length: > 0 } sha ? $", prototype sha256 `{sha}`" : ", prototype hash not recorded");
            if (design?.IssueRepo is { Length: > 0 } repo && design.IssueNumber is { } issue)
            {
                line.Append($", design issue {repo}#{issue.ToString(CultureInfo.InvariantCulture)}");
            }
            if (design?.Record.FirstOrDefault(r => Is(r, flight.Code, d.Version)) is { } placed)
            {
                if (placed.Folder is { Length: > 0 } folder)
                {
                    line.Append(", " + DesignSyncText.Placed(folder));
                }
                else if (placed.Oversize is { Length: > 0 } note)
                {
                    line.Append(", " + note);
                }
            }
            if (design is { IssueNumber: { } n, IssueRepo: { Length: > 0 } issueRepo } && FlightRepo(flight) is { } here && !string.Equals(here, issueRepo, StringComparison.OrdinalIgnoreCase))
            {
                line.Append(", " + DesignSyncText.Elsewhere(n, issueRepo));
            }
            if (design is not null && IdeaRefs(design) is { Length: > 0 } ideas)
            {
                line.Append($", for {string.Join(", ", ideas)}");
            }
            text.AppendLine(line.ToString());
        }
        text.AppendLine(SectionEnd);
        return text.ToString();
    }

    private string? FlightRepo(FlightRow flight) =>
        repos.Repos().FirstOrDefault(r => string.Equals(r.Path, flight.RepoPath, StringComparison.OrdinalIgnoreCase))?.GitHub;

    /// <summary>
    /// Commits each built design's files into the flight's plane (<see cref="DesignRepoFiles"/>) and keeps the result on the design's record row, so the Designs section says it.
    /// An oversized design raises one card per flight, design and version. A design that cannot be read or committed is logged and skipped; it never fails the PR.
    /// </summary>
    public async Task PlaceFilesAsync(FlightRow flight, CancellationToken cancellationToken)
    {
        if (Git is not { } git || flight.Designs.Length == 0)
        {
            return;
        }
        var here = FlightRepo(flight);
        foreach (var d in flight.Designs)
        {
            try
            {
                if (store.Find(d.Id) is not { } design)
                {
                    continue;
                }
                var files = design.IssueNumber is not null && design.IssueRepo is { Length: > 0 } && here is not null && string.Equals(design.IssueRepo, here, StringComparison.OrdinalIgnoreCase)
                    ? store.ReadVersion(d.Id, d.Version) : new Dictionary<string, byte[]>();
                var result = await DesignRepoFiles.PlaceAsync(git, flight.Worktree, flight.Code, here, design, d, files, cancellationToken);
                Put(d.Id, flight.Code, d.Version, r => r with { Folder = result.Folder, Oversize = result.Note });
                if (result.Kind == DesignPlacement.Oversized)
                {
                    state.RaiseCard(CardBin.ForYou, OversizeKind, Verbs.FlightTarget(flight.Code), $"{flight.Code}: design {d.Id} v{d.Version.ToString(CultureInfo.InvariantCulture)} is not committed to the repo",
                        $"{result.Note}. The files stay on {DesignSyncText.Branch(design.IssueNumber!.Value)}; make the renders smaller and publish a new version to have them placed.",
                        [new CardChoice { Label = "Place the files again", Verb = Verbs.OpenPr, Target = Verbs.FlightTarget(flight.Code) }],
                        summary: $"The files of design {d.Id} v{d.Version.ToString(CultureInfo.InvariantCulture)} are over a size limit, so {flight.Code}'s PR does not carry them. Make the renders smaller, publish a new version, then place them again.",
                        cause: $"{flight.Code}/{d.Id}/v{d.Version.ToString(CultureInfo.InvariantCulture)}");
                }
            }
            catch (Exception ex) when (ex is DesignException or FlightException or IOException)
            {
                await Error.WriteLineAsync($"design: {d.Id}'s files were not placed in {flight.Code}'s plane: {ex.Message}");
            }
        }
    }

    private string[] IdeaRefs(DesignRow design) =>
        [.. design.Ideas().Select(key => IdeaIds.Parse(key).Ref is { Repo: { } name } r
            ? (repos.Repos().FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))?.GitHub ?? name) + "#" + r.Number.ToString(CultureInfo.InvariantCulture)
            : null).OfType<string>()];

    /// <summary><paramref name="body"/> with the section of <paramref name="section"/> put in place of the one it holds, or at its end when it holds none; the body as it was when the flight has no designs.</summary>
    internal static string Splice(string body, string section)
    {
        if (section.Length == 0)
        {
            return body;
        }
        var start = body.IndexOf(SectionStart, StringComparison.Ordinal);
        var end = body.IndexOf(SectionEnd, StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            return body[..start] + section.TrimEnd('\r', '\n') + body[(end + SectionEnd.Length)..];
        }
        return body.TrimEnd('\r', '\n') + "\n\n" + section.TrimEnd('\r', '\n') + "\n";
    }

    /// <summary>
    /// The PR of <paramref name="flight"/> is open (or took a push): its body holds the current Designs section, and each design issue is told once.
    /// Safe to say again; a failed call is logged and the next call makes it.
    /// </summary>
    public async Task OpenedAsync(FlightRow flight, int pr, string? prUrl, CancellationToken cancellationToken)
    {
        if (flight.Designs.Length == 0)
        {
            return;
        }
        await _one.WaitAsync(cancellationToken);
        try
        {
            try
            {
                var body = await github.PullRequestBodyAsync(flight.RepoPath, pr, cancellationToken);
                if (body is not null)
                {
                    var next = Splice(body, Section(flight));
                    if (!string.Equals(next, body, StringComparison.Ordinal))
                    {
                        await github.EditPullRequestBodyAsync(flight.RepoPath, pr, next, cancellationToken);
                    }
                }
            }
            catch (FlightException ex)
            {
                await Error.WriteLineAsync($"design: {flight.Code}'s PR #{pr} body has no current Designs section: {ex.Message}");
            }
            foreach (var d in flight.Designs)
            {
                var design = store.Find(d.Id);
                if (design is not { IssueNumber: { } issue } || RepoOf(design.IssueRepo) is not { } info
                    || design.Record.Any(r => r.Opened == true && Is(r, flight.Code, d.Version)))
                {
                    continue;
                }
                try
                {
                    await github.CommentIssueAsync(info.Path, issue, $"{flight.Code} builds v{d.Version.ToString(CultureInfo.InvariantCulture)} of this design: {prUrl ?? "#" + pr.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
                    Put(d.Id, flight.Code, d.Version, r => r with { Pr = pr, Opened = true });
                }
                catch (FlightException ex)
                {
                    await Error.WriteLineAsync($"design: {d.Id} got no comment that {flight.Code} builds it: {ex.Message}");
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private static bool Is(DesignRecordRow row, string flight, int version) =>
        string.Equals(row.Flight, flight, StringComparison.OrdinalIgnoreCase) && row.Version == version;

    /// <summary>Changes the row of (flight, version) of design <paramref name="id"/>, made when it is not there yet.</summary>
    private void Put(string id, string flight, int version, Func<DesignRecordRow, DesignRecordRow> change) =>
        store.Update(id, design =>
        {
            var found = design.Record.FirstOrDefault(r => Is(r, flight, version)) ?? new DesignRecordRow { Flight = flight, Version = version };
            return design with { Record = [.. design.Record.Where(r => !Is(r, flight, version)), change(found)] };
        });

    /// <summary>
    /// The PR <paramref name="pr"/> of <paramref name="flight"/> merged as <paramref name="mergedAt"/>: each design's result is written to GitHub, and remembered, so a retry never doubles it.
    /// Never throws for GitHub's refusal: it is logged, and <see cref="SweepAsync"/> tries again.
    /// </summary>
    public async Task LandedAsync(FlightRow flight, int pr, string mergedAt, CancellationToken cancellationToken)
    {
        if (flight.Designs.Length == 0)
        {
            return;
        }
        await _one.WaitAsync(cancellationToken);
        try
        {
            foreach (var d in flight.Designs)
            {
                // The merge is on the row before anything is said, so the sweep can finish what a failed call left.
                Put(d.Id, flight.Code, d.Version, r => r with { Pr = r.Pr ?? pr, MergedAt = r.MergedAt ?? mergedAt });
                await TryLandedAsync(d.Id, flight.Code, d.Version, cancellationToken);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Writes the result of every merged flight that is not written yet (a failed call earlier, or a restart between): the timer's round.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            foreach (var design in store.List())
            {
                foreach (var row in design.Record.Where(r => r.MergedAt is not null && r.Landed is null).ToList())
                {
                    await TryLandedAsync(design.Id, row.Flight, row.Version, cancellationToken);
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task TryLandedAsync(string id, string code, int version, CancellationToken cancellationToken)
    {
        try
        {
            await WriteLandedAsync(id, code, version, cancellationToken);
        }
        catch (FlightException ex)
        {
            await Error.WriteLineAsync($"design: {id} was not told that {code} built v{version.ToString(CultureInfo.InvariantCulture)}: {ex.Message}");
        }
    }

    private async Task WriteLandedAsync(string id, string code, int version, CancellationToken cancellationToken)
    {
        var design = store.Find(id);
        var row = design?.Record.FirstOrDefault(r => Is(r, code, version));
        if (design is not { IssueNumber: { } issue } || row is not { Landed: null } || RepoOf(design.IssueRepo) is not { } info)
        {
            return;
        }
        var built = $"Built as v{version.ToString(CultureInfo.InvariantCulture)} by {code} in #{row.Pr?.ToString(CultureInfo.InvariantCulture)}, merged at {row.MergedAt}";
        var current = design.Current ?? version;
        var other = state.Flights().FirstOrDefault(f => f.State != FlightState.Landed && !string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase)
            && f.Designs.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)));
        if (version < current)
        {
            await github.CommentIssueAsync(info.Path, issue, $"{built}; built v{version.ToString(CultureInfo.InvariantCulture)}; the design is at v{current.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
            Put(id, code, version, r => r with { Landed = "commented" });
            return;
        }
        if (other is not null)
        {
            await github.CommentIssueAsync(info.Path, issue, $"{built}; {other.Code} still builds this design, so the issue stays open until it lands", cancellationToken);
            Put(id, code, version, r => r with { Landed = "commented" });
            return;
        }
        // The lock labels first: a close that fails leaves an issue open with no lock, which the next round closes.
        var labels = await github.IssueLabelsOrThrowAsync(info.Path, issue, cancellationToken);
        if (labels.Contains(PilotMarks.Editing, StringComparer.OrdinalIgnoreCase))
        {
            await github.RemoveIssueLabelAsync(info.Path, issue, PilotMarks.Editing, cancellationToken);
        }
        foreach (var node in labels.Select(PilotMarks.NodeOf).Where(n => n is { Length: > 0 }))
        {
            await github.RemoveIssueLabelAsync(info.Path, issue, PilotMarks.NodeLabel(node!), cancellationToken);
        }
        store.Update(id, r => r with { LockWanted = false, LockHeld = false, LockedBy = null, LockError = null });
        await github.CloseIssueAsync(info.Path, issue, built, cancellationToken);
        Put(id, code, version, r => r with { Landed = "closed" });
    }
}
