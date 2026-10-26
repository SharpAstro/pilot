using System.Collections.Immutable;
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
/// <remarks>
/// Each list is a search box over its results, DIR.Lib's <see cref="SearchInteraction{TResult}"/>, as tianwen's planner
/// search is: typing filters, Up and Down move the highlight (the host's router hands the arrows to <see cref="ActiveSearch"/>),
/// Enter chooses the highlighted repo (again to unchoose it) or picks the highlighted issue, Ctrl+Enter goes on (Next,
/// Create), and Escape goes back a step, or closes the wizard from the first. The × at the top right closes it too.
/// </remarks>
internal sealed class NewFlightWizard
{
    public enum Step
    {
        Repos,
        Issues,
        Review,
        Creating,
    }

    /// <summary>Goes on from a step without leaving the keyboard: Next on the repos, Create on the review.</summary>
    public static readonly KeyChord NextShortcut = new(InputKey.Enter, InputModifier.Ctrl);

    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Accent = new(0x6f, 0x9b, 0xd8, 0xff);
    private static readonly RGBAColor32 Warn = new(0xe0, 0x5a, 0x5a, 0xff);
    private static readonly RGBAColor32 RowHoverBg = new(0x22, 0x24, 0x2e, 0xff);
    private static readonly RGBAColor32 ChosenBg = new(0x26, 0x33, 0x4a, 0xff);
    private static readonly RGBAColor32 HighlightBg = new(0x33, 0x40, 0x5c, 0xff);
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
    private readonly RepoSearch _repoSearch;
    private readonly IssueSearch _issueSearch;
    private RepoInfo[]? _repos;
    private string? _reposError;
    private int _issueFor;
    private string? _startWith;
    private bool _startAtIssues;

    /// <summary>Whether the issue step shows the new-issue form in place of the issues.</summary>
    public bool NewIssueOpen { get; private set; }

    /// <summary>The new issue's title (the first field of the form).</summary>
    public TextInputState IssueTitle { get; } = new();

    /// <summary>The new issue's text, optional (the second field).</summary>
    public TextInputState IssueBody { get; } = new();

    /// <summary>What became of the last New issue, or why it could not run.</summary>
    public string? IssueStatus { get; private set; }

    /// <summary>Creates an issue in the repo at the path with a title and an optional body (gh, through the node); returns it. Null leaves the wizard without a New issue entry.</summary>
    public Func<string, string, string?, Task<IssueInfo>>? CreateIssue { get; set; }

    public NewFlightWizard()
    {
        _repoSearch = new RepoSearch(this);
        _issueSearch = new IssueSearch(this);
    }

    /// <summary>Whether the page shows the wizard.</summary>
    public bool IsOpen { get; private set; }

    public Step Current { get; private set; }

    /// <summary>The search box over the repos (step 1).</summary>
    public TextInputState RepoFilter { get; } = new();

    /// <summary>The search box over the issues of the repo whose turn it is (step 2).</summary>
    public TextInputState IssueFilter { get; } = new();

    /// <summary>The search the arrow keys move through now: the host's router asks for it (<c>InputRouter.ActiveSearch</c>).</summary>
    public SearchInteraction? ActiveSearch => !IsOpen ? null : Current switch
    {
        Step.Repos => _repoSearch,
        Step.Issues when !NewIssueOpen => _issueSearch,
        _ => null,
    };

    /// <summary>The repos the box shows now, in order; the highlighted one is <see cref="SearchInteraction.SelectedIndex"/>.</summary>
    public IReadOnlyList<RepoInfo> RepoResults => _repoSearch.Results;

    /// <summary>The issues the box shows now.</summary>
    public IReadOnlyList<IssueInfo> IssueResults => _issueSearch.Results;

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

    /// <summary>The repos chosen, in the order their flights land.</summary>
    public IReadOnlyList<RepoInfo> Chosen => _chosen;

    /// <summary>The flights it would make now, in order.</summary>
    public IReadOnlyList<NewFlightPick> Picks => [.. _chosen.Where(r => _picked.ContainsKey(r.Path)).Select(r => new NewFlightPick(r, _picked[r.Path]))];

