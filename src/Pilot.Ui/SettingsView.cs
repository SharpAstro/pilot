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
/// does not show its settings to another machine, so the title names this node's owner, the top bar offers no node pills, and
/// a line under the bar says another node's settings are changed on that machine. Generic over the surface, as the other pages are.
/// </summary>
internal sealed class SettingsView<TSurface>(Renderer<TSurface> renderer) : PilotWidgetBase<TSurface>(renderer)
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

    private static RGBAColor32 Ground => Palette.Current.Ground;
    private static RGBAColor32 HeaderBg => Palette.Current.HeaderBg;
    private static RGBAColor32 RowAltBg => Palette.Current.RowAltBg;
    private static RGBAColor32 Bright => Palette.Current.Bright;
    private static RGBAColor32 Body => Palette.Current.Body;
    private static RGBAColor32 Dim => Palette.Current.Dim;
    private static RGBAColor32 Faint => Palette.Current.Faint;
    private static RGBAColor32 Ok => Palette.Current.Flying;
    private static RGBAColor32 Todo => Palette.Current.Held;
    private static RGBAColor32 Optional => Palette.Current.Away;
    private static RGBAColor32 Button => Palette.Current.Button;
    private static RGBAColor32 ButtonHover => Palette.Current.ButtonHover;
    private static RGBAColor32 Chosen => Palette.Current.Chosen;
    private static RGBAColor32 FieldBg => Palette.Current.MenuBg;

    private readonly ListScrollController _scroll = new() { Mode = ScrollBarMode.Interactive };
    private readonly Dictionary<string, TextInputState> _fields = new(StringComparer.Ordinal);
    // What the page has read, as one immutable record swapped whole: reads land on the pool, the render thread draws what
    // it finds, and neither waits on the other.
    // Checks: what the node last measured (null until it first answers); Checking: a read the person is waiting on is out
    // (the first, a refresh, or the one after a fix), so the rows say so; the controls pressed and not yet answered are <see cref="Presses"/>'s.
    // Repos: the node's tracked repos the list pickers offer (null until read); ReposProblem: why they could not be read.
    // ChecksTicket: the ticket of the checks read shown, so a slower, older read cannot put its answer over a newer one.
    private sealed record Seen(SettingsPage? Page, SetupChecks? Checks, bool Checking, string? ChecksProblem, GrantInfo[] Given, string[] Held, string? Problem, RepoInfo[]? Repos = null, string? ReposProblem = null, long ChecksTicket = 0);

    private Seen _read = new(null, null, false, null, [], [], null);
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
    public Action? Changed { get => Presses.Changed; set => Presses.Changed = value; }

    /// <summary>The controls pressed and not yet answered, by their ids: each reads its working word, dimmed, and takes no second press.</summary>
    internal Presses Presses { get; } = new();

    private bool Busy(string control) => Presses.PendingOf(control) is not null;

    /// <summary>What the last button or field said, under the header.</summary>
    public string? Said { get; set; }

    /// <summary>The header's ? card: which pilot this is and where its things are.</summary>
    public AboutCard? About { get; set; }

    /// <summary>The header's Autopilot switch, the same on every page.</summary>
    public AutopilotSwitch? Autopilot { get; set; }
    
    /// <summary>The header's Night switch, the same on every page.</summary>
    public NightSwitch? Night { get; set; }

    /// <summary>The window's node selection, which names this node's owner; the Settings top bar shows no pills.</summary>
    public NodeFilter? Nodes { get; set; }

    /// <summary>The page as last read, or null before the first read.</summary>
    public SettingsPage? Page => Volatile.Read(ref _read).Page;

    /// <summary>The setup checks as last measured, or null before the node first answers.</summary>
    public SetupChecks? Checks => Volatile.Read(ref _read).Checks;

    public void Render(DateTimeOffset now, RectF32 bounds)
    {
        Palette.Use(Night);
        BeginFrame();
        PageWidth = Scale.ToDesignX(bounds.Width);
        RenderLayout(Tree(now), bounds);
    }

    /// <summary>The room's width in design units as the last <see cref="Render"/> or <c>Tree(now, width)</c> had it; null before the first. A range row lays its captions out against it (#770).</summary>
    internal float? PageWidth { get; set; }

    /// <summary>The page as a tree: what <see cref="Render"/> paints, and what pilot-tui arranges in cells.</summary>
    internal Layout.Node Tree(DateTimeOffset now, float? width = null)
    {
        Palette.Use(Night);
        _drawnAt = now;
        _measured = null;
        // A host with no render of its own (pilot-tui) hands the room's width in design units, as PullRequests.Tree takes it.
        PageWidth = width ?? PageWidth;
        Read();
        var read = Volatile.Read(ref _read);
        var page = read.Page;
        var problem = read.Problem;
        // The line waiting for the page sits at the top, in the gutter, where the page's first row will be: stretched on its own
        // it was drawn two thirds down, against the rail.
        var body = page is null
            ? Layout.Builder.VStack([.. OtherNodesRow(), Layout.Builder.Text(problem is null ? (Load is null ? "settings are the node's: open pilot on a node" : "reading the node's settings…") : $"the node did not send its settings: {problem}", 13f, Dim).RowH(28f)]).Pad(Gutter, 8f)
            : Main(page, read, now);
        var header = Header(page, read.Checks, problem);
        var tree = Layout.Builder.VStack(header, body.Stretch()).Bg(Ground).Stretch();
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
        if (Busy(control))
        {
            return;
        }
        Said = doing;
        _ = ActAsync();

        async Task ActAsync()
        {
            SettingsReply? reply = null;
            // Working at once; the answer's row goes in before the control stops working, so no draw shows the old row with the control idle.
            await Presses.PressAsync(control, control, async ct =>
            {
                reply = await Task.Run(() => send(verb, args, ct), ct).ConfigureAwait(false);
                if (reply.Row is not null)
                {
                    Interlocked.Increment(ref _swaps);
                }
                System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with
                {
                    Page = reply.Row is { } row && read.Page is { } page ? page with { Settings = [.. page.Settings.Select(s => s.Key == row.Key ? row : s)] } : read.Page,
                });
                if (reply.Kind == ReplyKind.Done)
                {
                    // What the person chose for this verb goes only once the node has taken it: a refusal leaves the choice to amend.
                    taken?.Invoke();
                }
                if (verb == Verbs.Set && reply.Row is null && reply.Kind == ReplyKind.NoAnswer && Load is { } load)
                {
                    // No row came (the node may be restarting for the change): read the settings, as the only way to know. The
                    // pill keeps saying it is setting until that read is in, so it never reads done for a change not yet seen.
                    try
                    {
                        await RefreshAsync(load, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // A client timeout while the node is still down: the line below says there was no answer, and the
                        // page's own next read shows the outcome. Unhandled, it left the header on "setting …" for good.
                    }
                }
                // The node's words, refusal included, are the page's line (Said); the control itself is back to its label. A
                // single value taken names what was set, from the row the node sent back; a list's value is lines, so its line is
                // the node's own, on one line as the header has.
                Said = SaidOf(reply);
                return null;
            }).ConfigureAwait(false);
            if (reply is null)
            {
                return;
            }
            if (verb == Verbs.Setup)
            {
                await ReadChecksAsync(refresh: false, waited: true, CancellationToken.None).ConfigureAwait(false);
            }
            else if (verb == Verbs.RevokeGrant)
            {
                await RefreshGrantsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Whose settings these are: this machine's node, named as the Chat page names its watchtower's owner.</summary>
    internal string Owner => TopBar.ThisNodeName(Nodes);

    /// <summary>The line under the bar while the window knows more than one node: the settings route is this machine's only.</summary>
    internal const string OtherNodesLine = "another node's settings are changed on that machine";

    /// <summary>The line about other nodes, as the page's first row, while there is more than one node.</summary>
    private IEnumerable<Layout.Node> OtherNodesRow() => Nodes is { } nodes && nodes.Choices.Count() > 1
        ? [Layout.Builder.Text(OtherNodesLine, 12f, Dim).RowH(22f).Pad(Gutter, 0f)]
        : [];

    private Layout.Node Header(SettingsPage? page, SetupChecks? checks, string? problem) =>
        // No node pills: these settings are this machine's, and a pill would leave them silently on this node.
        TopBar.Build(new PageHeader($"{Owner}'s settings (this machine)")
        {
            Counts = page is null ? "" : Summary(page, checks),
            Source = Said ?? page?.Restart ?? (page is null ? (problem ?? "") : $"saved in {page.File} · the command line wins over it"),
        }, new TopBarParts(null, null, Autopilot, About, Night));

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
        var rows = new List<Layout.Node>(OtherNodesRow());
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
            rows.Add(SettingRowNode(page.Settings[i], i));
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
                Layout.Builder.Text(title, 15f, Bright).Selectable().WAuto(),
                Layout.Builder.Text(hint, 12f, Faint).Selectable().WStar())
            .WithGap(12f).Pad(8f, 10f).RowH(38f);

    // Setup's heading carries the refresh: it measures the checks again, and shows it is working until the node answers.
    private Layout.Node SetupSection(Seen read)
    {
        var hint = read.ChecksProblem is { } problem ? $"could not read the checks again: {problem}" : $"what it takes for {Owner} to be part of the fleet";
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
                        Send is null ? Layout.Builder.Spacer().WStar() : NodePill("Revoke", $"settings:revoke:{grant.Id}", () => Act(Verbs.RevokeGrant, [grant.Id], $"revoking {grant.Label}…", $"settings:revoke:{grant.Id}")),
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
            ? NodePill(label, action, () => Act(Verbs.Setup, [check.Key], $"{check.Title}: {label}…", action))
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

    private Layout.Node SettingRowNode(SettingRow setting, int index)
    {
        if (IsRange(setting))
        {
            return RangeRowNode(setting, index);
        }
        var state = setting.Source == SettingSources.CommandLine ? "from the node's command line, which wins"
            : setting.Pending ? $"saved as {(setting.Saved ?? "the default")}: applies at the next start"
            : setting.Source == SettingSources.File ? "saved" : "default";
        // Beside its title when the editor's line fits the room left of it; else under the title with the row's whole width, where
        // what still does not fit flows onto lines of its own (#770), so no pill or chip is cut by the page's edge.
        var measure = TextWidth();
        var need = EditorNeed(setting, measure);
        var room = PageWidth ?? WideRoom;
        var stacked = room - SlackAround - 14f - TitleWidth < need;
        var width = Math.Max(0f, room - SlackAround - (stacked ? 0f : 14f + TitleWidth));
        var stripe = Layout.Builder.Spacer().Bg(setting.Pending ? Todo : Faint).WFixed(StripeWidth).HStar();
        var title = Layout.Builder.VStack(
            Layout.Builder.Text(setting.Label, 14f, Bright).Selectable().RowH(22f),
            Layout.Builder.Text(state, 11f, setting.Pending ? Todo : Dim).Selectable().RowH(18f));
        var body = Layout.Builder.VStack(
            Editor(setting, width, need, measure),
            Layout.Builder.Text(setting.Detail, 11f, Dim).WithTooltip(setting.Detail).Selectable().RowH(18f));
        Layout.Node row;
        if (stacked)
        {
            // WStar: as the range row's, the row is Auto wide by default and an Auto stack counts its Star body as nothing.
            row = Layout.Builder.HStack(stripe, Layout.Builder.VStack(title.WStar().HAuto(), body.WithGap(4f).WStar().HAuto()).WithGap(4f).WStar().HAuto())
                .WithGap(14f).Pad(8f, 6f).WStar().HAuto();
        }
        else
        {
            row = Layout.Builder.HStack(stripe, title.WFixed(TitleWidth), body.WStar()).WithGap(14f).Pad(8f, 6f);
            // Repo roots grow a line per folder: their row takes the height its lines ask for.
            // WStar as the range row's: an Auto wide row counts its Star body as nothing, so the list's wrapping controls measured against no width.
            row = setting.Many ? row.WStar().HAuto() : row.RowH(RowHeight);
        }
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    // What a setting's editor asks for on one line, in design units: its pills (and the reset after them) or its field. A list is a chip a line,
    // each cut to the room it has, so it asks for what a chip can read in.
    private const float EditorGap = 6f;
    private const float PillPad = 10f;
    private const float FieldWidth = 220f;
    private const float ListNeed = 240f;

    private static float EditorNeed(SettingRow setting, Func<string, float, float> measure)
    {
        if (setting.Many)
        {
            return ListNeed;
        }
        float PillOf(string label) => measure(label, 12f) + 2 * PillPad;
        var width = setting.Choices.Length > 0
            ? setting.Choices.Sum(c => PillOf(c == "none" ? "default" : c)) + (setting.Choices.Length - 1) * EditorGap
            : FieldWidth;
        return setting.Saved is not null && setting.Source != SettingSources.CommandLine ? width + EditorGap + PillOf("reset") : width;
    }

    // A role's range (#599): its key ends in -range and its choices are the line of pairs, in order.
    internal static bool IsRange(SettingRow setting) => setting.Key.EndsWith("-range", StringComparison.Ordinal) && setting.Choices.Length > 1;

    // Which handle a pair press moves (default or max, "" for the nearer one) and whether a press sets this node's range or the fleet's, by key.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _handle = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _fleetScope = new(StringComparer.Ordinal);

    /// <summary>Where a range's two ends sit on the line of pairs; a bare model sits on its lowest effort. -1 for what is not on the line.</summary>
    internal static (int Default, int Max) HandlesOf(SettingRow setting, bool fleet = false) => HandlesOf(setting, EditedText(setting, fleet));

    /// <summary>The stored text of the range a press edits: the fleet's when the fleet scope is on and one is set, else the row's own.</summary>
    internal static string EditedText(SettingRow setting, bool fleet) => fleet && setting.FleetValue is { Length: > 0 } fv ? fv : setting.Value;

    /// <summary>The range a press on pair <paramref name="pick"/> sends: <paramref name="text"/> with only the end that moved replaced, so a bare end stays bare; null when nothing moves.</summary>
    internal static string? PressedRange(SettingRow setting, string text, int pick, string handle)
    {
        var now = HandlesOf(setting, text);
        if (Moved(now, pick, handle) is not { } next)
        {
            return null;
        }
        var ends = text.Split("..", 2);
        var def = ends[0].Trim();
        var max = ends.Length > 1 ? ends[1].Trim() : def;
        if (next.Default != now.Default)
        {
            // a bare max has no effort, so a default on its own pair or above it would send "opus-low..opus", which TryParse refuses
            return !max.Contains('-') && next.Default >= now.Max ? null : $"{setting.Choices[pick]}..{max}";
        }
        return $"{def}..{setting.Choices[pick]}";
    }

    private static (int Default, int Max) HandlesOf(SettingRow setting, string text)
    {
        int At(string pair)
        {
            var found = Array.FindIndex(setting.Choices, c => string.Equals(c, pair, StringComparison.OrdinalIgnoreCase));
            return found >= 0 ? found : Array.FindIndex(setting.Choices, c => c.StartsWith(pair + "-", StringComparison.OrdinalIgnoreCase));
        }
        var ends = text.Split("..", 2);
        var def = At(ends[0].Trim());
        var max = ends.Length > 1 ? At(ends[1].Trim()) : def;
        return (def, max);
    }

    /// <summary>
    /// The range the handles hold after pair <paramref name="pick"/> is pressed: the chosen handle ("default" or "max") moves to
    /// it, or with none chosen the nearer one does. A press that would put the max below the default, or the default above the max,
    /// changes nothing (null), so no handle ever lands on a pair the range does not allow.
    /// </summary>
    internal static (int Default, int Max)? Moved((int Default, int Max) now, int pick, string handle)
    {
        var moving = handle is "default" or "max" ? handle
            : pick < now.Default ? "default"
            : pick > now.Max ? "max"
            : Math.Abs(pick - now.Default) <= Math.Abs(pick - now.Max) ? "default" : "max";
        var next = moving == "default" ? (pick, now.Max) : (now.Default, pick);
        return next.Item1 > next.Item2 || next == now ? null : next;
    }

    private static string SourceWords(SettingRow setting) => setting.Source switch
    {
        SettingSources.Fleet => "set for the fleet",
        SettingSources.ThisNode => "set on this node",
        SettingSources.BuiltIn or SettingSources.Default => "built in",
        SettingSources.CommandLine => "from the node's command line, which wins",
        _ => setting.Source,
    };

    // What the room around a range row's line of pairs takes, in design units: the page's gutters (2 x 16), the row's padding (2 x 8), the
    // stripe (4), a gap (14) and a scroll bar's slack, so the width worked out is never more than the line gets.
    private const float SlackAround = 2 * Gutter + 16f + StripeWidth + 14f + 16f;
    private const float WideRoom = 900f;
    private const float PairGap = 2f;
    private const float TextMargin = 6f;

    // A text's width in design units: measured with the font the page draws with, or a guess where there is none (a terminal, before a first render).
    // One measure context and one memo a frame (Tree starts them over), not a context per row: the same text at the same size is measured once.
    private Func<string, float, float>? _measured;

    private Func<string, float, float> TextWidth()
    {
        if (_measured is { } cached)
        {
            return cached;
        }
        if (PageWidth is null || string.IsNullOrEmpty(FontPath))
        {
            return _measured = (text, size) => text.Length * size * 0.6f;
        }
        var context = MeasureContext();
        var scale = Scale.X <= 0f ? 1f : Scale.X;
        var memo = new Dictionary<(string, float), float>();
        return _measured = (text, size) =>
        {
            if (!memo.TryGetValue((text, size), out var width))
            {
                memo[(text, size)] = width = context.MeasureText(text, size).Width / scale;
            }
            return width;
        };
    }

    private Layout.Node RangeRowNode(SettingRow setting, int index)
    {
        var key = setting.Key;
        var handle = _handle.GetValueOrDefault(key, "");
        var fleet = _fleetScope.GetValueOrDefault(key);
        var (def, max) = HandlesOf(setting, fleet);
        var n = setting.Choices.Length;
        var measure = TextWidth();
        // Beside its title when the line has room for a cell a pair can read in; else under the title, with the row's whole width.
        var room = PageWidth ?? WideRoom;
        var beside = room - SlackAround - 14f - TitleWidth;
        var stacked = beside < n * 30f;
        var lineWidth = Math.Max(n * 12f, stacked ? room - SlackAround : beside);
        var cellWidth = RangeCaptions.Column(n, lineWidth, PairGap);
        var form = RangeCaptions.LabelForm(setting.Choices, cellWidth - 2 * 2f, TextMargin, text => measure(text, 11f));
        var cells = new List<Layout.Node>();
        for (var i = 0; i < n; i++)
        {
            var pair = setting.Choices[i];
            var at = i;
            var end = i == def || i == max;
            var inside = i > def && i < max;
            var allowed = PressedRange(setting, EditedText(setting, fleet), i, handle) is not null;
            var action = $"settings:{key}:pair:{pair}";
            var label = RangeCaptions.Label(pair, form);
            // A pair whose press is with the node reads as working, as the done mark does: off the Button blue that says it can be pressed,
            // onto the menu ground in the dim ink (4.6:1; Dim on Button is only 3.3:1). A handle's pair keeps its Chosen mark and its Bright ink
            // (5.1:1; Body on Chosen is only 3.7:1), as a chosen working Pill does: it shows it is working by the missing hit alone.
            Layout.Node CellOf(bool working) => Layout.Builder.Text(label, 11f, end ? Bright : working ? Dim : allowed || inside ? Body : Faint, TextAlign.Center).Pad(2f, 4f).WStar().HFixed(ControlH)
                .Bg(end ? Chosen : working || inside ? FieldBg : Button).WithTooltip(pair);
            // A pair that can be pressed asks the node, so it goes through Presses (working at once, no second press while it is out);
            // one that cannot is a plain control label.
            var cell = allowed
                ? Presses.Button(action, action,
                    () => CellOf(false).BgHover(end ? Chosen : ButtonHover).Follows(new HitResult.ButtonHit(action), _ => PickPair(setting, at)),
                    _ => CellOf(true), doneMark: false)
                : CellOf(false);
            cells.Add(cell);
        }
        var pairLines = new List<Layout.Node>();
        if (form > 0)
        {
            // The effort alone says nothing of the model: it shows once over its run of pairs, in full when the run is wide enough.
            var models = RangeCaptions.Groups(setting.Choices).Select(g =>
            {
                var span = (g.To - g.From + 1) * cellWidth + (g.To - g.From) * PairGap;
                var name = measure(g.Model, 10f) + TextMargin <= span ? g.Model : g.Model.Length > 0 ? char.ToUpperInvariant(g.Model[0]).ToString() : "?";
                return (name, g.From, g.To);
            });
            pairLines.Add(CaptionLine(models, n, lineWidth, Dim, null, ""));
        }
        // WStar on every line: a stack of Star cells is Auto wide by default, and measures to its gaps alone, so its cells were given no width.
        pairLines.Add(Layout.Builder.HStack([.. cells]).WithGap(PairGap).WStar().HFixed(ControlH));
        var captions = RangeCaptions.Place(n, def, max, lineWidth, PairGap, TextMargin, text => measure(text, 10f));
        foreach (var line in captions.Select(c => c.Line).Distinct().Order())
        {
            pairLines.Add(CaptionLine(captions.Where(c => c.Line == line).Select(c => (c.Text, c.From, c.To)), n, lineWidth, Body, Bright, handle));
        }
        // The four pills flow onto a second line where the row is too narrow for them on one: Auto wide pills in a stack ran past the page's edge (#770).
        var scope = Layout.Builder.WrapH(
                Pill("this node", $"settings:{key}:scope:node", () => SetScope(key, false), chosen: !fleet).HFixed(ControlH),
                Pill("fleet", $"settings:{key}:scope:fleet", () => SetScope(key, true), chosen: fleet).HFixed(ControlH),
                Pill("default handle", $"settings:{key}:handle:default", () => SetHandle(key, "default"), chosen: handle == "default").HFixed(ControlH),
                Pill("max handle", $"settings:{key}:handle:max", () => SetHandle(key, "max"), chosen: handle == "max").HFixed(ControlH))
            .WithGap(6f).WithLineGap(4f).WStar().HAuto();
        var lines = new List<Layout.Node>(pairLines)
        {
            Layout.Builder.Text($"{(fleet ? "setting the fleet's range" : "setting this node's range")}: a press moves {(handle.Length > 0 ? "the " + handle + " handle" : "the nearer handle")}", 11f, Dim).Selectable().RowH(16f),
            scope,
        };
        if ((setting.Saved is not null && setting.Source != SettingSources.CommandLine && !fleet) || (fleet && setting.FleetValue is not null))
        {
            var action = $"settings:{key}:default:{(fleet ? "fleet" : "node")}";
            var args = fleet ? new[] { key, "default", "--fleet" } : [key, "default"];
            lines.Add(Layout.Builder.HStack(NodePill(fleet ? "reset the fleet's" : "reset", action, () => Act(Verbs.Set, args, $"{setting.Label}: back to the default…", action)), Layout.Builder.Spacer().WStar()).HFixed(ControlH));
        }
        var fleetLine = setting.FleetValue is { } fv ? $"the fleet's range is {fv}" : "no fleet range is set";
        lines.Add(Layout.Builder.Text($"{setting.Value} · {SourceWords(setting)} · {fleetLine}", 11f, Body).WithTooltip(setting.Detail).Selectable().RowH(18f));
        if (setting.Deviations.Length > 0)
        {
            var text = "differs from the fleet's: " + string.Join("; ", setting.Deviations.Select(d => $"{d.Node} runs {d.Value}"));
            lines.Add(WrappedProse.Paragraphs(text, 11f, Todo, 40));
        }
        lines.Add(Layout.Builder.Text(setting.Detail, 11f, Dim).WithTooltip(setting.Detail).Selectable().RowH(18f));
        var title = Layout.Builder.VStack(
            Layout.Builder.Text(setting.Label, 14f, Bright).Selectable().RowH(22f),
            Layout.Builder.Text(SourceWords(setting), 11f, Dim).Selectable().RowH(18f));
        var body = Layout.Builder.VStack([.. lines]).WithGap(4f);
        var stripe = Layout.Builder.Spacer().Bg(setting.Deviations.Length > 0 ? Todo : Faint).WFixed(StripeWidth).HStar();
        var row = (stacked
                ? Layout.Builder.HStack(stripe, Layout.Builder.VStack(title.WStar().HAuto(), body.WStar().HAuto()).WithGap(4f).WStar().HAuto())
                : Layout.Builder.HStack(stripe, title.WFixed(TitleWidth), body.WStar().HAuto()))
            // WStar: the row is Auto wide by default, and an Auto stack counts its Star body as nothing, so it was as wide as the stripe and the title and no more.
            .WithGap(14f).Pad(8f, 6f).WStar().HAuto();
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    // One line of captions under (or, for the models, over) the line of pairs: runs over their columns, spacers between, each run as wide as its columns.
    private static Layout.Node CaptionLine(IEnumerable<(string Text, int From, int To)> runs, int columns, float width, RGBAColor32 colour, RGBAColor32? chosen, string handle)
    {
        var parts = RangeCaptions.Segments(runs, columns, width, PairGap).Select(s => s.Text is { } text
            ? Layout.Builder.Text(text, 10f, chosen is { } bright && handle.Length > 0 && text.StartsWith(handle, StringComparison.Ordinal) ? bright : colour, TextAlign.Center).Selectable().WStar(s.Width).HFixed(14f)
            : Layout.Builder.Spacer().WStar(s.Width).HFixed(14f));
        return Layout.Builder.HStack([.. parts]).WStar().HFixed(14f);
    }

    private void SetScope(string key, bool fleet)
    {
        _fleetScope[key] = fleet;
        Changed?.Invoke();
    }

    private void SetHandle(string key, string handle)
    {
        _handle[key] = _handle.GetValueOrDefault(key, "") == handle ? "" : handle;
        Changed?.Invoke();
    }

    private void PickPair(SettingRow setting, int pick)
    {
        var fleet = _fleetScope.GetValueOrDefault(setting.Key);
        if (PressedRange(setting, EditedText(setting, fleet), pick, _handle.GetValueOrDefault(setting.Key, "")) is not { } range)
        {
            return;
        }
        string[] args = fleet ? [setting.Key, range, "--fleet"] : [setting.Key, range];
        var action = $"settings:{setting.Key}:pair:{setting.Choices[pick]}";
        Act(Verbs.Set, args, $"{setting.Label}: {range}{(fleet ? " for the fleet" : "")}…", action);
    }

    // How a setting is changed: a pill per choice, a field (Enter saves), or for repo roots each folder with an x and a field to add one.
    // It has `width` design units: on one line where its `need` fits, else its pills flow onto lines of their own (#770).
    private Layout.Node Editor(SettingRow setting, float width, float need, Func<string, float, float> measure)
    {
        if (setting.Many)
        {
            // WStar: the list's controls wrap, and a wrap in an Auto wide stack measures against no width, so each pill took a line of its own.
            return ListEditor(setting, width, measure).WStar().HAuto();
        }
        var parts = new List<Layout.Node>();
        if (setting.Choices.Length > 0)
        {
            foreach (var choice in setting.Choices)
            {
                var chosen = string.Equals(choice, setting.Saved ?? setting.Value, StringComparison.OrdinalIgnoreCase);
                var action = $"settings:{setting.Key}:{choice}";
                // The wire value for "leave it to Claude Code" is none; the button says default, as the help line does.
                var label = choice == "none" ? "default" : choice;
                parts.Add(NodePill(label, action, () => Act(Verbs.Set, [setting.Key, choice], $"setting {setting.Label} to {label}…", action), chosen, working: SettingWord, doneMark: false));
            }
        }
        else
        {
            parts.Add(Field(setting, setting.Value, text => [setting.Key, text]).WFixed(Math.Min(FieldWidth, width)));
        }
        if (setting.Saved is not null && setting.Source != SettingSources.CommandLine)
        {
            var action = $"settings:{setting.Key}:default";
            // "reset" takes the saved value out (back to what the node does unset); no choice on the row reads the same.
            parts.Add(NodePill("reset", action, () => Act(Verbs.Set, [setting.Key, "default"], $"{setting.Label}: back to the default…", action)));
        }
        if (need <= width)
        {
            parts.Add(Layout.Builder.Spacer().WStar());
            return Layout.Builder.HStack([.. parts]).WithGap(EditorGap).RowH(ControlH);
        }
        return Layout.Builder.WrapH([.. parts.Select(p => p.HFixed(ControlH))]).WithGap(EditorGap).WithLineGap(4f).WStar().HAuto();
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
    private Layout.Node ListEditor(SettingRow setting, float width, Func<string, float, float> measure)
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
            stack.Add(Layout.Builder.HStack(Chip(setting, entry, entries, width, measure), Layout.Builder.Spacer().WStar()).RowH(ControlH));
        }
        var controls = new List<Layout.Node>();
        var offering = IsRepoList(key) && LoadRepos is not null;
        if (offering)
        {
            controls.Add(read.Repos is null
                ? Idle(read.ReposProblem is null ? "reading repos…" : "repos not read", read.ReposProblem is null ? "the node's repos are being read" : $"the node did not list its repos: {read.ReposProblem}", Button)
                : ChoosePill(key, pick));
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
                : NodePill("Add", add, () => Act(Verbs.Set, [key, .. entries, Entry(pick)], $"{setting.Label}: adding {pick}…", add, () => ClearPick(key))).HFixed(ControlH));
        }
        var field = Field(setting, offering ? "or a folder's path, then Enter" : "add a folder, then Enter", text => [key, .. entries, trackers && !HasTracker(text) ? $"{text}={tracker}" : text]);
        if (offering)
        {
            // The pills flow onto a second line where the row is too narrow for them (a long picked repo's name widens its pill): an Auto wide stack ran past the page's edge.
            stack.Add(Layout.Builder.WrapH([.. controls]).WithGap(6f).WithLineGap(4f).WStar().HAuto());
            if (choice.Opened == key && read.Repos is { } repos)
            {
                stack.Add(Layout.Builder.HStack(Offered(key, repos, entries, pick), Layout.Builder.Spacer().WStar()).HAuto());
            }
            stack.Add(Layout.Builder.HStack(field.WFixed(Math.Min(280f, width)), Layout.Builder.Spacer().WStar()).RowH(ControlH));
        }
        else
        {
            stack.Add(field.RowH(ControlH));
        }
        return Layout.Builder.VStack([.. stack]).WithGap(6f);
    }

    // The pill that opens the repo menu: the picked repo's name cut to a chip's room, the whole name on hover.
    private Layout.Node ChoosePill(string key, string? pick)
    {
        var pill = Pill(pick is null ? "Choose a repo ▾" : $"{Elide(pick)} ▾", $"settings:{key}:choose", () => Toggle(key), chosen: pick is not null);
        return (pick is null ? pill : pill.WithTooltip(pick)).HFixed(ControlH);
    }

    private static bool HasTracker(string text) => text.EndsWith("=github", StringComparison.OrdinalIgnoreCase) || text.EndsWith("=shortcut", StringComparison.OrdinalIgnoreCase);

    // An entry as a chip: its text (a long one elided in the middle to the room the chip has, the whole on hover) and the × that sends the list without it.
    private Layout.Node Chip(SettingRow setting, string entry, string[] entries, float width, Func<string, float, float> measure)
    {
        var action = $"settings:{setting.Key}:remove:{entry}";
        // The room the text has: the chip's width less its padding (2 x 8), the × pill and a margin for a measure's rounding.
        var budget = width - 2 * 8f - (measure("×", 12f) + 2 * PillPad) - TextMargin;
        return Layout.Builder.HStack(
                Layout.Builder.Text(ElideToFit(entry, budget, measure), 12f, Body, TextAlign.Center).WithTooltip(entry).Selectable().Pad(8f, 4f).WAuto(),
                // The × stays a typeset rune for want of a close glyph: SharpAstro/DIR.Lib#151.
                NodePill("×", action, () => Act(Verbs.Set, [setting.Key, .. entries.Where(e => e != entry).DefaultIfEmpty("default")], $"{setting.Label}: removing {entry}…", action)))
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
    internal static string Elide(string text, int chars = ChipChars) =>
        text.Length <= chars ? text : $"{text[..(chars / 3)]}…{text[^(chars - chars / 3 - 1)..]}";

    // The most a chip's text is cut to (a path's drive and last folders, still), however narrow the room.
    private const int LeastChars = 9;

    // The text elided as little as the room (design units, at 12 units) allows, never past LeastChars and never longer than ChipChars.
    // A binary search over the character counts (the elided text only gets shorter as the count falls): log2(35) measures, not up to 35 a chip a frame.
    internal static string ElideToFit(string text, float room, Func<string, float, float> measure)
    {
        var most = Math.Min(text.Length, ChipChars);
        if (most <= LeastChars || measure(Elide(text, most), 12f) <= room)
        {
            return Elide(text, most);
        }
        var low = LeastChars;
        var high = most;
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (measure(Elide(text, mid), 12f) <= room)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }
        return Elide(text, low);
    }

    // A control that cannot be pressed now, in the shape of a pill.
    private static Layout.Node Idle(string label, string why, RGBAColor32 background) =>
        Layout.Builder.Text(label, 12f, Dim, TextAlign.Center).Selectable().Pad(10f, 4f).WAuto().Bg(background).WithTooltip(why).HFixed(ControlH);

    // A field's working id is its own: Enter sends, and the field says "saving…" until the node answers (a second Enter is ignored meanwhile).
    private Layout.Node Field(SettingRow setting, string placeholder, Func<string, string[]> args)
    {
        var control = $"settings:{setting.Key}:field";
        if (!_fields.TryGetValue(setting.Key, out var input))
        {
            input = new TextInputState();
            _fields[setting.Key] = input;
        }
        input.Placeholder = Busy(control) ? "saving…" : placeholder;
        input.OnCommit = text =>
        {
            if (text.Trim().Length > 0 && !Busy(control))
            {
                Act(Verbs.Set, args(text.Trim()), $"{setting.Label}: {text.Trim()}…", control);
                input.Clear();
            }
            return Task.CompletedTask;
        };
        return Layout.Builder.TextInput(input, 13f, Palette.Input).Bg(FieldBg);
    }

    private static string[] Roots(SettingRow setting) =>
        (setting.Saved ?? setting.Value).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>What a setting's choice pill reads while its <c>set</c> is with the node (the owner, 2026-11-07: "when clicking 'on' instead of 'done' it should show 'Setting...'").</summary>
    internal const string SettingWord = "setting";

    /// <summary>
    /// The header's line for a reply: the node's own words when it said more than a bare "done"; a single value taken with a bare
    /// "done" names the setting and what it is now ("LAN set to off", the wire's
    /// none read as "default", as its pill says); anything else, a list's change and a refusal included, is the node's own
    /// words on one line, as the header has one.
    /// </summary>
    internal static string SaidOf(SettingsReply reply) =>
        reply.Kind == ReplyKind.Done && reply.Row is { Many: false } set && reply.Line == SettingsCommands.BareDone
            ? $"{set.Label} set to {(set.Value == "none" ? "default" : set.Value.ReplaceLineEndings(" ").Trim())}"
            : reply.Line.ReplaceLineEndings(" ").Trim();

    // A pill that asks the node, through the press helper: while its press is with the node it reads "label…" (or "working…" when
    // given), dimmed, with no hit.
    private Layout.Node NodePill(string label, string action, Action onClick, bool chosen = false, string? working = null, bool doneMark = true) =>
        // A choice (given its working word) shows the node's new value once taken, not "done" in place of the pill.
        Presses.Button(action, action, () => Pill(label, action, onClick, chosen), _ => Pill(working ?? label, action, onClick, chosen, working: true), doneMark);

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
    /// <summary>A reply's line when the node took the verb and said nothing of how (no <c>Way</c>).</summary>
    public const string BareDone = "done";

    public static async Task<SettingsReply> SendAsync(NodeClient client, string verb, string[] args, string who, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await client.SendAsync(new CommandRequest { Verb = verb, Target = Verbs.NodeTarget, Args = args, Who = who }, cancellationToken);
            return envelope.Success && envelope.Response is { Ok: true } result
                ? new SettingsReply(result.Way ?? BareDone, ReplyKind.Done, result.Setting)
                : new SettingsReply($"refused: {envelope.Response?.Error ?? envelope.Error}", ReplyKind.Refused);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            // A restart for the change can close the connection before the answer is read: the page reads again.
            return new SettingsReply($"no answer ({ex.Message}); the node may be restarting", ReplyKind.NoAnswer);
        }
    }
}
