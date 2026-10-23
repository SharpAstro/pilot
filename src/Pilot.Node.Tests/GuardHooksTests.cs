using System.Text.Json.Nodes;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>runaway-agent's three guards: merged into a settings file as its install.sh merges them, found by the setup
/// checklist, and installed by its fix with a second run changing nothing.</summary>
public sealed class GuardHooksTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2g-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
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

    private static IEnumerable<string> Commands(string settings, string name) =>
        JsonNode.Parse(settings)!["hooks"]![name]!.AsArray().SelectMany(e => e!["hooks"]!.AsArray()).Select(h => h!["command"]!.GetValue<string>());

    [Fact]
    public void TheGuardsAreAddedToAFileWithOtherHooksAndNothingElseChanges()
    {
        var updated = GuardHooks.Install(Other, "/home/ada/.claude/hooks");

        var root = JsonNode.Parse(updated)!;
        root["model"]!.GetValue<string>().ShouldBe("opus");
        Commands(updated, "Stop").ShouldBe(["/x/stop.sh"]);
        Commands(updated, "PreToolUse").ShouldBe(["/x/mine.sh", "/home/ada/.claude/hooks/big_read_guard.sh", "/home/ada/.claude/hooks/pgrep_self_match_guard.sh"]);
        Commands(updated, "PostToolUse").ShouldBe(["/home/ada/.claude/hooks/fat_result_notice.sh"]);
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        pre[0]!["matcher"]!.GetValue<string>().ShouldBe("Edit");
        (pre[1]!["matcher"]!.GetValue<string>(), pre[2]!["matcher"]!.GetValue<string>()).ShouldBe(("", "Bash"));
        GuardHooks.Missing(updated).ShouldBeEmpty();
    }

    [Fact]
    public void AnEmptyOrMissingFileGetsThemToo()
    {
        GuardHooks.Missing("").Count.ShouldBe(3);
        var updated = GuardHooks.Install("", "/h");
        GuardHooks.Missing(updated).ShouldBeEmpty();
    }

    [Fact]
    public void ASecondInstallGivesTheSameTextBack()
    {
        var once = GuardHooks.Install(Other, "/h");
        GuardHooks.Install(once, "/h").ShouldBeSameAs(once);
    }

    [Theory]
    [InlineData("bash /opt/ra/big_read_guard.sh")]
    [InlineData("\"C:/Users/ada x/.claude/hooks/big_read_guard.sh\"")]
    [InlineData("/usr/bin/env sh $HOME/.claude/hooks/big_read_guard.sh")]
    [InlineData("$HOME/.claude/hooks/big_read_guard.sh")]
    public void AGuardWiredByHandInAnotherSpellingCountsAndIsLeftAlone(string command)
    {
        var settings = $$"""{ "hooks": { "PreToolUse": [ { "matcher": "", "hooks": [ { "type": "command", "command": {{JsonValue.Create(command)!.ToJsonString()}} } ] } ] } }""";

        GuardHooks.Missing(settings).Select(g => g.Script).ShouldBe(["fat_result_notice.sh", "pgrep_self_match_guard.sh"]);
        Commands(GuardHooks.Install(settings, "/h"), "PreToolUse").ShouldBe([command, "/h/pgrep_self_match_guard.sh"]);
    }

    [Fact]
    public void ACommandThatOnlyMentionsAScriptIsNotWiringIt()
    {
        var settings = """{ "hooks": { "PreToolUse": [ { "hooks": [ { "type": "command", "command": "echo big_read_guard.sh" } ] } ] } }""";

        GuardHooks.Missing(settings).Count.ShouldBe(3);
    }

    [Fact]
    public void AScriptUnderTheWrongEventIsNotTheGuard()
    {
        var settings = """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "/h/fat_result_notice.sh" } ] } ] } }""";

        GuardHooks.Missing(settings).Select(g => g.Script).ShouldContain("fat_result_notice.sh");
    }

    [Fact]
    public void ACommandWithASpaceInItsFolderIsQuoted() =>
        GuardHooks.Command(@"C:\Users\Ada B\.claude\hooks", "big_read_guard.sh").ShouldBe("\"C:/Users/Ada B/.claude/hooks/big_read_guard.sh\"");

    private (NodeSetup Setup, string Settings) Setup(string? clone, string settingsText)
    {
        var settings = Path.Combine(_root, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, settingsText);
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), "pilot-node", Path.Combine(_root, ".claude"))
        {
            SettingsFile = settings,
            FindRunawayAgent = () => clone,
            Run = (_, _, _) => (1, ""),
        };
        return (setup, settings);
    }

    private string Clone()
    {
        var clone = Directory.CreateDirectory(Path.Combine(_root, "src", "runaway-agent")).FullName;
        foreach (var guard in GuardHooks.Guards)
        {
            File.WriteAllText(Path.Combine(clone, guard.Script), $"#!/usr/bin/env bash\n# {guard.Script}\n");
        }
        return clone;
    }

    private static SetupCheck GuardCheck(NodeSetup setup) => setup.Checks().Single(c => c.Key == NodeSetup.GuardHooksKey);

    [Fact]
    public async Task TheCheckOffersTheFixWithACloneAndItInstallsThemOnceOnly()
    {
        var (setup, settings) = Setup(Clone(), Other);

        var before = GuardCheck(setup);
        (before.Ok, before.Optional, before.Fix).ShouldBe((false, true, "Install guards"));
        before.Line.ShouldContain("big_read_guard.sh, fat_result_notice.sh, pgrep_self_match_guard.sh");

        (await setup.FixAsync(NodeSetup.GuardHooksKey, Ct)).ShouldBeNull();

        var hooks = Path.Combine(_root, ".claude", "hooks");
        GuardHooks.Guards.ShouldAllBe(g => File.Exists(Path.Combine(hooks, g.Script)));
        var after = GuardCheck(setup);
        (after.Ok, after.Fix).ShouldBe((true, null));
        var installed = File.ReadAllText(settings);
        JsonNode.Parse(installed)!["model"]!.GetValue<string>().ShouldBe("opus");
        var stamp = File.GetLastWriteTimeUtc(settings);

        (await setup.FixAsync(NodeSetup.GuardHooksKey, Ct)).ShouldBeNull();

        File.ReadAllText(settings).ShouldBe(installed);
        File.GetLastWriteTimeUtc(settings).ShouldBe(stamp);
    }

    [Fact]
    public async Task ACloneThatHasMovedOnFailsTheCheckUntilTheFixCopiesItAgain()
    {
        var clone = Clone();
        var (setup, settings) = Setup(clone, Other);
        (await setup.FixAsync(NodeSetup.GuardHooksKey, Ct)).ShouldBeNull();
        GuardCheck(setup).Ok.ShouldBeTrue();

        File.WriteAllText(Path.Combine(clone, "fat_result_notice.sh"), "#!/usr/bin/env bash\n# a newer one\n");

        var stale = GuardCheck(setup);
        (stale.Ok, stale.Optional, stale.Fix).ShouldBe((false, true, "Install guards"));
        stale.Line.ShouldContain("fat_result_notice.sh");
        stale.Line.ShouldNotContain("big_read_guard.sh");

        (await setup.FixAsync(NodeSetup.GuardHooksKey, Ct)).ShouldBeNull();

        var hooks = Path.Combine(_root, ".claude", "hooks");
        File.ReadAllText(Path.Combine(hooks, "fat_result_notice.sh")).ShouldContain("a newer one");
        var fresh = GuardCheck(setup);
        (fresh.Ok, fresh.Fix).ShouldBe((true, null));

        var installed = File.ReadAllText(settings);
        var stamps = GuardHooks.Guards.Select(g => File.GetLastWriteTimeUtc(Path.Combine(hooks, g.Script))).ToList();
        var settingsStamp = File.GetLastWriteTimeUtc(settings);

        (await setup.FixAsync(NodeSetup.GuardHooksKey, Ct)).ShouldBeNull();

        File.ReadAllText(settings).ShouldBe(installed);
        File.GetLastWriteTimeUtc(settings).ShouldBe(settingsStamp);
        GuardHooks.Guards.Select(g => File.GetLastWriteTimeUtc(Path.Combine(hooks, g.Script))).ShouldBe(stamps);
    }

    [Fact]
    public async Task WithNoCloneTheCheckSaysWhereToGetItAndTheFixRefuses()
    {
        var (setup, settings) = Setup(null, Other);

        var check = GuardCheck(setup);
        (check.Ok, check.Fix, check.Optional).ShouldBe((false, null, true));
        check.Line.ShouldContain(GuardHooks.Home);
        (await setup.FixAsync(NodeSetup.GuardHooksKey, Ct)).ShouldNotBeNull().ShouldContain(GuardHooks.Home);
        File.ReadAllText(settings).ShouldBe(Other);
    }

    [Fact]
    public void AMissingSettingsFileIsAllGuardsMissing()
    {
        var (setup, settings) = Setup(null, "");
        File.Delete(settings);

        GuardCheck(setup).Ok.ShouldBeFalse();
    }

    [Fact]
    public void ASettingsFileThatIsNotJsonIsReportedNotThrown()
    {
        var (setup, _) = Setup(Clone(), "not json");

        var check = GuardCheck(setup);
        (check.Ok, check.Fix).ShouldBe((false, null));
    }
}