    /// <param name="repo">A repo to start with chosen, by its name or checkout path: chosen once the repos are in.</param>
    /// <param name="atIssues">Goes on to that repo's issues by itself, as the Repos page's New flight does.</param>
    public void Open(string? repo = null, bool atIssues = false)
    {
        _startWith = repo;
        _startAtIssues = atIssues && repo is not null;
        NewIssueOpen = false;
        IssueStatus = null;
        IsOpen = true;
        Current = Step.Repos;
        _chosen.Clear();
        _picked.Clear();
        _issues.Clear();
        _issueErrors.Clear();
        _repos = null;
        _reposError = null;
        Status = null;
        Clear(RepoFilter, _repoSearch);
        if (LoadRepos is { } load)
        {
            _ = LoadAsync(async () => _repos = await load(CancellationToken.None), ex => _reposError = ex.Message, _repoSearch);
        }
    }

    public void Close() => IsOpen = false;

    /// <summary>Shows the new-issue form on the issue step, for the repo whose turn it is.</summary>
    public void BeginNewIssue()
    {
        if (Current != Step.Issues || CreateIssue is null)
        {
            return;
        }
        NewIssueOpen = true;
        IssueStatus = null;
        IssueTitle.Text = IssueFilter.Text.Trim();
        IssueTitle.CursorPos = IssueTitle.Text.Length;
        IssueBody.Text = "";
        IssueBody.CursorPos = 0;
        Changed?.Invoke();
    }

    /// <summary>Creates the issue of the form through <see cref="CreateIssue"/>, then picks it for the repo as if it had been listed.</summary>
    public async Task CreateIssueAsync()
    {
        var title = IssueTitle.Text.Trim();
        if (!NewIssueOpen || CreateIssue is not { } create || _issueFor >= _chosen.Count)
        {
            return;
        }
        if (title.Length == 0)
        {
            IssueStatus = "failed: an issue needs a title";
            Changed?.Invoke();
            return;
        }
        var repo = _chosen[_issueFor];
        var body = IssueBody.Text.Trim();
        IssueStatus = "creating the issue…";
        Changed?.Invoke();
        try
        {
            var issue = await create(repo.Path, title, body.Length > 0 ? body : null);
            _issues[repo.Path] = [issue, .. _issues.TryGetValue(repo.Path, out var known) ? known.Where(i => i.Number != issue.Number) : []];
            NewIssueOpen = false;
            IssueStatus = null;
            Pick(issue);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            IssueStatus = $"failed: {ex.Message}";
        }
        Changed?.Invoke();
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
        NewIssueOpen = false;
        _issueFor = 0;
        ShowIssuesFor(_chosen[0]);
    }

    /// <summary>Picks <paramref name="issue"/> for the repo whose turn it is, and moves to the next repo, or to the review.</summary>
    public void Pick(IssueInfo issue)
    {
        _picked[_chosen[_issueFor].Path] = issue;
        if (_issueFor + 1 < _chosen.Count)
        {
            _issueFor++;
            ShowIssuesFor(_chosen[_issueFor]);
        }
        else
        {
            Current = Step.Review;
        }
    }

    /// <summary>A step back: the issue before, the repos, or (from the repos) out of the wizard.</summary>
    public void Back()
    {
        if (NewIssueOpen)
        {
            NewIssueOpen = false;
            IssueStatus = null;
            return;
        }
        switch (Current)
        {
            case Step.Repos:
                Close();
                break;
            case Step.Issues when _issueFor > 0:
                _issueFor--;
                ShowIssuesFor(_chosen[_issueFor]);
                break;
            case Step.Issues:
                Current = Step.Repos;
                break;
            case Step.Review:
                Current = Step.Issues;
                _issueFor = _chosen.Count - 1;
                ShowIssuesFor(_chosen[_issueFor]);
                break;
        }
    }

