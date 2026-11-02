using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Flights page's verbs: running a flight's action on the node, the grant and connect presses, the row menu and the why box an action asks.
internal sealed partial class FlightsView<TSurface>
{
    /// <summary>
    /// Runs <paramref name="action"/> of flight <paramref name="code"/> from its button <paramref name="hit"/> through <see cref="Presses"/>: the
    /// button says it is working at once, takes no second press, and the outcome is said at the button and in the page's bar (<see cref="Say"/>).
    /// </summary>
    internal Task Run(FlightAction action, string code, string hit)
    {
        if (OnAction is not { } send || Presses.PendingOf(code) is not null)
        {
            return Task.CompletedTask;
        }
        Say($"{action.Label}…");
        return PressedAsync();

        async Task PressedAsync()
        {
            var refused = await Presses.PressAsync(code, hit, _ => send(action), say: why => $"refused: {action.Label}: {why}");
            Say(refused ?? $"ok: {action.Label}");
            Presses.Changed?.Invoke();
        }
    }

    /// <summary>Asks <paramref name="node"/> for a grant through <see cref="OnConnect"/>: the button reads "asking…" until the grant answers.</summary>
    internal void AskGrant(NodeMark node, string hit)
    {
        if (OnConnect is { } connect)
        {
            _ = Presses.PressAsync($"connect:{node.Where}", hit, _ => connect(node));
        }
    }

    /// <summary>What the node said to the last button pressed on <paramref name="line"/>, when it refused: under its buttons, in its words.</summary>
    private IEnumerable<Layout.Node> RefusalOf(FlightLine line)
    {
        if (Presses.RefusalLine(line.Code, 12f, Held, 3) is { } refusal)
        {
            yield return refusal;
        }
    }

    /// <summary>The Connect pill of a watch-only row: "asking…", dimmed and with no hit, until the grant answers.</summary>
    private Layout.Node ConnectButton(NodeMark node, string hit) =>
        Presses.Button($"connect:{node.Where}", hit, () => Pill("Connect", hit, () => AskGrant(node, hit)), _ => CardsView<TSurface>.Dead("asking…"));

    /// <summary>The node of <paramref name="line"/> when it is watched and not commanded, else null.</summary>
    private NodeMark? WatchOnly(FlightLine line) => line.Node is { Here: false } node && CanSteer?.Invoke(node) == false ? node : null;

    /// <summary>
    /// Opens <paramref name="line"/>'s menu where it was right-clicked, the row selected: what can be done with the flight now
    /// (its actions), its PR in the browser, its plane's folder, and its code and branch to copy.
    /// </summary>
    public void OpenMenu(FlightLine line, int index, PointerPress at)
    {
        ListCursor.MoveTo(index);
        var flight = line.Row;
        var items = new List<DropdownItem<string>>();
        if (CanCommand && WatchOnly(line) is { } watched)
        {
            // Disabled, and why: the one entry that can be chosen asks for the grant.
            items.Add(new DropdownItem<string>(SessionsView<TSurface>.SteerHint(watched), "connect") { OnChoose = () => AskGrant(watched, $"flight:{flight.Code}:connect") });
        }
        else if (CanCommand)
        {
            foreach (var action in FlightAction.For(line))
            {
                var label = action.WhyPrompt is null && action.ChatMessage is null ? action.Label : action.Label + "…";
                // Shown and dead, with the reason, while its node is unreachable: the verb goes to the node.
                items.Add(line.Stale is not null
                    ? DropdownItem<string>.Disabled(label, action.Verb, StaleWhy(line))
                    : new DropdownItem<string>(label, action.Verb) { OnChoose = () => Choose(action, flight.Code) });
            }
        }
        items.Add(new DropdownItem<string>("Open flight", "flight") { OnChoose = () => OpenDetail(flight.Code) });
        if (OnOpen is { } open)
        {
            if (flight.PullRequestUrl is { } url)
            {
                items.Add(new DropdownItem<string>($"Open PR #{flight.PullRequest}", "pr") { OnChoose = () => open(url) });
            }
            if (flight.State != FlightState.Landed)
            {
                items.Add(new DropdownItem<string>("Open its plane's folder", "plane") { OnChoose = () => open(flight.Worktree) });
            }
            items.Add(new DropdownItem<string>("Open its repo's folder", "repo") { OnChoose = () => open(flight.RepoPath) });
        }
        if (CopyText is { } copy)
        {
            items.Add(new DropdownItem<string>($"Copy {flight.Code}", "copy-code") { OnChoose = () => copy(flight.Code) });
            items.Add(new DropdownItem<string>("Copy its branch", "copy-branch") { OnChoose = () => copy(flight.Branch) });
        }
        if (items.Count == 0)
        {
            return;
        }
        _menu.Items = [.. items];
        // Hung where the press was, a point: the menu is as wide as its widest entry, measured, and kept on screen.
        _menuAt = new RectF32(at.X, at.Y, 0f, 0f);
        _menu.IsOpen = true;
    }

