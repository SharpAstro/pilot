using System.Diagnostics;
using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A head described in words (pilot #691): identical, up to date with the base, newer by N commits, behind the base. Built from a
/// temp git repo; the words lead and the shas follow.
/// </summary>
[Trait("Category", "Slow")]
public sealed class HeadWordsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hw-" + Guid.NewGuid().ToString("N")[..8]);

    public HeadWordsTests()
    {
        Directory.CreateDirectory(_dir);
        G("init", "-q", "-b", "main");
        Write("a.txt", "a\n");
        Commit("start");
        G("checkout", "-q", "-b", "flight");
        Write("code.cs", "class C\n{\n    int x = 1;\n}\n");
        Commit("the flight's code");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(_dir, recursive: true);
    }

    private string G(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-c", "user.email=t@example.com", "-c", "user.name=t" }.Concat(args))
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}{output}");
    }

    private void Write(string file, string text) => File.WriteAllText(Path.Combine(_dir, file), text);

    private void Commit(string message)
    {
        G("add", "-A");
        G("commit", "-q", "-m", message);
    }

    private Task<HeadWords> Describe(string reviewed, string head) =>
        HeadWords.DescribeAsync(new GitCli(), _dir, "main", "main", reviewed, head, Ct);

    private string Head => G("rev-parse", "HEAD");

    [Fact]
    public async Task TheReviewedHeadIsIdenticalAndSaysNothingMore()
    {
        var head = Head;

        var words = await Describe(head, head);

        words.Relation.ShouldBe(HeadRelation.Identical);
        words.Repinnable.ShouldBeTrue();
        words.Words.ShouldBe("identical to what was reviewed");
    }

    [Fact]
    public async Task ABaseMergedInSinceTheReviewIsUpToDateWithTheBaseWordsFirstShasAfter()
    {
        var reviewed = Head;
        G("checkout", "-q", "main");
        Write("base.txt", "base\n");
        Commit("base moves");
        G("checkout", "-q", "flight");
        G("merge", "--no-ff", "-q", "-m", "merge of main", "main");
        var head = Head;

        var words = await Describe(reviewed, head);

        words.Relation.ShouldBe(HeadRelation.UpToDateWithBase);
        words.Repinnable.ShouldBeTrue();
        words.Text.ShouldBe($"up to date with main: main merged in since review, code unchanged ({CommitRefs.Short(head)} · reviewed {CommitRefs.Short(reviewed)})");
        words.Text.IndexOf("up to date", StringComparison.Ordinal).ShouldBeLessThan(words.Text.IndexOf(CommitRefs.Short(head), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACodeChangeIsNewerByCommitsWithTheirSubjectsAndAsksForAReview()
    {
        var reviewed = Head;
        Write("code.cs", "class C\n{\n    int x = 2;\n}\n");
        Commit("Fix the parser");
        Write("other.cs", "class D { }\n");
        Commit("Add D");

        var words = await Describe(reviewed, Head);

        words.Relation.ShouldBe(HeadRelation.Newer);
        words.Repinnable.ShouldBeFalse();
        words.Words.ShouldBe("newer than the review: 2 commits (Add D; Fix the parser)");
    }

    [Fact]
    public async Task ADocumentationChangeIsNewerButItsCodeIsUnchangedSoItIsRepinnable()
    {
        var reviewed = Head;
        Write("CHANGELOG.md", "one\n\ntwo\n");
        Commit("CHANGELOG blank line");

        var words = await Describe(reviewed, Head);

        words.Relation.ShouldBe(HeadRelation.Newer);
        words.CodeUnchanged.ShouldBeTrue();
        words.Repinnable.ShouldBeTrue();
        words.Words.ShouldContain("CHANGELOG blank line");
    }

    [Fact]
    public async Task AWhitespaceOnlyChangeIsRepinnableAndALineOfCodeIsNot()
    {
        var reviewed = Head;
        Write("code.cs", "class C\n{\n        int x = 1;   \n}\n");
        Commit("indent");
        var spaced = Head;
        Write("code.cs", "class C\n{\n        int x = 3;\n}\n");
        Commit("change");

        (await Describe(reviewed, spaced)).Repinnable.ShouldBeTrue();
        (await Describe(reviewed, Head)).Repinnable.ShouldBeFalse();
    }

    [Fact]
    public async Task SpaceInsideALineAndIndentationWhereItIsMeaningAreChangesOfCode()
    {
        Write("code.cs", "class C\n{\n    string s = \"a b\";\n}\n");
        Commit("a string");
        var reviewed = Head;
        Write("code.cs", "class C\n{\n    string s = \"ab\";\n}\n");
        Commit("space inside a string");
        (await Describe(reviewed, Head)).Repinnable.ShouldBeFalse();

        Write("run.py", "if x:\n    y()\nz()\n");
        Commit("py");
        var py = Head;
        Write("run.py", "if x:\n    y()\n    z()\n");
        Commit("py indent");
        (await Describe(py, Head)).Repinnable.ShouldBeFalse();

        var indented = Head;
        Write("run.py", "if x:\n    y()   \n    z()\n\n");
        Commit("py trailing space and a blank line");
        (await Describe(indented, Head)).Repinnable.ShouldBeTrue();
    }

    [Theory]
    [InlineData("src/run.py", true)]
    [InlineData(".github/workflows/dotnet.yml", true)]
    [InlineData("Makefile", true)]
    [InlineData("src/Pilot.Node/Flights/HeadWords.cs", false)]
    public void IndentationMattersWhereItIsMeaning(string path, bool matters) =>
        HeadWords.IndentationMatters(path).ShouldBe(matters);

    [Fact]
    public async Task AHeadBehindItsBaseSaysByHowManyCommitsAndANotBehindOneSaysNothing()
    {
        var head = Head;
        (await HeadWords.BehindAsync(new GitCli(), _dir, "main", "main", head, Ct)).ShouldBeNull();
        G("checkout", "-q", "main");
        Write("b.txt", "b\n");
        Commit("one");
        Write("c.txt", "c\n");
        Commit("two");
        G("checkout", "-q", "flight");

        (await HeadWords.BehindAsync(new GitCli(), _dir, "main", "main", head, Ct)).ShouldBe("behind main by 2 commits");
    }

    [Fact]
    public async Task ACommitTheRepoLacksIsUnknownNotAnError()
    {
        var words = await Describe(new string('a', 40), Head);

        words.Relation.ShouldBe(HeadRelation.Unknown);
        words.Repinnable.ShouldBeFalse();
    }
}