    /// <summary>What Ctrl+Enter does on this step: Next on the repos, Create on the review.</summary>
    public void GoOn()
    {
        if (Current == Step.Repos)
        {
            ToIssues();
        }
        else if (Current == Step.Issues && NewIssueOpen)
        {
            _ = CreateIssueAsync();
        }
        else if (Current == Step.Review)
        {
            _ = CreateAsync();
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

    // The repos the box shows for its text: by name or GitHub name, all of them for none.
    private ImmutableArray<RepoInfo> MatchRepos(string text) =>
        [.. (_repos ?? []).Where(r => text.Trim().Length == 0 || r.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)
            || (r.GitHub?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false))];

    // The issues of the repo whose turn it is, by the start of their number or their words.
    private ImmutableArray<IssueInfo> MatchIssues(string text)
    {
        var filter = text.Trim().TrimStart('#');
        return _issueFor < _chosen.Count && _issues.TryGetValue(_chosen[_issueFor].Path, out var issues)
            ? [.. issues.Where(i => filter.Length == 0
                || i.Number.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(filter, StringComparison.Ordinal)
                || i.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))]
            : [];
    }

    private void ShowIssuesFor(RepoInfo repo)
    {
        NewIssueOpen = false;
        Clear(IssueFilter, _issueSearch);
        if (_issues.ContainsKey(repo.Path) || LoadIssues is not { } load)
        {
            return;
        }
        _issueErrors.Remove(repo.Path);
        _ = LoadAsync(async () => _issues[repo.Path] = await load(repo.Path, CancellationToken.None), ex => _issueErrors[repo.Path] = ex.Message, _issueSearch);
    }

