namespace Pilot.Buddy;

/// <summary>
/// The chat under the bubble when the cat is clicked (#1357; a deliberate addition to the design, whose screens do not show it): the watchtower's ask with
/// its choices as buttons (<c>choice:&lt;i&gt;</c>), its answer as it streams in (a long one ends in "more in the GUI" with a link, <c>more</c>), what the
/// node refused, and the one-line input (<c>input</c>), which stays on screen disabled with the reason when the watchtower is down. The idle cat's
/// shortcut <c>whatsgoing</c> asks "What's going on?".
/// </summary>
public sealed class ChatCard : BuddyCard
{
    /// <summary>The card's width, as an ask's bubble.</summary>
    public const float CardWidth = SpeechBubble.AskWidth;

    /// <summary>The input's and a choice button's heights.</summary>
    public const float InputHeight = 30, ButtonHeight = 28;

    /// <summary>The most lines of a question, an answer and a refusal.</summary>
    public const int MaxQuestionLines = 3, MaxAnswerLines = 6, MaxRefusalLines = 2;

    private bool _ask;

    private ChatCard()
    {
        Width = CardWidth;
    }

    /// <inheritdoc />
    protected override bool AmberBorder => _ask;

    /// <summary>The buttons of the pending ask, in order: <c>choice:&lt;i&gt;</c> presses <c>Choices[i]</c>.</summary>
    public IReadOnlyList<ChoiceButton> Choices { get; private set; } = [];

    /// <summary>Lays out the chat card for the chat's state.</summary>
    public static ChatCard Build<TSurface>(Pen<TSurface> pen, BuddyChat chat)
    {
        var card = new ChatCard();
        var inner = CardWidth - 2 * Padding;
        var y = Padding;

        if (chat.PendingAsk is { } ask)
        {
            card._ask = true;
            card.Items.Add(new CardItem(CardItemKind.WarnIcon, Padding, y + 1, SpeechBubble.IconSize, SpeechBubble.IconSize));
            var textX = Padding + SpeechBubble.IconSize + SpeechBubble.IconGap;
            y = card.AddWrapped(pen, ask.Question, textX, y, CardWidth - Padding - textX, 14, CardTone.Header, MaxQuestionLines, "question") + Gap;
            card.Choices = ask.Choices;
            for (var i = 0; i < ask.Choices.Count; i++)
            {
                card.Button(pen, $"choice:{i}", ask.Choices[i].Label, y, inner);
                y += ButtonHeight + 6;
            }
            if (ask.Choices.Count == 0)
            {
                y = card.Link(pen, "Answer in the GUI", "more", y, inner) + Gap;
            }
            else
            {
                y += Gap - 6;
            }
        }

        var (answer, more) = chat.ShownAnswer;
        if (answer is not null)
        {
            y = card.AddWrapped(pen, answer, Padding, y, inner, 13, CardTone.Body, MaxAnswerLines, "answer") + Gap;
            if (more || card.Tooltips.ContainsKey("answer"))
            {
                y = card.Link(pen, BuddyChat.MoreInTheGui, "more", y, inner) + Gap;
            }
        }
        else if (chat.Working)
        {
            card.AddLine(pen, "The watchtower is on it.", Padding, y, inner, 13, CardTone.Muted);
            y += LineHeight(pen, 13) + Gap;
        }

        if (chat.Refused is { } refused)
        {
            y = card.AddWrapped(pen, refused, Padding, y, inner, 13, CardTone.Bad, MaxRefusalLines, "refused") + Gap;
        }

        y += card.Input(pen, chat, y, inner);
        if (chat.DisabledReason is null && chat.Input.Length == 0 && chat.PendingAsk is null)
        {
            y += 6;
            y = card.Link(pen, BuddyChat.WhatsGoingOn, "whatsgoing", y, inner, arrow: false);
        }
        card.Height = y + Padding;
        return card;
    }

    private void Button<TSurface>(Pen<TSurface> pen, string id, string label, float y, float width)
    {
        Items.Add(new CardItem(CardItemKind.Box, Padding, y, width, ButtonHeight));
        var (fit, cut) = Fit(pen, label, 13, width - 16);
        var textW = pen.Measure(fit, 13).Width;
        var lineH = LineHeight(pen, 13);
        var left = Padding + (width - textW) / 2;
        var top = y + (ButtonHeight - lineH) / 2;
        Items.Add(new CardItem(CardItemKind.Text, left, top, textW + 1, lineH, fit, 13, CardTone.Button));
        Lines.Add(new CardLine(fit, left, top, textW + 1, lineH, 13));
        if (cut)
        {
            Tooltips[id] = label;
        }
        Hits.Add(new CardHit(id, Padding, y, width, ButtonHeight));
    }

    private float Link<TSurface>(Pen<TSurface> pen, string text, string id, float y, float width, bool arrow = true)
    {
        var textW = pen.Measure(text, 13).Width;
        AddLine(pen, text, Padding, y, MathF.Min(width, textW + 1), 13, CardTone.Accent);
        var lineH = LineHeight(pen, 13);
        if (arrow)
        {
            Items.Add(new CardItem(CardItemKind.Arrow, Padding + textW + 4, y + lineH / 2 - 4, 8, 8));
        }
        Hits.Add(new CardHit(id, Padding, y, textW + (arrow ? 14 : 0), lineH));
        return y + lineH;
    }

    private float Input<TSurface>(Pen<TSurface> pen, BuddyChat chat, float y, float width)
    {
        var disabled = chat.DisabledReason;
        Items.Add(new CardItem(CardItemKind.Box, Padding, y, width, InputHeight));
        var lineH = LineHeight(pen, 13);
        var top = y + (InputHeight - lineH) / 2;
        var room = width - 20;
        if (disabled is not null)
        {
            // Not hidden: the box stays, and the reason is its text.
            AddLine(pen, disabled, Padding + 10, top, room, 13, CardTone.Muted, "input");
        }
        else if (chat.Input.Length == 0)
        {
            AddLine(pen, BuddyChat.Placeholder, Padding + 10, top, room, 13, CardTone.Muted);
            Items.Add(new CardItem(CardItemKind.Cursor, Padding + 10, top, 1, lineH));
        }
        else
        {
            // What is typed shows its end, with an ellipsis in front of what scrolled out: the cursor is always in view.
            var shown = chat.Input;
            while (shown.Length > 1 && pen.Measure(shown, 13).Width > room - 4)
            {
                shown = shown[1..];
            }
            if (shown.Length < chat.Input.Length)
            {
                shown = TextWrap.Ellipsis + shown[1..];
                Tooltips["input"] = chat.Input;
            }
            var w = pen.Measure(shown, 13).Width;
            Items.Add(new CardItem(CardItemKind.Text, Padding + 10, top, w + 1, lineH, shown, 13, CardTone.Button));
            Lines.Add(new CardLine(shown, Padding + 10, top, w + 1, lineH, 13));
            Items.Add(new CardItem(CardItemKind.Cursor, Padding + 10 + w + 1, top, 1, lineH));
        }
        Hits.Add(new CardHit("input", Padding, y, width, InputHeight));
        return InputHeight;
    }
}
