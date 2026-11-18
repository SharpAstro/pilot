using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// Build this version (PIL1153): the design page's one declaration, drawn by the window and the terminal alike. The button sends <c>design-build</c> for the
/// version on show; a design with no repo asks for one first, and a design that came from an idea asks for the milestone its idea is planned into. The page
/// then shows the flights built from that version (the snapshot's <c>FromDesign</c> and <c>FromDesignVersion</c>) with their state and PR, and while one
/// flies the button is not offered.
/// </summary>
internal sealed partial class DesignsView<TSurface>
{
    /// <summary>Asked to build a version (PIL1153): the design's id, the version, the repo and the milestone (each null when the page did not ask). The host sends <c>design-build</c> and answers with <see cref="Built"/>.</summary>
    public Action<string, int, string?, string?>? OnBuild { get; set; }

    /// <summary>Asked for a flight's page (its code): the link on a built version's line.</summary>
    public Action<string>? OnFlight { get; set; }

    /// <summary>Asked to open a web page (its address): the link on a built version's PR.</summary>
    public Action<string>? OnUrl { get; set; }

    private const string BuildKey = "designs:build";

    /// <summary>The label of the button: what it does to the version on show.</summary>
    internal const string BuildLabel = "Build this version";

    // What the page asks while a build is being put together: the design and version it is for (a change of either forgets it), the choices made so far.
    private (string Id, int Version)? _buildFor;
    private bool _buildAsking;
    private string? _buildRepo;
    private string? _buildMilestone;
    private string? _buildNote;
    private DesignsScreen? _buildScreen;

    private sealed record BuildAnswer(string Id, int Version, string? Flight, string? Why);
    private BuildAnswer? _buildAnswer;

    /// <summary>What the node answered to the last build when it refused, or the code of the flight it made; null before any.</summary>
    internal string? BuildNote => _buildNote;

    /// <summary>Whether the page is asking for a repo or a milestone before it sends.</summary>
    internal bool BuildAsking => _buildAsking;

    private bool Building => StartPresses.PendingOf(BuildKey) is not null;

    // A design with a repo of its own, or one from an idea (whose repo is the idea's), asks for no repo.
    internal static bool NeedsRepo(DesignRow row) => string.IsNullOrWhiteSpace(row.Repo) && string.IsNullOrWhiteSpace(row.FromIdea);

    // Only the first build plans the idea; a later one files a fresh issue in the idea's repo and asks for no milestone.
    internal static bool NeedsMilestone(DesignRow row) => !string.IsNullOrWhiteSpace(row.FromIdea) && row.Builds.Length == 0;

    /// <summary>The flights built from the version <paramref name="version"/> of the design <paramref name="id"/> on the screen, oldest first.</summary>
    internal static FlightRow[] BuiltFrom(DesignsScreen? screen, string id, int version) => DesignReads.BuiltOf(screen?.Flights, id, version);

    /// <summary>The flight of that version that is still in the air (not landed), or null: while there is one the node refuses a second build.</summary>
    internal static FlightRow? InTheAir(DesignsScreen? screen, string id, int version) => BuiltFrom(screen, id, version).LastOrDefault(f => f.State != FlightState.Landed);

