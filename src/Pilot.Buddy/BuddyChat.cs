using System.Text.Json.Nodes;
using Pilot.Contracts;

namespace Pilot.Buddy;

/// <summary>The node's chat verbs the buddy uses: the ones the GUI and the CLI share, behind an interface so tests use a fake (no node, no network).</summary>
public interface IBuddyNode
{
    /// <summary>Says <paramref name="text"/> to the watchtower (the node's <c>tell watchtower</c>, as the GUI's chat sends). Null when the node took it, else why not.</summary>
    Task<string?> SayAsync(string text, CancellationToken cancellationToken);

    /// <summary>Asks the watchtower <paramref name="question"/> through the chat, the same path as <see cref="SayAsync"/> (a question is a say). Null when taken, else why not.</summary>
    Task<string?> AskAsync(string question, CancellationToken cancellationToken);

    /// <summary><c>chat-now</c>: cancels the watchtower's turn so the queued message <paramref name="sendId"/> is answered next. Null when done, else why not.</summary>
    Task<string?> ChatNowAsync(string sendId, CancellationToken cancellationToken);

    /// <summary><c>answer-ask</c>: <paramref name="decision"/> is allow, deny or answer (with the JSON in <paramref name="words"/>). Null when taken, else why not.</summary>
    Task<string?> AnswerAskAsync(string requestId, string decision, string? words, CancellationToken cancellationToken);

    /// <summary>The chat as the node keeps it, from the entry after <paramref name="after"/> (-1 for the last ones); null when the node did not answer.</summary>
    Task<ChatPage?> GetChatAsync(int after, CancellationToken cancellationToken);
}

/// <summary>Hands a link to the running pilot-gui through its InstanceGate (a second launch raises the window and passes the link). Behind an interface so tests use a fake.</summary>
public interface IGuiHandOff
{
    /// <summary>Opens <paramref name="link"/> in the GUI (<c>pilot://card/&lt;id&gt;</c>, as a toast's click does; empty just raises the window). False when the GUI did not take it.</summary>
    bool Open(string link);

    /// <summary>
    /// The fallback when no GUI runs (<see cref="Open"/> returned false): starts pilot-gui with <paramref name="link"/>, or hands the link to the shell as a toast's
    /// click does. False when neither worked. Blocks while it starts, so callers run it off the window's thread.
    /// </summary>
    bool Launch(string link) => false;
}

/// <summary>A button of the chat card: a choice of a question, or allow/deny of a permission prompt. <c>Words</c> is the JSON an answer sends.</summary>
public sealed record ChoiceButton(string Label, string RequestId, string Decision, string? Words);

/// <summary>
/// The chat under the cat's bubble (#1357): a one-line input, the watchtower's answer as it streams in, and its asks with their choices as buttons. It
/// speaks the node's own chat verbs through <see cref="IBuddyNode"/>, so the exchange is the one the GUI and the CLI share. State only: the card that
/// draws it is <see cref="ChatCard"/>, and the window feeds it keys and the node's <c>chatChanged</c> events.
/// </summary>
public sealed class BuddyChat(IBuddyNode node, IGuiHandOff gui, Func<string?> disabledReason, object? gate = null)
{
    // The window draws under this lock, so every write of the chat's state (typed on the window's thread, set by a chat verb on the pool) takes it too.
    private readonly object _gate = gate ?? new();

    /// <summary>The gate the window draws under.</summary>
    public object Gate => _gate;

    /// <summary>What the input says while empty and usable.</summary>
    public const string Placeholder = "Ask the watchtower…";

    /// <summary>The words that tell a long answer goes on in the GUI.</summary>
    public const string MoreInTheGui = "more in the GUI";

    /// <summary>The question the idle cat asks when clicked, and the card's own shortcut.</summary>
    public const string WhatsGoingOn = "What's going on?";

    /// <summary>The most characters of an answer the card keeps for itself; the rest is in the GUI.</summary>
    public const int AnswerLimit = 600;

    private string _input = string.Empty;
    private int _fromIndex = -1;
    private ChatPage? _page;

    /// <summary>True while the chat card is open (set by the desk).</summary>
    public bool CardOpen { get; set; }

    /// <summary>Whether a <c>chatChanged</c> is worth a read: the card is open, a turn works, or an ask waits.</summary>
    public bool Wanted => CardOpen || Working || PendingAsk is not null;

    /// <summary>What has been typed.</summary>
    public string Input => _input;

    /// <summary>The refusal of the last send, shown under the input; null with none.</summary>
    public string? Refused { get; private set; }

    /// <summary>Why the input is disabled (watchtower down, node not answering), else null. The input is drawn with this text, never hidden.</summary>
    public string? DisabledReason => disabledReason();

    /// <summary>Whether Enter would send.</summary>
    public bool CanSend => DisabledReason is null && _input.Trim().Length > 0;

    /// <summary>True while the watchtower is working on its turn.</summary>
    public bool Working => _page?.Working ?? false;

    /// <summary>The words of the answer to what was said, as far as they have come (the turn's partial words, else its finished ones); null before any.</summary>
    public string? Answer
    {
        get
        {
            if (_page is not { } page)
            {
                return null;
            }
            var said = page.Entries.Where(e => e.Kind == TranscriptKind.Assistant && e.Index >= _fromIndex).Select(e => e.Text).ToList();
            if (page.Partial is { Length: > 0 } partial)
            {
                said.Add(partial);
            }
            return said.Count == 0 ? null : string.Join("\n", said);
        }
    }

