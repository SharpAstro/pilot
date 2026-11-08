using System.Text.Json.Nodes;
using DIR.Lib;
using Pilot.Contracts;
using static Pilot.Ui.TranscriptEntryView;

namespace Pilot.Ui;

/// <summary>
/// The 'Waiting on you' panel (PIL855): what the stream watchtower waits on the person for, from the chat page's asks. A permission prompt
/// shows the tool and its input with Allow and Deny; a question shows its options (radio marks for one choice, checkboxes for several) with
/// an Other box for the person's own words, a Send answer button once every question has one, and Decline. The answer is the node's
/// <c>answer-ask</c>; the same ask is a card in For you, and whichever answers first closes the other.
/// </summary>
internal sealed partial class ChatView<TSurface>
{
    /// <summary>The panel's title, above the ask.</summary>
    internal const string WaitingTitle = "Waiting on you";

    /// <summary>The panel's share of the page's height, as the question card's.</summary>
    private const float WaitWeight = 45f;

    /// <summary>
    /// Answers the ask with the given id (<c>allow</c>, <c>deny</c> or <c>answer</c>, then the words: a reason, or the JSON of each question's answer;
    /// the node's <c>answer-ask</c>): null when the node did it, else what it said in refusing it. Null leaves the panel without its buttons.
    /// </summary>
    public Func<string, string, string?, Task<string?>>? OnAnswerAsk { get; set; }

    /// <summary>Answers an ask of another node's watchtower, given that node; as <see cref="OnAnswerAsk"/>.</summary>
    public Func<NodeMark, string, string, string?, Task<string?>>? OnAnswerAskOn { get; set; }

    private Func<string, string, string?, Task<string?>>? CurrentAnswerAsk() => Node is { } node
        ? (OnAnswerAskOn is { } on ? (id, decision, words) => on(node, id, decision, words) : null)
        : OnAnswerAsk;

    // What the person has picked and typed for an ask they have not answered yet, by its id: a pick per option, the Other box per question.
    private sealed class AskInput
    {
        public Dictionary<int, HashSet<string>> Picks { get; } = [];

        public Dictionary<int, TextInputState> Others { get; } = [];
    }

    private readonly Dictionary<string, AskInput> _askInputs = new(StringComparer.Ordinal);

    // The id of the ask the panel showed last, so its scroll starts at the top for the next one.
    private string? _waitingOn;

    private static string AskKey(string id) => $"chat:ask:{id}";

    /// <summary>The Other box of question <paramref name="question"/> of the ask <paramref name="askId"/>, once the panel has drawn it; for a test to type into.</summary>
    internal TextInputState OtherBox(string askId, int question) => OtherOf(_askInputs[askId], question);

    private AskInput InputOf(ChatAsk ask)
    {
        if (!_askInputs.TryGetValue(ask.Id, out var input))
        {
            _askInputs[ask.Id] = input = new AskInput();
        }
        return input;
    }

    private TextInputState OtherOf(AskInput input, int question)
    {
        if (!input.Others.TryGetValue(question, out var other))
        {
            input.Others[question] = other = new TextInputState { Placeholder = "In your own words" };
        }
        return other;
    }

    /// <summary>What the person's picks and Other box say for question <paramref name="q"/>: the Other words when there are any, else the picked labels (several joined by a comma); empty when unanswered.</summary>
    private static string AnswerTo(AskInput input, int q, ChatAskQuestion question)
    {
        var other = input.Others.TryGetValue(q, out var box) ? box.Text.Trim() : "";
        var picked = input.Picks.TryGetValue(q, out var set) ? question.Options.Select(o => o.Label).Where(set.Contains).ToList() : [];
        return question.MultiSelect
            ? string.Join(", ", other.Length > 0 ? picked.Append(other) : picked)
            : other.Length > 0 ? other : picked.FirstOrDefault() ?? "";
    }

