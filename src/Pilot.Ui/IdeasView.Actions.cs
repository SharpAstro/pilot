using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

// The Ideas page's verbs on the node: noting an idea, Plan, Drop, Edit, Tidy and Move to repo, and the milestone picker an action opens.
internal sealed partial class IdeasView<TSurface>
{
    /// <summary>Notes what is in the box as an idea, as Enter does. The box is emptied at once; Claude picks the repo.</summary>
    public async Task NoteAsync()
    {
        var text = NoteInput.Text.Trim();
        if (text.Length == 0 || Send is null)
        {
            return;
        }
        NoteInput.Clear();
        string[] args = Context is { Length: > 0 } context ? [text, context] : [text];
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _noting, list => list.Add(text));
        IdeaReply? reply;
        try
        {
            reply = await RunAsync($"note:{Interlocked.Increment(ref _notes)}", "ideas:note", "noting", Verbs.NoteIdea, Verbs.RepoTarget(Verbs.AnyRepo), args);
            // Held before the ideating entry goes, so no frame has neither; the node's own row replaces it when the screen holds the key.
            if (reply is { Ok: true, Idea: { } filed })
            {
                System.Collections.Immutable.ImmutableInterlocked.Update(ref _held, list => list.RemoveAll(i => i.Qualified == filed.Qualified).Add(filed));
            }
        }
        finally
        {
            System.Collections.Immutable.ImmutableInterlocked.Update(ref _noting, list => list.Remove(text));
            Changed?.Invoke();
        }
        if (reply is null)
        {
            return;
        }
        if (!reply.Ok)
        {
            Say($"not noted: {reply.Error}", refusal: true);
        }
        else if (reply.Way == IdeaEditWays.Asked)
        {
            Say("Claude could not tell which repo it belongs in: choose one below", refusal: false);
        }
        else
        {
            Say($"noted {Filed(reply.Made)}", refusal: false);
        }
    }

    // "<repo> I<n>" as the node says a filed idea, in the page's way of naming one.
    private string Filed(string? made)
    {
        if (made is null)
        {
            return "an idea";
        }
        var parts = made.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && !string.Equals(parts[0], ShownRepo, StringComparison.OrdinalIgnoreCase) ? made : parts[^1];
    }

    // One verb through the press helper, under the idea's key (a note's own): working at once, a second press of the idea does nothing,
    // and a refusal is kept beside the idea's row in the node's words as well as said at the top. Null when nothing was sent.
    private async Task<IdeaReply?> RunAsync(string key, string hit, string word, string verb, string target, string[] args)
    {
        if (Send is not { } send)
        {
            return null;
        }
        IdeaReply? reply = null;
        System.Collections.Immutable.ImmutableInterlocked.Update(ref _doing, d => d.SetItem(key, word));
        await Presses.PressAsync(key, hit, async ct =>
        {
            try
            {
                reply = await send(verb, target, args, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException)
            {
                reply = new IdeaReply(false, ex.Message);
            }
            return reply.Ok ? null : reply.Error;
        }, CancellationToken.None);
        return reply;
    }

    // A board action on a target (a repo or an item): the same helper, keyed by the target, so it works at once and takes no second press.
    private Task<IdeaReply?> RunAsync(string verb, string target, string[] args) =>
        RunAsync($"board:{target}", $"ideas:board:{verb}:{target}", "working", verb, target, args);

    private Task<IdeaReply?> RunAsync(IdeaRow idea, string hit, string word, string verb, string[] args) =>
        RunAsync(KeyOf(idea), hit, word, verb, TargetOf(idea), args);

    private void Say(string text, bool refusal)
    {
        _said = text;
        _saidRefusal = refusal;
        Changed?.Invoke();
    }

    private void Run(Func<Task> task, string what)
    {
        if (Track is { } track)
        {
            track(task, what);
        }
        else
        {
            _ = task();
        }
    }

    // The target of an idea's verb.
    private static string TargetOf(IdeaRow idea) => IdeaIds.Target(idea.Repo, idea.Number);

    /// <summary>Opens <paramref name="mode"/> on the idea, closing any other: its box is filled for an Edit, empty otherwise.</summary>
    internal void OpenOn(IdeaRow idea, Mode mode)
    {
        var key = KeyOf(idea);
        if (_open is { } open && open.Key == key && open.Mode == mode)
        {
            Close();
            return;
        }
        _open = new Open(key, mode);
        _editFrom = null;
        RowInput.Text = "";
        RowInput.CursorPos = 0;
        if (mode == Mode.Edit)
        {
            SeedEdit(idea.Title, idea.Body);
        }
        RowInput.Placeholder = mode switch
        {
            Mode.Plan => "filter the milestones, or type a new one's title",
            Mode.Drop => "why drop it?",
            _ => "",
        };
        if (mode is Mode.Plan or Mode.Drop)
        {
            Ui.Focus.Focus(RowInput);
        }
        else if (mode == Mode.Edit)
        {
            Ui.Focus.Focus(EditInput);
        }
        _choices = null;
        _picked = null;
        _breakUp = null;
        if (mode == Mode.BreakUp)
        {
            Run(() => BreakUpAsync(idea), $"break up {idea.Id}");
        }
        if (mode == Mode.Tidy)
        {
            _proposal = null;
            Run(() => TidyAsync(idea), $"tidy {idea.Id}");
        }
        else if (mode == Mode.Plan)
        {
            if (_proposal is { Tidy.Milestone: { } kept } held && held.Key == key)
            {
                // A tidy already asked Claude where it belongs: Plan starts from that.
                _choices = new Choices(key, kept, true);
                Seed();
            }
            else
            {
                _choices = new Choices(key, null, false);
                Run(() => ProposeAsync(idea), $"milestone for {idea.Id}");
            }
        }
        Changed?.Invoke();
    }

    private async Task ProposeAsync(IdeaRow idea)
    {
        var reply = await RunAsync(Verbs.ProposeMilestone, TargetOf(idea), []);
        if (reply is null || _open is not { Mode: Mode.Plan } open || open.Key != KeyOf(idea))
        {
            return;
        }
        _choices = new Choices(open.Key, reply.Ok ? reply.Milestones : null, true);
        Seed();
        if (!reply.Ok)
        {
            Say($"no milestone proposed for {Name(idea)}: {reply.Error}", refusal: true);
        }
        Changed?.Invoke();
    }

    // The proposal is chosen at first: a milestone it names, else the new one it suggests. No proposal chooses nothing.
    private void Seed()
    {
        _picked = null;
        if (_choices?.Answer is not { } answer)
        {
            return;
        }
        if (Proposed(answer) is { } named)
        {
            _picked = "m:" + named.Title;
        }
        else if (answer.NewTitle is { Length: > 0 })
        {
            _picked = "s";
        }
    }

    private static MilestoneChoice? Proposed(MilestoneProposal answer) =>
        answer.Title is { Length: > 0 } title ? answer.Open.FirstOrDefault(m => string.Equals(m.Title, title, StringComparison.OrdinalIgnoreCase)) : null;

    // What the list shows for the filter typed: the proposal first, then the rest; the typed text as a new milestone when nothing matches.
    private List<Item> Items(string filter)
    {
        var items = new List<Item>();
        if (_choices is not { Done: true })
        {
            // Claude is still choosing: typed text must not become a new milestone before the open ones are known.
            return items;
        }
        if (_choices.Answer is { } answer)
        {
            var proposed = Proposed(answer);
            Item? suggested = answer.NewTitle is { Length: > 0 } newTitle
                ? new Item("s", newTitle, answer.NewDescription, true, proposed is null ? "proposed" : "suggested", proposed is null ? answer.Reason : null)
                : null;
            if (suggested is not null && proposed is null)
            {
                items.Add(suggested);
            }
            foreach (var m in answer.Open.OrderBy(m => proposed is not null && m.Title == proposed.Title ? 0 : 1))
            {
                var isProposed = proposed is not null && m.Title == proposed.Title;
                var meta = string.Join(" · ", new[] { isProposed ? "proposed" : null, m.DueOn is { } due ? "due " + due.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null, $"{m.OpenIssues} open" }.OfType<string>());
                items.Add(new Item("m:" + m.Title, m.Title, null, false, meta, isProposed ? answer.Reason : null));
            }
            if (suggested is not null && proposed is not null)
            {
                items.Add(suggested);
            }
        }
        var wanted = filter.Trim();
        if (wanted.Length == 0)
        {
            return items;
        }
        items = [.. items.Where(i => i.Title.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        if (items.Count == 0)
        {
            items.Add(new Item("n", wanted, null, true, "", null));
        }
        return items;
    }

    // The milestone Plan would use: the one picked while it is in the list, else, once something is typed, the first the filter leaves.
    private Item? Chosen(List<Item> items)
    {
        if (_picked is { } picked && items.FirstOrDefault(i => i.Key == picked) is { } item)
        {
            return item;
        }
        return RowInput.Text.Trim().Length > 0 ? items.FirstOrDefault() : null;
    }

    /// <summary>Plans the idea into the milestone chosen in the list (or the new one typed or suggested); nothing chosen is not sent.</summary>
    public async Task PlanChosenAsync(IdeaRow idea)
    {
        if (_open is { Mode: Mode.Plan } waiting && waiting.Key == KeyOf(idea) && _choices is not { Done: true })
        {
            Say("Claude is still choosing a milestone; pick one when the list is in", refusal: true);
            return;
        }
        if (_open is not { Mode: Mode.Plan } open || open.Key != KeyOf(idea) || Chosen(Items(RowInput.Text)) is not { } chosen)
        {
            Say("pick a milestone to plan it in", refusal: true);
            return;
        }
        string[] args = !chosen.IsNew ? [chosen.Title] : chosen.Description is { Length: > 0 } description ? [Verbs.NewMilestoneArg, chosen.Title, description] : [Verbs.NewMilestoneArg, chosen.Title];
        await PlanAsync(idea, args);
    }

    /// <summary>Puts away the open action and its box.</summary>
    public void Close()
    {
        _open = null;
        _proposal = null;
        _breakUp = null;
        _choices = null;
        Ui.Focus.BlurIfFocused(RowInput);
        Ui.Focus.BlurIfFocused(EditInput);
        Changed?.Invoke();
    }

    // Plans the idea: moved out of Ideas to the milestone the args name, or to the new one they ask for.
    private async Task PlanAsync(IdeaRow idea, string[] args)
    {
        Close();
        var reply = await RunAsync(idea, $"ideas:plan-go:{KeyOf(idea)}", "planning", Verbs.PlanIdea, args);
        if (reply is not null)
        {
            Say(reply.Ok ? $"planned {Name(idea)}{(reply.Made is { Length: > 0 } flight ? $" as flight {flight}" : "")}" : $"{Name(idea)} not planned: {reply.Error}", !reply.Ok);
        }
    }

    /// <summary>Drops the idea, <paramref name="why"/> its comment; a Drop with no why is not sent.</summary>
    public async Task DropAsync(IdeaRow idea, string why)
    {
        if (why.Trim().Length == 0)
        {
            Say("say why it is dropped", refusal: true);
            return;
        }
        Close();
        var reply = await RunAsync(idea, $"ideas:drop-send:{KeyOf(idea)}", "dropping", Verbs.DropIdea, [why.Trim()]);
        if (reply is not null)
        {
            Say(reply.Ok ? $"dropped {Name(idea)}" : $"{Name(idea)} not dropped: {reply.Error}", !reply.Ok);
        }
    }

    /// <summary>Saves the idea's title as typed, with the body it has, from the version this page read (its <c>updated_at</c>).</summary>
    public async Task EditAsync(IdeaRow idea, string title, string? body = null, DateTimeOffset? from = null)
    {
        if (title.Trim().Length == 0)
        {
            Say("an idea needs a title", refusal: true);
            return;
        }
        var text = body is null ? title.Trim() : (body.Length > 0 ? title.Trim() + "\n" + body : title.Trim());
        await SaveAsync(Verbs.EditIdea, idea, from ?? idea.UpdatedAt, text, "edited", "not edited");
    }

    private async Task SaveAsync(string verb, IdeaRow idea, DateTimeOffset from, string text, string ok, string failed)
    {
        Close();
        var reply = await RunAsync(idea, $"ideas:{(verb == Verbs.EditIdea ? "edit-send" : "tidy-save")}:{KeyOf(idea)}", verb == Verbs.EditIdea ? "saving" : "saving the tidy", verb, [from.ToString("O", CultureInfo.InvariantCulture), text]);
        if (reply is null)
        {
            return;
        }
        if (!reply.Ok)
        {
            Say($"{Name(idea)} {failed}: {reply.Error}", refusal: true);
        }
        else if (reply.Way == IdeaEditWays.Asked)
        {
            Say($"{Name(idea)} changed on GitHub too and the two disagree: choose below", refusal: false);
        }
        else
        {
            Say($"{ok} {Name(idea)}{(reply.Way == IdeaEditWays.Merged ? ", merged with what changed on GitHub" : "")}", refusal: false);
        }
    }

    private async Task TidyAsync(IdeaRow idea)
    {
        var reply = await RunAsync(idea, $"ideas:tidy:{KeyOf(idea)}", "tidying", Verbs.TidyIdea, []);
        if (reply is null)
        {
            return;
        }
        if (reply is { Ok: true, Tidy: { } tidy })
        {
            if (_open is { Mode: Mode.Tidy } open && open.Key == KeyOf(idea))
            {
                _proposal = new Proposal(open.Key, tidy);
                Changed?.Invoke();
            }
        }
        else
        {
            Close();
            Say(reply.Ok ? $"nothing to tidy in {Name(idea)}" : $"{Name(idea)} not tidied: {reply.Error}", !reply.Ok);
        }
    }

    private async Task BreakUpAsync(IdeaRow idea)
    {
        var reply = await RunAsync(idea, $"ideas:breakup:{KeyOf(idea)}", "breaking it up", Verbs.BreakUpIssue, []);
        if (reply is null)
        {
            return;
        }
        if (reply is { Ok: true, BreakUp: { Parts.Length: > 0 } parts })
        {
            if (_open is { Mode: Mode.BreakUp } open && open.Key == KeyOf(idea))
            {
                _breakUp = (open.Key, parts);
                Changed?.Invoke();
            }
        }
        else
        {
            Close();
            Say(reply.Ok ? $"nothing to break up in {Name(idea)}" : $"{Name(idea)} not broken up: {reply.Error}", !reply.Ok);
        }
    }

    /// <summary>Accepts the break-up on screen, from the version Claude read: the first part over the item, the rest as new issues.</summary>
    public async Task AcceptBreakUpAsync(IdeaRow idea)
    {
        if (_breakUp is not { } kept || kept.Key != KeyOf(idea))
        {
            return;
        }
        Close();
        var reply = await RunAsync(idea, $"ideas:breakup-accept:{KeyOf(idea)}", "breaking it up", Verbs.AcceptBreakUp, [kept.Parts.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)]);
        if (reply is not null)
        {
            Say(reply.Ok ? $"broke up {Name(idea)}{(reply.Made is { Length: > 0 } made ? ": " + made : "")}" : $"{Name(idea)} not broken up: {reply.Error}", !reply.Ok);
        }
    }

    /// <summary>Saves the tidy proposal on screen, from the version Claude read.</summary>
    public Task SaveTidyAsync(IdeaRow idea)
    {
        if (_proposal is not { } proposal || proposal.Key != KeyOf(idea))
        {
            return Task.CompletedTask;
        }
        var tidy = proposal.Tidy;
        var text = tidy.Body.Length > 0 ? tidy.Title + "\n" + tidy.Body : tidy.Title;
        return SaveAsync(Verbs.SaveTidyIdea, idea, tidy.UpdatedAt, text, "tidied", "not tidied");
    }

    /// <summary>Takes the tidy proposal into the edit box, to be changed by hand before it is saved.</summary>
    internal void EditProposal(IdeaRow idea)
    {
        if (_proposal is not { } proposal || proposal.Key != KeyOf(idea))
        {
            return;
        }
        var tidy = proposal.Tidy;
        _open = new Open(proposal.Key, Mode.Edit);
        _editFrom = tidy.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        _proposal = null;
        SeedEdit(tidy.Title, tidy.Body);
        Ui.Focus.Focus(EditInput);
        Changed?.Invoke();
    }

    // The edit box holds the whole idea: the title, then the body after a line break.
    private void SeedEdit(string title, string body)
    {
        EditInput.Clear();
        EditInput.InsertText(body.Length > 0 ? title + "\n" + body : title);
    }

    /// <summary>Moves the idea to <paramref name="repo"/>: its number there is new, its old id keeps finding it.</summary>
    public async Task MoveAsync(IdeaRow idea, string repo)
    {
        Close();
        var reply = await RunAsync(idea, $"ideas:move:{KeyOf(idea)}:{repo}", "moving", Verbs.MoveIdea, [repo]);
        if (reply is not null)
        {
            Say(reply.Ok ? $"moved {Name(idea)} to {Filed(reply.Made ?? repo)}" : $"{Name(idea)} not moved: {reply.Error}", !reply.Ok);
        }
    }
}
