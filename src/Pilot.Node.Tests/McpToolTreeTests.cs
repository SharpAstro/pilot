using System.CommandLine;
using System.Text.Json;
using Pilot.Commands;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>PIL1087: a subcommand's MCP tool carries the arguments and options of its parent commands, and a call spells them where the CLI parses them.</summary>
public sealed class McpToolTreeTests(ITestOutputHelper output)
{
    private static RootCommand Root() => PilotCommand.Build(Path.GetTempPath(), TextWriter.Null, TextWriter.Null, "mcp", agent: true);

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static string Key(Option option) => option.Name.TrimStart('-');

    /// <summary>The visible arguments and own options of every ancestor of the leaf that scope its subcommands (McpServer.ScopingKeys), as (name, required).</summary>
    private static IEnumerable<(string Name, bool Required)> AncestorKeys(Command[] chain) =>
        chain[..^1].SelectMany(c =>
            c.Arguments.Where(a => !a.Hidden && !chain[^1].Arguments.Contains(a) && McpServer.ScopingKeys.Contains($"{c.Name}:{a.Name}")).Select(a => (a.Name, a.Arity.MinimumNumberOfValues > 0))
                .Concat(c.Options.Where(o => !o.Hidden && !chain[^1].Options.Contains(o) && o.Name is not ("--help" or "--version") && McpServer.ScopingKeys.Contains($"{c.Name}:{Key(o)}")).Select(o => (Key(o), o.Required))));

    /// <summary>The tools whose schema lacks (or does not require) a key of an ancestor command; the check a second test feeds a broken schema.</summary>
    internal static List<string> Lacking(Dictionary<string, (string[] Path, Command[] Chain)> tools, Func<string, string[], Command[], ToolSchema> schemaOf)
    {
        var lacking = new List<string>();
        foreach (var (name, (path, chain)) in tools)
        {
            var schema = schemaOf(name, path, chain);
            foreach (var (key, required) in AncestorKeys(chain))
            {
                // A name the leaf shares is the leaf's own; the clash test names it.
                if (!schema.Properties.ContainsKey(key) || (required && !schema.Required.Contains(key)))
                {
                    lacking.Add($"{name}: {key}");
                }
            }
        }
        return lacking;
    }