    /// <summary>The panel for the first of <paramref name="asks"/>; the rest wait their turn, said in its title.</summary>
    private Layout.Node WaitingOnYou(ChatAsk[] asks)
    {
        // Inputs of asks that are gone (answered, called off) go with them.
        foreach (var gone in _askInputs.Keys.Where(id => asks.All(a => a.Id != id)).ToList())
        {
            _askInputs.Remove(gone);
        }
        var ask = asks[0];
        if (_waitingOn != ask.Id)
        {
            _waitingOn = ask.Id;
            _askScroll = NewAskScroll();
        }
        _askScroll.Mode = _askScroll.MaxOffset > 1f ? ScrollBarMode.Interactive : ScrollBarMode.None;
        var input = InputOf(ask);
        var key = AskKey(ask.Id);
        var body = new List<Layout.Node>
        {
            Layout.Builder.Text(asks.Length > 1 ? $"{WaitingTitle} · 1 of {asks.Length}" : WaitingTitle, 11f, Body).Selectable(),
        };
        var actions = new List<Layout.Node>();
        if (ask.Questions is { Length: > 0 } questions)
        {
            for (var q = 0; q < questions.Length; q++)
            {
                body.AddRange(QuestionBlock(ask, input, q, questions[q]));
            }
            var answers = questions.Select((question, q) => AnswerTo(input, q, question)).ToArray();
            var ready = answers.All(a => a.Length > 0);
            var json = new JsonObject();
            for (var q = 0; q < questions.Length; q++)
            {
                json[questions[q].Question] = answers[q];
            }
            actions.Add(ready
                ? AskButton(key, ask.Id, "answer", "Send answer", () => AnswerAsk(ask.Id, "answer", json.ToJsonString(), $"{key}:answer"))
                : CardsView<TSurface>.Dead("Send answer"));
            actions.Add(AskButton(key, ask.Id, "deny", "Decline to answer", () => AnswerAsk(ask.Id, "deny", null, $"{key}:deny")));
            if (!ready)
            {
                body.Add(Layout.Builder.Text(questions.Length > 1 ? "Choose an option or write your own for each question." : "Choose an option or write your own.", 11f, Body).Selectable());
            }
        }
        else
        {
            body.Add(Layout.Builder.Text("The watchtower asks leave to", 11f, Body).Selectable());
            // Whole, never cut: a long command scrolls inside the capped panel.
            body.Add(WrappedProse.Of(ask.Summary, 14f, Bright));
            if (ask.Input is { Length: > 0 } raw)
            {
                body.Add(Layout.Builder.Text(ask.Tool, 11f, Body).Selectable());
                // Whole as well: Allow runs the whole call, so the person sees all of it (a long heredoc or a gh body scrolls here).
                body.Add(WrappedProse.Of(raw, 12f, Dim));
            }
            actions.Add(AskButton(key, ask.Id, "allow", "Allow", () => AnswerAsk(ask.Id, "allow", null, $"{key}:allow")));
            actions.Add(AskButton(key, ask.Id, "deny", "Deny", () => AnswerAsk(ask.Id, "deny", null, $"{key}:deny")));
        }
        // The text alone scrolls; the buttons stay under it, always in view.
        var text = Layout.Builder.VStack(
                Layout.Builder.HStack(
                        Layout.Builder.VStack([.. body]).WithGap(4f).WStar(),
                        Layout.Builder.Spacer().WFixed(TranscriptEntryView.ScrollGutter))
                    .WStar()
                    .HAuto())
            .WithScroll(_askScroll)
            .WStar()
            .HStarUpToContent(1f, AskTextMinHeight);
        var parts = new List<Layout.Node> { text, Layout.Builder.WrapH([.. actions]).WithGap(ChoiceGap).WithLineGap(ChoiceLineGap).WStar() };
        if (Presses.RefusalLine(key, 12f, Bright, 3) is { } refusal)
        {
            parts.Add(refusal);
        }
        return Layout.Builder.HStack(
                Layout.Builder.Spacer().Bg(Prompt).WFixed(3f).HStar(),
                Layout.Builder.VStack([.. parts]).WithGap(ChoiceGap).WStar().HStar())
            .WithGap(8f)
            .Pad(8f, AskPadY)
            .Bg(Marked)
            .WStar()
            .HStarUpToContent(WaitWeight, AskMinHeight);
    }

