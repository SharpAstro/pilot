using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Settings page: the setup checklist (what it takes for this machine's node to be part of the fleet, each item with
/// the fix the node can make) above the node's settings (on the LAN, its prefix, repo roots, the update channel and
/// policy, jobs). The settings are read from the node's settings route while the page is shown and are drawn as soon as they
/// come; the setup checks run programs, so they are read on their own task from their own route and each row says
/// "checking…" until they answer, then how old they are. A button or a field's Enter is one verb on the node
/// (<c>setup node</c>, <c>set node</c>); the control shows it is working until the node answers, and a changed setting is
/// shown from the answer's row, never from the click and with no second read. This machine's node only: a node on the LAN
/// does not show its settings to another machine. Generic over the surface, as the other pages are.
/// </summary>
internal sealed class SettingsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

    // The checks are read quietly this often while shown (the node re-measures once its cache is 10 s old); their age is shown meanwhile.
    private static readonly TimeSpan ChecksEvery = TimeSpan.FromSeconds(30);

    // The node's repos change rarely (a clone is added): read on the first show, and again this often while shown.
    private static readonly TimeSpan ReposEvery = TimeSpan.FromSeconds(60);

    // A measurement older than this is marked stale rather than shown as current.
    private static readonly TimeSpan ChecksStale = TimeSpan.FromSeconds(90);

    // The page stops reading once it has not been drawn for this long: it is not shown.
    private static readonly TimeSpan Unseen = TimeSpan.FromSeconds(3);

    private const float Gutter = 16f;
    private const float StripeWidth = 4f;
    private const float RowHeight = 54f;
    private const float TitleWidth = 230f;
    private const float FixWidth = 200f;

    private static readonly RGBAColor32 Ground = new(0x12, 0x12, 0x18, 0xff);
    private static readonly RGBAColor32 HeaderBg = new(0x1b, 0x1c, 0x24, 0xff);
    private static readonly RGBAColor32 RowAltBg = new(0x16, 0x17, 0x1e, 0xff);
    private static readonly RGBAColor32 Bright = new(0xe6, 0xe7, 0xee, 0xff);
    private static readonly RGBAColor32 Body = new(0xc4, 0xc6, 0xd0, 0xff);
    private static readonly RGBAColor32 Dim = new(0x86, 0x89, 0x98, 0xff);
    private static readonly RGBAColor32 Faint = new(0x5c, 0x5f, 0x6c, 0xff);
    private static readonly RGBAColor32 Ok = new(0x4c, 0xc3, 0x8a, 0xff);
    private static readonly RGBAColor32 Todo = new(0xf0, 0xb4, 0x3c, 0xff);
    private static readonly RGBAColor32 Optional = new(0x6a, 0x6d, 0x7a, 0xff);
    private static readonly RGBAColor32 Button = new(0x2a, 0x3a, 0x58, 0xff);
    private static readonly RGBAColor32 ButtonHover = new(0x35, 0x4a, 0x70, 0xff);
    private static readonly RGBAColor32 Chosen = new(0x2f, 0x5f, 0xa8, 0xff);
    private static readonly RGBAColor32 FieldBg = new(0x1f, 0x21, 0x2b, 0xff);

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly Dictionary<string, TextInputState> _fields = new(StringComparer.Ordinal);
    // What the page has read, as one immutable record swapped whole: reads land on the pool, the render thread draws what
    // it finds, and neither waits on the other.
    // Checks: what the node last measured (null until it first answers); Checking: a read the person is waiting on is out
    // (the first, a refresh, or the one after a fix), so the rows say so; Working: the ids of the controls pressed and not yet answered.
    // Repos: the node's tracked repos the list pickers offer (null until read); ReposProblem: why they could not be read.
    // ChecksTicket: the ticket of the checks read shown, so a slower, older read cannot put its answer over a newer one.
    private sealed record Seen(SettingsPage? Page, SetupChecks? Checks, bool Checking, string? ChecksProblem, GrantInfo[] Given, string[] Held, string? Problem, System.Collections.Immutable.ImmutableHashSet<string> Working, RepoInfo[]? Repos = null, string? ReposProblem = null, long ChecksTicket = 0);

    private Seen _read = new(null, null, false, null, [], [], null, []);
    private CancellationTokenSource? _reading;
    private CancellationTokenSource? _readingChecks;
    private CancellationTokenSource? _readingRepos;

    // A list picker's own state, which is the person's choice and not a setting's value: the repo picked (and the tracker for
    // repo-trackers) before Add sends it, and the list whose repo choices are open. A choice goes once the node has taken it.
    // Swapped whole like Seen, so the render thread and a click never wait on each other.
    private sealed record Choice(System.Collections.Immutable.ImmutableDictionary<string, string> Picked, System.Collections.Immutable.ImmutableDictionary<string, string> Tracker, string? Opened);

    private Choice _choice = new(System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, null);
    private DateTimeOffset _drawnAt;

    // Counts the settings a reply swapped in, so a read that began before one does not put the old row back.
    private long _swaps;
    // Orders the checks reads: each takes a ticket, and an answer older than the one in Seen.ChecksTicket is dropped;
    // _waited counts the reads someone waits on.
    private long _ticket;
    private int _waited;

    /// <summary>Reads the node's settings page (its settings alone, which the node answers at once); null where there is no node to read (a snapshot file).</summary>
    public Func<CancellationToken, Task<SettingsPage>>? Load { get; set; }

    /// <summary>Reads the setup checks on their own (<c>true</c> asks the node to measure them again); null where there is no node to read.</summary>
    public Func<bool, CancellationToken, Task<SetupChecks>>? LoadChecks { get; set; }

    /// <summary>Reads the node's repos (its repos route), which the lists of repos offer by name; null where there is no node to read.</summary>
    public Func<CancellationToken, Task<RepoInfo[]>>? LoadRepos { get; set; }

    /// <summary>Sends one verb to the node (<c>set</c>, <c>setup</c>) with its words; returns what became of it, with the changed setting's row.</summary>
    public Func<string, string[], CancellationToken, Task<SettingsReply>>? Send { get; set; }

    /// <summary>Reads the grants this node has given (its grants route); null where there is no node to read.</summary>
    public Func<CancellationToken, Task<GrantInfo[]>>? LoadGrants { get; set; }

    /// <summary>The nodes this user holds a token for, by <c>host:port</c>: read from the token file with each refresh.</summary>
    public Func<IReadOnlyList<string>>? LoadHeld { get; set; }

    /// <summary>Drops the token held for a node; the host also recomputes the steerable set so its rows turn watch-only on the next tick.</summary>
    public Action<string>? Forget { get; set; }

    /// <summary>The grants given, as last read.</summary>
    public GrantInfo[] Given => Volatile.Read(ref _read).Given;

    /// <summary>Called when what the page shows changed, from whichever thread: the host redraws.</summary>
    public Action? Changed { get; set; }

    /// <summary>What the last button or field said, under the header.</summary>
    public string? Said { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }

    /// <summary>The window's node selection, whose pills the top bar shows while there is more than one node.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The page as last read, or null before the first read.</summary>
    public SettingsPage? Page => Volatile.Read(ref _read).Page;

    /// <summary>The setup checks as last measured, or null before the node first answers.</summary>
    public SetupChecks? Checks => Volatile.Read(ref _read).Checks;

    public void Render(DateTimeOffset now, RectF32 bounds)
    {
        BeginFrame();
        RenderLayout(Tree(now), bounds);
    }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(DateTimeOffset now)
    {
        _drawnAt = now;
        Read();
        var read = Volatile.Read(ref _read);
        var page = read.Page;
        var problem = read.Problem;
        // The line waiting for the page sits at the top, in the gutter, where the page's first row will be: stretched on its own
        // it was drawn two thirds down, against the rail.
        var body = page is null
            ? Layout.Builder.VStack(Layout.Builder.Text(problem is null ? (Load is null ? "settings are the node's: open pilot on a node" : "reading the node's settings…") : $"the node did not send its settings: {problem}", 13f, Dim).RowH(28f)).Pad(Gutter, 8f)
            : Main(page, read, now);
        var tree = Layout.Builder.VStack(Header(page, read.Checks, problem), body.Stretch()).Bg(Ground).Stretch();
        return About?.Over(tree) ?? tree;
    }

    public override bool HandleInput(InputEvent evt) => false;

    /// <summary>Reads the settings now, and again every few seconds while it is drawn; the checks are read beside them, on their own task.</summary>
    private void Read()
    {
        if (_reading is null && Load is { } load)
        {
            var reading = _reading = new CancellationTokenSource();
            _ = LoopAsync(token => RefreshAsync(load, token), Every, reading, () => _reading = null);
        }
        if (_readingChecks is null && LoadChecks is not null)
        {
            var reading = _readingChecks = new CancellationTokenSource();
            _ = LoopAsync(token => ReadChecksAsync(refresh: false, waited: Checks is null, token), ChecksEvery, reading, () => _readingChecks = null);
        }
        if (_readingRepos is null && LoadRepos is { } loadRepos)
        {
            var reading = _readingRepos = new CancellationTokenSource();
            _ = LoopAsync(token => ReadReposAsync(loadRepos, token), ReposEvery, reading, () => _readingRepos = null);
        }
    }

    private async Task ReadReposAsync(Func<CancellationToken, Task<RepoInfo[]>> load, CancellationToken cancellationToken)
    {
        try
        {
            var repos = await load(cancellationToken).ConfigureAwait(false);
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Repos = repos, ReposProblem = null });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // The picker keeps the repos it had, and says it could not read them again.
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { ReposProblem = ex.Message });
        }
        Changed?.Invoke();
    }

    private async Task LoopAsync(Func<CancellationToken, Task> step, TimeSpan every, CancellationTokenSource reading, Action done)
    {
        try
        {
            while (!reading.IsCancellationRequested && DateTimeOffset.UtcNow - _drawnAt < Unseen)
            {
                await step(reading.Token).ConfigureAwait(false);
                await Task.Delay(every, reading.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Unseen: the next draw reads again from the start.
            done();
            reading.Dispose();
        }
    }

    private async Task RefreshAsync(Func<CancellationToken, Task<SettingsPage>> load, CancellationToken cancellationToken)
    {
        var swaps = Volatile.Read(ref _swaps);
        try
        {
            var page = await load(cancellationToken).ConfigureAwait(false);
            // A reply put a newer row in while this read was out: this read may carry the old one, so the next read shows the page.
            if (swaps == Volatile.Read(ref _swaps))
            {
                System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Page = page, Problem = null });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Problem = ex.Message });
        }
        // The settings are drawn now; the grants below them come after.
        Changed?.Invoke();
        await RefreshGrantsAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>
    /// Reads the setup checks. <paramref name="waited"/> says someone waits on this read (the first, a refresh, the one after a fix):
    /// the rows then say "checking…" until it answers; the quiet read the page makes now and then does not blank them.
    /// </summary>
    private async Task ReadChecksAsync(bool refresh, bool waited, CancellationToken cancellationToken)
    {
        if (LoadChecks is not { } load)
        {
            return;
        }
        var ticket = Interlocked.Increment(ref _ticket);
        if (waited)
        {
            Interlocked.Increment(ref _waited);
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Checking = true });
            Changed?.Invoke();
        }
        try
        {
            var measured = await load(refresh, cancellationToken).ConfigureAwait(false);
            // An answer older than the one already shown (a slow read passed by a refresh) is not shown.
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => ticket > read.ChecksTicket
                ? read with { Checks = measured, ChecksProblem = null, ChecksTicket = ticket }
                : read);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // The rows keep what was measured, with its age; the Setup heading says the read failed.
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { ChecksProblem = ex.Message });
        }
        finally
        {
            if (waited && Interlocked.Decrement(ref _waited) == 0)
            {
                System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Checking = false });
            }
            Changed?.Invoke();
        }
    }

    private async Task RefreshGrantsAsync(CancellationToken cancellationToken)
    {
        GrantInfo[]? given = null;
        if (LoadGrants is { } loadGrants)
        {
            try
            {
                given = await loadGrants(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // The list keeps what it had: the settings above it are still good.
            }
        }
        var held = LoadHeld?.Invoke();
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Given = given ?? read.Given, Held = held is null ? read.Held : [.. held] });
    }

    /// <summary>Drops the token held for <paramref name="endpoint"/> and shows the list without it at once.</summary>
    private void ForgetNode(string endpoint)
    {
        // At once in the list; the token file (Forget's write, and the list read back) on the pool.
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Held = [.. read.Held.Where(h => !string.Equals(h, endpoint, StringComparison.OrdinalIgnoreCase))] });
        Said = $"forgot {endpoint}: its rows are watch-only until you Connect again";
        Changed?.Invoke();
        _ = Task.Run(() =>
        {
            Forget?.Invoke(endpoint);
            if (LoadHeld?.Invoke() is { } held)
            {
                System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Held = [.. held] });
            }
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Sends <paramref name="verb"/> and says what became of it. <paramref name="control"/> shows it is working until the node
    /// answers; the answer's row (a <c>set</c>) replaces the shown one, and nothing is read again but what the verb touches: a setup
    /// fix reads the checks, a revoke the grants. A refusal changes no row: the node's value stays, and the line says why.
    /// </summary>
    private void Act(string verb, string[] args, string doing, string control, Action? taken = null)
    {
        if (Send is not { } send)
        {
            return;
        }
        var added = false;
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => (added = !read.Working.Contains(control)) ? read with { Working = read.Working.Add(control) } : read);
        if (!added)
        {
            return;
        }
        Said = doing;
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            var reply = await send(verb, args, CancellationToken.None).ConfigureAwait(false);
            // The row goes in and the control stops working in one swap, so no draw shows the old row with the control idle.
            if (reply.Row is not null)
            {
                Interlocked.Increment(ref _swaps);
            }
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with
            {
                Page = reply.Row is { } row && read.Page is { } page ? page with { Settings = [.. page.Settings.Select(s => s.Key == row.Key ? row : s)] } : read.Page,
                Working = read.Working.Remove(control),
            });
            if (reply.Kind == ReplyKind.Done)
            {
                // What the person chose for this verb goes only once the node has taken it: a refusal leaves the choice to amend.
                taken?.Invoke();
            }
            Said = reply.Line.ReplaceLineEndings(" ").Trim();
            Changed?.Invoke();
            if (verb == Verbs.Setup)
            {
                await ReadChecksAsync(refresh: false, waited: true, CancellationToken.None).ConfigureAwait(false);
            }
            else if (verb == Verbs.RevokeGrant)
            {
                await RefreshGrantsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else if (verb == Verbs.Set && reply.Row is null && reply.Kind == ReplyKind.NoAnswer && Load is { } load)
            {
                // No row came (the node may be restarting for the change): read the settings, as the only way to know.
                await RefreshAsync(load, CancellationToken.None).ConfigureAwait(false);
            }
            Changed?.Invoke();
        });
    }

    private Layout.Node Header(SettingsPage? page, SetupChecks? checks, string? problem) =>
        TopBar.Build(new PageHeader("settings")
        {
            Counts = page is null ? "" : Summary(page, checks),
            Source = Said ?? page?.Restart ?? (page is null ? (problem ?? "") : $"saved in {page.File} · the command line wins over it"),
        }, new TopBarParts(Nodes, null, Autopilot, About));

    /// <summary>The header's counts: the setup steps left only once the checks are known, then what is saved for the next start.</summary>
    internal static string Summary(SettingsPage page, SetupChecks? checks)
    {
        var pending = page.Settings.Count(s => s.Pending);
        var pendingText = pending == 0 ? "" : $"{pending} saved for the next start";
        if (checks is null)
        {
            return pendingText;
        }
        var needed = checks.Checks.Where(c => !c.Optional).ToArray();
        var left = needed.Count(c => !c.Ok);
        var text = left == 0 ? "set up" : $"{left} of {needed.Length} setup steps left";
        return pending == 0 ? text : $"{text} · {pendingText}";
    }

    private Layout.Node Main(SettingsPage page, Seen read, DateTimeOffset now)
    {
        var rows = new List<Layout.Node>();
        if (LoadChecks is not null)
        {
            rows.Add(SetupSection(read));
            if (read.Checks is not { } measured)
            {
                // Before the node's first answer the page does not know the rows: one stands for them.
                rows.Add(PendingChecksRow(read.ChecksProblem));
            }
            else
            {
                for (var i = 0; i < measured.Checks.Length; i++)
                {
                    rows.Add(CheckRow(measured.Checks[i], i, measured.CheckedAt, now, read.Checking));
                }
            }
        }
        rows.Add(Section("Settings", "the node restarts to run with a change at once; its sessions, flights and jobs carry on"));
        for (var i = 0; i < page.Settings.Length; i++)
        {
            rows.Add(SettingRowNode(page.Settings[i], i, read.Working));
        }
        var given = read.Given;
        var held = read.Held;
        if (LoadGrants is not null || LoadHeld is not null)
        {
            rows.Add(Section("Grants", "who may command this node, and which nodes you may command"));
            rows.Add(Subhead("Given by this node"));
            if (given.Length == 0)
            {
                rows.Add(Layout.Builder.Text("no machine on the LAN holds a grant here", 12f, Dim).RowH(26f).Pad(Gutter, 2f));
            }
            for (var i = 0; i < given.Length; i++)
            {
                rows.Add(GrantRow(given[i], i));
            }
            rows.Add(Subhead("Held by you"));
            if (held.Length == 0)
            {
                rows.Add(Layout.Builder.Text("no other node is commanded from here: Connect on its row asks for a grant", 12f, Dim).RowH(26f).Pad(Gutter, 2f));
            }
            for (var i = 0; i < held.Length; i++)
            {
                rows.Add(HeldRow(held[i], i));
            }
        }
        return Layout.Builder.VStack([.. rows]).WithScroll(_scroll).Pad(Gutter, 8f).Stretch();
    }

    private static Layout.Node Section(string title, string hint) =>
        Layout.Builder.HStack(
                Layout.Builder.Text(title, 15f, Bright).WAuto(),
                Layout.Builder.Text(hint, 12f, Faint).WStar())
            .WithGap(12f).Pad(8f, 10f).RowH(38f);

    // Setup's heading carries the refresh: it measures the checks again, and shows it is working until the node answers.
    private Layout.Node SetupSection(Seen read)
    {
        var hint = read.ChecksProblem is { } problem ? $"could not read the checks again: {problem}" : "what it takes for this machine's node to be part of the fleet";
        return Layout.Builder.HStack(
                Layout.Builder.Text("Setup", 15f, Bright).WAuto(),
                Layout.Builder.Text(hint, 12f, read.ChecksProblem is null ? Faint : Todo).WStar(),
                Pill("refresh", "settings:checks:refresh", () => _ = ReadChecksAsync(refresh: true, waited: true, CancellationToken.None), working: read.Checking))
            .WithGap(12f).Pad(8f, 10f).RowH(38f);
    }

    private static Layout.Node PendingChecksRow(string? problem)
    {
        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(problem is null ? Faint : Todo).WFixed(StripeWidth).HStar(),
                Layout.Builder.VStack(
                        Layout.Builder.Text("Setup checks", 14f, Bright).RowH(22f),
                        Layout.Builder.Text(problem is null ? "checking…" : "not read", 11f, problem is null ? Dim : Todo).RowH(18f))
                    .WFixed(TitleWidth),
                Layout.Builder.Text(problem is null ? "checking… (runs netsh and gh)" : $"the node did not send its setup checks: {problem}", 12f, Body).WStar(),
                Layout.Builder.Spacer().WFixed(FixWidth))
            .WithGap(14f).Pad(8f, 6f).RowH(RowHeight);
        return row;
    }

    private static Layout.Node Subhead(string text) =>
        Layout.Builder.Text(text, 12f, Dim).RowH(24f).Pad(Gutter, 2f);

    private Layout.Node GrantRow(GrantInfo grant, int index)
    {
        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(Ok).WFixed(StripeWidth).HStar(),
                Layout.Builder.VStack(
                        Layout.Builder.Text(grant.Label, 14f, Bright).RowH(22f),
                        Layout.Builder.Text($"since {grant.GrantedAt.ToLocalTime():yyyy-MM-dd HH:mm}", 11f, Dim).RowH(18f))
                    .WFixed(TitleWidth),
                Layout.Builder.Text("may command this node; Revoke cuts it back to watching at once", 12f, Body).WStar(),
                Layout.Builder.HStack(
                        Send is null ? Layout.Builder.Spacer().WStar() : Pill("Revoke", $"settings:revoke:{grant.Id}", () => Act(Verbs.RevokeGrant, [grant.Id], $"revoking {grant.Label}…", $"settings:revoke:{grant.Id}"), working: Volatile.Read(ref _read).Working.Contains($"settings:revoke:{grant.Id}")),
                        Layout.Builder.Spacer().WStar())
                    .WFixed(FixWidth))
            .WithGap(14f).Pad(8f, 6f).RowH(RowHeight);
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    private Layout.Node HeldRow(string endpoint, int index)
    {
        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(Ok).WFixed(StripeWidth).HStar(),
                Layout.Builder.VStack(
                        Layout.Builder.Text(endpoint, 14f, Bright).RowH(22f),
                        Layout.Builder.Text("you may command it", 11f, Dim).RowH(18f))
                    .WFixed(TitleWidth),
                Layout.Builder.Text("Forget drops the token: its rows turn watch-only, with Connect offered", 12f, Body).WStar(),
                Layout.Builder.HStack(
                        Forget is null ? Layout.Builder.Spacer().WStar() : Pill("Forget", $"settings:forget:{endpoint}", () => ForgetNode(endpoint)),
                        Layout.Builder.Spacer().WStar())
                    .WFixed(FixWidth))
            .WithGap(14f).Pad(8f, 6f).RowH(RowHeight);
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    // A line this long does not fit the row's middle column on one line (the defender check lists every folder it misses): it
    // wraps full width under the row instead, so the whole text is shown, never cut.
    private const int CheckLineFits = 100;

    // Under the title's column: the row's padding (8), the stripe (StripeWidth, 4) and the gap (14) in.
    private const float CheckLineIndent = 26f;

    // A check row: the measured line with how old it is; while the node measures again, what that runs instead. The old line is
    // not shown then, as it would read as current. A measurement past ChecksStale is marked, so it never passes for current.
    private Layout.Node CheckRow(SetupCheck check, int index, DateTimeOffset checkedAt, DateTimeOffset now, bool checking)
    {
        var line = checking ? $"checking… ({check.Runs ?? "reads files"})" : check.Line;
        var tooLong = line.Length > CheckLineFits;
        var age = now - checkedAt;
        var stale = age > ChecksStale;
        var action = $"settings:fix:{check.Key}";
        var fix = !checking && check.Fix is { } label && Send is not null
            ? Pill(label, action, () => Act(Verbs.Setup, [check.Key], $"{check.Title}: {label}…", action), working: Volatile.Read(ref _read).Working.Contains(action))
            : Layout.Builder.Spacer().WStar();
        var status = checking ? "checking…" : $"{(check.Ok ? "done" : check.Optional ? "optional" : "to do")} · checked {SessionLine.Ago(age)}{(stale ? " (stale)" : "")}";
        var statusColor = checking ? Dim : stale ? Todo : check.Ok ? Ok : check.Optional ? Dim : Todo;
        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(checking ? Faint : check.Ok ? Ok : check.Optional ? Optional : Todo).WFixed(StripeWidth).HStar(),
                Layout.Builder.VStack(
                        Layout.Builder.Text(check.Title, 14f, Bright).RowH(22f),
                        Layout.Builder.Text(status, 11f, statusColor).RowH(18f))
                    .WFixed(TitleWidth),
                tooLong ? Layout.Builder.Spacer().WStar() : Layout.Builder.Text(line, 12f, Body).WithTooltip(line).WStar(),
                Layout.Builder.HStack(fix, Layout.Builder.Spacer().WStar()).WFixed(FixWidth))
            .WithGap(14f).Pad(8f, 6f).RowH(RowHeight);
        if (tooLong)
        {
            var whole = Layout.Builder.VStack(row, WrappedProse.Paragraphs(line, 12f, Body, 40).Pad(CheckLineIndent, 2f))
                .WStar().HAuto();
            return (index & 1) == 1 ? whole.Bg(RowAltBg) : whole;
        }
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    private Layout.Node SettingRowNode(SettingRow setting, int index, System.Collections.Immutable.ImmutableHashSet<string> working)
    {
        var state = setting.Source == SettingSources.CommandLine ? "from the node's command line, which wins"
            : setting.Pending ? $"saved as {(setting.Saved ?? "the default")}: applies at the next start"
            : setting.Source == SettingSources.File ? "saved" : "default";
        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(setting.Pending ? Todo : Faint).WFixed(StripeWidth).HStar(),
                Layout.Builder.VStack(
                        Layout.Builder.Text(setting.Label, 14f, Bright).RowH(22f),
                        Layout.Builder.Text(state, 11f, setting.Pending ? Todo : Dim).RowH(18f))
                    .WFixed(TitleWidth),
                Layout.Builder.VStack(
                        setting.Many ? Editor(setting, working).HAuto() : Editor(setting, working).RowH(26f),
                        Layout.Builder.Text(setting.Detail, 11f, Dim).WithTooltip(setting.Detail).RowH(18f))
                    .WStar())
            .WithGap(14f).Pad(8f, 6f);
        // Repo roots grow a line per folder: their row takes the height its lines ask for.
        row = setting.Many ? row.HAuto() : row.RowH(RowHeight);
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    // How a setting is changed: a pill per choice, a field (Enter saves), or for repo roots each folder with an x and a field to add one.
    private Layout.Node Editor(SettingRow setting, System.Collections.Immutable.ImmutableHashSet<string> working)
    {
        var parts = new List<Layout.Node>();
        if (setting.Choices.Length > 0)
        {
            foreach (var choice in setting.Choices)
            {
                var chosen = string.Equals(choice, setting.Saved ?? setting.Value, StringComparison.OrdinalIgnoreCase);
                var action = $"settings:{setting.Key}:{choice}";
                // The wire value for "leave it to Claude Code" is none; the button says default, as the help line does.
                var label = choice == "none" ? "default" : choice;
                parts.Add(Pill(label, action, () => Act(Verbs.Set, [setting.Key, choice], $"{setting.Label}: {label}…", action), chosen, working.Contains(action)));
            }
        }
        else if (setting.Many)
        {
            return ListEditor(setting, working);
        }
        else
        {
            parts.Add(Field(setting, setting.Value, text => [setting.Key, text], working).WFixed(220f));
        }
        if (setting.Saved is not null && setting.Source != SettingSources.CommandLine)
        {
            var action = $"settings:{setting.Key}:default";
            // "reset" takes the saved value out (back to what the node does unset); no choice on the row reads the same.
            parts.Add(Pill("reset", action, () => Act(Verbs.Set, [setting.Key, "default"], $"{setting.Label}: back to the default…", action), working: working.Contains(action)));
        }
        parts.Add(Layout.Builder.Spacer().WStar());
        return Layout.Builder.HStack([.. parts]).WithGap(6f);
    }

    // The lists of repos (named by folder) offer the node's repos to choose from; repo roots hold folders, which are not repos.
    private static bool IsRepoList(string key) => key is "priority-repos" or "no-auto-merge-repos" or "repo-trackers";

    // The most repos the open choice lists at once (the field above narrows them), and the longest an entry is spelt before its middle is elided.
    private const int OfferedRows = 10;
    private const int ChipChars = 44;
    private const float ControlH = 26f;
    private const float OfferedWidth = 320f;
    private const float OfferedRowH = 24f;

    /// <summary>
    /// A list setting: each entry as a chip with a × (the list without it is what is sent), then for a list of repos a choice of
    /// the node's repos by name with an Add (and for repo-trackers the tracker), and a free entry for a folder outside them.
    /// </summary>
    private Layout.Node ListEditor(SettingRow setting, System.Collections.Immutable.ImmutableHashSet<string> working)
    {
        var key = setting.Key;
        var entries = Roots(setting);
        var read = Volatile.Read(ref _read);
        var choice = Volatile.Read(ref _choice);
        var trackers = key == "repo-trackers";
        var pick = choice.Picked.GetValueOrDefault(key);
        var tracker = choice.Tracker.GetValueOrDefault(key, "github");
        string Entry(string name) => trackers ? $"{name}={tracker}" : name;

        // A row a chip, left-aligned: the height is what the rows add up to, whatever the width.
        var stack = new List<Layout.Node>();
        if (entries.Length == 0)
        {
            stack.Add(Layout.Builder.Text("none yet", 12f, Dim).RowH(ControlH));
        }
        foreach (var entry in entries)
        {
            stack.Add(Layout.Builder.HStack(Chip(setting, entry, entries, working), Layout.Builder.Spacer().WStar()).RowH(ControlH));
        }
        var controls = new List<Layout.Node>();
        var offering = IsRepoList(key) && LoadRepos is not null;
        if (offering)
        {
            controls.Add(read.Repos is null
                ? Idle(read.ReposProblem is null ? "reading repos…" : "repos not read", read.ReposProblem is null ? "the node's repos are being read" : $"the node did not list its repos: {read.ReposProblem}", Button)
                : Pill(pick is null ? "Choose a repo ▾" : $"{pick} ▾", $"settings:{key}:choose", () => Toggle(key), chosen: pick is not null).HFixed(ControlH));
            if (trackers)
            {
                foreach (var which in new[] { "github", "shortcut" })
                {
                    controls.Add(Pill(which, $"settings:{key}:tracker:{which}", () => SetTracker(key, which), chosen: tracker == which).HFixed(ControlH));
                }
            }
            var add = $"settings:{key}:add";
            controls.Add(pick is null
                ? Idle("Add", "choose a repo to add", FieldBg)
                : Pill("Add", add, () => Act(Verbs.Set, [key, .. entries, Entry(pick)], $"{setting.Label}: adding {pick}…", add, () => ClearPick(key)), working: working.Contains(add)).HFixed(ControlH));
        }
        var field = Field(setting, offering ? "or a folder's path, then Enter" : "add a folder, then Enter", text => [key, .. entries, trackers && !HasTracker(text) ? $"{text}={tracker}" : text], working);
        if (offering)
        {
            controls.Add(Layout.Builder.Spacer().WStar());
            stack.Add(Layout.Builder.HStack([.. controls]).WithGap(6f).RowH(ControlH));
            if (choice.Opened == key && read.Repos is { } repos)
            {
                stack.Add(Layout.Builder.HStack(Offered(key, repos, entries, pick), Layout.Builder.Spacer().WStar()).HAuto());
            }
            stack.Add(Layout.Builder.HStack(field.WFixed(280f), Layout.Builder.Spacer().WStar()).RowH(ControlH));
        }
        else
        {
            stack.Add(field.RowH(ControlH));
        }
        return Layout.Builder.VStack([.. stack]).WithGap(6f);
    }

    private static bool HasTracker(string text) => text.EndsWith("=github", StringComparison.OrdinalIgnoreCase) || text.EndsWith("=shortcut", StringComparison.OrdinalIgnoreCase);

    // An entry as a chip: its text (a long one elided in the middle, the whole on hover) and the × that sends the list without it.
    private Layout.Node Chip(SettingRow setting, string entry, string[] entries, System.Collections.Immutable.ImmutableHashSet<string> working)
    {
        var action = $"settings:{setting.Key}:remove:{entry}";
        return Layout.Builder.HStack(
                Layout.Builder.Text(Elide(entry), 12f, Body, TextAlign.Center).WithTooltip(entry).Pad(8f, 4f).WAuto(),
                // The × stays a typeset rune for want of a close glyph: SharpAstro/DIR.Lib#151.
                Pill("×", action, () => Act(Verbs.Set, [setting.Key, .. entries.Where(e => e != entry).DefaultIfEmpty("default")], $"{setting.Label}: removing {entry}…", action), working: working.Contains(action)))
            .WithGap(0f).Bg(FieldBg).WAuto().HFixed(ControlH);
    }

    // The node's repos not yet in the list, by name with the folder on hover, narrowed by what the field holds; a press picks one.
    private Layout.Node Offered(string key, RepoInfo[] repos, string[] entries, string? pick)
    {
        var listed = entries.Select(e => key == "repo-trackers" ? e.Split('=')[0] : e).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var narrow = _fields.TryGetValue(key, out var input) ? input.Text.Trim() : "";
        var fits = repos
            .Where(r => !listed.Contains(r.Name) && (narrow.Length == 0 || r.Name.Contains(narrow, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(narrow, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var rows = new List<Layout.Node>();
        foreach (var repo in fits.Take(OfferedRows))
        {
            rows.Add(Indented(Layout.Builder.Text(Elide(repo.Name), 12f, repo.Name == pick ? Bright : Body))
                .WithTooltip(repo.Path)
                .BgHover(ButtonHover)
                .Follows(new HitResult.ButtonHit($"settings:{key}:pick:{repo.Name}"), _ => Pick(key, repo.Name)));
        }
        if (fits.Length == 0)
        {
            rows.Add(Indented(Layout.Builder.Text(repos.Length == 0 ? "the node lists no repos: give a folder's path below" : "no repo left to add" + (narrow.Length > 0 ? $" matching {Elide(narrow)}" : ""), 12f, Dim)));
        }
        else if (fits.Length > OfferedRows)
        {
            rows.Add(Indented(Layout.Builder.Text($"{fits.Length - OfferedRows} more: type in the field to narrow them", 11f, Dim)));
        }
        return Layout.Builder.VStack([.. rows]).Bg(Button).WFixed(OfferedWidth).HAuto();
    }

    // A menu row's text, set in from the row's edge.
    private static Layout.Node Indented(Layout.Node text) =>
        Layout.Builder.HStack(Layout.Builder.Spacer().WFixed(10f), text.WStar()).RowH(OfferedRowH);

    private void Toggle(string key)
    {
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _choice, c => c with { Opened = c.Opened == key ? null : key });
        Changed?.Invoke();
    }

    private void Pick(string key, string repo)
    {
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _choice, c => c with { Picked = c.Picked.SetItem(key, repo), Opened = null });
        Changed?.Invoke();
    }

    private void SetTracker(string key, string tracker)
    {
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _choice, c => c with { Tracker = c.Tracker.SetItem(key, tracker) });
        Changed?.Invoke();
    }

    private void ClearPick(string key) =>
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _choice, c => c with { Picked = c.Picked.Remove(key) });

    // Long text elided in the middle (a path's ends say the most); where it is shown the whole of it is on hover.
    private static string Elide(string text) =>
        text.Length <= ChipChars ? text : $"{text[..(ChipChars / 3)]}…{text[^(ChipChars - ChipChars / 3 - 1)..]}";

    // A control that cannot be pressed now, in the shape of a pill.
    private static Layout.Node Idle(string label, string why, RGBAColor32 background) =>
        Layout.Builder.Text(label, 12f, Dim, TextAlign.Center).Pad(10f, 4f).WAuto().Bg(background).WithTooltip(why).HFixed(ControlH);

    // A field's working id is its own: Enter sends, and the field says "saving…" until the node answers (a second Enter is ignored meanwhile).
    private Layout.Node Field(SettingRow setting, string placeholder, Func<string, string[]> args, System.Collections.Immutable.ImmutableHashSet<string> working)
    {
        var control = $"settings:{setting.Key}:field";
        if (!_fields.TryGetValue(setting.Key, out var input))
        {
            input = new TextInputState();
            _fields[setting.Key] = input;
        }
        input.Placeholder = working.Contains(control) ? "saving…" : placeholder;
        input.OnCommit = text =>
        {
            if (text.Trim().Length > 0 && !Volatile.Read(ref _read).Working.Contains(control))
            {
                Act(Verbs.Set, args(text.Trim()), $"{setting.Label}: {text.Trim()}…", control);
                input.Clear();
            }
            return Task.CompletedTask;
        };
        return Layout.Builder.TextInput(input, 13f).Bg(FieldBg);
    }

    private static string[] Roots(SettingRow setting) =>
        (setting.Saved ?? setting.Value).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // A pill whose verb is out shows "…" after its label and does not click again until the node answers.
    private static Layout.Node Pill(string label, string action, Action onClick, bool chosen = false, bool working = false)
    {
        var pill = Layout.Builder.Text(working ? $"{label}…" : label, 12f, working && !chosen ? Body : Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(chosen ? Chosen : Button);
        return working ? pill : pill.BgHover(chosen ? Chosen : ButtonHover).Follows(new HitResult.ButtonHit(action), _ => onClick());
    }
}

