using System.Text.Json.Nodes;
using Pilot.Node.Sessions;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The test guard (<see cref="TestGuard"/>, a rule of the merge-guard hook): a node-started session's direct <c>dotnet test</c>, and its
/// <c>dotnet build</c> (any project, or none named), run, publish and pack, are refused, pointing at <c>pilot run</c>; a person's session is untouched. All on
/// the hook's own JSON input, in memory; only the roster tests touch a temp folder, which they remove.
/// </summary>
public sealed class TestGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2t-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            TestDirs.DeleteTree(_root);
        }
    }

    private static string Input(string command, string tool = "Bash", string? sessionId = null)
    {
        var root = new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = new JsonObject { ["command"] = command }, ["cwd"] = "/work/repo/.claude/worktrees/plane" };
        if (sessionId is not null)
        {
            root["session_id"] = sessionId;
        }
        return root.ToJsonString();
    }

    private static string? Marked(string command, string tool = "Bash") => TestGuard.Decide(Input(command, tool), isNodeSession: true);

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet test Pilot.Node.Tests -c Release --no-build --filter FullyQualifiedName~X")]
    [InlineData("cd src && dotnet test Pilot.Node.Tests -c Release")]
    [InlineData("dotnet build Pilot.slnx -c Release")]
    [InlineData("dotnet build src/Pilot.sln")]
    // Every build, any project or none named: a pilot-driven session compiles through the job system (the owner, 2026-11-09).
    [InlineData("dotnet build")]
    [InlineData("dotnet build src/Pilot.Node/Pilot.Node.csproj -c Release")]
    [InlineData("dotnet build -c Release --no-restore")]
    [InlineData("dotnet run --project src/Pilot.Gui")]
    [InlineData("dotnet publish src/Pilot.Node -c Release")]
    [InlineData("dotnet build -c Release Pilot.slnx")]
    [InlineData("dotnet build src/Pilot.Node.Tests/Pilot.Node.Tests.csproj")]
    [InlineData(@"dotnet build src\Pilot.Node.Tests\Pilot.Node.Tests.csproj -c Release")]
    [InlineData("dotnet.exe test")]
    [InlineData("/usr/bin/dotnet test")]
    [InlineData("env DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet test")]
    [InlineData("DOTNET_NOLOGO=1 dotnet test")]
    [InlineData("timeout 600 dotnet test")]
    [InlineData("time dotnet build Pilot.slnx")]
    [InlineData("nice -n 5 dotnet test")]
    [InlineData("cd /c/work/repo/src && dotnet test Pilot.Node.Tests")]
    [InlineData("git status && dotnet test | tail -20")]
    [InlineData("dotnet test 2>&1 | tail -5")]
    [InlineData("(cd src; dotnet test)")]
    [InlineData("if true; then dotnet test; fi")]
    [InlineData("bash -c 'dotnet test'")]
    [InlineData("bash -lc \"cd src && dotnet build Pilot.slnx\"")]
    [InlineData("eval 'dotnet test'")]
    [InlineData("echo $(dotnet test)")]
    [InlineData("pwsh -NoProfile -Command \"dotnet test\"")]
    public void ANodeSessionsDirectTestRunOrSolutionBuildIsRefused(string command)
    {
        var reason = Marked(command).ShouldNotBeNull();
        reason.ShouldContain("pilot run -- <command>");
        reason.ShouldContain("leg's check");
    }

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet build Pilot.slnx -c Release")]
    [InlineData("cd src; dotnet test Pilot.Node.Tests -c Release")]
    [InlineData("& dotnet test")]
    [InlineData("$out = dotnet test")]
    [InlineData("Set-Location src; dotnet build Pilot.slnx")]
    [InlineData("cd src && dotnet build Pilot.Node.Tests\\Pilot.Node.Tests.csproj")]
    [InlineData("DOTNET.EXE TEST")]
    [InlineData("Dotnet Build PILOT.SLNX")]
    [InlineData("pwsh -Command 'dotnet test'")]
    [InlineData("iex 'dotnet test'")]
    [InlineData("bash -c 'dotnet test'")]
    [InlineData("dotnet build 'Pilot Node.slnx'")]
    [InlineData("<# build it #> dotnet test")]
    public void PowerShellSpellingsAreReadToo(string command) => Marked(command, "PowerShell").ShouldNotBeNull();

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet build Pilot.slnx -c Release")]
    [InlineData("dotnet build src/Pilot.Node.Tests/Pilot.Node.Tests.csproj")]
    [InlineData("cd src && timeout 60 dotnet test")]
    public void APersonsOwnSessionIsNeverRefused(string command)
    {
        TestGuard.Decide(Input(command), isNodeSession: false).ShouldBeNull();
        TestGuard.Decide(Input(command, "PowerShell"), isNodeSession: false).ShouldBeNull();
    }

    [Theory]
    [InlineData("dotnet --version")]
    [InlineData("dotnet --list-sdks")]
    [InlineData("dotnet --list-runtimes")]
    [InlineData("dotnet --info")]
    [InlineData("dotnet")]
    [InlineData("dotnet restore Pilot.slnx")]
    [InlineData("dotnet restore src/Pilot.Node.Tests/Pilot.Node.Tests.csproj")]
    [InlineData("cd src && dotnet restore")]
    [InlineData("dotnet new console")]
    [InlineData("dotnet format whitespace")]
    [InlineData("pilot run -- dotnet test Pilot.Node.Tests -c Release --no-build")]
    [InlineData("pilot run --fail-fast -- dotnet test Pilot.Node.Tests -c Release")]
    [InlineData("pilot run -- dotnet build Pilot.slnx -c Release")]
    [InlineData(@"C:\Users\me\AppData\Local\pilot2\bin\pilot.exe run -- dotnet test Pilot.Node.Tests")]
    [InlineData("grep -rn 'dotnet test' README.md")]
    [InlineData("echo dotnet test")]
    [InlineData("git commit -m \"run dotnet test first\"")]
    [InlineData("cat Pilot.Node.Tests.csproj")]
    [InlineData("# dotnet test")]
    [InlineData("ls src && echo dotnet build Pilot.slnx")]
    public void ThatGoesThroughAJobOrNeedsNoneIsLetThrough(string command)
    {
        Marked(command).ShouldBeNull();
        Marked(command, "PowerShell").ShouldBeNull();
    }

    [Fact]
    public void ThePowerShellOnlyFormsAreOnlyReadAsPowerShell()
    {
        // A $name = assignment is PowerShell's; Bash reads "$out" as the program, so nothing there is dotnet.
        Marked("$out = dotnet test").ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"tool_name\":\"Bash\"}")]
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{}}")]
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":5}}")]
    [InlineData("{\"tool_name\":7,\"tool_input\":{\"command\":\"dotnet test\"}}")]
    [InlineData("{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"\"}}")]
    public void MalformedInputPasses(string json) => TestGuard.Decide(json, isNodeSession: true).ShouldBeNull();

    [Theory]
    [InlineData("Read")]
    [InlineData("Edit")]
    [InlineData("Task")]
    [InlineData("bash")]
    public void AnotherToolsCallPasses(string tool) => TestGuard.Decide(Input("dotnet test", tool), isNodeSession: true).ShouldBeNull();

    [Fact]
    public void TheSessionIsOnlyAskedOnceALineIsFoundToRefuse()
    {
        var asked = 0;
        TestGuard.Decide(Input("git status"), () => { asked++; return true; }).ShouldBeNull();
        TestGuard.Decide(Input("dotnet --version"), () => { asked++; return true; }).ShouldBeNull();
        asked.ShouldBe(0);
        TestGuard.Decide(Input("dotnet test"), () => { asked++; return true; }).ShouldNotBeNull();
        asked.ShouldBe(1);
    }

    [Fact]
    public void ADeepNestOfShellsStopsReading()
    {
        // Three levels are read, as the merge rule's are; a line nested further passes, which is fail-open.
        static string Wrap(string command) => "bash -c '" + command.Replace("'", "'\\''") + "'";
        Marked(Wrap(Wrap(Wrap("dotnet test")))).ShouldNotBeNull();
        Marked(Wrap(Wrap(Wrap(Wrap("dotnet test"))))).ShouldBeNull();
    }

    [Fact]
    public void TheRefusalNamesTheCommandItRefused()
    {
        Marked("dotnet test").ShouldNotBeNull().ShouldContain("`dotnet test`");
        Marked("dotnet build src/Pilot.slnx").ShouldNotBeNull().ShouldContain("`dotnet build src/Pilot.slnx`");
    }

    [Fact]
    public void TheRefusalHasNoEmDashAndNoDashesForPunctuation()
    {
        var reason = Marked("dotnet test").ShouldNotBeNull();
        reason.ShouldNotContain("\u2014");
        // Only the commands it names, in backticks, may carry a double dash.
        System.Text.RegularExpressions.Regex.Replace(reason, "`[^`]*`", "").ShouldNotContain(" -- ");
    }

    [Fact]
    public void TheEnvironmentMarksANodeSession()
    {
        TestGuard.IsNodeSession(Input("x"), env: name => name == TestGuard.SessionVariable ? "worker" : null, rosterFile: Path.Combine(_root, "none.json")).ShouldBeTrue();
        TestGuard.IsNodeSession(Input("x"), env: _ => null, rosterFile: Path.Combine(_root, "none.json")).ShouldBeFalse();
        TestGuard.IsNodeSession(Input("x"), env: _ => "", rosterFile: Path.Combine(_root, "none.json")).ShouldBeFalse();
    }

    [Fact]
    public void TheRosterMarksANodeSessionWhoseHookSeesNoEnvironment()
    {
        // claude --bg hands the session to a daemon: its hook has no PILOT_SESSION, but the node recorded the job id when it started it.
        var file = SessionRoster.FileIn(_root);
        SessionRoster.Mark(file, "3f2a9c1e-1111-2222-3333-444455556666");

        TestGuard.IsNodeSession(Input("x", sessionId: "3f2a9c1e-1111-2222-3333-444455556666"), env: _ => null, rosterFile: file).ShouldBeTrue();
        TestGuard.IsNodeSession(Input("x", sessionId: "3f2a9c1e"), env: _ => null, rosterFile: file).ShouldBeTrue();
        TestGuard.IsNodeSession(Input("x", sessionId: "3F2A9C1E-9999"), env: _ => null, rosterFile: file).ShouldBeTrue();
        TestGuard.IsNodeSession(Input("x", sessionId: "aaaaaaaa-1111"), env: _ => null, rosterFile: file).ShouldBeFalse();
        TestGuard.IsNodeSession(Input("x"), env: _ => null, rosterFile: file).ShouldBeFalse();
        TestGuard.IsNodeSession("not json", env: _ => null, rosterFile: file).ShouldBeFalse();

        TestGuard.Decide(Input("dotnet test", sessionId: "3f2a9c1e-0"), () => TestGuard.IsNodeSession(Input("dotnet test", sessionId: "3f2a9c1e-0"), _ => null, file)).ShouldNotBeNull();
        TestGuard.Decide(Input("dotnet test", sessionId: "bbbbbbbb-0"), () => TestGuard.IsNodeSession(Input("dotnet test", sessionId: "bbbbbbbb-0"), _ => null, file)).ShouldBeNull();
    }

    [Fact]
    public void ARosterThatIsMissingOrBrokenMarksNothing()
    {
        var file = SessionRoster.FileIn(_root);
        SessionRoster.Contains(file, "3f2a9c1e").ShouldBeFalse();
        File.WriteAllText(file, "{ not json");
        SessionRoster.Contains(file, "3f2a9c1e").ShouldBeFalse();
        File.WriteAllText(file, "{\"a\":1}");
        SessionRoster.Contains(file, "3f2a9c1e").ShouldBeFalse();
        // A mark over a broken file does not throw, and the file is mended by it.
        SessionRoster.Mark(file, "3f2a9c1e");
        SessionRoster.Contains(file, "3f2a9c1e").ShouldBeTrue();
        SessionRoster.Mark(null, "3f2a9c1e");
        SessionRoster.Mark(file, null);
        SessionRoster.Mark(file, " ");
    }

    [Fact]
    public void TheRosterKeepsTheNewestIdsAndMarksOnceOnly()
    {
        var file = SessionRoster.FileIn(_root);
        SessionRoster.Mark(file, "11111111-a");
        SessionRoster.Mark(file, "11111111-b");
        JsonNode.Parse(File.ReadAllText(file))!.AsArray().Count.ShouldBe(1);
        SessionRoster.Mark(file, "22222222");
        JsonNode.Parse(File.ReadAllText(file))!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["11111111", "22222222"]);
    }

    [Fact]
    public async Task TheHooksOwnRunAnswersADenyForAMarkedSessionAndNothingForAPersons()
    {
        var file = SessionRoster.FileIn(_root);
        SessionRoster.Mark(file, "3f2a9c1e");

        using var marked = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Input("dotnet test", sessionId: "3f2a9c1e-0")));
        var denied = new StringWriter();
        (await MergeGuard.RunAsync(marked, denied, roster: file)).ShouldBe(0);
        var answer = JsonNode.Parse(denied.ToString())!["hookSpecificOutput"]!;
        (answer["permissionDecision"]!.GetValue<string>(), answer["hookEventName"]!.GetValue<string>()).ShouldBe(("deny", "PreToolUse"));
        answer["permissionDecisionReason"]!.GetValue<string>().ShouldContain("pilot run -- <command>");

        // PILOT_SESSION may be set in this process by another test run: the unmarked case is one whose id is not on the roster and whose variable is absent.
        if (Environment.GetEnvironmentVariable(TestGuard.SessionVariable) is not { Length: > 0 })
        {
            using var person = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Input("dotnet test", sessionId: "cccccccc-0")));
            var silent = new StringWriter();
            (await MergeGuard.RunAsync(person, silent, roster: file)).ShouldBe(0);
            silent.ToString().ShouldBeEmpty();
        }

        using var broken = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not json"));
        var nothing = new StringWriter();
        (await MergeGuard.RunAsync(broken, nothing, roster: file)).ShouldBe(0);
        nothing.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("se1-pil909-test-guard", "worker")]
    [InlineData("se1-pil909-plan", "planner")]
    [InlineData("se1-pil909-review", "reviewer")]
    [InlineData("se1-review-pilot-900", "reviewer")]
    [InlineData("se1-fix-pilot-900", "fixer")]
    [InlineData("se1-watchtower", "watchtower")]
    public void TheNodesCallsMarkTheSessionWithItsRole(string name, string role)
    {
        var start = ClaudeCli.StartInfo("claude", ["--bg", "-n", name, "--permission-mode", "auto"], Path.GetTempPath());
        start.Environment[TestGuard.SessionVariable].ShouldBe(role);
    }

    [Fact]
    public void AnAdHocSessionOrACallForNoSessionIsNotMarked()
    {
        ClaudeCli.StartInfo("claude", ["--bg", "-n", "my own notes"], null).Environment.ContainsKey(TestGuard.SessionVariable).ShouldBeFalse();
        ClaudeCli.StartInfo("claude", ["agents", "--json", "--all"], null).Environment.ContainsKey(TestGuard.SessionVariable).ShouldBeFalse();
        ClaudeCli.StartInfo("claude", ["--bg", "--resume", "3f2a9c1e-0"], null).Environment.ContainsKey(TestGuard.SessionVariable).ShouldBeFalse();
    }

    [Fact]
    public void AResumeThatCarriesNoNameIsMarkedByTheRoleItIsGiven()
    {
        ClaudeCli.StartInfo("claude", ["--bg", "--resume", "3f2a9c1e-0"], null, role: "worker").Environment[TestGuard.SessionVariable].ShouldBe("worker");
    }

    [Fact]
    public void AMarkInTheNodesOwnEnvironmentDoesNotLeakToACallForNoSession()
    {
        var before = Environment.GetEnvironmentVariable(TestGuard.SessionVariable);
        Environment.SetEnvironmentVariable(TestGuard.SessionVariable, "worker");
        try
        {
            ClaudeCli.StartInfo("claude", ["agents"], null).Environment.ContainsKey(TestGuard.SessionVariable).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestGuard.SessionVariable, before);
        }
    }
}
