using System.Text.RegularExpressions;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// Uncommitted work is committed, never dropped by default (the owner, 2026-11-03: "always commit, don't drop unless it's garbage and
/// someone verified that"). No text the node says to a worker or the person tells them to drop files, and the cards that used to
/// (a worker stopped with files, a refused finish or landing) say commit.
/// </summary>
public sealed partial class CommitNotDropTests
{
    [GeneratedRegex(@"\bor drop\b|\bdrop them\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrDrop();

    private static string NodeSources()
    {
        // The repo's src folder: the one holding Pilot.slnx, found by walking up from the test's directory.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pilot.slnx")))
            {
                return Path.Combine(dir.FullName, "Pilot.Node");
            }
        }
        throw new InvalidOperationException("Pilot.slnx was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void NoSourceOfTheNodeTellsAnyoneToDropFiles()
    {
        var root = NodeSources();
        var found = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetRelativePath(root, f), Line: i + 1, Text: line)))
            // Whole words: "or dropped" (a queue entry) is no word to a worker about its files.
            .Where(l => OrDrop().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        found.ShouldBeEmpty("the node says commit, never drop: " + string.Join("\n", found));
    }

    [Fact]
    public void TheTableSummaryOfAStoppedWorkerSaysCommitAndNamesNoDrop()
    {
        var summary = CardSummaries.For(StopGate.Kind, "flight/COD54/parser", "COD54: a session stopped", []);

        summary.ShouldContain("told once to commit");
        summary.ShouldNotContain("drop", Case.Insensitive);
    }

    [Fact]
    public void TheSummaryOfAStoppedWorkerNamesTheFilesOrHowManyPlusTheFirstFew()
    {
        CardSummaries.Usable(StopGate.Summary("COD54", ["?? notes.txt"])).ShouldBeTrue();
        StopGate.Summary("COD54", ["?? notes.txt"]).ShouldContain("commit notes.txt and stopped again with it still there");

        var many = StopGate.Summary("COD54", ["?? src/a/b/one.cs", " M two.cs", "A  three.cs", "?? four.cs", "?? five.cs"]);
        many.ShouldContain("5 files, one.cs, two.cs, three.cs and more");
        // Names only: a path of three folders would read as a log and the summary would be thrown away.
        CardSummaries.Usable(many).ShouldBeTrue();
        many.Length.ShouldBeLessThanOrEqualTo(240);
    }

    [Fact]
    public void TheClosedWithWorkSummaryOffersANewPrNotADrop()
    {
        var summary = CardSummaries.For(PrChecks.ClosedWithWork, "flight/COD54", "t", []);

        summary.ShouldContain("open a new one for that work");
        summary.ShouldNotContain("drop", Case.Insensitive);
    }

    [Fact]
    public void AReviewPassedWithNotesSaysInPlainWordsWhatTheNotesAreAndThatThereIsNothingToDo()
    {
        var summary = CardSummaries.ReviewNotes("COD54", ["rename `x` to `count`", "a comment is stale"]);

        summary.ShouldContain("passed with 2 small notes");
        summary.ShouldContain("do not block the merge");
        summary.ShouldContain("rename `x` to `count`");
        summary.ShouldContain("nothing for you to do");
        // The node's own machinery is no part of it.
        summary.ShouldNotContain("leg goes round");
        summary.ShouldNotContain("no leg");
        CardSummaries.Usable(summary).ShouldBeTrue();
        summary.Length.ShouldBeLessThanOrEqualTo(240);

        CardSummaries.ReviewNotes("COD54", ["one nit"]).ShouldContain("1 small note that does not block");
        // A long first note is cut so the sentence fits the panel.
        CardSummaries.ReviewNotes("COD54", [new string('x', 300)]).Length.ShouldBeLessThanOrEqualTo(240);
    }

    [Fact]
    public void TheNotLandedSummaryNamesTheRefusalWithoutThePlanesFolderOrAPointerToTheTitle()
    {
        var plane = Path.Combine(Path.GetTempPath(), "planes", "COD54");
        var summary = CardSummaries.NotLanded("COD54", 59, $"COD54 merged at 1a2b3c4d, but {plane} holds changes not committed; commit them, then land it", plane);

        summary.ShouldContain("PR #59 of COD54 merged on GitHub");
        summary.ShouldContain("its plane holds changes not committed");
        summary.ShouldNotContain("1a2b3c4d");
        summary.ShouldNotContain(plane);
        summary.ShouldNotContain("title says why");
        summary.Length.ShouldBeLessThanOrEqualTo(240);
        CardSummaries.Usable(summary).ShouldBeTrue();
    }
}
