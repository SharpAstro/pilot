using System.Text.Json;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>Every sha the node shows is information (#691): 9 characters, a link to its commit, after the words that say what it is.</summary>
public sealed class CommitRefsTests
{
    private const string Sha = "bc32366a1f0d9e8c7b6a5f4e3d2c1b0a99887766";

    [Fact]
    public void AShaShowsAsNineCharacters()
    {
        CommitRefs.Short(Sha).ShouldBe("bc32366a1");
        CommitRefs.Short("abc").ShouldBe("abc");
    }

    [Fact]
    public void TheUrlIsTheCommitPageOfTheReposOwnerAndName()
    {
        CommitRefs.Url("SharpAstro/Codecs", Sha).ShouldBe($"https://github.com/SharpAstro/Codecs/commit/{Sha}");
        CommitRefs.RepoOf("https://github.com/SharpAstro/Codecs/pull/64").ShouldBe("SharpAstro/Codecs");
        CommitRefs.RepoOf("https://example.org/x").ShouldBeNull();
    }

    [Fact]
    public void OnlyShasMixingDigitsAndLettersAreNamedAndOnlyWhenTheRepoIsKnown()
    {
        var named = CommitRefs.Named("o/r", ["up to date with master (bc32366a1 · reviewed 1a149a1e0), 123456789 and abcdefabc, extraordinary", null]);
        named.Keys.ShouldBe(["bc32366a1", "1a149a1e0"]);
        named["bc32366a1"].ShouldBe("https://github.com/o/r/commit/bc32366a1");
        CommitRefs.Named(null, ["bc32366a1"]).ShouldBeEmpty();
    }

    [Fact]
    public void ACardCarriesTheCommitsItsTextNamesAndTheWireRoundTripsThem()
    {
        NodeWire.Version.ShouldBeGreaterThanOrEqualTo(9);
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        var card = state.RaiseCard(CardBin.ForYou, "pr-ready", "flight/COD57", "COD57 is up to date with master", $"master merged in since review ({CommitRefs.Short(Sha)})",
            link: "https://github.com/SharpAstro/Codecs/pull/64");

        card.Commits.ShouldBe(new Dictionary<string, string> { ["bc32366a1"] = $"https://github.com/SharpAstro/Codecs/commit/bc32366a1" });

        var json = JsonSerializer.Serialize(card, PilotJsonContext.Default.CardRow);
        JsonSerializer.Deserialize(json, PilotJsonContext.Default.CardRow)!.Commits.ShouldBe(card.Commits);
        // A card an older node wrote has no field and reads with none.
        var old = JsonSerializer.Deserialize(json.Replace("\"commits\"", "\"x\""), PilotJsonContext.Default.CardRow)!;
        old.Commits.ShouldBeEmpty();
    }
}
