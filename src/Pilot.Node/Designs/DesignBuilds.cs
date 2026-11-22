using System.Globalization;
using System.Text;
using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node.Designs;

/// <summary>
/// <c>design-build</c> (PIL1153): builds one published version of a design. It files one issue in the design's repo holding the brief, the version's
/// NOTES.md, its renders (each screen with the path of its file on the node, since gh takes no image) and the path and SHA-256 of its
/// <c>prototype.json</c>, makes one flight on that issue that remembers the design, the version and the hash, and records the build on the design. A design that
/// came from an idea plans that idea (plan-idea's path) instead of filing a second issue. A version whose flight is still in the air is not built again.
/// </summary>
public sealed class DesignBuilds(NodeState state, DesignStore store, RepoCatalog repos, Planes planes, IGitHub github, FlightCommander flights, IdeaCommander ideas, TimeProvider time)
{
    // Two builds must not both make a flight for one version.
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>Where a refusal after the issue is filed, or a comment that could not be made, is told: the node's log; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    public async Task<CommandResult> RunAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken);
        try
        {
            return await BuildAsync(command, cancellationToken);
        }
        catch (Exception ex) when (ex is DesignException or FlightException)
        {
            return state.Complete(command, ex.Message);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<CommandResult> BuildAsync(CommandRequest command, CancellationToken cancellationToken)
    {
        var args = command.Args ?? [];
        string Arg(int at) => at < args.Length ? args[at].Trim() : "";
        var id = DesignStore.Normalise(Verbs.DesignIdOf(command.Target) is { Length: > 0 } given ? given : throw new DesignException($"design-build takes a design target, {Verbs.DesignTargetPrefix}DSN-<n>, not {command.Target}"));
        var design = store.Find(id) ?? throw new DesignException($"There is no design {id}");
        if (design.Current is not { } current)
        {
            throw new DesignException($"{id} has no published version to build: publish one first (pilot design publish {id} --why ...)");
        }
        var version = current;
        if (Arg(0).Length > 0)
        {
            if (!int.TryParse(Arg(0), NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 1)
            {
                throw new DesignException($"{Arg(0)} is no version of {id}: a version is a number, 1 to {design.Versions ?? current}");
            }
        }
        var folder = Path.Combine(store.FolderOf(id), DesignStore.VersionsFolderName, "v" + version.ToString(CultureInfo.InvariantCulture));
        var prototype = Path.Combine(folder, DesignStore.PrototypeFile);
        if (!File.Exists(prototype))
        {
            throw new DesignException($"{id} has no version {version} (it has v1 to v{design.Versions ?? current})");
        }

        // Refused while a flight built from this version is in the air; a landed one (or one withdrawn, which the node no longer lists) lets it be built again.
        var flying = design.Builds.Where(b => b.Version == version && b.Flight is { Length: > 0 })
            .Select(b => state.Flights().FirstOrDefault(f => string.Equals(f.Code, b.Flight, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(f => f is { State: not FlightState.Landed })
            ?? state.Flights().FirstOrDefault(f => f.Designs.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) && d.Version == version) && f.State != FlightState.Landed);
        if (flying is not null)
        {
            throw new DesignException($"{id} v{version} is being built already: flight {flying.Code} is {flying.State.ToString().ToLowerInvariant()}; build it again once that flight has landed or been withdrawn");
        }

        var idea = design.Ideas().FirstOrDefault() is { Length: > 0 } key ? IdeaIds.Parse(key).Ref : null;
        var text = IssueText(design, version, folder, prototype);
        var title = $"Build {design.Title ?? id} ({id} v{version.ToString(CultureInfo.InvariantCulture)})";
        var sha = DesignStore.Sha256Of(prototype);

        string code;
        string repoName;
        int issue;
        string? way;
        if (idea is { } from && design.Builds.Length == 0 && !state.Flights().Any(f => f.Designs.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))))
        {
            // The first build of an idea-born design: the idea's own issue is the build's: plan-idea makes its flight (and refuses with the open milestones when none is named); no second issue is filed.
            var repoOfIdea = from.Repo ?? (Arg(1).Length > 0 ? Arg(1) : design.Repo) ?? IdeaIds.DefaultRepo;
            var (name, path) = await RepoOfAsync(repoOfIdea, cancellationToken);
            var planned = await ideas.PlanForDesignBuildAsync(command with
            {
                Verb = Verbs.PlanIdea,
                Target = IdeaIds.Target(name, from.Number),
                Args = Arg(2).Length > 0 ? [Arg(2)] : [],
                RequestId = null,
            }, cancellationToken);
            if (planned is not { Ok: true, Made: { Length: > 0 } plannedCode })
            {
                throw new FlightException(planned.Error ?? $"{design.Ideas().FirstOrDefault()} was not planned");
            }
            code = plannedCode;
            repoName = name;
            issue = from.Number;
            way = planned.Way;
            try
            {
                await github.CommentIssueAsync(path, issue, text, cancellationToken);
            }
            catch (FlightException ex)
            {
                await Error.WriteLineAsync($"design: {id} v{version} is built as {code}, but the idea's issue {issue} got no comment of it: {ex.Message}");
            }
        }
        else
        {
            // A later build of an idea-born design (the idea has its flight and has left the Ideas milestone) files a fresh issue in the idea's repo, as a design with no idea does.
            var repoGiven = Arg(1).Length > 0 ? Arg(1) : design.Repo ?? idea?.Repo;
            if (repoGiven is not { Length: > 0 })
            {
                throw new DesignException($"{id} names no repo and none is given: build it into one with --repo <name>");
            }
            var (name, path) = await RepoOfAsync(repoGiven, cancellationToken);
            repoName = name;
            issue = Arg(2).Length > 0
                ? (await github.CreateIssueInMilestoneAsync(path, title, text, Arg(2), cancellationToken)).Number
                : (await github.CreateIssueAsync(path, title, text, cancellationToken)).Number;
            var made = await flights.RunAsync(command with
            {
                Verb = Verbs.CreateFlight,
                Target = Verbs.RepoTarget(path),
                Args = [issue.ToString(CultureInfo.InvariantCulture), title],
                RequestId = null,
            }, cancellationToken);
            if (made is not { Ok: true, Made: { Length: > 0 } madeCode })
            {
                throw new FlightException($"issue {issue} is filed in {name}, but no flight was made on it: {made.Error}");
            }
            code = madeCode;
            way = made.Way;
        }

        // A flight made on another node (maintenance) is not in these rows: that node holds the flight, and the build is recorded here all the same.
        if (state.Flights().FirstOrDefault(f => f.Code == code) is { } flight)
        {
            state.PutFlight(flight with { Designs = [.. flight.Designs.Where(d => !string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)), new FlightDesignRow { Id = id, Version = version, Sha256 = sha }] });
        }
        store.Update(id, row => row with
        {
            Builds = [.. row.Builds, new DesignBuildRow { Version = version, Flight = code, Repo = repoName, Issue = issue, At = time.GetUtcNow() }],
        });
        return state.Complete(command, null, way: way, made: code);
    }

