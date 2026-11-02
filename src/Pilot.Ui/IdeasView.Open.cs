using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Ideas page's open row: the edit and tidy panels under an idea, and the diff of a tidy.
internal sealed partial class IdeasView<TSurface>
{
    private IEnumerable<Layout.Node> OpenRows(IdeaRow idea, Mode mode)
    {
        switch (mode)
        {
            case Mode.Plan:
            case Mode.Drop:
            case Mode.Edit:
                if (mode == Mode.Edit)
                {
                    yield return Layout.Builder.TextEditor(EditInput, 14f, colors: InputColors, focusOnOpen: true).WStar();
                }
                else
                {
                    if (mode == Mode.Plan)
                    {
                        // What the box is for, said: "plan in" over an empty box left the owner unsure what it wanted (2026-10-29).
                        yield return WrappedProse.Of($"Plan makes {Name(idea)} a flight: its issue goes into a milestone and a planner starts on it. Pick the milestone to put it in.", 12f, P.Body).WStar();
                    }
                    yield return Layout.Builder.HStack(
                            Layout.Builder.Text(mode == Mode.Plan ? "milestone" : "why?", 12f, P.Dim).Selectable().WAuto(),
                            Layout.Builder.TextInput(RowInput, 14f, InputColors, focusOnOpen: true).WStar())
                        .WithGap(10f).RowH(30f);
                }
                Item? chosen = null;
                if (mode == Mode.Plan)
                {
                    if (_choices is { Done: false })
                    {
                        yield return Layout.Builder.Text("● Claude is choosing a milestone…", 12f, P.Held).Selectable().RowH(18f);
                    }
                    var items = Items(RowInput.Text);
                    chosen = Chosen(items);
                    foreach (var item in items)
                    {
                        var pick = item.Key;
                        var label = (chosen?.Key == pick ? "● " : "") + (item.IsNew ? $"new milestone: {item.Title}" : item.Title) + (item.Meta.Length > 0 ? " · " + item.Meta : "");
                        var rows = new List<Layout.Node>
                        {
                            CardsView<TSurface>.Pill(label, $"ideas:milestone:{KeyOf(idea)}:{pick}", () =>
                            {
                                _picked = pick;
                                Changed?.Invoke();
                            }, chosen?.Key == pick ? Lit : Tint, P.Bright),
                        };
                        if (item.Description is { Length: > 0 } description)
                        {
                            rows.Add(WrappedProse.Of(description, 12f, P.Dim).WStar());
                        }
                        if (item.Reason is { Length: > 0 } reason)
                        {
                            rows.Add(WrappedProse.Of(reason, 12f, P.Dim).WStar());
                        }
                        yield return Layout.Builder.VStack([.. rows]).WithGap(2f).WStar().HAuto();
                    }
                    if (_choices is { Done: true, Answer.Note: { Length: > 0 } note })
                    {
                        yield return WrappedProse.Of(note, 12f, P.Dim).WStar();
                    }
                }
                var buttons = new List<Layout.Node>();
                if (mode == Mode.Plan)
                {
                    buttons.Add(chosen is null
                        ? Layout.Builder.Text("Plan", 12f, P.Faint, TextAlign.Center).Pad(10f, 4f).Bg(P.GhostBg).WAuto()
                        : NodeButton(idea, "Plan", "plan-go", "planning", () => Run(() => PlanChosenAsync(idea), $"plan {idea.Id}"), Lit));
                }
                if (mode == Mode.Edit)
                {
                    buttons.Add(CardsView<TSurface>.Pill("Edit on GitHub ↗", $"ideas:edit-github:{KeyOf(idea)}", () => { if (idea.Url.Length > 0) { OnOpen?.Invoke(idea.Url); } }, Tint, Link));
                }
                buttons.Add(CardsView<TSurface>.Pill("Cancel", $"ideas:cancel:{KeyOf(idea)}", Close, Tint, P.Body));
                yield return Layout.Builder.WrapH([.. buttons]).WithGap(8f).WithLineGap(4f).WStar();
                if (mode == Mode.Edit)
                {
                    var editHint = Hint(EditInput, "", out var editColor);
                    if (editHint.Length > 0)
                    {
                        yield return Layout.Builder.Text(editHint, 11f, editColor).Selectable().RowH(16f);
                    }
                }
                yield return Layout.Builder.Text(mode switch
                {
                    Mode.Plan => "Type to filter · Enter plans the chosen milestone · Esc cancels",
                    Mode.Edit => "First line is the title · Ctrl+Enter saves · Esc cancels" + (Upload is null ? "" : " · Ctrl+V pastes an image"),
                    _ => "Enter sends · Esc cancels",
                }, 11f, P.Faint).Selectable().RowH(16f);
                break;
            case Mode.Tidy:
                foreach (var node in TidyRows(idea))
                {
                    yield return node;
                }
                break;
            case Mode.BreakUp:
                foreach (var node in BreakUpRows(idea))
                {
                    yield return node;
                }
                break;
            case Mode.Move:
                var repos = _screen.Repos.Where(r => !string.Equals(r, idea.Repo, StringComparison.OrdinalIgnoreCase)).ToList();
                yield return repos.Count == 0
                    ? Layout.Builder.Text("The node tracks no other repo.", 12f, P.Dim).Selectable().RowH(18f)
                    : Layout.Builder.WrapH([.. repos.Select(r => NodeButton(idea, r, $"move:{KeyOf(idea)}:{r}", "moving", () => Run(() => MoveAsync(idea, r), $"move {idea.Id}"), Tint, idOf: false))])
                        .WithGap(8f).WithLineGap(4f).WStar();
                yield return CardsView<TSurface>.Pill("Cancel", $"ideas:cancel:{KeyOf(idea)}", Close, Tint, P.Body);
                break;
        }
    }

