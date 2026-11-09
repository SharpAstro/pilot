using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>A bash or sh line runs Git Bash on Windows, never System32's WSL launcher (pilot #697).</summary>
public sealed class GitBashTests
{
    private static readonly string Win = Path.Combine(Path.GetTempPath(), "fake-windows");
    private static readonly string Wsl = Path.Combine(Win, "System32", "bash.exe");
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "fake-git");
    private static readonly string GitBashExe = Path.Combine(Root, "bin", "bash.exe");

    private static Func<string, string?> Env(string? configured, params string[] path) => name => name switch
    {
        GitBash.PathVariable => configured,
        "PATH" => string.Join(Path.PathSeparator, path),
        "SystemRoot" => Win,
        _ => null,
    };

    [Fact]
    public void TheConfiguredPathWinsWhenItExists()
    {
        var fake = Path.Combine(Path.GetTempPath(), "elsewhere", "bash.exe");

        GitBash.Resolve(Env(fake, Path.Combine(Root, "cmd")), p => p == fake || p == GitBashExe).ShouldBe(fake);
    }

    [Fact]
    public void AConfiguredPathThatIsNotAFileIsIgnoredForTheGitOnPath()
    {
        GitBash.Resolve(Env(Path.Combine(Path.GetTempPath(), "gone", "bash.exe"), Path.Combine(Root, "cmd")), p => p == Path.Combine(Root, "cmd", "git.exe") || p == GitBashExe)
            .ShouldBe(GitBashExe);
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("bin")]
    [InlineData("mingw64/bin")]
    public void GitBashIsTheBinBashOfTheRootOfTheGitOnPath(string gitFolder)
    {
        var dir = Path.Combine(Root, Path.Combine(gitFolder.Split('/')));

        GitBash.Resolve(Env(null, Path.Combine(Win, "System32"), dir), p => p == Path.Combine(dir, "git.exe") || p == GitBashExe || p == Wsl)
            .ShouldBe(GitBashExe);
    }

    [Fact]
    public void NothingUnderTheWindowsFolderIsEverTaken()
    {
        // The WSL launcher exists and is configured, and a git sits beside it: still none.
        GitBash.Resolve(Env(Wsl, Path.Combine(Win, "System32")), p => p == Wsl || p == Path.Combine(Win, "System32", "git.exe")).ShouldBeNull();
    }

    [Fact]
    public void WithNeitherAConfiguredPathNorAGitThereIsNone() =>
        GitBash.Resolve(Env(null, Path.Combine(Win, "System32")), p => p == Wsl).ShouldBeNull();

    [Theory]
    [InlineData("bash -c 'echo hi'", true)]
    [InlineData("bash", true)]
    [InlineData("  bash -lc \"uname -s\"", true)]
    [InlineData("sh -c 'x'", true)]
    [InlineData("bash.exe -c 'x'", true)]
    [InlineData("SH.EXE -c x", true)]
    [InlineData("dotnet test", false)]
    [InlineData("bashful -c x", false)]
    [InlineData("echo bash -c x", false)]
    [InlineData("pwsh -c 'bash -c x'", false)]
    public void OnlyALineHeadedByBashOrShNeedsGitBash(string line, bool needs) => GitBash.NeedsGitBash(line).ShouldBe(needs);

    [Fact]
    public void TheHeadIsRewrittenToACallOfGitBashAndTheRestIsLeftAsItIs()
    {
        GitBash.Rewrite("bash -c 'echo hi'", @"C:\Program Files\Git\bin\bash.exe").ShouldBe(@"& 'C:\Program Files\Git\bin\bash.exe' -c 'echo hi'");
        GitBash.Rewrite("sh.exe -lc 'a && b'", "/g/bash.exe").ShouldBe("& '/g/bash.exe' -lc 'a && b'");
        GitBash.Rewrite("dotnet test", "/g/bash.exe").ShouldBe("dotnet test");
    }

    [Fact]
    public void TheScriptLineHoldsTheGitBashPathNotTheBareName()
    {
        var script = ShellScript.Body(ShellScript.Prepared("bash -c 'echo hi'", GitBashExe));

        script.ShouldStartWith($"& '{GitBashExe}' -c 'echo hi'\n");
    }

    [Fact]
    public void ALineThatNeedsGitBashWhereThereIsNoneFailsWithWhy()
    {
        var ex = Should.Throw<InvalidOperationException>(() => ShellScript.Prepared("bash -c 'echo hi'", null));

        ex.Message.ShouldBe(GitBash.NotFound);
        ShellScript.Prepared("dotnet test", null).ShouldBe("dotnet test");
    }

    [Fact]
    public void WithNoGitBashARunOfABashLineIsRefusedSayingSoAndToCallDotnetDirectly()
    {
        var refusal = GitBash.Refusal("bash -c 'dotnet test'", null, windows: true).ShouldNotBeNull();

        refusal.ShouldContain("no Git Bash was found");
        refusal.ShouldContain("CLAUDE_CODE_GIT_BASH_PATH unset and no git on PATH");
        refusal.ShouldContain("call dotnet directly");
        GitBash.Refusal("bash -c 'dotnet test'", GitBashExe, windows: true).ShouldBeNull();
        GitBash.Refusal("dotnet test", null, windows: true).ShouldBeNull();
        GitBash.Refusal("bash -c 'x'", null, windows: false).ShouldBeNull();
    }

    [Fact]
    public void TheScriptsOfACheckAndOfAJobBothRunTheResolvedGitBash()
    {
        if (!OperatingSystem.IsWindows() || GitBash.Resolve() is not { } gitBash)
        {
            return;
        }
        var dir = TestTemp.NewPath();
        Directory.CreateDirectory(dir);
        try
        {
            using (var check = ShellScript.For("bash -c 'echo hi'", dir))
            {
                File.ReadAllText(check.Script!).ShouldContain($"& '{gitBash}' -c 'echo hi'");
            }
            var log = Path.Combine(dir, "j.log");
            using var job = DetachedShell.For("j1", "bash -c 'echo hi'", dir, log, log + ".exit");
            File.ReadAllText(job.Script!).ShouldContain($"& '{gitBash}' -c 'echo hi'");
            gitBash.ShouldNotContain("System32", Case.Insensitive);
        }
        finally
        {
            TestTemp.Delete(dir);
        }
    }
}

/// <summary>A bash line as a real job on Windows: Git Bash ran it, not WSL.</summary>
[Collection(JobsCollection.Name)]
public sealed class GitBashJobTests : JobTestsBase
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task ABashJobRunsInGitBashNotInWsl()
    {
        if (!OperatingSystem.IsWindows() || GitBash.Resolve() is null)
        {
            return;
        }
        var made = Run("bash -c 'uname -s'");

        made.Ok.ShouldBeTrue(made.Error);
        var job = await Ended(made.Made.ShouldNotBeNull());
        var log = ReadLog(job.LogPath);
        (job.State, job.ExitCode).ShouldBe((Pilot.Contracts.JobState.Succeeded, 0), log);
        (log.Contains("MINGW", StringComparison.Ordinal) || log.Contains("MSYS", StringComparison.Ordinal)).ShouldBeTrue(log);
        log.ShouldNotContain("Linux");
    }
}
