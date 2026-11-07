using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests.Sessions;

/// <summary>A permission prompt is said by the call it asks leave for, with the tool in brackets.</summary>
public sealed class PermissionAskTests
{
    private const string Plane = @"C:\repos\pilot\.claude\worktrees\pil843";

    [Fact]
    public void EnterWorktreeIntoTheLegsOwnPlaneSaysSo()
    {
        PermissionAsks.Describe("EnterWorktree", Plane, Plane).ShouldBe("switch its folder to its own plane (EnterWorktree)");
        // Slashes, case and a trailing separator do not tell one folder from another.
        PermissionAsks.Describe("EnterWorktree", "c:/repos/pilot/.claude/worktrees/PIL843/", Plane).ShouldBe("switch its folder to its own plane (EnterWorktree)");
    }

    [Fact]
    public void EnterWorktreeIntoAnotherFolderNamesIt()
    {
        PermissionAsks.Describe("EnterWorktree", @"C:\repos\other", Plane).ShouldBe(@"switch its folder to C:\repos\other (EnterWorktree)");
        PermissionAsks.Describe("EnterWorktree", @"C:\repos\other").ShouldBe(@"switch its folder to C:\repos\other (EnterWorktree)");
        PermissionAsks.Describe("EnterWorktree", null).ShouldBe("switch its folder to a new worktree (EnterWorktree)");
    }

    [Theory]
    [InlineData("Bash", "git push origin main", "run: git push origin main (Bash)")]
    [InlineData("PowerShell", "Get-ChildItem", "run: Get-ChildItem (PowerShell)")]
    [InlineData("Edit", "src/A.cs", "edit src/A.cs (Edit)")]
    [InlineData("Write", "src/B.cs", "edit src/B.cs (Write)")]
    [InlineData("NotebookEdit", "n.ipynb", "edit n.ipynb (NotebookEdit)")]
    [InlineData("Read", "src/C.cs", "read src/C.cs (Read)")]
    [InlineData("Glob", "**/*.cs", "read **/*.cs (Glob)")]
    [InlineData("Grep", "TODO", "read TODO (Grep)")]
    [InlineData("WebFetch", "https://example.org/a", "fetch https://example.org/a (WebFetch)")]
    [InlineData("WebSearch", "dotnet 10 release", "search the web for dotnet 10 release (WebSearch)")]
    public void EachToolFamilySaysWhatItWouldDo(string tool, string input, string expected) =>
        PermissionAsks.Describe(tool, input).ShouldBe(expected);

    [Fact]
    public void AnyOtherToolIsNamedWithItsSummary()
    {
        PermissionAsks.Describe("mcp__pilot__status", "all nodes").ShouldBe("use mcp__pilot__status: all nodes");
        PermissionAsks.Describe("Skill", null).ShouldBe("use Skill");
        // A command tool with no command known falls back to the tool alone.
        PermissionAsks.Describe("Bash", null).ShouldBe("use Bash");
    }

    [Fact]
    public void ALongCommandIsCutButTheToolInBracketsStaysWhole()
    {
        var phrase = PermissionAsks.Describe("Bash", new string('x', 400));

        phrase.ShouldEndWith("... (Bash)");
        phrase.Length.ShouldBeLessThan(130);
    }

    [Fact]
    public void AskingTextIsReadBackAsTheSamePhraseAndAQuestionIsNone()
    {
        PermissionAsks.FromAsking("Bash: git push origin main").ShouldBe("run: git push origin main (Bash)");
        PermissionAsks.FromAsking("EnterWorktree: " + Plane, Plane).ShouldBe("switch its folder to its own plane (EnterWorktree)");
        PermissionAsks.FromAsking("Skill").ShouldBe("use Skill");
        PermissionAsks.FromAsking("AskUserQuestion: Which one?").ShouldBeNull();
        PermissionAsks.FromAsking(null).ShouldBeNull();
        PermissionAsks.FromAsking("").ShouldBeNull();
    }

    [Fact]
    public void TheAskingLineIsTheToolAndItsSummaryAndNoneForAQuestion()
    {
        PermissionAsks.Asking("Bash", "dotnet test").ShouldBe("Bash: dotnet test");
        PermissionAsks.Asking("Skill", null).ShouldBe("Skill");
        PermissionAsks.Asking("AskUserQuestion", "Which one?").ShouldBeNull();
        PermissionAsks.Asking(null, "x").ShouldBeNull();
    }

    [Fact]
    public void NoPhraseHoldsAnEmDashOrDoubleHyphen()
    {
        foreach (var phrase in new[] { PermissionAsks.Describe("EnterWorktree", Plane, Plane), PermissionAsks.Describe("Bash", "ls"), PermissionAsks.Describe("X", null) })
        {
            phrase.ShouldNotContain("\u2014");
            phrase.ShouldNotContain("--");
        }
    }
}
