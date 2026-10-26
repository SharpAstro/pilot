using System.Text.Json.Nodes;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The merge guard: a PreToolUse hook on Bash that refuses a merge made around the node, and a push to a default
/// branch, naming pilot flight merge; everything else passes. Wired user-wide by the setup checklist's Merge guard check.</summary>
public sealed class MergeHookTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2m-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Input(string command, string tool = "Bash") =>
        new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = new JsonObject { ["command"] = command }, ["cwd"] = "/work/repo/.claude/worktrees/plane" }.ToJsonString();

    [Theory]
    [InlineData("/work/repo/.claude/worktrees/pil12", true)]
    [InlineData(@"C:\r\pilot\.claude\worktrees\agent-1", true)]
    [InlineData(@"C:\Users\me\AppData\Local\pilot2\watchtower", true)]
    [InlineData(@"C:\r\pilot", false)]
    [InlineData("/work/repo", false)]
    public void OnlyASessionTheNodeStartedIsGuarded(string folder, bool guarded) => MergeGuard.Guarded(folder).ShouldBe(guarded);

    [Fact]
    public void ThePersonsOwnSessionMergesAsTheySay()
    {
        var input = new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = "Bash", ["tool_input"] = new JsonObject { ["command"] = "gh pr merge 249 --squash" }, ["cwd"] = "/work/repo" }.ToJsonString();
        MergeGuard.Decide(input, Git("flight/x")).ShouldBeNull();
    }

    /// <summary>A repo whose default branch is main and whose checkout is on <paramref name="branch"/>.</summary>
    private static MergeGuard.GitQuery Git(string? branch, string? defaultBranch = "origin/main") => (_, args) =>
        args.Contains("HEAD") && args.Count == 4 ? branch : args.Contains("refs/remotes/origin/HEAD") ? defaultBranch : null;

    private static string? Decide(string command, string? branch = "flight/x", string? defaultBranch = "origin/main") =>
        MergeGuard.Decide(Input(command), Git(branch, defaultBranch));

    [Theory]
    [InlineData("gh pr merge 1")]
    [InlineData("gh pr merge 12 --squash --auto")]
    [InlineData("gh pr -R o/r merge 3")]
    [InlineData("gh.exe pr merge 1")]
    [InlineData("""gh api graphql -f query='mutation{enqueuePullRequest(input:{pullRequestId:"X"}){clientMutationId}}'""")]
    [InlineData("""gh api graphql -f query="mutation { mergePullRequest(input:{pullRequestId:\"X\"}) { clientMutationId } }" """)]
    [InlineData("gh api -X PUT repos/o/r/pulls/3/merge")]
    [InlineData("gh api --method PUT /repos/o/r/pulls/3/merge -f merge_method=squash")]
    [InlineData("gh api -XPUT repos/o/r/pulls/3/merge")]
    [InlineData("git push origin main")]
    [InlineData("git push origin HEAD:master")]
    [InlineData("git push -u origin +main")]
    [InlineData("git push origin HEAD:refs/heads/main")]
    [InlineData("git push origin :main")]
    [InlineData("git push --all")]
    [InlineData("git -C /work/plane push origin main")]
    // Compound lines, wrappers, subshells and nested shells: each part is read.
    [InlineData("cd /work/plane && gh pr merge 1")]
    [InlineData("gh pr view 1; gh pr merge 1")]
    [InlineData("echo done | gh pr merge 1")]
    [InlineData("GH_TOKEN=x gh pr merge 1")]
    [InlineData("sudo gh pr merge 1")]
    [InlineData("sudo -u root gh pr merge 1")]
    [InlineData("env -u CLAUDECODE gh pr merge 1")]
    [InlineData("nice -n 5 gh pr merge 1")]
    [InlineData("echo $(gh pr merge 1)")]
    [InlineData("bash -c 'git fetch && gh pr merge 1'")]
    [InlineData("eval \"gh pr merge 1\"")]
    public void AMergeOrAPushToMainIsRefusedNamingPilotFlightMerge(string command)
    {
        Decide(command).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("gh pr view 1")]
    [InlineData("gh pr create --fill")]
    [InlineData("gh pr comment 1 --body merge")]
    [InlineData("gh pr checks 1")]
    [InlineData("gh api repos/o/r/pulls/3")]
    [InlineData("gh api repos/o/r/pulls/3/merge")]
    [InlineData("gh api -X PUT repos/o/r/pulls/3/reviews")]
    [InlineData("gh api graphql -f query='query{viewer{login}}'")]
    [InlineData("git push origin flight/x")]
    [InlineData("git push -u origin HEAD")]
    [InlineData("git push origin flight/x:flight/x")]
    [InlineData("git push origin mainline")]
    [InlineData("git push")]
    [InlineData("git status && git log main..HEAD")]
    [InlineData("git merge main")]
    [InlineData("echo 'gh pr merge 1'")]
    [InlineData("grep -r \"git push origin main\" docs")]
    [InlineData("dotnet test src/Pilot.Node.Tests")]
    public void EverythingElseFromAFlightBranchPasses(string command)
    {
        Decide(command).ShouldBeNull();
    }

    [Theory]
    // gh's -R / --repo value is not the group word.
    [InlineData("gh -R o/r pr merge 1")]
    [InlineData("gh --repo o/r pr merge 1")]
    [InlineData("gh -R o/r api -X PUT repos/o/r/pulls/3/merge")]
    [InlineData("gh --repo o/r api graphql -f query='mutation{enqueuePullRequest(input:{}){x}}'")]
    // Shell keywords in front of the command.
    [InlineData("if gh pr checks 1; then gh pr merge 1; fi")]
    [InlineData("for p in 1 2; do gh pr merge $p; done")]
    [InlineData("while true; do git push origin main; done")]
    [InlineData("{ gh pr merge 1; }")]
    [InlineData("! gh pr merge 1")]
    [InlineData("if false; then :; elif true; then gh pr merge 1; else :; fi")]
    // Wrappers with a word of their own.
    [InlineData("timeout 60 gh pr merge 1")]
    [InlineData("timeout -k 5 60 gh pr merge 1")]
    [InlineData("timeout 60 git push origin main")]
    [InlineData("echo 1 | xargs gh pr merge")]
    [InlineData("echo 1 | xargs -I{} gh pr merge {}")]
    [InlineData("echo 1 | xargs -n 1 gh pr merge")]
    // A heredoc's body is not commands, but the line after it is.
    [InlineData("cat > x.md <<EOF\nnotes\nEOF\ngh pr merge 1")]
    [InlineData("cat <<'EOF' | cat\nDon't\nEOF\ngit push origin main")]
    [InlineData("cat <<-EOF\n\tbody\n\tEOF\ngh pr merge 1")]
    // Backslash-newline continues the line.
    [InlineData("gh pr \\\n merge 1")]
    [InlineData("gh pr \\\r\n  merge 1")]
    [InlineData("git push \\\n  origin main")]
    [InlineData("gh pr mer\\\nge 1")]
    public void AMergeHiddenBehindOtherShellSyntaxIsStillRefused(string command)
    {
        Decide(command).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("cat > x.md <<EOF\ngh pr merge 1\nEOF")]
    [InlineData("cat > x.md <<'EOF'\ngit push origin main\nEOF\n")]
    [InlineData("gh pr create --body-file - <<EOF\ntitle\ngh pr merge 1\nEOF")]
    [InlineData("git commit -F - <<-EOF\n\tgit push origin main\n\tEOF")]
    [InlineData("cat <<A <<B\ngh pr merge 1\nA\ngit push origin main\nB")]
    [InlineData("echo $((1<<2))")]
    public void AHeredocBodyIsNotACommand(string command)
    {
        Decide(command).ShouldBeNull();
    }

    [Theory]
    // Claude's usual commit and PR forms: a command substitution in double quotes with a heredoc in it, whose body has an odd number
    // of double quotes and lines that quote a merge or a push; none of it is a command.
    [InlineData("git commit -m \"$(cat <<'EOF'\nUse a 5\" display\nthen run `gh pr merge 1` or git push origin main\nEOF\n)\"")]
    [InlineData("git commit -m \"$(cat <<'EOF'\nfix (the parser\n\ngh pr merge 1\ngit push origin main\n\nCo-Authored-By: x\nEOF\n)\"")]
    [InlineData("git add -A && git commit -m \"$(cat <<'EOF'\nA 5\" display\ngh pr merge 1\nEOF\n)\" && git push origin flight/x")]
    [InlineData("gh pr create --title t --body \"$(cat <<'EOF'\nA 5\" display\n`gh pr merge 1` and `git push origin main`\nEOF\n)\"")]
    [InlineData("gh pr create --body \"$(cat <<EOF\nhe said \"hi\n$ gh pr merge 1\nEOF\n)\"")]
    // Backticks in double quotes hold text of their own the same way.
    [InlineData("git commit -m \"a 5\\\" display `echo ok`\"")]
    public void ACommitOrPrBodyInACommandSubstitutionIsNotCommands(string command)
    {
        Decide(command).ShouldBeNull();
    }

    [Theory]
    // What runs in a command substitution in double quotes is still read.
    [InlineData("echo \"$(gh pr merge 1)\"")]
    [InlineData("echo \"done: $(git push origin main)\"")]
    [InlineData("echo \"`gh pr merge 1`\"")]
    [InlineData("git commit -m \"$(cat <<'EOF'\nbody\nEOF\n)\" && gh pr merge 1")]
    [InlineData("git commit -m \"$(cat <<'EOF'\nA 5\" display\nEOF\n)\"; git push origin main")]
    [InlineData("echo \"$(cat <<'EOF'\nbody\nEOF\ngh pr merge 1\n)\"")]
    public void ACommandRunInsideDoubleQuotesIsRefused(string command)
    {
        Decide(command).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Fact]
    public void ARedirectionIsNotARefspecSoAPlainPushFromMainIsStillRefused()
    {
        foreach (var command in new[] { "git push origin 2>&1", "git push origin > /dev/null", "git push origin >/dev/null 2>&1", "git push origin 2> err.txt", "git push origin &> out.log", "git push origin >> log", "git push > /dev/null", "git push 2>&1 | tail -3" })
        {
            Decide(command, branch: "main").ShouldNotBeNull($"{command} from main").ShouldContain("pilot flight merge");
            Decide(command, branch: "flight/x").ShouldBeNull($"{command} from a flight branch");
        }
        // The refspec after a redirection still counts.
        Decide("git push origin 2>&1 main", branch: "flight/x").ShouldNotBeNull();
        Decide("git push origin > log main", branch: "flight/x").ShouldNotBeNull();
        Decide("git push origin 2>&1 flight/x", branch: "main").ShouldBeNull();
    }

    [Fact]
    public void ATagsOnlyPushMovesNoBranchSoItPassesFromMain()
    {
        foreach (var command in new[] { "git push --tags", "git push origin --tags", "git push --tags origin", "git push origin v1.2.0", "git push origin refs/tags/v1.2.0" })
        {
            Decide(command, branch: "main").ShouldBeNull(command);
        }
        // A branch refspec beside --tags is still a push to it, and --follow-tags pushes the current branch along with the tags.
        Decide("git push origin --tags main", branch: "flight/x").ShouldNotBeNull().ShouldContain("pilot flight merge");
        Decide("git push --tags origin HEAD:master", branch: "flight/x").ShouldNotBeNull();
        Decide("git push --follow-tags", branch: "main").ShouldNotBeNull().ShouldContain("pilot flight merge");
        Decide("git push --follow-tags origin", branch: "flight/x").ShouldBeNull();
    }

    [Fact]
    public void AGitBashPathAndATildeAreMappedBeforeTheCheckoutIsRead()
    {
        // The mapping itself, whatever machine runs the tests.
        MergeGuard.ShellPath("/c/Users/me/repo", windows: true, @"C:\Users\me").ShouldBe(@"C:/Users/me/repo");
        MergeGuard.ShellPath("/C", windows: true, @"C:\Users\me").ShouldBe(@"C:\");
        MergeGuard.ShellPath("/c/Users/me/repo", windows: false, "/home/me").ShouldBe("/c/Users/me/repo");
        MergeGuard.ShellPath("/cx/y", windows: true, @"C:\Users\me").ShouldBe("/cx/y");
        MergeGuard.ShellPath("~", windows: false, "/home/me").ShouldBe("/home/me");
        MergeGuard.ShellPath("~/x", windows: false, "/home/me").ShouldBe("/home/me/x");
        MergeGuard.ShellPath("$HOME/x", windows: false, "/home/me").ShouldBe("/home/me/x");
        MergeGuard.ShellPath("${HOME}/x", windows: false, "/home/me").ShouldBe("/home/me/x");
        MergeGuard.ShellPath("~other/x", windows: false, "/home/me").ShouldBe("~other/x");
        MergeGuard.ShellPath("../x", windows: true, @"C:\Users\me").ShouldBe("../x");

        // And through the guard: the folder the branch is asked in is the real one.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new List<string>();
        MergeGuard.GitQuery git = (folder, args) =>
        {
            folders.Add(folder);
            return args.Contains("HEAD") && args.Count == 4 ? "main" : null;
        };
        MergeGuard.Decide(Input("cd ~/x && git push"), git).ShouldNotBeNull().ShouldContain("pilot flight merge");
        folders.ShouldAllBe(f => f.StartsWith(Path.GetFullPath(home), StringComparison.OrdinalIgnoreCase) && !f.Contains('~'));

        if (OperatingSystem.IsWindows())
        {
            folders.Clear();
            MergeGuard.Decide(Input("cd /c/Users/me/repo && git push"), git).ShouldNotBeNull().ShouldContain("pilot flight merge");
            folders.ShouldAllBe(f => f == @"C:\Users\me\repo");
            folders.Clear();
            MergeGuard.Decide(Input("git -C /d/work/plane push"), git).ShouldNotBeNull();
            folders.ShouldAllBe(f => f == @"D:\work\plane");
        }
    }

    [Theory]
    [InlineData("git commit -m @'\nDon't\n'@; gh pr merge 1")]
    [InlineData("git commit -m @\"\nDon't\n\"@; git push origin main")]
    public void APowerShellHereStringIsOneWordAndTheCommandAfterItIsRead(string command)
    {
        DecidePs(command).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("git commit -m @'\ngh pr merge 1\n'@")]
    [InlineData("git commit -m @'\nDon't; gh pr merge 1\n'@")]
    [InlineData("git commit -m @\"\ngit push origin main\n\"@")]
    public void APowerShellHereStringsBodyIsNotACommand(string command)
    {
        DecidePs(command).ShouldBeNull();
    }

    [Fact]
    public void APlainPushIsRefusedOnlyWhenTheCheckoutIsOnTheDefaultBranch()
    {
        Decide("git push", branch: "flight/x").ShouldBeNull();
        Decide("git push", branch: "main").ShouldNotBeNull().ShouldContain("pilot flight merge");
        Decide("git push origin", branch: "master").ShouldNotBeNull().ShouldContain("pilot flight merge");
        Decide("git push origin HEAD", branch: "main").ShouldNotBeNull().ShouldContain("pilot flight merge");
        Decide("git push origin HEAD", branch: "flight/x").ShouldBeNull();
        // A detached HEAD names no branch to push to.
        Decide("git push", branch: null).ShouldBeNull();
    }

    [Fact]
    public void TheReposDefaultBranchCountsWhateverItIsCalled()
    {
        Decide("git push origin trunk", defaultBranch: "origin/trunk").ShouldNotBeNull().ShouldContain("pilot flight merge");
        Decide("git push", branch: "trunk", defaultBranch: "origin/trunk").ShouldNotBeNull();
        Decide("git push origin trunk", defaultBranch: null).ShouldBeNull();
    }

    [Fact]
    public void ACdInTheLineMovesWhereTheCheckoutIsRead()
    {
        var folders = new List<string>();
        MergeGuard.GitQuery git = (folder, args) =>
        {
            folders.Add(folder);
            return args.Contains("HEAD") && args.Count == 4 ? "main" : null;
        };

        MergeGuard.Decide(Input("cd ../other && git push"), git).ShouldNotBeNull();

        folders.ShouldAllBe(f => f.Replace('\\', '/').EndsWith("/.claude/worktrees/other", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("gh pr checks 1  # wait; then gh pr merge 1 via the node")]
    [InlineData("gh pr checks 1 # gh pr merge 1")]
    [InlineData("# gh pr merge 1\ngh pr view 1")]
    [InlineData("git push origin flight/x # not main; git push origin main would be refused")]
    public void ACommentIsNotCode(string command)
    {
        Decide(command).ShouldBeNull();
        DecidePs(command).ShouldBeNull();
    }

    [Fact]
    public void AnApostropheInACommentDoesNotOpenAQuote()
    {
        Decide("# don't\ngit push origin main").ShouldNotBeNull().ShouldContain("pilot flight merge");
        Decide("git status # don't\ngit push", branch: "main").ShouldNotBeNull().ShouldContain("pilot flight merge");
        DecidePs("# don't\ngit push origin main").ShouldNotBeNull().ShouldContain("pilot flight merge");
        DecidePs("git status # don't\r\ngit push", branch: "main").ShouldNotBeNull();
        // And a comment ends at its line: the next line's merge is still one.
        Decide("gh pr view 1 # look\ngh pr merge 1").ShouldNotBeNull();
        // A # inside a word, a quote or an expansion is no comment.
        Decide("gh pr view 1 && echo a#b; gh pr merge 1").ShouldNotBeNull();
        Decide("echo '#' ; gh pr merge 1").ShouldNotBeNull();
        Decide("echo $# ; gh pr merge 1").ShouldNotBeNull();
    }

    [Fact]
    public void APowerShellBlockCommentIsNotCode()
    {
        DecidePs("gh pr view 1 <# gh pr merge 1; git push origin main #>").ShouldBeNull();
        DecidePs("<# note #> gh pr merge 1").ShouldNotBeNull().ShouldContain("pilot flight merge");
        DecidePs("<#\ngit push origin main\n#>\ngit push origin flight/x").ShouldBeNull();
    }

    /// <summary>A repo per folder: the checkout is on main in a folder named onmain, and on a feature branch anywhere else.</summary>
    private static MergeGuard.GitQuery PerFolderGit() => (folder, args) =>
        args.Contains("HEAD") && args.Count == 4
            ? folder.Replace('\\', '/').EndsWith("/onmain", StringComparison.Ordinal) ? "main" : "feature/x"
            : args.Contains("refs/remotes/origin/HEAD") ? "origin/main" : null;

    private static string? DecideFolders(string command, string tool = "Bash") => MergeGuard.Decide(Input(command, tool), PerFolderGit());

    [Theory]
    [InlineData("(cd ../onmain && git fetch) && git push -u origin HEAD")]
    [InlineData("MAINSHA=$(cd ../onmain && git rev-parse HEAD); git push -u origin HEAD")]
    [InlineData("pushd ../onmain && git pull && popd && git push -u origin HEAD")]
    [InlineData("echo \"$(cd ../onmain && git rev-parse HEAD)\" && git push")]
    [InlineData("echo `cd ../onmain && pwd`; git push")]
    [InlineData("(cd ../onmain; git status); git push")]
    [InlineData("(pushd ../onmain; git status); git push")]
    public void ASubshellsOrAPoppedCdLeavesTheFolderAsItWas(string command)
    {
        DecideFolders(command).ShouldBeNull();
    }

    [Theory]
    [InlineData("(cd ../onmain && git push)")]
    [InlineData("(cd ../onmain && git status) ; cd ../onmain && git push")]
    [InlineData("cd ../onmain && (git status) && git push")]
    [InlineData("pushd ../onmain && git push")]
    [InlineData("pushd ../onmain && popd && pushd ../onmain && git push")]
    [InlineData("pushd ../a && pushd ../onmain && popd && popd && cd ../onmain && git push")]
    [InlineData("$(cd ../onmain && git push)")]
    public void AnUnscopedCdStillMovesTheFolder(string command)
    {
        DecideFolders(command).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("(Set-Location ../onmain; git fetch); git push -u origin HEAD")]
    [InlineData("$sha = (Set-Location ../onmain; git rev-parse HEAD); git push -u origin HEAD")]
    [InlineData("Push-Location ../onmain; git pull; Pop-Location; git push -u origin HEAD")]
    [InlineData("pushd ../onmain; git pull; popd; git push -u origin HEAD")]
    public void PowerShellScopesAndPopsTheFolderToo(string command)
    {
        DecideFolders(command, "PowerShell").ShouldBeNull();
    }

    [Theory]
    [InlineData("Push-Location ../onmain; git push")]
    [InlineData("Push-Location ../onmain; Push-Location ..; Pop-Location; git push")]
    [InlineData("(Set-Location ../onmain; git push)")]
    public void PowerShellsPushLocationMovesTheFolder(string command)
    {
        DecideFolders(command, "PowerShell").ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("$out = gh pr merge 1 --squash 2>&1")]
    [InlineData("$out=gh pr merge 1")]
    [InlineData("$out= gh pr merge 1")]
    [InlineData("$null = git push origin main")]
    [InlineData("$env:X = 'y'; $r = gh api -X PUT repos/o/r/pulls/3/merge")]
    [InlineData("$n += git push origin main")]
    [InlineData("$a = $b = gh pr merge 1")]
    [InlineData("$out = & gh pr merge 1")]
    public void APowerShellAssignmentsCommandIsRead(string command)
    {
        DecidePs(command).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("$out = gh pr view 1 2>&1")]
    [InlineData("$null = git push origin flight/x")]
    [InlineData("$x = 'gh pr merge 1'")]
    [InlineData("$x = 1")]
    public void APowerShellAssignmentOfSomethingElsePasses(string command)
    {
        DecidePs(command).ShouldBeNull();
    }

    [Theory]
    [InlineData("bash -lc 'gh pr merge 1'")]
    [InlineData("bash -ec \"git push origin main\"")]
    [InlineData("sh -xc 'gh pr merge 1'")]
    [InlineData("bash -l -c 'gh pr merge 1'")]
    [InlineData("zsh -ic 'gh pr merge 1'")]
    [InlineData("bash -c 'gh pr merge 1'")]
    public void AShellsCombinedFlagsAreFoundToo(string command)
    {
        Decide(command).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("bash -lc 'gh pr view 1'")]
    [InlineData("bash -l script.sh")]
    [InlineData("bash --login -c 'git status'")]
    [InlineData("bash -lc")]
    public void AShellWithNothingToRefusePasses(string command)
    {
        Decide(command).ShouldBeNull();
    }

    private static string? DecidePs(string command, string? branch = "flight/x", string? defaultBranch = "origin/main") =>
        MergeGuard.Decide(Input(command, "PowerShell"), Git(branch, defaultBranch));

    [Theory]
    [InlineData("gh pr merge 1")]
    [InlineData("gh pr merge 12 --squash --auto")]
    [InlineData("gh pr -R o/r merge 3")]
    [InlineData("GH.EXE pr merge 1")]
    [InlineData("& gh pr merge 1")]
    [InlineData("& \"C:\\Program Files\\GitHub CLI\\gh.exe\" pr merge 1")]
    [InlineData("gh api graphql -f query='mutation{enqueuePullRequest(input:{pullRequestId:\"X\"}){clientMutationId}}'")]
    [InlineData("gh api graphql -f query=\"mutation { mergePullRequest(input:{pullRequestId:`\"X`\"}) { clientMutationId } }\"")]
    [InlineData("gh api -X PUT repos/o/r/pulls/3/merge")]
    [InlineData("gh api --method PUT /repos/o/r/pulls/3/merge -f merge_method=squash")]
    [InlineData("git push origin main")]
    [InlineData("git push origin HEAD:master")]
    [InlineData("git push origin 'HEAD:master'")]
    [InlineData("git push -u origin +main")]
    [InlineData("git push origin HEAD:refs/heads/main")]
    [InlineData("git push --all")]
    [InlineData("git -C C:\\work\\plane push origin main")]
    // Compound lines, sub-expressions, script blocks, nested shells: each part is read.
    [InlineData("cd C:\\work\\plane; gh pr merge 1")]
    [InlineData("gh pr view 1; gh pr merge 1")]
    [InlineData("gh pr view 1 && gh pr merge 1")]
    [InlineData("gh pr view 1 || gh pr merge 1")]
    [InlineData("gh pr view 1\r\ngh pr merge 1")]
    [InlineData("gh pr view 1 `\n| gh pr merge 1")]
    [InlineData("echo done | gh pr merge 1")]
    [InlineData("$env:GH_TOKEN = 'x'; gh pr merge 1")]
    [InlineData("echo $(gh pr merge 1)")]
    [InlineData("& { gh pr merge 1 }")]
    [InlineData("foreach ($n in 1,2) { gh pr merge $n }")]
    [InlineData("Invoke-Expression 'gh pr merge 1'")]
    [InlineData("iex \"git push origin main\"")]
    [InlineData("pwsh -NoProfile -Command \"gh pr merge 1\"")]
    [InlineData("powershell -c 'git push origin main'")]
    [InlineData("bash -c 'gh pr merge 1'")]
    public void PowerShellIsReadToo(string command)
    {
        DecidePs(command).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("gh pr view 1")]
    [InlineData("gh pr create --fill")]
    [InlineData("gh api repos/o/r/pulls/3")]
    [InlineData("gh api -X PUT repos/o/r/pulls/3/reviews")]
    [InlineData("gh api graphql -f query='query{viewer{login}}'")]
    [InlineData("git push origin flight/x")]
    [InlineData("git push")]
    [InlineData("git status; git log main..HEAD")]
    [InlineData("Write-Output 'gh pr merge 1'")]
    [InlineData("Select-String -Path docs -Pattern \"git push origin main\"")]
    [InlineData("dotnet test src\\Pilot.Node.Tests")]
    // A backslash is a path's in PowerShell, not an escape that would hide the next character.
    [InlineData("cd C:\\work\\main; gh pr view 1")]
    public void PowerShellEverythingElsePasses(string command)
    {
        DecidePs(command).ShouldBeNull();
    }

    [Fact]
    public void APlainPushFromPowerShellIsRefusedOnlyOnTheDefaultBranch()
    {
        DecidePs("git push", branch: "flight/x").ShouldBeNull();
        DecidePs("git push", branch: "main").ShouldNotBeNull().ShouldContain("pilot flight merge");
        DecidePs("Set-Location ../other; git push", branch: "master").ShouldNotBeNull();
    }

    [Theory]
    [InlineData("gh pr merge 1", "Read")]
    [InlineData("gh pr merge 1", "Edit")]
    [InlineData("gh pr merge 1", "bash")]
    public void OnlyABashOrPowerShellCallIsRead(string command, string tool)
    {
        MergeGuard.Decide(Input(command, tool), Git("main")).ShouldBeNull();
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    public void BothShellToolsAreRefused(string tool)
    {
        MergeGuard.Decide(Input("gh pr merge 1", tool), Git("flight/x")).ShouldNotBeNull().ShouldContain("pilot flight merge");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "tool_name": "Bash" }""")]
    [InlineData("""{ "tool_name": "Bash", "tool_input": { "command": 4 } }""")]
    public void InputItCannotReadPasses(string json)
    {
        MergeGuard.Decide(json, Git("main")).ShouldBeNull();
    }

    [Fact]
    public async Task TheCommandAnswersWithADenyOnStdoutAndExitsZero()
    {
        using var stdin = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Input("gh pr merge 1")));
        var stdout = new StringWriter();

        (await MergeGuard.RunAsync(stdin, stdout, Git("flight/x"))).ShouldBe(0);

        var answer = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        answer["hookEventName"]!.GetValue<string>().ShouldBe("PreToolUse");
        answer["permissionDecision"]!.GetValue<string>().ShouldBe("deny");
        answer["permissionDecisionReason"]!.GetValue<string>().ShouldContain("pilot flight merge");
    }

    [Fact]
    public async Task TheCommandSaysNothingForAnAllowedCallAndForGarbage()
    {
        foreach (var text in new[] { Input("gh pr view 1"), "garbage" })
        {
            using var stdin = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
            var stdout = new StringWriter();

            (await MergeGuard.RunAsync(stdin, stdout, Git("flight/x"))).ShouldBe(0);

            stdout.ToString().ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task TheCommandLineRunsItAndSetupCommandsLineStaysFailOpenOnBadOptions()
    {
        using var stdin = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Input("gh pr merge 1")));
        var output = new StringWriter();

        var exit = await NodeCommandLine.RunAsync([MergeGuard.Argument], stdin, output, new StringWriter(), Ct, nodePath: "pilot-node");
        (await NodeCommandLine.RunAsync([MergeGuard.Argument, "--no-such-option"], new MemoryStream(), new StringWriter(), new StringWriter(), Ct, nodePath: "pilot-node")).ShouldBe(0);

        exit.ShouldBe(0);
        output.ToString().ShouldContain("pilot flight merge");
    }

    private const string Other = """
        {
          "model": "opus",
          "hooks": {
            "PreToolUse": [ { "matcher": "Edit", "hooks": [ { "type": "command", "command": "/x/mine.sh" } ] } ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "/x/stop.sh" } ] } ]
          }
        }
        """;

    private static IEnumerable<JsonNode> Handlers(string settings, string name) =>
        JsonNode.Parse(settings)!["hooks"]![name]!.AsArray().SelectMany(e => e!["hooks"]!.AsArray()).Select(h => h!);

    [Fact]
    public void InstallAddsOneBashAndPowerShellHandlerBesideTheOthersAndASecondInstallChangesNothing()
    {
        var once = MergeGuard.Install(Other, "/opt/pilot/pilot-node");

        var root = JsonNode.Parse(once)!;
        root["model"]!.GetValue<string>().ShouldBe("opus");
        Handlers(once, "Stop").Select(h => h["command"]!.GetValue<string>()).ShouldBe(["/x/stop.sh"]);
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        pre.Count.ShouldBe(2);
        pre[0]!["matcher"]!.GetValue<string>().ShouldBe("Edit");
        pre[1]!["matcher"]!.GetValue<string>().ShouldBe("Bash|PowerShell");
        var handler = pre[1]!["hooks"]![0]!;
        (handler["command"]!.GetValue<string>(), handler["args"]![0]!.GetValue<string>(), handler["async"]).ShouldBe(("/opt/pilot/pilot-node", "merge-guard", null));
        MergeGuard.Installed(once, "/opt/pilot/pilot-node").ShouldBeTrue();
        MergeGuard.Install(once, "/opt/pilot/pilot-node").ShouldBeSameAs(once);
    }

    [Fact]
    public void ANewSpellingOfTheCommandReplacesTheGuardAndLeavesNoSecondOne()
    {
        var old = MergeGuard.Install("", @"C:\old\pilot-node.exe");
        MergeGuard.Installed(old, @"C:\new\pilot-node.exe").ShouldBeFalse();

        var updated = MergeGuard.Install(old, @"C:\new\pilot-node.exe");

        var guards = Handlers(updated, "PreToolUse").ToList();
        guards.Count.ShouldBe(1);
        guards[0]["command"]!.GetValue<string>().ShouldBe(@"C:\new\pilot-node.exe");
        MergeGuard.Installed(updated, @"C:\new\pilot-node.exe").ShouldBeTrue();
    }

    [Fact]
    public void ItAndPilotsHooksCoexistWithNeitherReportingTheOtherAsMissing()
    {
        var both = MergeGuard.Install(HookCommands.Install(Other, "/n/pilot-node", null), "/n/pilot-node");

        HookCommands.Install(both, "/n/pilot-node", null).ShouldBeSameAs(both);
        MergeGuard.Install(both, "/n/pilot-node").ShouldBeSameAs(both);
        Handlers(both, "PreToolUse").Count().ShouldBe(3);
    }

    [Fact]
    public void RemovingPilotsHooksTakesTheGuardWithThemAndNothingElse()
    {
        var both = MergeGuard.Install(HookCommands.Install(Other, "/n/pilot-node", null), "/n/pilot-node");

        var guardGone = MergeGuard.Remove(both);

        Handlers(guardGone, "PreToolUse").Select(h => h["command"]!.GetValue<string>()).ShouldBe(["/x/mine.sh", "/n/pilot-node"]);
        MergeGuard.Remove(guardGone).ShouldBeSameAs(guardGone);
        MergeGuard.Remove(HookCommands.Install("", "/n/pilot-node", null)).ShouldNotContain("merge-guard");
        JsonNode.Parse(MergeGuard.Remove(MergeGuard.Install("", "/n/pilot-node")))!.AsObject().Count.ShouldBe(0);
    }

    private (NodeSetup Setup, string Settings) Setup(string settingsText)
    {
        var settings = Path.Combine(_root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, settingsText);
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), "pilot-node", Path.Combine(_root, ".claude"))
        {
            SettingsFile = settings,
            FindRunawayAgent = () => null,
            Run = (_, _, _) => (1, ""),
        };
        return (setup, settings);
    }

    private static SetupCheck GuardCheck(NodeSetup setup) => setup.Checks().Single(c => c.Title == "Merge guard");

    [Fact]
    public async Task TheChecklistHasATodoForItUntilItsFixInstallsItUserWideAndASecondRunChangesNothing()
    {
        var (setup, settings) = Setup(Other);

        var before = GuardCheck(setup);
        (before.Key, before.Ok, before.Optional, before.Fix).ShouldBe((NodeSetup.MergeGuardKey, false, false, "Install merge guard"));
        before.Line.ShouldContain(settings);

        (await setup.FixAsync(NodeSetup.MergeGuardKey, Ct)).ShouldBeNull();

        var after = GuardCheck(setup);
        (after.Ok, after.Fix).ShouldBe((true, null));
        var installed = File.ReadAllText(settings);
        JsonNode.Parse(installed)!["model"]!.GetValue<string>().ShouldBe("opus");
        Handlers(installed, "PreToolUse").Count(h => h["args"]?[0]?.GetValue<string>() == "merge-guard").ShouldBe(1);
        var stamp = File.GetLastWriteTimeUtc(settings);

        (await setup.FixAsync(NodeSetup.MergeGuardKey, Ct)).ShouldBeNull();

        File.ReadAllText(settings).ShouldBe(installed);
        File.GetLastWriteTimeUtc(settings).ShouldBe(stamp);
    }

    [Fact]
    public async Task TheFixCreatesTheSettingsFileWhenThereIsNone()
    {
        var (setup, settings) = Setup("");
        File.Delete(settings);

        GuardCheck(setup).Ok.ShouldBeFalse();
        (await setup.FixAsync(NodeSetup.MergeGuardKey, Ct)).ShouldBeNull();

        GuardCheck(setup).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task AGuardForAnotherBinaryFailsTheCheckUntilTheFixReplacesIt()
    {
        var (setup, settings) = Setup(MergeGuard.Install(Other, "/old/pilot-node"));

        var stale = GuardCheck(setup);
        (stale.Ok, stale.Fix).ShouldBe((false, "Install merge guard"));

        (await setup.FixAsync(NodeSetup.MergeGuardKey, Ct)).ShouldBeNull();

        GuardCheck(setup).Ok.ShouldBeTrue();
        Handlers(File.ReadAllText(settings), "PreToolUse").Count(h => h["args"]?[0]?.GetValue<string>() == "merge-guard").ShouldBe(1);
    }

    private const string BashOnly = """
        { "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "/n/pilot-node", "args": ["merge-guard"], "timeout": 10 } ] } ] } }
        """;

    [Fact]
    public async Task AnOlderGuardOnBashAloneIsStaleUntilTheFixWidensItToPowerShellWithNoSecondEntry()
    {
        MergeGuard.Installed(BashOnly, "/n/pilot-node").ShouldBeFalse();
        var (setup, settings) = Setup(BashOnly);
        var stale = GuardCheck(setup);
        (stale.Ok, stale.Fix).ShouldBe((false, "Install merge guard"));

        (await setup.FixAsync(NodeSetup.MergeGuardKey, Ct)).ShouldBeNull();

        GuardCheck(setup).Ok.ShouldBeTrue();
        var pre = JsonNode.Parse(File.ReadAllText(settings))!["hooks"]!["PreToolUse"]!.AsArray();
        pre.Count.ShouldBe(1);
        pre[0]!["matcher"]!.GetValue<string>().ShouldBe("Bash|PowerShell");
    }

    [Fact]
    public void ASettingsFileThatIsNotJsonIsReportedNotThrown()
    {
        var (setup, _) = Setup("not json");

        var check = GuardCheck(setup);
        (check.Ok, check.Fix).ShouldBe((false, null));
    }
}