    private async Task LoadAsync(Func<Task> load, Action<Exception> failed, IRefreshable search)
    {
        try
        {
            await load().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            failed(ex);
        }
        // Opened from a session: its repo comes chosen.
        if (_startWith is { } name && _repos?.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Path.TrimEnd('/', '\\'), name.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) is { } repo
            && !_chosen.Contains(repo))
        {
            _chosen.Add(repo);
        }
        _startWith = null;
        if (_startAtIssues && Current == Step.Repos && _chosen.Count > 0)
        {
            ToIssues();
        }
        _startAtIssues = false;
        search.Refresh();
        Changed?.Invoke();
    }

    private static void Clear(TextInputState input, IRefreshable search)
    {
        input.Text = "";
        input.CursorPos = 0;
        search.Refresh();
    }

    /// <summary>The wizard as a tree, in the space the flights take otherwise.</summary>
    internal Layout.Node Tree()
    {
        RepoFilter.Placeholder = "type to filter · Up/Down to move · Enter to choose · Ctrl+Enter for the issues";
        IssueFilter.Placeholder = "type a number or words · Up/Down to move · Enter to pick";
        var body = Current switch
        {
            Step.Repos => Repos(),
            Step.Issues => Issues(),
            _ => Review(),
        };
        return Layout.Builder.VStack(body, Footer()).Pad(16f, 8f).Stretch();
    }

    // The step's title, with the × that closes the wizard at its right.
    private Layout.Node Title(string text, string hint) =>
        Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.Text(text, 18f, Bright).WStar(),
                        Layout.Builder.Text("×", 18f, Body, TextAlign.Center).Pad(10f, 0f).WAuto().BgHover(ButtonHover)
                            .Follows(new HitResult.ButtonHit("wizard:close"), _ => Close())
                            .WithTooltip("Close the wizard (Escape on the repos)"))
                    .RowH(28f),
                Layout.Builder.Text(hint, 12f, Dim).RowH(20f))
            .RowH(52f);

    private Layout.Node Repos()
    {
        var rows = new List<Layout.Node>
        {
            Title("New flight · 1. Repos",
                "Pick the repos the work is in, in the order their flights land: a library before the app that takes it. Each gets a flight that waits for the one before."),
            Layout.Builder.TextInput(RepoFilter, 14f, focusOnOpen: true).RowH(28f),
        };
        if (_repos is null)
        {
            rows.Add(Layout.Builder.Text(_reposError is { } error ? $"the node could not list its repos: {error}" : "loading the repos…", 13f, _reposError is null ? Dim : Warn).RowH(RowHeight));
            return Layout.Builder.VStack([.. rows]).HStar();
        }
        if (_repos.Length == 0)
        {
            rows.Add(Layout.Builder.Text("no repos yet: start the node with --repo-root <the folder your clones are in>", 13f, Dim).RowH(RowHeight));
        }
        var results = _repoSearch.Results;
        var list = new Layout.Node[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            var repo = results[i];
            var at = i;
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
                .Follows(new HitResult.ButtonHit($"wizard:repo:{repo.Name}"), _ => _repoSearch.CommitAt(at));
            list[i] = i == _repoSearch.SelectedIndex ? row.Bg(HighlightBg) : order >= 0 ? row.Bg(ChosenBg) : row;
        }
        _repoScroll.EnsureVisible(_repoSearch.SelectedIndex);
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
            Layout.Builder.TextInput(IssueFilter, 14f, focusOnOpen: true).RowH(28f),
        };
        if (NewIssueOpen)
        {
            rows.RemoveAt(1);
            rows.Add(Layout.Builder.Text("Title", 12f, Dim).RowH(20f));
            rows.Add(Layout.Builder.TextInput(IssueTitle, 14f, focusOnOpen: true).RowH(28f));
            rows.Add(Layout.Builder.Text("Text (optional)", 12f, Dim).RowH(20f));
            rows.Add(Layout.Builder.TextInput(IssueBody, 14f).RowH(28f));
            if (IssueStatus is { } said)
            {
                rows.Add(Layout.Builder.Text(said, 13f, said.StartsWith("failed", StringComparison.Ordinal) ? Warn : Dim).RowH(RowHeight));
            }
            rows.Add(Layout.Builder.Spacer().HStar());
            return Layout.Builder.VStack([.. rows]).HStar();
        }
        if (!_issues.TryGetValue(repo.Path, out var issues))
        {
            rows.Add(Layout.Builder.Text(_issueErrors.TryGetValue(repo.Path, out var error) ? $"no issues: {error}" : "loading the open issues…", 13f,
                _issueErrors.ContainsKey(repo.Path) ? Warn : Dim).RowH(RowHeight));
            return Layout.Builder.VStack([.. rows]).HStar();
        }
        if (issues.Length == 0)
        {
            rows.Add(Layout.Builder.Text($"{repo.Name} has no open issues: use New issue to open one, then pick it here", 13f, Dim).RowH(RowHeight));
        }
        _picked.TryGetValue(repo.Path, out var picked);
        var results = _issueSearch.Results;
        var list = new Layout.Node[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            var issue = results[i];
            var at = i;
            var row = Layout.Builder.HStack(
                    Layout.Builder.Text($"#{issue.Number}", 14f, Accent).WFixed(70f),
                    Layout.Builder.Text(issue.Title, 14f, Bright).WStar(),
                    Layout.Builder.Text(string.Join(", ", issue.Labels), 12f, Dim, TextAlign.Far).WFixed(200f))
                .WithGap(10f)
                .PadX(6f)
                .RowH(RowHeight)
                .BgHover(RowHoverBg)
                .Follows(new HitResult.ButtonHit($"wizard:issue:{issue.Number}"), _ => _issueSearch.CommitAt(at));
            list[i] = i == _issueSearch.SelectedIndex ? row.Bg(HighlightBg) : picked?.Number == issue.Number ? row.Bg(ChosenBg) : row;
        }
        _issueScroll.EnsureVisible(_issueSearch.SelectedIndex);
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
        var buttons = new List<Layout.Node>();
        if (Current is Step.Issues or Step.Review)
        {
            buttons.Add(Pill("Back", "wizard:back", Back));
        }
        if (Current == Step.Issues && CreateIssue is not null)
        {
            buttons.Add(NewIssueOpen
                ? Pill("Create issue and pick it", "wizard:create-issue", () => _ = CreateIssueAsync(), go: true).WithShortcut(NextShortcut)
                : Pill("New issue", "wizard:new-issue", BeginNewIssue));
        }
        if (Current == Step.Repos && _chosen.Count > 0)
        {
            buttons.Add(Pill($"Next: {(_chosen.Count == 1 ? "its issue" : $"their issues ({_chosen.Count} repos)")}", "wizard:next", ToIssues, go: true)
                .WithShortcut(NextShortcut));
        }
        if (Current == Step.Review)
        {
            buttons.Add(Pill(Picks.Count == 1 ? "Create and plan" : $"Create {Picks.Count} flights and plan them", "wizard:create", () => _ = CreateAsync(), go: true)
                .WithShortcut(NextShortcut));
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
            .Follows(new HitResult.ButtonHit(action), _ => onClick());

    /// <summary>A search whose results are worked out again from its text: when its data arrives, or its text is cleared.</summary>
    private interface IRefreshable
    {
        void Refresh();
    }

    /// <summary>The repos' search box: Enter chooses the highlighted repo, or unchooses it; Escape leaves the wizard.</summary>
    private sealed class RepoSearch(NewFlightWizard wizard) : SearchInteraction<RepoInfo>(wizard.RepoFilter, () => wizard.Changed?.Invoke()), IRefreshable
    {
        protected override bool AutoSelectFirstResult => true;

        public void Refresh() => Requery(Input.Text);

        protected override ImmutableArray<RepoInfo> Query(string text) => wizard.MatchRepos(text);

        protected override void Commit(RepoInfo result)
        {
            wizard.Toggle(result);
            RequestRedraw();
        }

        // Enter with nothing highlighted (no repo matches): nothing to choose.
        protected override void CommitRawQuery(string text)
        {
        }

        protected override void Dismiss()
        {
            wizard.Back();
            RequestRedraw();
        }
    }

    /// <summary>The issues' search box: Enter picks the highlighted issue and moves on; Escape goes back a step.</summary>
    private sealed class IssueSearch(NewFlightWizard wizard) : SearchInteraction<IssueInfo>(wizard.IssueFilter, () => wizard.Changed?.Invoke()), IRefreshable
    {
        protected override bool AutoSelectFirstResult => true;

        public void Refresh() => Requery(Input.Text);

        protected override ImmutableArray<IssueInfo> Query(string text) => wizard.MatchIssues(text);

        protected override void Commit(IssueInfo result)
        {
            wizard.Pick(result);
            RequestRedraw();
        }

        protected override void CommitRawQuery(string text)
        {
        }

        protected override void Dismiss()
        {
            wizard.Back();
            RequestRedraw();
        }
    }
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
        var routed = new List<string>();
        foreach (var (repo, issue) in picks)
        {
            var made = await SendAsync(client, Verbs.CreateFlight, Verbs.RepoTarget(repo.Path),
                ["#" + issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), issue.Title], who, cancellationToken);
            if (made is not { Ok: true, Made: { } code })
            {
                return $"failed: {repo.Name} #{issue.Number}: {made?.Error ?? "the node did not answer"}"
                    + (codes.Count > 0 ? $" (made {string.Join(", ", codes)} before it)" : "");
            }
            // Made on another node of the fleet (this one is in maintenance): this node has no flight of that code to connect or plan.
            if (made.Way is { Length: > 0 } way)
            {
                routed.Add(way);
                continue;
            }
            codes.Add(code);
        }
        if (codes.Count == 0)
        {
            return string.Join("; ", routed);
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
        if (routed.Count > 0)
        {
            line += $"; {string.Join("; ", routed)}";
        }
        return problems.Count == 0 ? line : $"{line}; but {string.Join("; ", problems)}";
    }

    private static async Task<CommandResult?> SendAsync(NodeClient client, string verb, string target, string[] args, string who, CancellationToken cancellationToken) =>
        (await client.SendAsync(new CommandRequest { Verb = verb, Target = target, Args = args, Who = who }, cancellationToken)).Response;
}
