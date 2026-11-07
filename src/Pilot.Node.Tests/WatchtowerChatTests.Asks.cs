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