    [Fact]
    public void JobArtifactTakesTheJobItsParentTakes()
    {
        var tools = McpServer.Tools(Root());
        var (path, chain) = tools["job_artifact"];
        var schema = McpServer.Describe("job_artifact", path, chain).InputSchema;

        schema.Properties.Keys.ShouldBe(["job", "name", "out", "node"], ignoreOrder: true);
        schema.Required.ShouldBe(["job", "name"], ignoreOrder: true);
        schema.Properties["job"].Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void JobArtifactIsSpelledWhereTheCliParsesIt_AndACallWithoutTheJobIsRefused()
    {
        var root = Root();
        var (path, chain) = McpServer.Tools(root)["job_artifact"];

        var spelled = McpServer.Spell(path, chain, Arguments("""{"job":"j6006","name":"shot.png","out":"x"}"""));
        spelled.ShouldBe(["job", "j6006", "artifact", "--out=x", "--", "shot.png"]);
        var parsed = root.Parse(spelled);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandResult.Command.Name.ShouldBe("artifact");
        parsed.GetValue<string>("name").ShouldBe("shot.png");
        parsed.GetValue<string>("job").ShouldBe("j6006");

        var plain = root.Parse(McpServer.Spell(path, chain, Arguments("""{"job":"j6006","name":"shot.png"}""")));
        plain.Errors.ShouldBeEmpty();
        plain.GetValue<string>("name").ShouldBe("shot.png");

        var schema = McpServer.Describe("job_artifact", path, chain).InputSchema;
        schema.Required.ShouldContain("job");
        // Left out, the parse of the leaf would not notice, so the spelling refuses.
        Should.Throw<PilotCommand.UsageException>(() => McpServer.Spell(path, chain, Arguments("""{"name":"shot.png"}"""))).Message.ShouldContain("job");
    }

    [Fact]
    public void AParentValueThatStartsWithADashIsRefused()
    {
        var (path, chain) = McpServer.Tools(Root())["job_artifact"];

        Should.Throw<PilotCommand.UsageException>(() => McpServer.Spell(path, chain, Arguments("""{"job":"--x","name":"shot.png"}""")));
    }

    [Fact]
    public void AKeyOnNoCommandOfTheChainIsRefusedNamingTheKeysOfAll()
    {
        var (path, chain) = McpServer.Tools(Root())["job_artifact"];

        var ex = Should.Throw<PilotCommand.UsageException>(() => McpServer.Spell(path, chain, Arguments("""{"nope":"x"}""")));
        ex.Message.ShouldContain("job");
        ex.Message.ShouldContain("name");
    }

    /// <summary>PIL1088: the run tool takes a cwd key, and a call with it reaches the CLI as --cwd.</summary>
    [Fact]
    public void TheRunToolTakesACwdThatReachesTheCliAsCwd()
    {
        var root = Root();
        var (path, chain) = McpServer.Tools(root)["run"];

        McpServer.Describe("run", path, chain).InputSchema.Properties.ShouldContainKey("cwd");
        var spelled = McpServer.Spell(path, chain, Arguments("""{"cwd":"chess","command":["echo","x"]}"""));
        spelled.ShouldContain("--cwd=chess");
        var parsed = root.Parse(spelled);
        parsed.Errors.ShouldBeEmpty();
        parsed.GetValue<string>("--cwd").ShouldBe("chess");
    }

    [Fact]
    public void EveryToolCarriesTheArgumentsAndOptionsOfItsParents()
    {
        var tools = McpServer.Tools(Root());

        Lacking(tools, (name, path, chain) => McpServer.Describe(name, path, chain).InputSchema).ShouldBeEmpty();
    }

    [Fact]
    public void AToolWhoseSchemaLacksAParentArgumentIsReported()
    {
        var tools = McpServer.Tools(Root());
        var broken = Lacking(tools, (name, path, chain) =>
        {
            var schema = McpServer.Describe(name, path, chain).InputSchema;
            if (name != "job_artifact")
            {
                return schema;
            }
            var properties = new Dictionary<string, PropertySchema>(schema.Properties);
            properties.Remove("job");
            return new ToolSchema(properties, [.. schema.Required.Where(r => r != "job")]);
        });

        broken.ShouldBe(["job_artifact: job"]);
    }

    /// <summary>job's id is one Argument object shared by job and its subcommands (cancel, pause, resume), so it is the same value, not a clash.</summary>
    [Fact]
    public void NoParentNameClashesWithItsLeafUnlessTheObjectIsShared()
    {
        var clashes = new List<string>();
        foreach (var (tool, (_, chain)) in McpServer.Tools(Root()))
        {
            var leaf = chain[^1];
            foreach (var ancestor in chain[..^1])
            {
                clashes.AddRange(ancestor.Arguments.Where(a => !a.Hidden && !leaf.Arguments.Contains(a) && leaf.Arguments.Any(l => l.Name == a.Name)).Select(a => $"{tool}: {a.Name}"));
                clashes.AddRange(ancestor.Options.Where(o => o.Name is not ("--help" or "--version") && !o.Hidden && !leaf.Options.Contains(o) && leaf.Options.Any(l => l.Name == o.Name)).Select(o => $"{tool}: {Key(o)}"));
                clashes.AddRange(ancestor.Arguments.Where(a => !a.Hidden && !leaf.Arguments.Contains(a) && leaf.Options.Any(l => Key(l) == a.Name)).Select(a => $"{tool}: {a.Name}"));
            }
        }

        clashes.ShouldBeEmpty();
    }

    /// <summary>A parent's own action's input (order's text) is not the subcommand's: order_done takes the id alone and spells as the CLI parses it.</summary>
    [Fact]
    public void OrderDoneStaysCallableWithTheIdAlone()
    {
        var root = Root();
        var (path, chain) = McpServer.Tools(root)["order_done"];

        var schema = McpServer.Describe("order_done", path, chain).InputSchema;
        schema.Required.ShouldBe(["id"]);
        schema.Properties.ContainsKey("text").ShouldBeFalse();
        var spelled = McpServer.Spell(path, chain, Arguments("""{"id":"o1"}"""));
        spelled.ShouldBe(["order", "done", "--", "o1"]);
        var parsed = root.Parse(spelled);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandResult.Command.Name.ShouldBe("done");
    }

    [Fact]
    public void TheToolsWithParentArgumentsAreListed()
    {
        var withParents = McpServer.Tools(Root())
            .Where(t => AncestorKeys(t.Value.Chain).Any())
            .Select(t => $"{t.Key}: {string.Join(", ", AncestorKeys(t.Value.Chain).Select(k => k.Name))}")
            .Order().ToList();

        output.WriteLine("tools with parent arguments or options: " + string.Join("; ", withParents));
        withParents.ShouldBe(["job_artifact: job"]);
        // The schemas agree: no other tool carries a key from an ancestor.
        foreach (var (name, (path, chain)) in McpServer.Tools(Root()))
        {
            var own = chain[^1].Arguments.Select(a => a.Name).Concat(chain[^1].Options.Select(Key)).Append("node").ToHashSet();
            var extra = McpServer.Describe(name, path, chain).InputSchema.Properties.Keys.Where(k => !own.Contains(k)).ToList();
            extra.ShouldBe(name == "job_artifact" ? ["job"] : []);
        }
    }
}