    /// <summary>The open milestones the idea of <paramref name="row"/> can be planned into: those of its repo, newest first.</summary>
    internal static MilestoneRow[] MilestonesFor(DesignsScreen? screen, DesignRow row)
    {
        var repo = IdeaIds.Parse(row.FromIdea ?? "").Ref?.Repo ?? RepoName(row.Repo) ?? IdeaIds.DefaultRepo;
        return [.. (screen?.Milestones ?? []).Where(m => string.Equals(m.Repo, repo, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Number)];
    }

    private static string? RepoName(string? repo) => string.IsNullOrWhiteSpace(repo) ? null : repo.Trim().Split('/', '\\')[^1];

    /// <summary>
    /// Presses Build this version for the version on show: asks for the repo, then the milestone, where the design needs them, and sends once it has them. False
    /// while it asks, while a build is with the node, while a flight of the version flies, or with no version or host.
    /// </summary>
    public bool Build()
    {
        if (_buildScreen?.Open is not { Version: { } version } page || OnBuild is null || Building || InTheAir(_buildScreen, page.Detail.Row.Id, version) is not null)
        {
            return false;
        }
        var row = page.Detail.Row;
        _buildNote = null;
        if (NeedsRepo(row) && _buildRepo is null || NeedsMilestone(row) && _buildMilestone is null)
        {
            _buildAsking = true;
            return false;
        }
        _buildAsking = false;
        StartPresses.NotePending(BuildKey, BuildKey);
        OnBuild(row.Id, version, NeedsRepo(row) ? _buildRepo : null, NeedsMilestone(row) ? _buildMilestone : null);
        return true;
    }

    /// <summary>Takes <paramref name="repo"/> as the repo to build in, and goes on to the next question or sends.</summary>
    public bool PickBuildRepo(string repo)
    {
        _buildRepo = repo;
        return Build();
    }

    /// <summary>Takes <paramref name="milestone"/> as the milestone the idea is planned into, and sends.</summary>
    public bool PickBuildMilestone(string milestone)
    {
        _buildMilestone = milestone;
        return Build();
    }

    /// <summary>Drops the question and the choices made: the Cancel of the ask.</summary>
    public void CancelBuild()
    {
        _buildAsking = false;
        _buildRepo = null;
        _buildMilestone = null;
    }

    /// <summary>The host's answer to a build: the flight it made (<paramref name="flight"/>) or why the node refused (<paramref name="why"/>). Safe from any thread.</summary>
    public void Built(string id, int version, string? flight, string? why) =>
        Volatile.Write(ref _buildAnswer, new BuildAnswer(id, version, flight, why));

    private void ApplyBuildAnswer()
    {
        if (Interlocked.Exchange(ref _buildAnswer, null) is not { } answer)
        {
            return;
        }
        StartPresses.NotePending(BuildKey, null);
        _buildRepo = null;
        _buildMilestone = null;
        _buildNote = answer.Flight is { } code ? $"Built as {code}." : answer.Why ?? "the node refused";
    }

    // The page's own state belongs to the design and version on show: another one starts with no question asked and no choice made.
    private void NoteBuildTarget(DesignsScreen screen)
    {
        _buildScreen = screen;
        var shown = screen.Open is { Version: { } v } open ? (open.Detail.Row.Id, v) : ((string, int)?)null;
        if (_buildFor != shown)
        {
            _buildFor = shown;
            _buildAsking = false;
            _buildRepo = null;
            _buildMilestone = null;
            _buildNote = null;
        }
    }

    // The right column's Build part: the flights built from the version on show, one line each (code linking to the flight, its state, its PR linking to GitHub),
    // then the button, or the question it asks first. Each line is one height with its middle, and what does not fit is cut with an ellipsis and whole on hover.
    private IEnumerable<Layout.Node> BuildPart(DesignPage page)
    {
        if (page.Version is not { } version)
        {
            yield break;
        }
        var row = page.Detail.Row;
        yield return Heading("Build");
        var built = BuiltFrom(_buildScreen, row.Id, version);
        foreach (var flight in built.OrderByDescending(f => f.CreatedAt).Take(3))
        {
            yield return BuiltLine(flight);
        }
        if (InTheAir(_buildScreen, row.Id, version) is not null)
        {
            yield break;
        }
        if (_buildAsking && !Building)
        {
            foreach (var node in BuildAsk(row))
            {
                yield return node;
            }
        }
        else
        {
            yield return BuildButtonRow(row, version);
        }
        if (_buildNote is { } note)
        {
            yield return WrappedProse.OneLine(note, 12f, Bright).RowH(22f).Whole(note);
        }
    }

    private Layout.Node BuiltLine(FlightRow flight)
    {
        var code = flight.Code;
        var state = flight.State == FlightState.Landed ? "landed" : flight.OnHold is not null ? "on hold" : flight.State.ToString().ToLowerInvariant();
        var text = flight.PullRequest is { } pr ? $"{state}, PR #{pr}" : state;
        Layout.Node Link(string label, string hit, string tip, Action? go) =>
            go is null
                ? Layout.Builder.Text(label, 12f, Bright, TextAlign.Center, TextAlign.Center).Pad(8f, 0f).WAuto().HFixed(26f)
                : Layout.Builder.Text(label, 12f, Bright, TextAlign.Center, TextAlign.Center).Pad(8f, 0f).WAuto().HFixed(26f).Bg(Button).BgHover(ButtonHover)
                    .WithTooltip(tip).Follows(new HitResult.ButtonHit(hit), _ => go());
        List<Layout.Node> parts =
        [
            Link(code, $"designs:flight:{code}", $"Open the flight {code}", OnFlight is null ? null : () => OnFlight(code)),
            WrappedProse.OneLine(state, 12f, Dim, selectable: false, vAlign: TextAlign.Center).WStar().HFixed(26f).Whole(text),
        ];
        if (flight.PullRequestUrl is { Length: > 0 } url && flight.PullRequest is { } number)
        {
            parts.Add(Link($"PR #{number}", $"designs:pr:{code}", $"Open {url}", OnUrl is null ? null : () => OnUrl(url)));
        }
        return Layout.Builder.HStack([.. parts]).WithGap(6f).RowH(26f);
    }

    private Layout.Node BuildButtonRow(DesignRow row, int version)
    {
        Layout.Node Label(string text, RGBAColor32 color) => Layout.Builder.Text(text, 12f, color, TextAlign.Center, TextAlign.Center).Pad(10f, 0f).WAuto().Bg(Button);
        var target = NeedsRepo(row) ? "in the repo you pick" : NeedsMilestone(row) ? $"its idea {row.FromIdea}, in the milestone you pick" : $"in {row.Repo}";
        var button = StartPresses.Button(BuildKey, BuildKey,
            () => Label(BuildLabel, Bright)
                .BgHover(ButtonHover)
                .WithTooltip($"Files an issue with v{version} of {row.Id} and flies it {target}; the review holds the build to this version")
                .Follows(new HitResult.ButtonHit(BuildKey), _ => Build()),
            _ => Label("Building…", Dim).WithTooltip($"Building v{version} of {row.Id}"));
        return Layout.Builder.HStack(button.WAuto().HFixed(26f)).WithGap(6f).RowH(26f);
    }

    // The question before the send: the repos of the node (a design with no repo), then the open milestones of the idea's repo, as chips of one row height.
    private IEnumerable<Layout.Node> BuildAsk(DesignRow row)
    {
        if (NeedsRepo(row) && _buildRepo is null)
        {
            yield return WrappedProse.OneLine("Which repo is it built in?", 12f, Bright, selectable: false).RowH(22f).Whole("Which repo is it built in?");
            var repos = _buildScreen?.Repos ?? [];
            if (repos.Count == 0)
            {
                yield return WrappedProse.OneLine("The node lists no repos.", 12f, Dim).RowH(22f).Whole("The node lists no repos.");
            }
            else
            {
                foreach (var repo in repos)
                {
                    var chosen = repo;
                    yield return Chip(chosen, $"designs:build-repo:{chosen}", $"Build in {chosen}", () => { PickBuildRepo(chosen); });
                }
            }
        }
        else
        {
            yield return WrappedProse.OneLine("Which milestone is its idea planned into?", 12f, Bright, selectable: false).RowH(22f).Whole("Which milestone is its idea planned into?");
            var milestones = MilestonesFor(_buildScreen, row);
            if (milestones.Length == 0)
            {
                yield return WrappedProse.OneLine("No open milestone in its repo.", 12f, Dim).RowH(22f).Whole("No open milestone in its repo.");
            }
            else
            {
                foreach (var m in milestones)
                {
                    var chosen = m;
                    yield return Chip(chosen.Title, $"designs:build-milestone:{chosen.Number}", $"Plan the idea into {chosen.Title}", () => { PickBuildMilestone(chosen.Title); });
                }
            }
        }
        yield return Chip("Cancel", "designs:build-cancel", "Do not build", CancelBuild);
    }

    // One full-width row of the column's one row height, like a version's: a long name ends in an ellipsis and the tooltip says it whole, never a chip wider than its column.
    private static Layout.Node Chip(string label, string hit, string tip, Action press) =>
        WrappedProse.OneLine(label, 12f, Bright, selectable: false, vAlign: TextAlign.Center).Pad(10f, 4f).Bg(Button).BgHover(ButtonHover)
            .WithTooltip(tip).Follows(new HitResult.ButtonHit(hit), _ => press()).RowH(26f);
}