    /// <summary>The answer cut to <see cref="AnswerLimit"/>, and whether it was cut: a cut one says <see cref="MoreInTheGui"/> with a link.</summary>
    public (string? Text, bool More) ShownAnswer
    {
        get
        {
            var answer = Answer;
            return answer is null ? (null, false) : answer.Length > AnswerLimit ? (answer[..AnswerLimit].TrimEnd() + TextWrap.Ellipsis, true) : (answer, false);
        }
    }

    /// <summary>The ask the watchtower waits on the person for, its question and its choices as buttons; null with none.</summary>
    public (string Question, IReadOnlyList<ChoiceButton> Choices)? PendingAsk
    {
        get
        {
            if (_page?.Asks is not { Length: > 0 } asks)
            {
                return null;
            }
            var ask = asks[0];
            if (ask.Questions is { Length: 1 } questions && !questions[0].MultiSelect && questions[0].Options.Length > 0)
            {
                var q = questions[0];
                return (q.Question, [.. q.Options.Select(o => new ChoiceButton(o.Label, ask.Id, "answer", Answers(q.Question, o.Label)))]);
            }
            if (ask.Questions is null)
            {
                // A permission prompt: allow or deny what it would do.
                return (ask.Summary, [new ChoiceButton("Allow", ask.Id, "allow", null), new ChoiceButton("Deny", ask.Id, "deny", null)]);
            }
            // Several questions, or ticked boxes: the GUI's panel does that, the card says so.
            return (ask.Summary, []);
        }
    }

    private static string Answers(string question, string label) => new JsonObject { [question] = label }.ToJsonString();

    /// <summary>Types text into the input line.</summary>
    public void Type(string text)
    {
        if (DisabledReason is null)
        {
            _input += text.Replace("\r", string.Empty).Replace('\n', ' ');
        }
    }

    /// <summary>Deletes the last character.</summary>
    public void Backspace()
    {
        lock (_gate)
        {
            if (_input.Length > 0)
            {
                _input = _input[..^1];
            }
        }
    }

    /// <summary>
    /// Enter: says the input to the watchtower. A message sent while the watchtower works is queued behind its turn; as the GUI does, it is then answered
    /// at once with <c>chat-now</c> (the turn is cancelled and the queued message taken next). Returns false when nothing was sent.
    /// </summary>
    public async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        if (!CanSend)
        {
            return false;
        }
        var text = _input.Trim();
        await BaselineAsync(cancellationToken);
        var refused = await node.SayAsync(text, cancellationToken);
        return await AfterSendAsync(text, refused, cancellationToken);
    }

    /// <summary>"What's going on?", from the card's shortcut or a click on the idle cat: asked through the chat like any other question.</summary>
    public async Task<bool> AskWhatsGoingOnAsync(CancellationToken cancellationToken)
    {
        if (DisabledReason is not null)
        {
            return false;
        }
        await BaselineAsync(cancellationToken);
        var refused = await node.AskAsync(WhatsGoingOn, cancellationToken);
        return await AfterSendAsync(WhatsGoingOn, refused, cancellationToken);
    }

    // The answer is what the watchtower says after this send, so the chat's length before it is the line: read once if never read.
    private async Task BaselineAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            await RefreshAsync(cancellationToken);
        }
    }

    private async Task<bool> AfterSendAsync(string text, string? refused, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Refused = refused;
            if (refused is not null)
            {
                return false;
            }
            _input = string.Empty;
            _fromIndex = _page?.Total ?? 0;
        }
        await RefreshAsync(cancellationToken);
        if (Working && _page?.Queued is { } queued && _page.QueuedIds is { } ids)
        {
            var i = Array.FindLastIndex(queued, q => q == text);
            if (i >= 0 && i < ids.Length && ids[i] is { Length: > 0 } sendId)
            {
                var chatNow = await node.ChatNowAsync(sendId, cancellationToken);
                lock (_gate)
                {
                    Refused = chatNow;
                }
            }
        }
        return true;
    }

    /// <summary>Reads the chat again: the host calls it on each <c>chatChanged</c> of the stream.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (await node.GetChatAsync(-1, cancellationToken) is { } page)
        {
            lock (_gate)
            {
                _page = page;
                if (_fromIndex < 0)
                {
                    _fromIndex = page.Total;
                }
            }
        }
    }

    /// <summary>A choice button was pressed: answers the ask through <c>answer-ask</c>. Returns true when the node took it.</summary>
    public async Task<bool> PressAsync(ChoiceButton button, CancellationToken cancellationToken)
    {
        var refused = await node.AnswerAskAsync(button.RequestId, button.Decision, button.Words, cancellationToken);
        lock (_gate)
        {
            Refused = refused;
        }
        if (refused is null)
        {
            await RefreshAsync(cancellationToken);
        }
        return refused is null;
    }

    /// <summary>"more in the GUI": hands the window the chat (it only raises the running window, as the cards link is not the chat's); starts the GUI when none runs. Blocks, so off the window's thread.</summary>
    public bool OpenInGui() => gui.Open(string.Empty) || gui.Launch(string.Empty);
}
