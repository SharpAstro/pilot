using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>pilot's allow rules in Claude Code's <c>permissions.allow</c>: the missing-rule diff on fixture settings texts,
/// the install that appends only those, and the setup check and fix (with its backup) around them.</summary>
public sealed class ClaudePermissionsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "p2cp-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string NodePath = "/opt/pilot2/bin/pilot-node";

    private static readonly IReadOnlyList<string> Rules = ClaudePermissions.Rules(NodePath);

    private const string Other = """
        {
          "model": "opus",
          "permissions": { "allow": [ "Bash(git status:*)", "Read" ], "deny": [ "Bash(rm -rf:*)" ] },
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "/x/stop.sh" } ] } ]
          }
        }
        """;

    private static IEnumerable<string> Allow(string settings) =>
        JsonNode.Parse(settings)!["permissions"]!["allow"]!.AsArray().Select(e => e!.GetValue<string>());

    [Fact]
    public void TheRulesAreEachVerbForBothShellsThenTheNodesBinariesByPath()
    {
        Rules.ShouldContain("Bash(pilot update:*)");
        Rules.ShouldContain("PowerShell(pilot update:*)");
        foreach (var verb in new[] { "update", "node", "answer", "dismiss", "settings", "job", "jobs", "cancel", "leg", "flight", "review", "tell", "say", "cards", "status", "sessions" })
        {
            Rules.ShouldContain($"Bash(pilot {verb}:*)");
            Rules.ShouldContain($"PowerShell(pilot {verb}:*)");
        }
        Rules.ShouldContain($"Bash({NodePath}:*)");
        Rules.ShouldContain($"PowerShell({NodePath}:*)");
        var cli = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(NodePath))!, "pilot");
        Rules.ShouldContain($"Bash({cli}:*)");
        Rules.ShouldContain($"PowerShell({cli}:*)");
        Rules.Count.ShouldBe((16 + 2) * 2);
        Rules.Distinct().Count().ShouldBe(Rules.Count);
    }

    [Fact]
    public void ABinaryNamedPilotIsNotRuledTwice() =>
        ClaudePermissions.Rules("/bin/pilot").Count.ShouldBe((16 + 1) * 2);

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{ "permissions": {} }""")]
    [InlineData("""{ "permissions": { "allow": [] } }""")]
    public void NoFileOrNoPermissionsMissesEveryRule(string settings) =>
        ClaudePermissions.Missing(settings, Rules).ShouldBe(Rules);

    [Fact]
    public void SomeRulesPresentLeavesTheOthersMissingInOrder()
    {
        var settings = $$"""{ "permissions": { "allow": [ "Bash(pilot update:*)", "PowerShell(pilot answer:*)", "Bash({{NodePath}}:*)" ] } }""";

        var missing = ClaudePermissions.Missing(settings, Rules);

        missing.ShouldBe(Rules.Except(["Bash(pilot update:*)", "PowerShell(pilot answer:*)", $"Bash({NodePath}:*)"]).ToList());
        missing.ShouldContain("Bash(pilot answer:*)");
        missing.ShouldContain("PowerShell(pilot update:*)");
    }

    [Fact]
    public void AllRulesPresentMissesNoneAndInstallsNothing()
    {
        var settings = new JsonObject { ["permissions"] = new JsonObject { ["allow"] = new JsonArray(Rules.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()) } }.ToJsonString();

        ClaudePermissions.Missing(settings, Rules).ShouldBeEmpty();
        ClaudePermissions.Install(settings, Rules).ShouldBeSameAs(settings);
    }

    [Theory]
    [InlineData("""{ "permissions": { "allow": [ "Bash" ] } }""", "Bash")]
    [InlineData("""{ "permissions": { "allow": [ "Bash(pilot:*)" ] } }""", "Bash(pilot update:*)")]
    public void AWiderRuleCoversThePilotRulesItIncludes(string settings, string covered)
    {
        ClaudePermissions.Missing(settings, Rules).ShouldNotContain(covered);
        ClaudePermissions.Missing(settings, Rules).ShouldContain("PowerShell(pilot update:*)");
        ClaudePermissions.Missing(settings, Rules).ShouldNotContain("Bash(pilot answer:*)");
    }

    [Fact]
    public void InstallAppendsOnlyTheMissingOnesAfterTheExistingEntriesInOrder()
    {
        var settings = """{ "permissions": { "allow": [ "Bash(git status:*)", "PowerShell(pilot say:*)", "Read" ] } }""";

        var updated = ClaudePermissions.Install(settings, Rules);

        var allow = Allow(updated).ToList();
        allow.Take(3).ShouldBe(["Bash(git status:*)", "PowerShell(pilot say:*)", "Read"]);
        allow.Skip(3).ShouldBe(Rules.Where(r => r != "PowerShell(pilot say:*)").ToList());
        allow.Distinct().Count().ShouldBe(allow.Count);
        ClaudePermissions.Missing(updated, Rules).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{ "model": "opus" }""")]
    [InlineData("""{ "permissions": {} }""")]
    public void InstallCreatesPermissionsAndAllowWhenAbsent(string settings)
    {
        var updated = ClaudePermissions.Install(settings, Rules);

        Allow(updated).ShouldBe(Rules);
    }

    [Fact]
    public void InstallKeepsEveryOtherKeyAndHookAsItWas()
    {
        var updated = ClaudePermissions.Install(Other, Rules);

        var before = JsonNode.Parse(Other)!.AsObject();
        var after = JsonNode.Parse(updated)!.AsObject();
        after["model"]!.GetValue<string>().ShouldBe("opus");
        JsonNode.DeepEquals(after["hooks"], before["hooks"]).ShouldBeTrue();
        JsonNode.DeepEquals(after["permissions"]!["deny"], before["permissions"]!["deny"]).ShouldBeTrue();
        after.Select(p => p.Key).ShouldBe(before.Select(p => p.Key));
        Allow(updated).Take(2).ShouldBe(["Bash(git status:*)", "Read"]);
    }

    [Fact]
    public void ASecondInstallGivesTheSameTextBack()
    {
        var once = ClaudePermissions.Install(Other, Rules);

        ClaudePermissions.Install(once, Rules).ShouldBeSameAs(once);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "permissions": [] }""")]
    [InlineData("""{ "permissions": { "allow": "Bash" } }""")]
    public void ATextThatIsNotASettingsObjectIsRefused(string settings)
    {
        Should.Throw<JsonException>(() => ClaudePermissions.Missing(settings, Rules));
        Should.Throw<JsonException>(() => ClaudePermissions.Install(settings, Rules));
    }

    [Fact]
    public void TheMissingRulesAreDescribedGroupedByTheToolsTheyMiss()
    {
        var settings = """{ "permissions": { "allow": [ "Bash(pilot answer:*)", "PowerShell(pilot update:*)" ] } }""";

        var line = ClaudePermissions.Describe(ClaudePermissions.Missing(settings, Rules));

        // node comes right after update in the verbs, so its two tools' group is met before answer's one.
        line.ShouldStartWith("pilot update (Bash); pilot node, dismiss, settings, job,");
        line.ShouldContain($"{NodePath}, ");
        line.ShouldEndWith("(Bash, PowerShell); pilot answer (PowerShell)");
    }

    private (NodeSetup Setup, string Settings) Setup(string? settingsText)
    {
        var settings = Path.Combine(_root, ".claude", "settings.json");
        if (settingsText is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            File.WriteAllText(settings, settingsText);
        }
        var setup = new NodeSetup(new NodeOptions("/tmp/n.sock", _root), NodePath, Path.Combine(_root, ".claude"))
        {
            SettingsFile = settings,
            FindRunawayAgent = () => null,
            Run = (_, _, _) => (1, ""),
        };
        return (setup, settings);
    }

    private static SetupCheck PermissionsCheck(NodeSetup setup) => setup.Checks().Single(c => c.Key == NodeSetup.ClaudePermissionsKey);

    [Fact]
    public async Task TheCheckIsTodoNamingWhatIsMissingUntilItsFixAllowsThemAndASecondFixChangesNothing()
    {
        var (setup, settings) = Setup(Other);

        var before = PermissionsCheck(setup);
        (before.Ok, before.Optional, before.Fix).ShouldBe((false, false, "Allow pilot's commands"));
        before.Line.ShouldContain($"{Rules.Count} of {Rules.Count}");
        before.Line.ShouldContain("pilot update, node, answer, dismiss, settings, job, jobs, cancel, leg, flight, review, tell, say, cards, status, sessions");
        before.Line.ShouldContain($"{NodePath}");
        before.Line.ShouldContain("(Bash, PowerShell)");
        before.Line.ShouldContain(settings);
        before.Line.ShouldContain("node still refuses a caller without the role");

        (await setup.FixAsync(NodeSetup.ClaudePermissionsKey, Ct)).ShouldBeNull();

        var after = PermissionsCheck(setup);
        (after.Ok, after.Fix).ShouldBe((true, null));
        var written = File.ReadAllText(settings);
        ClaudePermissions.Missing(written, Rules).ShouldBeEmpty();

        var modified = File.GetLastWriteTimeUtc(settings);
        (await setup.FixAsync(NodeSetup.ClaudePermissionsKey, Ct)).ShouldBeNull();
        File.ReadAllText(settings).ShouldBe(written);
        File.GetLastWriteTimeUtc(settings).ShouldBe(modified);
    }

    [Fact]
    public async Task TheFixKeepsTheFileAsItWasBesideItBeforeWriting()
    {
        var (setup, settings) = Setup(Other);

        (await setup.FixAsync(NodeSetup.ClaudePermissionsKey, Ct)).ShouldBeNull();

        File.ReadAllText(settings + ".pilot-bak").ShouldBe(Other);
        File.ReadAllText(settings).ShouldNotBe(Other);

        // Nothing left to do: no second backup over the first.
        await File.WriteAllTextAsync(settings + ".pilot-bak", "kept", Ct);
        (await setup.FixAsync(NodeSetup.ClaudePermissionsKey, Ct)).ShouldBeNull();
        File.ReadAllText(settings + ".pilot-bak").ShouldBe("kept");
    }

    [Fact]
    public async Task AMissingFileIsCreatedWithTheRulesAndNothingIsBackedUp()
    {
        var (setup, settings) = Setup(null);
        PermissionsCheck(setup).Ok.ShouldBeFalse();

        (await setup.FixAsync(NodeSetup.ClaudePermissionsKey, Ct)).ShouldBeNull();

        Allow(File.ReadAllText(settings)).ShouldBe(Rules);
        File.Exists(settings + ".pilot-bak").ShouldBeFalse();
        PermissionsCheck(setup).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task MalformedJsonIsRefusedAndTheFileIsLeftByteForByte()
    {
        const string Broken = "{ \"permissions\": { \"allow\": [ \"Bash\" ,, } ";
        var (setup, settings) = Setup(Broken);
        var bytes = File.ReadAllBytes(settings);

        var refusal = await setup.FixAsync(NodeSetup.ClaudePermissionsKey, Ct);

        refusal.ShouldNotBeNull().ShouldContain(settings);
        File.ReadAllBytes(settings).ShouldBe(bytes);
        File.Exists(settings + ".pilot-bak").ShouldBeFalse();
        File.Exists(settings + ".pilot.tmp").ShouldBeFalse();
        var check = PermissionsCheck(setup);
        (check.Ok, check.Line).ShouldBe((false, check.Line));
        check.Line.ShouldContain(settings);
    }

    [Fact]
    public async Task TheSetupKeyIsListedAmongTheOnesSetupTakes()
    {
        var (setup, _) = Setup(Other);

        (await setup.FixAsync("nonsense", Ct)).ShouldNotBeNull().ShouldContain(NodeSetup.ClaudePermissionsKey);
    }
}
