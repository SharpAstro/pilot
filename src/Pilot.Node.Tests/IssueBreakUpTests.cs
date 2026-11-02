using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A splitter that answers what the test set, or fails, and keeps what it was asked.</summary>
internal sealed class FakeIssueSplitter : IIssueSplitter
{
    public BreakUpPart[] Answer { get; set; } =
    [
        new("Draw the rings", "the ring mesh", []),
        new("Shadow the planet", "cast onto the planet", ["Soft or hard edge?"]),
    ];

    public string? Fails { get; set; }

    public List<(IdeaText Issue, int? Into)> Asked { get; } = [];

    public Task<BreakUpPart[]> SplitAsync(IdeaText issue, int? into, string repoPath, CancellationToken cancellationToken)
    {
        Asked.Add((issue, into));
        return Fails is { } why ? throw new FlightException(why) : Task.FromResult(Answer);
    }
}

/// <summary>break-up-issue and accept-break-up: a proposal that writes nothing, and accepting it, through a fake splitter.</summary>
public sealed class IssueBreakUpTests : IssueBoardFixture
{
    private static string Issue(int number) => IssueIds.Target("pilot", number);

    private async Task<BreakUpProposal> Propose(string target, params string[] args)
    {
        var result = await Run(Verbs.BreakUpIssue, target, args);
        result.Ok.ShouldBeTrue(result.Error);
        return result.BreakUp.ShouldNotBeNull();
    }

    private Task<CommandResult> Accept(string target, BreakUpProposal proposal) => Run(Verbs.AcceptBreakUp, target, proposal.UpdatedAt.ToString("O"));

    private MilestoneIssue Now(int number) => GitHub.MilestoneIssuesOf[Path.TrimEndingDirectorySeparator(Path.GetFullPath(Repo))].Single(i => i.Number == number);

    [Fact]
    public async Task AProposalCarriesThePartsAndWritesNothingToGitHub()
    {
        var proposal = await Propose(Issue(2), "2");

        proposal.Parts.Select(p => p.Title).ShouldBe(["Draw the rings", "Shadow the planet"]);
        (proposal.CurrentTitle, proposal.Milestone).ShouldBe(("Fix the GPU crash", "v1"));
        Splitter.Asked.Single().Into.ShouldBe(2);
        GitHub.Edits.ShouldBeEmpty();
        GitHub.IssuesCreated.ShouldBeEmpty();
        GitHub.Moves.ShouldBeEmpty();
        GitHub.PrioritySets.ShouldBeEmpty();
    }

    [Fact]
    public async Task AcceptingKeepsTheOriginalWithTheFirstPartAndMakesTheOthersInItsMilestoneWithItsPriority()
    {
        GitHub.HasPriorityField = true;
        GitHub.IssuePriorities[2] = IssuePriority.High;
        Splitter.Answer = [.. Splitter.Answer, new BreakUpPart("Glow", "soft glow", [])];
        var proposal = await Propose(Issue(2));

        var result = await Accept(Issue(2), proposal);

        result.Ok.ShouldBeTrue(result.Error);
        result.Made.ShouldBe("#8 #9");
        var original = Now(2);
        original.Title.ShouldBe("Draw the rings");
        original.Body.ShouldContain("the ring mesh");
        original.Body.ShouldContain("Split from #2; siblings #8, #9");
        GitHub.IssuesCreated.Select(c => c.Title).ShouldBe(["Shadow the planet", "Glow"]);
        var second = Now(8);
        (second.Milestone, second.Title).ShouldBe(("v1", "Shadow the planet"));
        second.Body.ShouldContain("Soft or hard edge?");
        second.Body.ShouldContain("Split from #2; siblings #9");
        Now(9).Body.ShouldContain("Split from #2; siblings #8");
        Now(9).Milestone.ShouldBe("v1");
        GitHub.PrioritySets.ShouldBe([(8, IssuePriority.High), (9, IssuePriority.High)]);
        State.Issues().Select(i => i.Number).ShouldContain(8);
    }

    [Fact]
    public async Task AnIdeaTargetWorksAsWellAsAnIssueTarget()
    {
        var target = IdeaIds.Target("pilot", 1);
        var proposal = await Propose(target);

        var result = await Accept(target, proposal);

        result.Ok.ShouldBeTrue(result.Error);
        Now(1).Title.ShouldBe("Draw the rings");
        var made = Now(int.Parse(result.Made!.TrimStart('#')));
        (made.Milestone, made.Body).ShouldBe((IdeaIds.Milestone, made.Body));
        made.Body.ShouldContain("Split from #1");
    }