    /// <summary>
    /// What a click on <paramref name="action"/>'s button does: asks its why under the button, puts its message in the chat,
    /// or runs its verb at once, as the action says it needs.
    /// </summary>
    internal void Choose(FlightAction action, string code)
    {
        if (action.ChatMessage is { } message)
        {
            CloseWhy();
            OnChat?.Invoke(message);
        }
        else if (action.WhyPrompt is { } prompt)
        {
            AskWhy(action, code, prompt);
        }
        else
        {
            CloseWhy();
            _ = Run(action, code, $"flight:{code}:{action.Verb}");
        }
    }

    private void AskWhy(FlightAction action, string code, string prompt)
    {
        _asking = (code, action);
        WhyInput.Text = "";
        WhyInput.CursorPos = 0;
        WhyInput.Placeholder = prompt;
        Ui.Focus.Focus(WhyInput);
    }

    /// <summary>Shuts the why field without sending, as Escape does.</summary>
    public void CloseWhy()
    {
        _asking = null;
        Ui.Focus.BlurIfFocused(WhyInput);
    }

    // Enter in the why field: the action's verb goes with the why as its one argument; nothing typed keeps the field open.
    private Task SendWhy(string text)
    {
        var why = text.Trim();
        if (_asking is not { } asking || (why.Length == 0 && !asking.Action.Confirm))
        {
            return Task.CompletedTask;
        }
        CloseWhy();
        var sent = asking.Action.Confirm ? asking.Action : asking.Action with { Args = [why] };
        _ = Run(sent, asking.Code, $"flight:{asking.Code}:{asking.Action.Verb}");
        return Task.CompletedTask;
    }

    // The why field's row under the buttons of <line>, or none: it is under the button of the action it was opened for, and
    // only while the flight still offers that action.
    private IEnumerable<Layout.Node> WhyRow(FlightLine line)
    {
        if (_asking is not { } asking || asking.Code != line.Code || line.Stale is not null
            || !FlightAction.For(line).Any(a => a.Verb == asking.Action.Verb && a.Target == asking.Action.Target && a.Args.SequenceEqual(asking.Action.Args)))
        {
            yield break;
        }
        var action = asking.Action;
        yield return Layout.Builder.HStack(
                Layout.Builder.Text(action.WhyPrompt ?? "why?", 12f, Dim).WAuto(),
                Layout.Builder.TextInput(WhyInput, 14f, Palette.Input, focusOnOpen: true).WStar(),
                Layout.Builder.Text("Enter sends · Esc cancels", 11f, Faint).WAuto())
            .WithGap(10f)
            .RowH(WhyRowHeight);
    }

    /// <summary>Keeps what the last action said, for the page to show under the flights.</summary>
    public void Say(string text) =>
        Volatile.Write(ref _said, string.Join(" · ", text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    /// <summary>
    /// The way a flight's Retry move runs on this window: its action through <see cref="Choose"/>; null where this window cannot
    /// command the flight (a snapshot, or a node it only watches), so the band says what failed and draws no dead button.
    /// </summary>
    private Action<FlightAction>? RetryOf(FlightLine line) => CanCommand && WatchOnly(line) is null ? action => Choose(action, line.Code) : null;
}