/// <summary>How the node took a verb.</summary>
internal enum ReplyKind
{
    Done,
    Refused,
    /// <summary>The connection closed before the answer was read (a restart for the change can do that).</summary>
    NoAnswer,
}

/// <summary>What became of a verb, in a line; for a <c>set</c> that saved, the setting's row as the node says it stands now.</summary>
internal sealed record SettingsReply(string Line, ReplyKind Kind = ReplyKind.Done, SettingRow? Row = null);

/// <summary>The Settings page's verbs on the node, for either host: what became of each, in a line, with the changed row.</summary>
internal static class SettingsCommands
{
    public static async Task<SettingsReply> SendAsync(NodeClient client, string verb, string[] args, string who, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await client.SendAsync(new CommandRequest { Verb = verb, Target = Verbs.NodeTarget, Args = args, Who = who }, cancellationToken);
            return envelope.Success && envelope.Response is { Ok: true } result
                ? new SettingsReply(result.Way ?? "done", ReplyKind.Done, result.Setting)
                : new SettingsReply($"refused: {envelope.Response?.Error ?? envelope.Error}", ReplyKind.Refused);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            // A restart for the change can close the connection before the answer is read: the page reads again.
            return new SettingsReply($"no answer ({ex.Message}); the node may be restarting", ReplyKind.NoAnswer);
        }
    }
}
