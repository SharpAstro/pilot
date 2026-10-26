using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// The Settings page: the setup checklist (what it takes for this machine's node to be part of the fleet, each item with
/// the fix the node can make) above the node's settings (on the LAN, its prefix, repo roots, the update channel and
/// policy, jobs). Read from the node's settings route while the page is shown; a button or a field's Enter is one verb on
/// the node (<c>setup node</c>, <c>set node</c>), after which the page reads again. This machine's node only: a node on
/// the LAN does not show its settings to another machine. Generic over the surface, as the other pages are.
/// </summary>
internal sealed class SettingsView<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(5);

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
    private sealed record Seen(SettingsPage? Page, GrantInfo[] Given, string[] Held, string? Problem);

    private Seen _read = new(null, [], [], null);
    private CancellationTokenSource? _reading;
    private DateTimeOffset _drawnAt;

    /// <summary>Reads the node's settings page; null where there is no node to read (a snapshot file).</summary>
    public Func<CancellationToken, Task<SettingsPage>>? Load { get; set; }

    /// <summary>Sends one verb to the node (<c>set</c>, <c>setup</c>) with its words; returns what became of it.</summary>
    public Func<string, string[], CancellationToken, Task<string>>? Send { get; set; }

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
        var body = page is null
            ? Layout.Builder.Text(problem is null ? (Load is null ? "settings are the node's: open pilot on a node" : "reading the node's settings…") : $"the node did not send its settings: {problem}", 13f, Dim).RowH(28f).Pad(Gutter, 8f)
            : Main(page);
        var tree = Layout.Builder.VStack(Header(page, problem), body.Stretch()).Bg(Ground).Stretch();
        return About?.Over(tree) ?? tree;
    }

    public override bool HandleInput(InputEvent evt) => false;

    /// <summary>Reads the page now, and again every few seconds while it is drawn.</summary>
    private void Read()
    {
        if (_reading is not null || Load is not { } load)
        {
            return;
        }
        var reading = _reading = new CancellationTokenSource();
        _ = ReadAsync(load, reading);
    }

    private async Task ReadAsync(Func<CancellationToken, Task<SettingsPage>> load, CancellationTokenSource reading)
    {
        try
        {
            while (!reading.IsCancellationRequested && DateTimeOffset.UtcNow - _drawnAt < Unseen)
            {
                await RefreshAsync(load, reading.Token).ConfigureAwait(false);
                await Task.Delay(Every, reading.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Unseen: the next draw reads again from the start.
            _reading = null;
            reading.Dispose();
        }
    }

    private async Task RefreshAsync(Func<CancellationToken, Task<SettingsPage>> load, CancellationToken cancellationToken)
    {
        try
        {
            var page = await load(cancellationToken).ConfigureAwait(false);
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Page = page, Problem = null });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _read, read => read with { Problem = ex.Message });
        }
        await RefreshGrantsAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
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

    /// <summary>Sends <paramref name="verb"/> and says what became of it, then reads the page again.</summary>
    private void Act(string verb, string[] args, string doing)
    {
        if (Send is not { } send)
        {
            return;
        }
        Said = doing;
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            Said = (await send(verb, args, CancellationToken.None).ConfigureAwait(false)).ReplaceLineEndings(" ").Trim();
            if (Load is { } load)
            {
                await RefreshAsync(load, CancellationToken.None).ConfigureAwait(false);
            }
            Changed?.Invoke();
        });
    }

    private Layout.Node Header(SettingsPage? page, string? problem) =>
        TopBar.Build(new PageHeader("settings")
        {
            Counts = page is null ? "" : Summary(page),
            Source = Said ?? page?.Restart ?? (page is null ? (problem ?? "") : $"saved in {page.File} · the command line wins over it"),
        }, new TopBarParts(Nodes, null, Autopilot, About));

    internal static string Summary(SettingsPage page)
    {
        var needed = page.Checks.Where(c => !c.Optional).ToArray();
        var left = needed.Count(c => !c.Ok);
        var pending = page.Settings.Count(s => s.Pending);
        var text = left == 0 ? "set up" : $"{left} of {needed.Length} setup steps left";
        return pending == 0 ? text : $"{text} · {pending} saved for the next start";
    }

    private Layout.Node Main(SettingsPage page)
    {
        var rows = new List<Layout.Node> { Section("Setup", "what it takes for this machine's node to be part of the fleet") };
        for (var i = 0; i < page.Checks.Length; i++)
        {
            rows.Add(CheckRow(page.Checks[i], i));
        }
        rows.Add(Section("Settings", "the node restarts at once to run with a change; its sessions, flights and jobs carry on"));
        for (var i = 0; i < page.Settings.Length; i++)
        {
            rows.Add(SettingRowNode(page.Settings[i], i));
        }
        var grants = Volatile.Read(ref _read);
        var given = grants.Given;
        var held = grants.Held;
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
                        Send is null ? Layout.Builder.Spacer().WStar() : Pill("Revoke", $"settings:revoke:{grant.Id}", () => Act(Verbs.RevokeGrant, [grant.Id], $"revoking {grant.Label}…")),
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

    private Layout.Node CheckRow(SetupCheck check, int index)
    {
        var fix = check.Fix is { } label && Send is not null
            ? Pill(label, $"settings:fix:{check.Key}", () => Act(Verbs.Setup, [check.Key], $"{check.Title}: {label}…"))
            : Layout.Builder.Spacer().WStar();
        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(check.Ok ? Ok : check.Optional ? Optional : Todo).WFixed(StripeWidth).HStar(),
                Layout.Builder.VStack(
                        Layout.Builder.Text(check.Title, 14f, Bright).RowH(22f),
                        Layout.Builder.Text(check.Ok ? "done" : check.Optional ? "optional" : "to do", 11f, check.Ok ? Ok : check.Optional ? Dim : Todo).RowH(18f))
                    .WFixed(TitleWidth),
                Layout.Builder.Text(check.Line, 12f, Body).WStar(),
                Layout.Builder.HStack(fix, Layout.Builder.Spacer().WStar()).WFixed(FixWidth))
            .WithGap(14f).Pad(8f, 6f).RowH(RowHeight);
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    private Layout.Node SettingRowNode(SettingRow setting, int index)
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
                        setting.Many ? Editor(setting).HAuto() : Editor(setting).RowH(26f),
                        Layout.Builder.Text(setting.Detail, 11f, Dim).RowH(18f))
                    .WStar())
            .WithGap(14f).Pad(8f, 6f);
        // Repo roots grow a line per folder: their row takes the height its lines ask for.
        row = setting.Many ? row.HAuto() : row.RowH(RowHeight);
        return (index & 1) == 1 ? row.Bg(RowAltBg) : row;
    }

    // How a setting is changed: a pill per choice, a field (Enter saves), or for repo roots each folder with an x and a field to add one.
    private Layout.Node Editor(SettingRow setting)
    {
        var parts = new List<Layout.Node>();
        if (setting.Choices.Length > 0)
        {
            foreach (var choice in setting.Choices)
            {
                var chosen = string.Equals(choice, setting.Saved ?? setting.Value, StringComparison.OrdinalIgnoreCase);
                parts.Add(Pill(choice, $"settings:{setting.Key}:{choice}", () => Act(Verbs.Set, [setting.Key, choice], $"{setting.Label}: {choice}…"), chosen));
            }
        }
        else if (setting.Many)
        {
            var roots = Roots(setting);
            var stack = new List<Layout.Node>();
            foreach (var root in roots)
            {
                stack.Add(Layout.Builder.HStack(
                        Layout.Builder.Text(root, 12f, Body).WAuto(),
                        // The × stays a typeset rune for want of a close glyph: SharpAstro/DIR.Lib#151.
                        Pill("×", $"settings:{setting.Key}:remove:{root}", () => Act(Verbs.Set, [setting.Key, .. roots.Where(r => r != root).DefaultIfEmpty("default")], $"{setting.Label}: removing {root}…")),
                        Layout.Builder.Spacer().WStar())
                    .WithGap(8f).RowH(26f));
            }
            stack.Add(Field(setting, "add a folder, then Enter", text => [setting.Key, .. roots, text]).RowH(26f));
            return Layout.Builder.VStack([.. stack]);
        }
        else
        {
            parts.Add(Field(setting, setting.Value, text => [setting.Key, text]).WFixed(220f));
        }
        if (setting.Saved is not null && setting.Source != SettingSources.CommandLine)
        {
            parts.Add(Pill("default", $"settings:{setting.Key}:default", () => Act(Verbs.Set, [setting.Key, "default"], $"{setting.Label}: back to the default…")));
        }
        parts.Add(Layout.Builder.Spacer().WStar());
        return Layout.Builder.HStack([.. parts]).WithGap(6f);
    }

    private Layout.Node Field(SettingRow setting, string placeholder, Func<string, string[]> args)
    {
        if (!_fields.TryGetValue(setting.Key, out var input))
        {
            input = new TextInputState();
            _fields[setting.Key] = input;
        }
        input.Placeholder = placeholder;
        input.OnCommit = text =>
        {
            if (text.Trim().Length > 0)
            {
                Act(Verbs.Set, args(text.Trim()), $"{setting.Label}: {text.Trim()}…");
                input.Clear();
            }
            return Task.CompletedTask;
        };
        return Layout.Builder.TextInput(input, 13f).Bg(FieldBg);
    }

    private static string[] Roots(SettingRow setting) =>
        (setting.Saved ?? setting.Value).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static Layout.Node Pill(string label, string action, Action onClick, bool chosen = false) =>
        Layout.Builder.Text(label, 12f, Bright, TextAlign.Center)
            .Pad(10f, 4f)
            .WAuto()
            .Bg(chosen ? Chosen : Button)
            .BgHover(chosen ? Chosen : ButtonHover)
            .Follows(new HitResult.ButtonHit(action), _ => onClick());
}

/// <summary>The Settings page's verbs on the node, for either host: what became of each, in a line.</summary>
internal static class SettingsCommands
{
    public static async Task<string> SendAsync(NodeClient client, string verb, string[] args, string who, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await client.SendAsync(new CommandRequest { Verb = verb, Target = Verbs.NodeTarget, Args = args, Who = who }, cancellationToken);
            return envelope.Success && envelope.Response is { Ok: true } result
                ? result.Way ?? "done"
                : $"refused: {envelope.Response?.Error ?? envelope.Error}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            // A restart for the change can close the connection before the answer is read: the page reads again either way.
            return $"no answer ({ex.Message}); the node may be restarting";
        }
    }
}
