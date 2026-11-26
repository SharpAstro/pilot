using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Buddy;

/// <summary>Which card is open above (or below) the cat, in the bubble's place.</summary>
public enum DeskCard
{
    /// <summary>None: the bubble of the notice, the hover summary or nothing shows.</summary>
    None,

    /// <summary>The sessions at a glance (click).</summary>
    Peek,

    /// <summary>What the cat watches and what wakes it (right-click).</summary>
    Settings,

    /// <summary>The one-line chat input, under the bubble of an ask or a trouble (click on the cat while it speaks).</summary>
    Chat,
}

/// <summary>What is under a point of the window.</summary>
public enum TargetKind
{
    /// <summary>Nothing of the drawing: the click would pass through.</summary>
    None,

    /// <summary>The cat's body, the part that peeks on a click and drags.</summary>
    Cat,

    /// <summary>The "z Z" over the sleeping cat.</summary>
    Zzz,

    /// <summary>The cushion the cat sits on.</summary>
    Cushion,

    /// <summary>The bubble, off its buttons.</summary>
    Bubble,

    /// <summary>One of the bubble's buttons; <see cref="Target.Id"/> is its label.</summary>
    BubbleButton,

    /// <summary>The open card, off its hits.</summary>
    Card,

    /// <summary>A hit of the open card; <see cref="Target.Id"/> is its id.</summary>
    CardHit,
}

/// <summary>A thing under the pointer and, for a button or a card's hit, which.</summary>
public readonly record struct Target(TargetKind Kind, string? Id = null);

/// <summary>
/// Where the last drawn scene put its parts (device pixels), so the window can say what a point is on. Built from the <see cref="SceneLayout"/> the scene
/// returned, so hit testing and drawing share one source.
/// </summary>
public sealed class BuddyGeometry(SceneLayout layout)
{
    /// <summary>The scene's own layout.</summary>
    public SceneLayout Layout { get; } = layout;

    /// <summary>The "z Z" box in the cat's own design pixels (see <see cref="GrumpCat"/>): the corner of the cat's box a click can wake.</summary>
    public const float ZzzX = 150, ZzzY = 0, ZzzWidth = 48, ZzzHeight = 30;

    private float S => Layout.Scale;

    /// <summary>The cat's body: its box down to the cushion.</summary>
    public (float X, float Y, float W, float H) CatBody => (Layout.CatX, Layout.CatY, GrumpCat.Width * S, GrumpCat.CushionY * S);

    /// <summary>The cushion under the cat.</summary>
    public (float X, float Y, float W, float H) Cushion => (Layout.CatX + 5 * S, Layout.CatY + GrumpCat.CushionY * S, GrumpCat.CushionWidth * S, GrumpCat.CushionHeight * S);

    /// <summary>The "z Z" over a sleeping cat.</summary>
    public (float X, float Y, float W, float H) Zzz => (Layout.CatX + ZzzX * S, Layout.CatY + ZzzY * S, ZzzWidth * S, ZzzHeight * S);

    /// <summary>The bubble's rectangle, or null when none shows.</summary>
    public (float X, float Y, float W, float H)? Bubble => Layout.Bubble is { } b ? (Layout.BubbleX, Layout.BubbleY, b.Width * S, b.Height * S) : null;

    /// <summary>The open card's rectangle, or null when none shows.</summary>
    public (float X, float Y, float W, float H)? Card => Layout.Card is { } c ? (Layout.CardX, Layout.CardY, c.Width * S, c.Height * S) : null;

    private static bool In((float X, float Y, float W, float H) r, float x, float y) => x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;

    /// <summary>What is at (<paramref name="x"/>, <paramref name="y"/>), the topmost first: a button or a hit, then the bubble or the card, then the cat.</summary>
    public Target At(float x, float y, bool asleep)
    {
        if (Layout.Card is { } card && Card is { } cardBox && In(cardBox, x, y))
        {
            return card.HitAt((x - cardBox.X) / S, (y - cardBox.Y) / S) is { } hit ? new Target(TargetKind.CardHit, hit.Id) : new Target(TargetKind.Card);
        }
        if (Layout.Bubble is { } bubble && Bubble is { } bubbleBox && In(bubbleBox, x, y))
        {
            var bx = (x - bubbleBox.X) / S;
            var by = (y - bubbleBox.Y) / S;
            foreach (var button in bubble.Buttons)
            {
                if (bx >= button.X && bx < button.X + button.Width && by >= button.Y && by < button.Y + button.Height)
                {
                    return new Target(TargetKind.BubbleButton, button.Label);
                }
            }
            return new Target(TargetKind.Bubble);
        }
        if (asleep && In(Zzz, x, y))
        {
            return new Target(TargetKind.Zzz);
        }
        if (In(CatBody, x, y))
        {
            return new Target(TargetKind.Cat);
        }
        return In(Cushion, x, y) ? new Target(TargetKind.Cushion) : default;
    }
}

/// <summary>What the desk asks of the window it sits in: a fake in a test, the SDL window in the host.</summary>
public interface IBuddyWindow
{
    /// <summary>Moves the window by (<paramref name="dx"/>, <paramref name="dy"/>) pixels while the cat is dragged.</summary>
    void MoveBy(int dx, int dy);

