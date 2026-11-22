using Pilot.Commands;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A design's targets (PIL1097) through the CLI against the in-memory node: <c>check</c> runs at every named target with its name on the finding's line,
/// <c>render</c> writes a PNG per screen per target, <c>publish</c> refuses a render that lacks a named target, and a design naming none behaves as #1096's did.
/// The fixture has a row of two 260 wide panels, which fits a desktop and overflows a phone 390 wide.
/// </summary>
public sealed class DesignTargetsTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private string Draft(string file) => Path.Combine(_node.Options.DataFolder, "designs", "DSN-1", "draft", file);

    private async Task<(int Exit, string Output)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString() + error);
    }

    /// <summary>A design whose draft is the phone fixture with its targets entry replaced by <paramref name="targets"/> (the fixture's own when null).</summary>
    private async Task DraftWith(string? targets = null)
    {
        (await Pilot("design", "new", "--", "A fleet screen")).Exit.ShouldBe(0);
        (await Pilot("design", "draft", "DSN-1")).Exit.ShouldBe(0);
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", "phone.prototype.json"));
        if (targets is not null)
        {
            var from = json.IndexOf("\"targets\"", StringComparison.Ordinal);
            var to = json.IndexOf("\"screens\"", StringComparison.Ordinal);
            json = json[..from] + (targets.Length == 0 ? "" : targets + ",\n  ") + json[to..];
        }
        File.WriteAllText(Draft("prototype.json"), json);
        // The fixture carries its screens (the older shape), so the starter's screens/ folder goes; the document names its two.
        Directory.Delete(Draft("screens"), recursive: true);
        File.WriteAllText(Draft("DESIGN.md"), "# Phone\n\n**Objective:** A home and a settings screen.\n\n## Home\n\n**Objective:** Shows home.\n\n```screen id=home\n```\n\n## Settings\n\n**Objective:** Shows settings.\n\n```screen id=settings\n```\n");
    }

    [Fact]
    public async Task CheckAtPhoneReportsAFindingWithTheTargetsNameThatDesktopAloneDoesNot()
    {
        await DraftWith();

        var (exit, said) = await Pilot("design", "check", "DSN-1");

        exit.ShouldNotBe(0, said);
        var lines = said.Split('\n').Where(l => l.Contains(" | ", StringComparison.Ordinal)).ToArray();
        lines.ShouldNotBeEmpty(said);
        lines.ShouldAllBe(l => l.Contains(" | Phone | ", StringComparison.Ordinal), said);
    }

    [Fact]
    public async Task CheckAtDesktopAloneIsClean()
    {
        await DraftWith(DesktopOnly);

        var (exit, said) = await Pilot("design", "check", "DSN-1");

        exit.ShouldBe(0, said);
        said.Trim().ShouldEndWith("clean");
    }

    private const string DesktopOnly = "\"targets\": [{ \"name\": \"Desktop\", \"width\": 1440, \"height\": 900, \"scale\": 1 }]";

    [Fact]
    public async Task RenderWritesOnePngPerScreenPerNamedTarget()
    {
        await DraftWith("\"targets\": [{ \"name\": \"Desktop\", \"width\": 1440, \"height\": 900, \"scale\": 1 }, { \"name\": \"Phone\", \"width\": 390, \"height\": 844, \"scale\": 3 }]");

        var (exit, said) = await Pilot("design", "render", "DSN-1");

        exit.ShouldBe(0, said);
        Directory.EnumerateFiles(Draft("renders"), "*.png").Select(Path.GetFileName).Order().ShouldBe(
            ["home.Desktop.png", "home.Phone.png", "settings.Desktop.png", "settings.Phone.png"]);
    }

    private const string BadTarget = "\"targets\": [{ \"name\": \"a/b\", \"width\": 390, \"height\": 844, \"scale\": 1 }]";

    [Fact]
    public async Task CheckSaysATargetNameThatIsNotAPlainFileName()
    {
        await DraftWith(BadTarget);

        var (exit, said) = await Pilot("design", "check", "DSN-1");

        exit.ShouldNotBe(0, said);
        said.ShouldContain("$.targets[0].name: 'a/b' is not a plain file name");
    }

    [Fact]
    public async Task RenderRefusesATargetNameThatIsNotAPlainFileNameAndKeepsTheEarlierRenders()
    {
        await DraftWith(DesktopOnly);
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        var before = Directory.EnumerateFiles(Draft("renders")).Select(Path.GetFileName).Order().ToArray();
        before.ShouldNotBeEmpty();
        File.WriteAllText(Draft("prototype.json"), File.ReadAllText(Draft("prototype.json")).Replace("\"name\": \"Desktop\"", "\"name\": \"a/b\""));

        var (exit, said) = await Pilot("design", "render", "DSN-1");

        exit.ShouldNotBe(0, said);
        said.ShouldContain("is not a plain file name");
        said.ShouldNotContain("ArgumentException");
        Directory.EnumerateFiles(Draft("renders")).Select(Path.GetFileName).Order().ShouldBe(before);
    }

    [Fact]
    public async Task PublishRefusesNamingTheTargetWhenItsPngIsMissing()
    {
        await DraftWith(DesktopOnly);
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        File.Delete(Draft(Path.Combine("renders", "home.Desktop.png")));

        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "brief");

        exit.ShouldNotBe(0, said);
        said.ShouldContain("at target Desktop");
        said.ShouldContain("home.Desktop.png");
    }

    [Fact]
    public async Task PublishTakesADesignWhoseTargetsAllHaveTheirPngs()
    {
        await DraftWith(DesktopOnly);
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);

        var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "brief");

        exit.ShouldBe(0, said);
        Directory.EnumerateFiles(Path.Combine(_node.Options.DataFolder, "designs", "DSN-1", "versions", "v1", "renders"), "*.png").Select(Path.GetFileName).Order()
            .ShouldBe(["home.Desktop.png", "settings.Desktop.png"]);
    }

    [Fact]
    public async Task ADesignNamingNoTargetsChecksAndRendersAsBefore()
    {
        await DraftWith("");

        var (exit, said) = await Pilot("design", "render", "DSN-1");

        exit.ShouldBe(0, said);
        Directory.EnumerateFiles(Draft("renders"), "*.png").Select(Path.GetFileName).Order().ShouldBe(["home.png", "settings.png"]);
        (await Pilot("design", "check", "DSN-1")).Exit.ShouldBe(0);
    }

    [Fact]
    public void TheBuiltInTargetsAreTheCatalogue()
    {
        DesignTargets.BuiltIn.Select(t => $"{t.Name} {t.Width}x{t.Height} @{t.Scale}").ShouldBe(
            ["Desktop 1440x900 @1", "Laptop 1280x800 @1.5", "Tablet 820x1180 @2", "Phone 390x844 @3"]);
        DesignTargets.Resolve("phone").ShouldBe(DesignTargets.Phone);
        DesignTargets.Resolve(null, 500, 700)!.Name.ShouldBe("500x700");
    }

    /// <summary>A target name becomes part of a file name: one that could leave the renders folder is refused (the review of #1116).</summary>
    [Theory]
    [InlineData("../../escape")]
    [InlineData("sub/dir")]
    [InlineData("sub\\dir")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("a:b")]
    public void ATargetNameThatIsNoPlainFileNameIsRefused(string name)
    {
        DesignTargets.IsPlainName(name).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => DesignTargets.RenderFile("home", name));
    }

    [Theory]
    [InlineData("Desktop")]
    [InlineData("phone-wide")]
    [InlineData("Wall 4K")]
    public void APlainTargetNameMakesTheRenderFile(string name) =>
        DesignTargets.RenderFile("home", name).ShouldBe($"home.{name}.png");
}
