using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>A flight the wizard is to make: the repo it flies in, and the issue it does there.</summary>
internal sealed record NewFlightPick(RepoInfo Repo, IssueInfo Issue);

/// <summary>
/// The Flights page's new-flight wizard, in three steps: the repos (one or more, in the order their flights land: a
/// library before the app that takes it), an issue in each, and a review of what will be made. Create makes a flight per
/// repo, has each wait for the one before it, and starts a planner on each (<see cref="NewFlights"/>), so no command is
/// typed. The page shows it in place of the flights while it is open; its data comes through the host's callbacks, so the
/// window and the terminal run the same wizard.
/// </summary>
internal sealed class NewFlightWizard
{
    public enum Step
    {
        Repos,
        Issues,
        Review,
        Creating,
    }

    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Accent = new(0x6f, 0x9b, 0xd8, 0xff);
    private static readonly RGBAColor32 Warn = new(0xe0, 0x5a, 0x5a, 0xff);
    private static readonly RGBAColor32 RowHoverBg = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 ChosenBg = new(0x26, 0x33, 0x4a, 0xff);
    private static readonly RGBAColor32 Button = new(0x2c, 0x35, 0x4a, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x3a, 0x46, 0x62, 0xff);
    private static readonly RGBAColor32 Go = new(0x1f, 0x6b, 0x45, 0xff);
    private static readonly RGBAColor32 GoHover = new(0x27, 0x85, 0x56, 0xff);

    private const float RowHeight = 30f;

    private readonly ListScrollController _repoScroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly ListScrollController _issueScroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly List<RepoInfo> _chosen = [];
    private readonly Dictionary<string, IssueInfo[]> _issues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _issueErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IssueInfo> _picked = new(StringComparer.OrdinalIgnoreCase);
    private RepoInfo[]? _repos;
    private string? _reposError;
    private int _issueFor;

    /// <summary>Whether the page shows the wizard.</summary>
    public bool IsOpen { get; private set; }

    public Step Current { get; private set; }

    /// <summary>The filter over the list on the current step: a repo's name, or an issue's number or words.</summary>
    public TextInputState Filter { get; } = new();

    /// <summary>What became of the last Create, or why it could not run.</summary>
    public string? Status { get; private set; }

    /// <summary>The repos a flight can be made in (the node's <c>GET /api/v1/repos</c>).</summary>
    public Func<CancellationToken, Task<RepoInfo[]>>? LoadRepos { get; set; }

    /// <summary>A repo's open issues (the node's <c>GET /api/v1/repos/issues</c>), by its checkout.</summary>
    public Func<string, CancellationToken, Task<IssueInfo[]>>? LoadIssues { get; set; }

    /// <summary>Makes the flights; returns what became of them, in a line.</summary>
    public Func<IReadOnlyList<NewFlightPick>, Task<string>>? Create { get; set; }

    /// <summary>Called when something it loaded or did changes what it shows, from whichever thread: the host redraws.</summary>
    public Action? Changed { get; set; }

    /// <summary>The flights it would make now, in order.</summary>
    public IReadOnlyList<NewFlightPick> Picks => [.. _chosen.Where(r => _picked.ContainsKey(r.Path)).Select(r => new NewFlightPick(r, _picked[r.Path]))];

    public void Open()
    {
        IsOpen = true;
        Current = Step.Repos;
        _chosen.Clear();
        _picked.Clear();
        _issues.Clear();
        _issueErrors.Clear();
        _repos = null;
        _reposError = null;
        Status = null;
        ClearFilter();
        if (LoadRepos is { } load)
        {
            _ = LoadAsync(async () => _repos = await load(CancellationToken.None), ex => _reposError = ex.Message);
        }
    }

    public void Close()
    {
        IsOpen = false;
        ClearFilter();
    }

