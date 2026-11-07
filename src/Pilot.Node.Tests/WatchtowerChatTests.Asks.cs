using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// What the stream watchtower waits on the person for (PIL855), over a fake claude process: each permission prompt and question is on the
/// chat page, is a card in For you whose buttons answer it, is answered once whichever the person uses, and a safe tool is answered by the node.
/// </summary>
public sealed partial class WatchtowerChatTests
{
    private static string CanUse(string id, string tool, string input) =>
        $$$"""{"type":"control_request","request_id":"{{{id}}}","request":{"subtype":"can_use_tool","tool_name":"{{{tool}}}","input":{{{input}}},"tool_use_id":"toolu_{{{id}}}"}}""";

    private const string RepoQuestion =
        """{"questions":[{"question":"Which repo?","header":"Repo","multiSelect":false,"options":[{"label":"tianwen","description":"the sky"},{"label":"pilot","description":"this one"}]}]}""";

    private const string ManyQuestion =
        """{"questions":[{"question":"Which flights?","header":"Flights","multiSelect":true,"options":[{"label":"PIL1","description":"a"},{"label":"PIL2","description":"b"}]}]}""";

    private static string[] ResponsesTo(WatchtowerStreamTests.FakeProcess process, string id) =>
        [.. process.Lines.Where(l => l.Contains("control_response") && JsonDocument.Parse(l).RootElement.GetProperty("response").GetProperty("request_id").GetString() == id)];

    [Fact]
    public async Task APromptAndAQuestionEachRaiseACardWhoseButtonsAnswerThemAndTheCardClosesOnTheChatsAnswer()
    {
        var process = await StreamIdleAsync();
        var chat = StreamChat();
        chat.Page(-1).Asks.ShouldBeNull();

        process.Emit(CanUse("p1", "Bash", """{"command":"git push origin main"}"""));
        process.Emit(CanUse("q1", "AskUserQuestion", RepoQuestion));
        await UntilAsync(() => chat.Page(-1).Asks is { Length: 2 }, "both asks on the page");

        var page = chat.Page(-1);
        var prompt = page.Asks![0];
        (prompt.Id, prompt.Tool, prompt.Summary).ShouldBe(("p1", "Bash", "run: git push origin main (Bash)"));
        prompt.Input.ShouldNotBeNull().ShouldContain("git push origin main");
        prompt.Questions.ShouldBeNull();
        var question = page.Asks[1];
        question.Questions.ShouldNotBeNull().ShouldHaveSingleItem().Options.Select(o => o.Label).ShouldBe(["tianwen", "pilot"]);
        question.Summary.ShouldBe("Which repo?");

        // Each is a card in For you, one each, whose buttons answer it.
        var cards = _state.Cards(openOnly: true).Where(c => c.Kind == "watchtower-ask").OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        cards.Length.ShouldBe(2);
        cards.ShouldAllBe(c => c.Bin == CardBin.ForYou);
        var promptCard = cards.Single(c => c.Subject == Watchtower.AskSubject("p1"));
        promptCard.Title.ShouldBe("The watchtower asks leave to run: git push origin main (Bash)");
        promptCard.Choices.Select(c => (c.Label, c.Verb, c.Target)).ShouldBe([("Allow", Verbs.AnswerAsk, "watchtower"), ("Deny", Verbs.AnswerAsk, "watchtower")]);
        promptCard.Choices.Select(c => c.Args).ShouldBe([["p1", "allow"], ["p1", "deny"]]);
        var questionCard = cards.Single(c => c.Subject == Watchtower.AskSubject("q1"));
        questionCard.Title.ShouldBe("The watchtower asks you: Which repo?");
        questionCard.Choices.Select(c => c.Label).ShouldBe(["tianwen", "pilot", Watchtower.DeclineLabel]);
        JsonDocument.Parse(questionCard.Choices[1].Args[2]).RootElement.GetProperty("Which repo?").GetString().ShouldBe("pilot");

        // The chat answers the prompt: written once, the card closes, the page no longer lists it.
        (await _tower.AnswerAskAsync(["p1", "allow"], Ct)).ShouldBeNull();
        var written = ResponsesTo(process, "p1").ShouldHaveSingleItem();
        JsonDocument.Parse(written).RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString().ShouldBe("allow");
        _state.Cards().Single(c => c.Id == promptCard.Id).State.ShouldBe(CardState.Closed);
        _state.Cards().Single(c => c.Id == promptCard.Id).Outcome.ShouldBe("allowed");
        chat.Page(-1).Asks.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe("q1");

        // A second answer, by the card's own choice or the chat's, is refused and writes nothing more.
        (await _tower.AnswerAskAsync(promptCard.Choices[0].Args, Ct)).ShouldNotBeNull().ShouldContain("not waiting");
        (await _tower.AnswerAskAsync(["p1", "deny", "too late"], Ct)).ShouldNotBeNull();
        ResponsesTo(process, "p1").Length.ShouldBe(1);

        // The card's button answers the question the same way, and its card closes with what was chosen.
        (await _tower.AnswerAskAsync(questionCard.Choices[1].Args, Ct)).ShouldBeNull();
        var answered = JsonDocument.Parse(ResponsesTo(process, "q1").ShouldHaveSingleItem()).RootElement.GetProperty("response").GetProperty("response");
        answered.GetProperty("updatedInput").GetProperty("answers").GetProperty("Which repo?").GetString().ShouldBe("pilot");
        _state.Cards().Single(c => c.Id == questionCard.Id).Outcome.ShouldNotBeNull().ShouldStartWith("answered:");
        chat.Page(-1).Asks.ShouldBeNull();
    }