    /// <summary>The drag ended: the window snaps to the chosen corner of the monitor it was dropped on and remembers it.</summary>
    void Dropped();
}

/// <summary>
/// What the person does to the cat, as the window's events reach it (DSN-2 v5): hover opens one eye, a click peeks (or, while the cat speaks, opens the
/// chat under its bubble), a right-click opens the settings, a drag moves the window, a click elsewhere or a lost focus closes the card. Pure: it
/// holds no window and no clock of its own, so a test drives it with a fake <see cref="IBuddyWindow"/>. <see cref="Version"/> moves on every change
/// of what is drawn, which is what the frame policy reads.
/// </summary>
public sealed class BuddyDesk(
    BuddyModel model, BuddyFeed feed, BuddyChat chat, IGuiHandOff gui, IBuddyWindow window,
    Func<BuddySettings> settings, Action<BuddySettings> changeSettings, TimeProvider time, TimeZoneInfo zone, object gate, Action wake)
{
    /// <summary>The pointer must move this far from where it went down before a press is a drag, not a click.</summary>
    public const float DragThreshold = 4;

    private long _version;
    private (float X, float Y, int Button, Target Target)? _press;
    private bool _dragging;
    private bool _inputFocus;

    /// <summary>The card open now.</summary>
    public DeskCard Card { get; private set; }

    /// <summary>Moves on every change of what the scene draws that the model does not report itself (a card opened, a button pressed, a character typed).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Where the last frame put things; set by the host after each draw.</summary>
    public BuddyGeometry? Geometry { get; set; }

    /// <summary>True while a drag is moving the window.</summary>
    public bool Dragging => _dragging;

    /// <summary>The scene's card, built with the pen it is measured with, or null while none is open.</summary>
    public Func<Pen<TSurface>, BuddyCard>? CardFactory<TSurface>() => Card switch
    {
        DeskCard.Peek => pen => PeekCard.Build(pen, feed.Peek()),
        DeskCard.Settings => pen => SettingsCard.Build(pen, settings()),
        DeskCard.Chat => pen => ChatCard.Build(pen, chat),
        _ => null,
    };

    /// <summary>
    /// What the open card (or the hover summary) would draw, as one string: the frame policy compares it with the last frame's, so a session that began
    /// to ask while the peek is open repaints it and a node event that changes nothing shown does not. The caller holds the gate.
    /// </summary>
    public string ContentKey() => Card switch
    {
        DeskCard.Peek => feed.Peek() is { } peek ? "peek|" + peek.Header + "|" + string.Join('|', peek.Rows.Select(r => $"{r.State},{r.Node},{r.What},{r.Status}")) : "peek",
        DeskCard.Chat => $"chat|{chat.Input}|{chat.Answer}|{chat.Refused}|{chat.DisabledReason}|{chat.Working}|{chat.PendingAsk?.Question}",
        DeskCard.Settings => "settings|" + settings(),
        _ => model.Mood == BuddyMood.OneEyeOpen ? "summary|" + feed.Summary : string.Empty,
    };

    private void Changed() => Interlocked.Increment(ref _version);

    private bool Asleep => model.Current is null;

    private Target TargetAt(float x, float y) => Geometry?.At(x, y, Asleep) ?? default;

    /// <summary>The pointer moved (window pixels). A drag moves the window; otherwise the cat's eye follows whether the pointer is on it.</summary>
    public void PointerMoved(float x, float y)
    {
        if (_press is { } press && !_dragging && press.Button == 1 && press.Target.Kind is TargetKind.Cat or TargetKind.Zzz or TargetKind.Cushion
            && MathF.Abs(x - press.X) + MathF.Abs(y - press.Y) > DragThreshold)
        {
            _dragging = true;
        }
        if (_dragging && _press is { } grab)
        {
            window.MoveBy((int)MathF.Round(x - grab.X), (int)MathF.Round(y - grab.Y));
            return;
        }
        var over = TargetAt(x, y).Kind is TargetKind.Cat or TargetKind.Zzz or TargetKind.Cushion;
        SetHover(over);
    }

    /// <summary>The pointer left the window.</summary>
    public void PointerLeft() => SetHover(false);

    private void SetHover(bool over)
    {
        // The hover only matters while the cat sleeps with nothing open; the model keeps it and shows it when it can.
        var was = model.Mood;
        model.Hover(over && Card == DeskCard.None);
        if (model.Mood != was)
        {
            Changed();
        }
    }

    /// <summary>A button went down at (<paramref name="x"/>, <paramref name="y"/>): 1 is the left one, 3 the right one.</summary>
    public void ButtonDown(int button, float x, float y) => _press = (x, y, button, TargetAt(x, y));

    /// <summary>The button came up: it ends a drag, or it was a click on what it went down on.</summary>
    public void ButtonUp(int button, float x, float y)
    {
        var press = _press;
        _press = null;
        if (_dragging)
        {
            _dragging = false;
            window.Dropped();
            Changed();
            return;
        }
        if (press is not { } p || p.Button != button)
        {
            return;
        }
        var up = TargetAt(x, y);
        // A click is a press and a release on the same thing.
        if (up != p.Target)
        {
            return;
        }
        if (button == 3)
        {
            if (up.Kind is TargetKind.Cat or TargetKind.Zzz or TargetKind.Cushion)
            {
                Open(Card == DeskCard.Settings ? DeskCard.None : DeskCard.Settings);
            }
            return;
        }
        if (button != 1)
        {
            return;
        }
        switch (up.Kind)
        {
            case TargetKind.Cat:
            case TargetKind.Zzz:
            case TargetKind.Cushion:
                Open(Card != DeskCard.None ? DeskCard.None : model.Current is { Kind: NoticeKind.Ask or NoticeKind.Trouble } ? DeskCard.Chat : DeskCard.Peek);
                break;
            case TargetKind.BubbleButton:
                PressBubbleButton(up.Id!);
                break;
            case TargetKind.CardHit:
                PressCardHit(up.Id!);
                break;
            case TargetKind.None:
                // A click that reaches the window off the drawing (the window had no shape): closes the card.
                Open(DeskCard.None);
                break;
        }
    }

    /// <summary>The window lost the focus: a click anywhere else closes the card.</summary>
    public void FocusLost()
    {
        _press = null;
        _dragging = false;
        _inputFocus = false;
        Open(DeskCard.None);
        SetHover(false);
    }

    /// <summary>Esc closes the card.</summary>
    public void Escape() => Open(DeskCard.None);

    private void Open(DeskCard card)
    {
        if (card == Card)
        {
            return;
        }
        Card = card;
        _inputFocus = card == DeskCard.Chat;
        model.Hover(false);
        if (card == DeskCard.Chat)
        {
            _ = RunAsync(ct => chat.RefreshAsync(ct));
        }
        Changed();
    }

    private void PressBubbleButton(string label)
    {
        var notice = model.Current;
        if (label == SpeechBubble.LaterLabel)
        {
            model.Dismiss();
            Changed();
            return;
        }
        // "Open the chat" / "Open the run": the GUI opens the card the notice is about; the notice has been seen, so the cat sleeps.
        if (notice is not null)
        {
            gui.Open(notice.Id.StartsWith("card:", StringComparison.Ordinal) ? BuddyFeed.CardLink(notice.Id["card:".Length..]) : string.Empty);
            model.Dismiss();
            Changed();
        }
    }

    private void PressCardHit(string id)
    {
        switch (Card)
        {
            case DeskCard.Settings:
                if (id == "done")
                {
                    Open(DeskCard.None);
                }
                else if (SettingsCard.Press(settings(), id, time.GetUtcNow(), zone) is { } changed)
                {
                    changeSettings(changed);
                    Changed();
                }
                break;
            case DeskCard.Peek when id.StartsWith("row:", StringComparison.Ordinal) && int.TryParse(id.AsSpan(4), out var row):
                var rows = feed.Peek().Rows;
                if (row >= 0 && row < rows.Count)
                {
                    gui.Open(rows[row].Link ?? string.Empty);
                }
                break;
            case DeskCard.Chat:
                PressChat(id);
                break;
        }
    }

    private void PressChat(string id)
    {
        if (id == "more")
        {
            chat.OpenInGui();
        }
        else if (id == "whatsgoing")
        {
            _ = RunAsync(ct => chat.AskWhatsGoingOnAsync(ct));
        }
        else if (id.StartsWith("choice:", StringComparison.Ordinal) && int.TryParse(id.AsSpan(7), out var i) && chat.PendingAsk is { } ask && i >= 0 && i < ask.Choices.Count)
        {
            var button = ask.Choices[i];
            _ = RunAsync(ct => chat.PressAsync(button, ct));
        }
        else if (id == "input")
        {
            _inputFocus = true;
        }
    }

    /// <summary>Typed text goes to the chat input while the chat card is open.</summary>
    public void TextTyped(string text)
    {
        if (Card == DeskCard.Chat && _inputFocus)
        {
            chat.Type(text);
            Changed();
        }
    }

    /// <summary>Backspace in the chat input.</summary>
    public void BackspacePressed()
    {
        if (Card == DeskCard.Chat && _inputFocus)
        {
            chat.Backspace();
            Changed();
        }
    }

    /// <summary>Enter in the chat input sends it to the watchtower.</summary>
    public void EnterPressed()
    {
        if (Card == DeskCard.Chat && _inputFocus)
        {
            _ = RunAsync(ct => chat.EnterAsync(ct));
        }
    }

    /// <summary>A chat verb runs on the thread pool; the desk repaints when it ends, under the gate the host draws under.</summary>
    private async Task RunAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        try
        {
            await work(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            // The node did not answer: the chat card shows why from the feed's own state on the next frame.
        }
        lock (gate)
        {
            Changed();
        }
        wake();
    }

    /// <summary>A card opened by the watchtower's <c>buddy_point</c> or a change from outside the person's hand reports itself through the model; this only repaints.</summary>
    public void Touch() => Changed();
}