    // One of the panel's answering buttons: working while the node has the answer, dead without a way to send it.
    private Layout.Node AskButton(string key, string id, string decision, string label, Action press) =>
        Presses.Button(key, $"{key}:{decision}",
            () => CurrentAnswerAsk() is null ? CardsView<TSurface>.Dead(label) : CardsView<TSurface>.Pill(label, $"{key}:{decision}", press),
            pressed => CardsView<TSurface>.Dead(pressed ? $"{label} …" : label));

    // A question: its header and words, an option per row (its mark, its label, what it does) and the Other box.
    private IEnumerable<Layout.Node> QuestionBlock(ChatAsk ask, AskInput input, int q, ChatAskQuestion question)
    {
        if (question.Header is { Length: > 0 } header)
        {
            yield return Layout.Builder.Text(header, 11f, Body).Selectable();
        }
        yield return WrappedProse.Of(question.Question, 14f, Bright);
        var picks = input.Picks.TryGetValue(q, out var set) ? set : input.Picks[q] = [];
        var other = OtherOf(input, q);
        for (var i = 0; i < question.Options.Length; i++)
        {
            var option = question.Options[i];
            // The mark says whether it is chosen, so colour carries nothing alone: a box for several, a dot for one.
            var on = picks.Contains(option.Label);
            var mark = question.MultiSelect ? (on ? "[x] " : "[ ] ") : (on ? "● " : "○ ");
            var hit = $"{AskKey(ask.Id)}:pick:{q}:{i + 1}";
            var label = option.Label;
            // A long label wraps in a pill the width of the panel rather than running past it.
            var pill = CardsView<TSurface>.ChoicePill(mark + label, false, hit, () =>
            {
                if (question.MultiSelect)
                {
                    if (!picks.Remove(label))
                    {
                        picks.Add(label);
                    }
                }
                else
                {
                    picks.Clear();
                    picks.Add(label);
                    // One choice: the words in the Other box would be a second answer, so they go.
                    other.Clear();
                }
                Changed?.Invoke();
            }, null, Bright, wrap: true);
            yield return option.Description is { Length: > 0 } description
                ? Layout.Builder.VStack(pill, WrappedProse.Of(description, 12f, Body).Pad(10f, 0f)).WithGap(2f).WStar()
                : Layout.Builder.VStack(pill).WStar();
        }
        yield return Layout.Builder.HStack(
                Layout.Builder.Text("Other", 12f, Body, TextAlign.Near, TextAlign.Center).Selectable().WFixed(56f).HFixed(28f),
                Layout.Builder.TextInput(other, 14f, Palette.Input).RowH(28f).WStar())
            .WithGap(8f)
            .WStar();
    }

    // The press of an answer: the node's answer sets the state, a refusal is said under the buttons, and the feed is read at once.
    private void AnswerAsk(string id, string decision, string? words, string hit)
    {
        var key = AskKey(id);
        if (CurrentAnswerAsk() is not { } answer || Presses.PendingOf(key) is not null)
        {
            return;
        }
        var run = AnswerAskAsync(key, hit, id, decision, words, answer);
        Track?.Invoke(() => run, "chat answer ask");
    }

    private async Task AnswerAskAsync(string key, string hit, string id, string decision, string? words, Func<string, string, string?, Task<string?>> answer)
    {
        await Presses.PressAsync(key, hit, _ => answer(id, decision, words));
        Refresh();
        Changed?.Invoke();
    }
}