    [Fact]
    public async Task AMultiSelectQuestionIsAnsweredInTheChatWithSeveralLabelsAndItsCardOffersOnlyToDecline()
    {
        var process = await StreamIdleAsync();
        process.Emit(CanUse("m1", "AskUserQuestion", ManyQuestion));
        await UntilAsync(() => StreamChat().Page(-1).Asks is { Length: 1 }, "the question");

        var card = _state.Cards(openOnly: true).Single(c => c.Kind == "watchtower-ask");
        card.Choices.Select(c => c.Label).ShouldBe([Watchtower.DeclineLabel]);
        card.Detail.ShouldNotBeNull().ShouldContain("answer it in the chat");
        StreamChat().Page(-1).Asks![0].Questions![0].MultiSelect.ShouldBeTrue();

        // An answer with an array of labels is several, joined by a comma; one that is no JSON object is refused and the ask stays.
        (await _tower.AnswerAskAsync(["m1", "answer", "not json"], Ct)).ShouldNotBeNull();
        StreamChat().Page(-1).Asks.ShouldNotBeNull();
        (await _tower.AnswerAskAsync(["m1", "answer", """{"Which flights?":["PIL1","PIL2"]}"""], Ct)).ShouldBeNull();

        JsonDocument.Parse(ResponsesTo(process, "m1").Single()).RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedInput")
            .GetProperty("answers").GetProperty("Which flights?").GetString().ShouldBe("PIL1, PIL2");
    }

    [Fact]
    public async Task ADeclinedQuestionAndAnAskTheProcessCallsOffCloseTheirCardsAndAToolInsideTheWatchtowersFolderIsAnsweredByTheNode()
    {
        var process = await StreamIdleAsync();
        var inside = Path.Combine(_options.WatchtowerFolder, "notes.md").Replace("\\", "\\\\");

        process.Emit(CanUse("safe", "Read", $$"""{"file_path":"{{inside}}"}"""));
        await UntilAsync(() => ResponsesTo(process, "safe").Length == 1, "the node's own answer to a read inside its folder");
        StreamChat().Page(-1).Asks.ShouldBeNull();
        _state.Cards(openOnly: true).Where(c => c.Kind == "watchtower-ask").ShouldBeEmpty();

        // The same read outside it is a prompt for the person.
        process.Emit(CanUse("outside", "Read", """{"file_path":"/somewhere/else.md"}"""));
        process.Emit(CanUse("q2", "AskUserQuestion", RepoQuestion));
        await UntilAsync(() => StreamChat().Page(-1).Asks is { Length: 2 }, "the prompt and the question");
        (await _tower.AnswerAskAsync(["q2", "deny"], Ct)).ShouldBeNull();
        JsonDocument.Parse(ResponsesTo(process, "q2").Single()).RootElement.GetProperty("response").GetProperty("response").GetProperty("behavior").GetString().ShouldBe("deny");

        // The turn ends with the prompt unanswered: claude waits on nothing now, so its card goes too.
        process.Emit(ResultLine);
        await UntilAsync(() => StreamChat().Page(-1).Asks is null, "the unanswered prompt to be called off");
        _state.Cards(openOnly: true).Where(c => c.Kind == "watchtower-ask").ShouldBeEmpty();
        _state.Cards().Single(c => c.Subject == Watchtower.AskSubject("outside")).Outcome.ShouldBe("cancelled");
    }
}
