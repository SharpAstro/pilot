using System.CommandLine;
using System.Text.Json;
using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>mark-design (PIL1152): the needs-design mark is a label on the idea's issue (made in the repo when missing), read back into the rows and shown by pilot ideas.</summary>
public sealed class IdeaDesignMarkTests : IssueBoardFixture
{
    private static string Idea(int number) => IdeaIds.Target("pilot", number);

    [Fact]
    public async Task MarkAddsTheLabelAndCreatesItWhenTheRepoHasNone()
    {
        var result = await Run(Verbs.MarkDesign, Idea(1));

        result.Ok.ShouldBeTrue(result.Error);
        GitHub.IssueLabels[1].Keys.ShouldContain(PilotMarks.NeedsDesign);
        GitHub.LabelsCreated.Keys.ShouldContain(PilotMarks.NeedsDesign);
        PilotMarks.NeedsDesign.StartsWith(PilotMarks.LabelPrefix, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task OffTakesTheLabelOffAndTheRowReadsFalse()
    {
        await Run(Verbs.MarkDesign, Idea(1));
        State.Ideas().Single(i => i.Id == "I1").NeedsDesign.ShouldBeTrue();

        var result = await Run(Verbs.MarkDesign, Idea(1), Verbs.PinOff);

        result.Ok.ShouldBeTrue(result.Error);
        GitHub.IssueLabels[1].Keys.ShouldNotContain(PilotMarks.NeedsDesign);
        State.Ideas().Single(i => i.Id == "I1").NeedsDesign.ShouldBeFalse();
    }

    [Fact]
    public async Task OnlyTheMarkedIdeaReadsNeedsDesign()
    {
        await Run(Verbs.MarkDesign, Idea(1));

        State.Ideas().Single(i => i.Id == "I1").NeedsDesign.ShouldBeTrue();
        State.Ideas().Single(i => i.Id == "I4").NeedsDesign.ShouldBeFalse();
    }

    [Fact]
    public async Task MarkingAMarkedIdeaAgainWritesNothing()
    {
        await Run(Verbs.MarkDesign, Idea(1));
        GitHub.LabelsCreated.Clear();
        var before = GitHub.IssueLabels[1].Count;

        (await Run(Verbs.MarkDesign, Idea(1))).Ok.ShouldBeTrue();
        (await Run(Verbs.MarkDesign, Idea(4), Verbs.PinOff)).Ok.ShouldBeTrue();

        GitHub.LabelsCreated.Keys.ShouldBeEmpty();
        GitHub.IssueLabels[1].Count.ShouldBe(before);
    }

    [Fact]
    public async Task AClosedIdeaOrABadTargetOrArgumentIsRefused()
    {
        (await Run(Verbs.MarkDesign, Idea(7))).Error.ShouldNotBeNull().ShouldContain("closed");
        (await Run(Verbs.MarkDesign, Idea(99))).Error.ShouldNotBeNull().ShouldContain("no issue #99");
        (await Run(Verbs.MarkDesign, "flight/PIL1")).Error.ShouldNotBeNull().ShouldContain("idea target");
        (await Run(Verbs.MarkDesign, Idea(1), "sideways")).Error.ShouldNotBeNull().ShouldContain("off");
    }

    [Fact]
    public void TheCliParsesDesignAndDesignNo()
    {
        PilotCommand.ReadIdea(["I381", "design"]).ShouldBe(new PilotCommand.IdeaCall(null, 381, "design", ""));
        PilotCommand.ReadIdea(["tianwen", "I12", "design"]).ShouldBe(new PilotCommand.IdeaCall("tianwen", 12, "design", ""));
        Should.Throw<PilotCommand.UsageException>(() => PilotCommand.ReadIdea(["I381", "design", "now"])).Message.ShouldContain("--no");
        PilotCommand.ReadIdea(["I381", "is", "wrong"]).Action.ShouldBeNull();
    }

    [Fact]
    public void ThePilotIdeasPrintoutShowsTheMarkOnMarkedRows()
    {
        var rows = new[]
        {
            new IdeaRow { Id = "I2", Repo = "pilot", Title = "Plain", State = IdeaState.Noted, Age = "1d", NeedsDesign = false },
            new IdeaRow { Id = "I1", Repo = "pilot", Title = "Rings", State = IdeaState.Noted, Age = "2d", NeedsDesign = true },
        };

        var text = PilotCommand.Ideas(rows, all: false, repo: null);

        text.ShouldContain("Rings  [needs design]");
        text.Split('\n').Single(l => l.Contains("Plain", StringComparison.Ordinal)).ShouldNotContain("needs design");
    }

    [Fact]
    public void TheMcpIdeaToolReachesTheDesignVerbWithTheNoOption()
    {
        var root = PilotCommand.Build(Path.GetTempPath(), TextWriter.Null, TextWriter.Null, "mcp", agent: true);
        var (path, chain) = McpServer.Tools(root)["idea"];
        var arguments = JsonDocument.Parse("""{"text":["I12","design"],"no":true}""").RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var parsed = root.Parse(McpServer.Spell(path, chain, arguments));

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandResult.Command.Name.ShouldBe("idea");
        PilotCommand.ReadIdea(parsed.GetValue<string[]>("text") ?? []).Action.ShouldBe("design");
        parsed.GetValue<bool>("--no").ShouldBeTrue();
    }

    [Fact]
    public void TheRowMemberIsDefaultedSoAnOlderStoreReadsFalse()
    {
        // An older store wrote no needsDesign: a row serialised without the mark (it is omitted when false) reads back false.
        var older = JsonSerializer.Serialize(new IdeaRow { Id = "I1", Repo = "pilot", Title = "t", State = IdeaState.Noted }, PilotJsonContext.Default.IdeaRow);

        older.ShouldNotContain("eedsDesign");
        JsonSerializer.Deserialize(older, PilotJsonContext.Default.IdeaRow).ShouldNotBeNull().NeedsDesign.ShouldBeFalse();
        var marked = JsonSerializer.Serialize(new IdeaRow { Id = "I1", Repo = "pilot", Title = "t", State = IdeaState.Noted, NeedsDesign = true }, PilotJsonContext.Default.IdeaRow);
        JsonSerializer.Deserialize(marked, PilotJsonContext.Default.IdeaRow).ShouldNotBeNull().NeedsDesign.ShouldBeTrue();
    }
}