    // Claude's proposal as a diff against what is there: removed lines red, added lines green, kept lines dim.
    private IEnumerable<Layout.Node> TidyRows(IdeaRow idea)
    {
        if (_proposal is not { } proposal || proposal.Key != KeyOf(idea))
        {
            yield return Layout.Builder.Text("● Claude is tidying it…", 12f, P.Held).Selectable().RowH(18f);
            yield break;
        }
        var tidy = proposal.Tidy;
        foreach (var (kind, line) in Diff(Join(tidy.CurrentTitle, tidy.CurrentBody), Join(tidy.Title, tidy.Body)))
        {
            var (mark, color) = kind switch { '+' => ("+ ", P.Flying), '-' => ("- ", P.Failed), _ => ("  ", P.Dim) };
            yield return Layout.Builder.HStack(
                    Layout.Builder.Text(mark, 13f, color).Selectable().WFixed(16f),
                    line.Length == 0 ? Layout.Builder.Spacer().WStar() : WrappedProse.Of(line, 13f, color))
                .WStar().HAuto();
        }
        if (tidy.Milestone is { } plan && (Proposed(plan)?.Title ?? plan.NewTitle) is { Length: > 0 } where)
        {
            yield return WrappedProse.Of($"Plan it in: {(Proposed(plan) is null ? "new milestone " : "")}{where}" + (plan.Reason is { Length: > 0 } why ? $" ({why})" : ""), 12f, P.Dim).WStar();
        }
        yield return Layout.Builder.WrapH(
                NodeButton(idea, "Save", "tidy-save", "saving the tidy", () => Run(() => SaveTidyAsync(idea), $"save tidy {idea.Id}"), Lit),
                CardsView<TSurface>.Pill("Discard", $"ideas:tidy-discard:{KeyOf(idea)}", Close, Tint, P.Body),
                CardsView<TSurface>.Pill("Edit", $"ideas:tidy-edit:{KeyOf(idea)}", () => EditProposal(idea), Tint, P.Bright))
            .WithGap(8f).WithLineGap(4f).WStar();
    }

    // Claude's parts: each with its title, body and open questions, then Accept and Discard.
    private IEnumerable<Layout.Node> BreakUpRows(IdeaRow idea)
    {
        if (_breakUp is not { } kept || kept.Key != KeyOf(idea))
        {
            yield return Layout.Builder.Text("● Claude is breaking it up…", 12f, P.Held).Selectable().RowH(18f);
            yield return CardsView<TSurface>.Pill("Cancel", $"ideas:cancel:{KeyOf(idea)}", Close, Tint, P.Body);
            yield break;
        }
        var number = 0;
        foreach (var part in kept.Parts.Parts)
        {
            number++;
            var rows = new List<Layout.Node> { WrappedProse.Of($"{number}. {part.Title}", 14f, P.Bright).WStar() };
            if (part.Body.Trim().Length > 0)
            {
                rows.Add(WrappedProse.Of(part.Body, 13f, P.Body).WStar());
            }
            foreach (var question in part.OpenQuestions)
            {
                rows.Add(WrappedProse.Of("? " + question, 12f, P.Dim).WStar());
            }
            yield return Layout.Builder.VStack([.. rows]).WithGap(2f).WStar().HAuto();
        }
        yield return WrappedProse.Of($"Accept puts part 1 over {Name(idea)} and makes the other {kept.Parts.Parts.Length - 1} as new issues in its milestone with its priority.", 12f, P.Dim).WStar();
        yield return Layout.Builder.WrapH(
                NodeButton(idea, "Accept", "breakup-accept", "breaking it up", () => Run(() => AcceptBreakUpAsync(idea), $"accept break-up {idea.Id}"), Lit),
                CardsView<TSurface>.Pill("Discard", $"ideas:breakup-discard:{KeyOf(idea)}", Close, Tint, P.Body))
            .WithGap(8f).WithLineGap(4f).WStar();
    }

    private static string Join(string title, string body) => body.Length > 0 ? title + "\n" + body : title;

    /// <summary>A line diff of <paramref name="before"/> against <paramref name="after"/> (longest common subsequence): kept lines ' ', removed '-', added '+'.</summary>
    internal static List<(char Kind, string Line)> Diff(string before, string after)
    {
        var a = before.ReplaceLineEndings("\n").Split('\n');
        var b = after.ReplaceLineEndings("\n").Split('\n');
        var common = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
            }
        }
        var lines = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                lines.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (common[x + 1, y] >= common[x, y + 1])
            {
                lines.Add(('-', a[x++]));
            }
            else
            {
                lines.Add(('+', b[y++]));
            }
        }
        while (x < a.Length)
        {
            lines.Add(('-', a[x++]));
        }
        while (y < b.Length)
        {
            lines.Add(('+', b[y++]));
        }
        return lines;
    }
}