    private async Task<(string Name, string Path)> RepoOfAsync(string given, CancellationToken cancellationToken)
    {
        if (repos.Repos().FirstOrDefault(r => string.Equals(r.Name, given, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return (named.Name, named.Path);
        }
        var main = await planes.MainCheckoutAsync(given, cancellationToken)
            ?? throw new DesignException($"{given} is no repo this node lists and no git checkout");
        return (System.IO.Path.GetFileName(main.TrimEnd('/', '\\')), main);
    }

    /// <summary>The issue's text: the brief, the version's notes, its renders by screen with their paths on the node, and the prototype's path and hash.</summary>
    private string IssueText(DesignRow design, int version, string folder, string prototype)
    {
        var id = design.Id;
        var brief = Path.Combine(store.FolderOf(id), DesignStore.BriefFile);
        // The document (DESIGN.md) where the version has one; an older version's NOTES.md otherwise.
        var notes = File.Exists(Path.Combine(folder, DesignStore.DocumentFile)) ? Path.Combine(folder, DesignStore.DocumentFile) : Path.Combine(folder, DesignStore.NotesFile);
        var renders = Path.Combine(folder, DesignChecker.RendersFolderName);
        var text = new StringBuilder();
        text.AppendLine($"Build design {id} version {version.ToString(CultureInfo.InvariantCulture)}{(design.Title is { Length: > 0 } t ? $": {t}" : "")}.");
        text.AppendLine();
        text.AppendLine("The flight's legs build to this version, and the review holds the build to it. The files are on the node that filed this issue; read them with `pilot design show " + id + "`.");
        text.AppendLine();
        text.AppendLine("## Brief");
        text.AppendLine();
        text.AppendLine(File.Exists(brief) ? File.ReadAllText(brief).Trim() : "(none)");
        text.AppendLine();
        text.AppendLine(Path.GetFileName(notes) == DesignStore.DocumentFile ? "## Design" : "## Notes");
        text.AppendLine();
        text.AppendLine(File.Exists(notes) && File.ReadAllText(notes).Trim() is { Length: > 0 } written ? written : "(none)");
        text.AppendLine();
        text.AppendLine("## Renders");
        text.AppendLine();
        var files = Directory.Exists(renders) ? Directory.EnumerateFiles(renders, "*.png").Order(StringComparer.Ordinal).ToArray() : [];
        if (files.Length == 0)
        {
            text.AppendLine("(none)");
        }
        foreach (var file in files)
        {
            text.AppendLine($"- {Path.GetFileNameWithoutExtension(file)}: `{file}`");
        }
        text.AppendLine();
        text.AppendLine("## Prototype");
        text.AppendLine();
        text.AppendLine($"- Path: `{prototype}`");
        text.AppendLine($"- SHA-256: `{DesignStore.Sha256Of(prototype)}`");
        return text.ToString().TrimEnd();
    }
}