    /// <summary>Chooses <paramref name="repo"/>, or unchooses it: its place in the list is the order its flight lands in.</summary>
    public void Toggle(RepoInfo repo)
    {
        if (_chosen.RemoveAll(r => string.Equals(r.Path, repo.Path, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            _chosen.Add(repo);
        }
        else
        {
            _picked.Remove(repo.Path);
        }
    }

    /// <summary>On to the issues: the first chosen repo's, loaded now.</summary>
    public void ToIssues()
    {
        if (_chosen.Count == 0)
        {
            return;
        }
        Current = Step.Issues;
        _issueFor = 0;
        ClearFilter();
        LoadIssuesFor(_chosen[0]);
    }

    /// <summary>Picks <paramref name="issue"/> for the repo whose turn it is, and moves to the next repo, or to the review.</summary>
    public void Pick(IssueInfo issue)
    {
        _picked[_chosen[_issueFor].Path] = issue;
        ClearFilter();
        if (_issueFor + 1 < _chosen.Count)
        {
            _issueFor++;
            LoadIssuesFor(_chosen[_issueFor]);
        }
        else
        {
            Current = Step.Review;
        }
    }

    public void Back()
    {
        ClearFilter();
        switch (Current)
        {
            case Step.Issues when _issueFor > 0:
                _issueFor--;
                break;
            case Step.Issues:
                Current = Step.Repos;
                break;
            case Step.Review:
                Current = Step.Issues;
                _issueFor = _chosen.Count - 1;
                break;
        }
    }

    /// <summary>Makes the flights, then closes: they show in the list, with their planners at work.</summary>
    public async Task CreateAsync()
    {
        if (Create is not { } create || Picks is not { Count: > 0 } picks)
        {
            return;
        }
        Current = Step.Creating;
        Status = $"making {picks.Count} {(picks.Count == 1 ? "flight" : "flights")}…";
        Changed?.Invoke();
        try
        {
            Status = await create(picks);
            IsOpen = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Status = $"failed: {ex.Message}";
            Current = Step.Review;
        }
        Changed?.Invoke();
    }

    private void LoadIssuesFor(RepoInfo repo)
    {
        if (_issues.ContainsKey(repo.Path) || LoadIssues is not { } load)
        {
            return;
        }
        _issueErrors.Remove(repo.Path);
        _ = LoadAsync(async () => _issues[repo.Path] = await load(repo.Path, CancellationToken.None), ex => _issueErrors[repo.Path] = ex.Message);
    }

    private async Task LoadAsync(Func<Task> load, Action<Exception> failed)
    {
        try
        {
            await load().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            failed(ex);
        }
        Changed?.Invoke();
    }

    private void ClearFilter()
    {
        Filter.Text = "";
        Filter.CursorPos = 0;
    }

    /// <summary>The wizard as a tree, in the space the flights take otherwise.</summary>
    internal Layout.Node Tree()
    {
        Filter.Placeholder = Current == Step.Repos ? "filter the repos by name" : "filter the issues by number or words";
        var body = Current switch
        {
            Step.Repos => Repos(),
            Step.Issues => Issues(),
            _ => Review(),
        };
        return Layout.Builder.VStack(body, Footer()).Pad(16f, 8f).Stretch();
    }

    private Layout.Node Title(string text, string hint) =>
        Layout.Builder.VStack(
                Layout.Builder.Text(text, 18f, Bright).RowH(28f),
                Layout.Builder.Text(hint, 12f, Dim).RowH(20f))
            .RowH(52f);

    private Layout.Node FilterBar() =>
        Layout.Builder.TextInput(Filter, 14f, focusOnOpen: true).RowH(28f);

    private Layout.Node Repos()
    {
        var rows = new List<Layout.Node>
        {
            Title("New flight · 1. Repos",
                "Pick the repos the work is in, in the order their flights land: a library before the app that takes it. Each gets a flight that waits for the one before."),
            FilterBar(),
        };
        if (_repos is null)
        {
            rows.Add(Layout.Builder.Text(_reposError is { } error ? $"the node could not list its repos: {error}" : "loading the repos…", 13f, _reposError is null ? Dim : Warn).RowH(RowHeight));
            return Layout.Builder.VStack([.. rows]).HStar();
        }
        var filter = Filter.Text.Trim();
        var shown = _repos.Where(r => filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (r.GitHub?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (_repos.Length == 0)
        {
            rows.Add(Layout.Builder.Text("no repos yet: start the node with --repo-root <the folder your clones are in>", 13f, Dim).RowH(RowHeight));
        }
        var list = shown.Select(repo =>
        {
            var order = _chosen.FindIndex(r => string.Equals(r.Path, repo.Path, StringComparison.OrdinalIgnoreCase));
            var row = Layout.Builder.HStack(
                    Layout.Builder.Text(order >= 0 ? $"{order + 1}" : "", 14f, Accent, TextAlign.Center).WFixed(28f),
                    Layout.Builder.Text(repo.Name, 14f, Bright).WFixed(260f),
                    Layout.Builder.Text(repo.GitHub ?? "not on GitHub", 12f, Dim).WStar(),
                    Layout.Builder.Text(repo.Flights == 0 ? "" : $"{repo.Flights} {(repo.Flights == 1 ? "flight" : "flights")}", 12f, Dim, TextAlign.Far).WFixed(90f))
                .WithGap(10f)
                .PadX(6f)
                .RowH(RowHeight)
                .BgHover(RowHoverBg)
                .Clickable(new HitResult.ButtonHit($"wizard:repo:{repo.Name}"), _ => Toggle(repo));
            return order >= 0 ? row.Bg(ChosenBg) : row;
        }).ToArray();
        rows.Add(Layout.Builder.VStack(list).WithScroll(_repoScroll).Stretch());
        return Layout.Builder.VStack([.. rows]).HStar();
    }

    private Layout.Node Issues()
    {
        var repo = _chosen[_issueFor];
        var rows = new List<Layout.Node>
        {
            Title($"New flight · 2. The issue in {repo.Name} ({_issueFor + 1} of {_chosen.Count})",
                "The flight does this issue: its code comes from it, its planner reads it, and its PR closes it."),
            FilterBar(),
        };
        if (!_issues.TryGetValue(repo.Path, out var issues))
        {
            rows.Add(Layout.Builder.Text(_issueErrors.TryGetValue(repo.Path, out var error) ? $"no issues: {error}" : "loading the open issues…", 13f,
                _issueErrors.ContainsKey(repo.Path) ? Warn : Dim).RowH(RowHeight));
            return Layout.Builder.VStack([.. rows]).HStar();
        }
        var filter = Filter.Text.Trim().TrimStart('#');
        var shown = issues.Where(i => filter.Length == 0
            || i.Number.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(filter, StringComparison.Ordinal)
            || i.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (issues.Length == 0)
        {
            rows.Add(Layout.Builder.Text($"{repo.Name} has no open issues: open one on GitHub, then pick it here", 13f, Dim).RowH(RowHeight));
        }
        _picked.TryGetValue(repo.Path, out var picked);
        var list = shown.Select(issue =>
        {
            var row = Layout.Builder.HStack(
                    Layout.Builder.Text($"#{issue.Number}", 14f, Accent).WFixed(70f),
                    Layout.Builder.Text(issue.Title, 14f, Bright).WStar(),
                    Layout.Builder.Text(string.Join(", ", issue.Labels), 12f, Dim, TextAlign.Far).WFixed(200f))
                .WithGap(10f)
                .PadX(6f)
                .RowH(RowHeight)
                .BgHover(RowHoverBg)
                .Clickable(new HitResult.ButtonHit($"wizard:issue:{issue.Number}"), _ => Pick(issue));
            return picked?.Number == issue.Number ? row.Bg(ChosenBg) : row;
        }).ToArray();
        rows.Add(Layout.Builder.VStack(list).WithScroll(_issueScroll).Stretch());
        return Layout.Builder.VStack([.. rows]).HStar();
    }

    private Layout.Node Review()
    {
        var picks = Picks;
        var rows = new List<Layout.Node>
        {
            Title("New flight · 3. Make them",
                "Each repo gets a flight on its issue, waiting for the one before it, and a planner that reads the issue and plans the legs. You start the first leg from its card."),
        };
        for (var i = 0; i < picks.Count; i++)
        {
            var (repo, issue) = picks[i];
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text($"{i + 1}.", 14f, Accent).WFixed(28f),
                    Layout.Builder.Text($"{repo.Name} #{issue.Number}", 14f, Bright).WFixed(260f),
                    Layout.Builder.Text(issue.Title, 14f, Body).WStar(),
                    Layout.Builder.Text(i == 0 ? "" : $"waits for {picks[i - 1].Repo.Name}", 12f, Dim, TextAlign.Far).WFixed(220f))
                .WithGap(10f)
                .PadX(6f)
                .RowH(RowHeight));
        }
        if (Status is { } status)
        {
            rows.Add(Layout.Builder.Text(status, 13f, status.StartsWith("failed", StringComparison.Ordinal) ? Warn : Dim).RowH(RowHeight));
        }
        rows.Add(Layout.Builder.Spacer().HStar());
        return Layout.Builder.VStack([.. rows]).HStar();
    }

    private Layout.Node Footer()
    {
        var buttons = new List<Layout.Node> { Pill("Cancel", "wizard:cancel", Close) };
        if (Current is Step.Issues or Step.Review)
        {
            buttons.Add(Pill("Back", "wizard:back", Back));
        }
        if (Current == Step.Repos && _chosen.Count > 0)
        {
            buttons.Add(Pill($"Next: {(_chosen.Count == 1 ? "its issue" : $"their issues ({_chosen.Count} repos)")}", "wizard:next", ToIssues, go: true));
        }
        if (Current == Step.Review)
        {
            buttons.Add(Pill(Picks.Count == 1 ? "Create and plan" : $"Create {Picks.Count} flights and plan them", "wizard:create", () => _ = CreateAsync(), go: true));
        }
        if (Current == Step.Creating)
        {
            buttons.Add(Layout.Builder.Text(Status ?? "", 13f, Dim).WAuto());
        }
        buttons.Add(Layout.Builder.Spacer().WStar());
        return Layout.Builder.HStack([.. buttons]).WithGap(8f).RowH(30f);
    }

    private static Layout.Node Pill(string label, string action, Action onClick, bool go = false) =>
        Layout.Builder.Text(label, 13f, Bright, TextAlign.Center)
            .Pad(12f, 5f)
            .WAuto()
            .Bg(go ? Go : Button)
            .BgHover(go ? GoHover : ButtonHover)
            .Clickable(new HitResult.ButtonHit(action), _ => onClick());
}

/// <summary>
/// What the wizard's Create does on the node, for either host: a flight per pick (<c>create-flight</c>, titled from its
/// issue), each after the first waiting for the one before (<c>connect</c>), then a planner on each (<c>plan-flight</c>).
/// </summary>
internal static class NewFlights
{
    /// <returns>What became of them, in a line: the codes made, and any step the node refused.</returns>
    public static async Task<string> CreateAsync(NodeClient client, IReadOnlyList<NewFlightPick> picks, string who, CancellationToken cancellationToken)
    {
        var codes = new List<string>();
        foreach (var (repo, issue) in picks)
        {
            var made = await SendAsync(client, Verbs.CreateFlight, Verbs.RepoTarget(repo.Path),
                [issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), issue.Title], who, cancellationToken);
            if (made is not { Ok: true, Made: { } code })
            {
                return $"failed: {repo.Name} #{issue.Number}: {made?.Error ?? "the node did not answer"}"
                    + (codes.Count > 0 ? $" (made {string.Join(", ", codes)} before it)" : "");
            }
            codes.Add(code);
        }
        var problems = new List<string>();
        for (var i = 1; i < codes.Count; i++)
        {
            var connected = await SendAsync(client, Verbs.Connect, Verbs.FlightTarget(codes[i]), [codes[i - 1]], who, cancellationToken);
            if (connected is not { Ok: true })
            {
                problems.Add($"{codes[i]} does not wait for {codes[i - 1]}: {connected?.Error}");
            }
        }
        foreach (var code in codes)
        {
            var planning = await SendAsync(client, Verbs.PlanFlight, Verbs.FlightTarget(code), [], who, cancellationToken);
            if (planning is not { Ok: true })
            {
                problems.Add($"{code} has no planner: {planning?.Error}");
            }
        }
        var line = $"made {string.Join(", ", codes)}; {(codes.Count == 1 ? "its planner is" : "their planners are")} planning the legs";
        return problems.Count == 0 ? line : $"{line}; but {string.Join("; ", problems)}";
    }

    private static async Task<CommandResult?> SendAsync(NodeClient client, string verb, string target, string[] args, string who, CancellationToken cancellationToken) =>
        (await client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, cancellationToken)).Response;
}
