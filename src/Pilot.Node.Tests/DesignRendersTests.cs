using Pilot.Commands;
using Pilot.Contracts;
using Pilot.Node.Designs;
using Shouldly;
using SharpAstro.Png;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary><c>design-renders</c> (PIL1186): a design's render PNGs as bytes, one line each, capped, with a draft's stale renders said so.</summary>
public sealed class DesignRendersTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private string DesignFolder => Path.Combine(_node.Options.DataFolder, "designs", "DSN-1");

    private string Draft(string file) => Path.Combine(DesignFolder, "draft", file);

    private async Task<(int Exit, string Output)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString() + error);
    }

    private const string Document = """
        # A fleet screen

        **Objective:** Shows the sample: a home screen, its settings and a confirm modal.

        ## Home

        **Objective:** Shows the fleet at a glance.

        ```screen id=home
        ```

        ## Settings

        **Objective:** Shows what can be changed.

        ```screen id=settings size=Phone
        ```

        ## Confirm

        **Objective:** Shows the question asked before a change.

        ```screen id=confirm
        ```

        """;

    /// <summary>A design with the sample's three screens (entry <c>home</c>), rendered and, unless <paramref name="publish"/> is false, published as v1.</summary>
    private async Task Made(bool publish = true)
    {
        (await Pilot("design", "new", "--", "A fleet screen")).Exit.ShouldBe(0);
        (await Pilot("design", "draft", "DSN-1")).Exit.ShouldBe(0);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", "sample.prototype.json"), Draft("prototype.json"), overwrite: true);
        Directory.Delete(Draft("screens"), recursive: true);
        File.WriteAllText(Draft("DESIGN.md"), Document);
        File.WriteAllText(Draft("NOTES.md"), "The sample.");
        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        if (publish)
        {
            var (exit, said) = await Pilot("design", "publish", "DSN-1", "--why", "brief");
            exit.ShouldBe(0, said);
        }
    }

    private async Task<DesignRenders> Ask(params string[] args)
    {
        var answer = await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.DesignRenders, Target = Verbs.DesignTarget("DSN-1"), Args = args, Who = "test" }, Ct);
        answer.Response!.Ok.ShouldBeTrue(answer.Response.Error);
        return answer.Response.DesignRenders!;
    }

    [Fact]
    public async Task NoScreenGivesTheEntryScreensBytesAndNamesTheOthers()
    {
        await Made();

        var renders = await Ask("", "", "", "");

        renders.Version.ShouldBe(1);
        renders.Stale.ShouldBeFalse();
        var picture = renders.Pictures.ShouldHaveSingleItem();
        picture.Screen.ShouldBe("home");
        picture.Png.ShouldBe(await File.ReadAllBytesAsync(Path.Combine(DesignFolder, "versions", "v1", "renders", "home.png"), Ct));
        picture.PrototypeSha256.ShouldBe(DesignChecker.StampOf(Path.Combine(DesignFolder, "versions", "v1", "renders")));
        picture.ScaledFrom.ShouldBeNull();
        renders.NotSent.ShouldBe(["settings", "confirm"], ignoreOrder: true);

        var plain = PilotCommand.DesignRendersPlain(renders);
        plain.ShouldContain($"DSN-1 v1 screen home size design sha256 {picture.PrototypeSha256}");
        plain.ShouldContain("other screens: ");
    }

    [Fact]
    public async Task ANamedScreenAndSizeNarrowTheAnswerAndAnUnknownOneIsRefusedWithTheOnesThereAre()
    {
        await Made();

        (await Ask("1", "", "settings", "")).Pictures.ShouldAllBe(p => p.Screen == "settings");

        var answer = await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.DesignRenders, Target = Verbs.DesignTarget("DSN-1"), Args = ["", "", "nowhere", ""], Who = "test" }, Ct);
        answer.Response!.Ok.ShouldBeFalse();
        answer.Response.Error.ShouldContain("the screens are");
        var size = await _node.Client.SendAsync(new CommandRequest { Verb = Verbs.DesignRenders, Target = Verbs.DesignTarget("DSN-1"), Args = ["", "", "home", "99x99"], Who = "test" }, Ct);
        size.Response!.Error.ShouldContain("the sizes are");
    }

    [Fact]
    public async Task APictureOverTheCapComesBackScaledAndItsLineSaysSo()
    {
        await Made(publish: false);
        const int width = 3000;
        const int height = 1200;
        var big = PngWriter.Encode(new byte[width * height * 4], width, height);
        await File.WriteAllBytesAsync(Path.Combine(DesignFolder, "draft", "renders", "home.png"), big, Ct);

        var renders = await Ask("", "draft", "home", "");

        var picture = renders.Pictures.ShouldHaveSingleItem();
        picture.ScaledFrom.ShouldBe($"{width}x{height}");
        picture.Width.ShouldBe(DesignRenderCaps.LongestSide);
        picture.Height.ShouldBeLessThan(height);
        picture.Png.Take(8).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        PilotCommand.DesignRendersPlain(renders).ShouldContain($"scaled from {width}x{height}");
    }

    [Fact]
    public async Task ADraftWhosePrototypeChangedAfterItsRenderIsAnsweredAsStaleNotRefused()
    {
        await Made(publish: false);
        (await Ask("", "draft", "", "")).Stale.ShouldBeFalse();

        await File.AppendAllTextAsync(Draft("prototype.json"), "\n", Ct);

        var renders = await Ask("", "draft", "", "");
        renders.Stale.ShouldBeTrue();
        renders.Version.ShouldBeNull();
        renders.Pictures.ShouldNotBeEmpty();
        PilotCommand.DesignRendersPlain(renders).ShouldContain("stale");
    }

    [Fact]
    public async Task MarkdownOutputPrintsAnImageReferencePerPictureOverFilesTheBytesWereWrittenTo()
    {
        await Made();

        var (exit, said) = await Pilot("design", "renders", "DSN-1", "--screen", "home", "--output-format", "markdown");

        exit.ShouldBe(0, said);
        said.ShouldContain("![DSN-1 v1 screen home size design");
        var open = said.IndexOf("](<", StringComparison.Ordinal) + 3;
        var path = said[open..said.IndexOf(">)", open, StringComparison.Ordinal)];
        File.Exists(path).ShouldBeTrue();
        (await File.ReadAllBytesAsync(path, Ct)).ShouldBe(await File.ReadAllBytesAsync(Path.Combine(DesignFolder, "versions", "v1", "renders", "home.png"), Ct));
    }
}