    [Fact]
    public async Task AClosedIssueIsRefused()
    {
        var result = await Run(Verbs.BreakUpIssue, Issue(7));

        result.Ok.ShouldBeFalse();
        result.Error.ShouldContain("closed");
        Splitter.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIssueAFlightIsFlyingIsRefusedAndOneThatLandedIsNot()
    {
        State.PutFlight(new FlightRow
        {
            Code = "PIL2", Repo = "pilot", RepoPath = Repo, Issue = 2, Title = "Fix the GPU crash", Branch = "flight/pil2", Worktree = Path.Combine(Repo, "plane"),
            Base = "origin/main", State = FlightState.Flying, CreatedAt = DateTimeOffset.UtcNow,
        });

        var result = await Run(Verbs.BreakUpIssue, Issue(2));

        result.Ok.ShouldBeFalse();
        result.Error.ShouldContain("PIL2");
        Splitter.Asked.ShouldBeEmpty();

        State.PutFlight(State.Flights().Single() with { State = FlightState.Landed });
        (await Run(Verbs.BreakUpIssue, Issue(2))).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AStaleUpdatedAtIsRefusedAndNothingIsWritten()
    {
        var proposal = await Propose(Issue(2));
        GitHub.ChangeElsewhere(Repo, 2, i => i with { Body = "someone edited" });

        var result = await Accept(Issue(2), proposal);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldContain("changed");
        GitHub.Edits.ShouldBeEmpty();
        GitHub.IssuesCreated.ShouldBeEmpty();
    }

    [Fact]
    public async Task AcceptingWithoutAProposalIsRefused()
    {
        var result = await Run(Verbs.AcceptBreakUp, Issue(2), DateTimeOffset.UtcNow.ToString("O"));

        result.Ok.ShouldBeFalse();
        result.Error.ShouldContain("break-up-issue first");
    }

    [Fact]
    public async Task ASplitterThatFailsGivesNoProposalAndKeepsNothing()
    {
        Splitter.Fails = "no luck";

        var result = await Run(Verbs.BreakUpIssue, Issue(2));

        result.Ok.ShouldBeFalse();
        result.Error.ShouldContain("no luck");
        (await Run(Verbs.AcceptBreakUp, Issue(2), DateTimeOffset.UtcNow.ToString("O"))).Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task APartCountBelowTwoIsRefused()
    {
        var result = await Run(Verbs.BreakUpIssue, Issue(2), "1");

        result.Ok.ShouldBeFalse();
        Splitter.Asked.ShouldBeEmpty();
    }

    [Fact]
    public void ThePromptNamesTheCountAndTheIssueAndKeepsImages()
    {
        var plain = ClaudeIssueSplitter.Prompt(new IdeaText("Ring work", "do shadows"), 3);
        plain.ShouldContain("3 distinct parts");
        plain.ShouldContain("Ring work");
        plain.ShouldContain("\"parts\"");
        plain.ShouldNotContain("images");

        var open = ClaudeIssueSplitter.Prompt(new IdeaText("Ring work", "![a](https://x.test/a.png)"), null);
        open.ShouldContain("two or more distinct parts");
        open.ShouldContain("images");
    }

    [Fact]
    public void ReadTakesPartsFromAFencedAnswerAndNeedsTwo()
    {
        var parts = ClaudeIssueSplitter.Read("```json\n{\"parts\": [{\"title\": \" A \", \"body\": \"a\", \"open_questions\": [\"why?\"]}, {\"title\": \"B\"}]}\n```").ShouldNotBeNull();

        parts.Select(p => p.Title).ShouldBe(["A", "B"]);
        parts[0].OpenQuestions.ShouldBe(["why?"]);
        parts[1].Body.ShouldBe("");
        ClaudeIssueSplitter.Read("{\"parts\": [{\"title\": \"only\"}]}").ShouldBeNull();
        ClaudeIssueSplitter.Read("{\"parts\": [{\"title\": \"A\"}, {\"body\": \"x\"}]}").ShouldBeNull();
        ClaudeIssueSplitter.Read("no json").ShouldBeNull();
    }

    [Fact]
    public void AnImageNoPartHoldsIsPutBackInTheFirstPart()
    {
        const string Image = "![shot](https://x.test/shot.png)";
        var parts = ClaudeIssueSplitter.KeepImages($"text\n\n{Image}", [new BreakUpPart("A", "a", []), new BreakUpPart("B", "b", [])]);

        parts[0].Body.ShouldContain(Image);
        parts[1].Body.ShouldBe("b");

        var kept = ClaudeIssueSplitter.KeepImages($"text\n\n{Image}", [new BreakUpPart("A", "a", []), new BreakUpPart("B", Image, [])]);
        kept[0].Body.ShouldBe("a");
        kept[1].Body.ShouldBe(Image);
    }
}
