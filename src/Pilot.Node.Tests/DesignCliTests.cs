using Pilot.Commands;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The <c>pilot design</c> and <c>pilot designs</c> commands (PIL1096) through the CLI, against the in-memory node.</summary>
public sealed class DesignCliTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private string DesignFolder(string id) => Path.Combine(_node.Options.DataFolder, "designs", id);

    private async Task<(int Exit, string Output)> Pilot(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await PilotCommand.RunAsync(["--node-socket", _node.SocketPath, .. args], _node.Folder, output, error, Ct);
        return (exit, output.ToString() + error);
    }

    [Fact]
    public async Task NewPrintsTheIdOfEachDesignInTurn()
    {
        var (exit, first) = await Pilot("design", "new", "--repo", "pilot", "--", "A login screen with a button");
        exit.ShouldBe(0);
        first.ShouldContain("DSN-1");
        (await Pilot("design", "new", "--title", "Settings", "--", "second")).Output.ShouldContain("DSN-2");

        var (_, shown) = await Pilot("design", "show", "DSN-1");
        shown.ShouldContain("DSN-1" + Environment.NewLine);
        shown.ShouldContain("repo: pilot");
        (await Pilot("design", "show", "DSN-2")).Output.ShouldContain("DSN-2  Settings");
    }

    [Fact]
    public async Task ADesignIsNeverTitledFromItsBriefAndNewRefusesATitleOverForty()
    {
        await Pilot("design", "new", "--", new string('w', 120));
        (await Pilot("design", "show", "DSN-1")).Output.ShouldContain("DSN-1" + Environment.NewLine);
        var (exit, said) = await Pilot("design", "new", "--title", new string('t', 41), "--", "brief");
        exit.ShouldNotBe(0);
        said.ShouldContain("41 characters, over the limit of 40");
    }

    [Fact]
    public async Task DesignTitleSetsTheRowAndTheDraftsHeadingAndRefusesEmptyOrOverForty()
    {
        await Pilot("design", "new", "--", "brief");
        (await Pilot("design", "draft", "DSN-1")).Exit.ShouldBe(0);

        var (exit, said) = await Pilot("design", "title", "DSN-1", "--", "Disk", "alert");
        exit.ShouldBe(0, said);
        (await Pilot("design", "show", "DSN-1")).Output.ShouldContain("DSN-1  Disk alert");
        File.ReadAllText(Path.Combine(DesignFolder("DSN-1"), "draft", "DESIGN.md")).ShouldStartWith("# Disk alert");

        var exact = new string('t', 40);
        (await Pilot("design", "title", "DSN-1", "--", exact)).Exit.ShouldBe(0);
        var (overExit, over) = await Pilot("design", "title", "DSN-1", "--", exact + "t");
        overExit.ShouldNotBe(0);
        over.ShouldContain("41 characters, over the limit of 40");
        var (emptyExit, empty) = await Pilot("design", "title", "DSN-1", "--", "   ");
        emptyExit.ShouldNotBe(0);
        empty.ShouldContain("title is empty");
        (await Pilot("design", "show", "DSN-1")).Output.ShouldContain("DSN-1  " + exact);
    }

    [Fact]
    public async Task BriefAppendsTheOwnersWordsUnderTheirTimeAndShowReadsThem()
    {
        await Pilot("design", "new", "--", "first words");
        var (exit, said) = await Pilot("design", "brief", "DSN-1", "--", "and", "then", "more");
        exit.ShouldBe(0);
        said.ShouldContain("DSN-1");

        var (_, shown) = await Pilot("design", "show", "DSN-1");
        shown.ShouldContain("brief:");
        var first = shown.IndexOf("first words", StringComparison.Ordinal);
        var second = shown.IndexOf("and then more", StringComparison.Ordinal);
        first.ShouldBeGreaterThan(0);
        second.ShouldBeGreaterThan(first);
        shown.ShouldContain("## 20");
        File.ReadAllText(Path.Combine(DesignFolder("DSN-1"), "BRIEF.md")).ShouldContain("and then more");
    }

    [Fact]
    public async Task SayAppendsToTheBriefKeepsAPictureAndChatReadsItBackWithTheDesignersStatus()
    {
        await Pilot("design", "new", "--", "first words");
        var picture = Path.Combine(_node.Folder, "sketch.png");
        await File.WriteAllBytesAsync(picture, [1, 2, 3], Ct);

        var (exit, said) = await Pilot("design", "say", "DSN-1", "--picture", picture, "--", "wider", "please");
        exit.ShouldBe(0);
        said.ShouldContain("DSN-1");

        File.ReadAllText(Path.Combine(DesignFolder("DSN-1"), "BRIEF.md")).ShouldContain("wider please");
        File.Exists(Path.Combine(DesignFolder("DSN-1"), "pictures", "1-sketch.png")).ShouldBeTrue();

        var (chatExit, chat) = await Pilot("design", "chat", "DSN-1");
        chatExit.ShouldBe(0);
        chat.ShouldContain("you: wider please");
        chat.ShouldContain("[picture: pictures/1-sketch.png]");
        chat.ShouldContain("designer: ");
        (await Pilot("design", "chat", "DSN-1", "--after", "1")).Output.ShouldNotContain("you: wider please");
    }

    [Fact]
    public async Task SayAndChatRefuseADesignThatIsNotThere()
    {
        (await Pilot("design", "say", "DSN-5", "--", "words")).Exit.ShouldBe(1);
        var (exit, said) = await Pilot("design", "chat", "DSN-5");
        exit.ShouldBe(1);
        said.ShouldContain("There is no design DSN-5");
    }

    [Fact]
    public async Task DraftMakesTheStarterRefusesOverItAndReplaceMakesItAgain()
    {
        await Pilot("design", "new", "--", "brief");

        var (exit, made) = await Pilot("design", "draft", "DSN-1");
        exit.ShouldBe(0);
        made.ShouldContain("DSN-1 draft");
        File.Exists(Path.Combine(DesignFolder("DSN-1"), "draft", "prototype.json")).ShouldBeTrue();
        File.Exists(Path.Combine(DesignFolder("DSN-1"), "draft", "DESIGN.md")).ShouldBeTrue();
        File.Exists(Path.Combine(DesignFolder("DSN-1"), "draft", "screens", "home.json")).ShouldBeTrue();

        var (again, refused) = await Pilot("design", "draft", "DSN-1");
        again.ShouldBe(1);
        refused.ShouldContain("refused");
        refused.ShouldContain("--replace");

        (await Pilot("design", "draft", "DSN-1", "--replace")).Exit.ShouldBe(0);
    }

    [Fact]
    public async Task ShowAndBriefRefuseADesignThatIsNotThere()
    {
        var (exit, said) = await Pilot("design", "show", "DSN-5");
        exit.ShouldBe(1);
        said.ShouldContain("There is no design DSN-5");
        (await Pilot("design", "brief", "DSN-5", "--", "words")).Exit.ShouldBe(1);
        (await Pilot("design", "show", "nonsense")).Output.ShouldContain("There is no design nonsense");
    }

    [Fact]
    public async Task NewTakesTerminalTargetsAndTerminalsSetsAndClearsThem()
    {
        var (exit, made) = await Pilot("design", "new", "--terminal", "80x24", "--terminal", "100x30", "--", "a console view");
        exit.ShouldBe(0);
        made.ShouldContain("DSN-1");
        (await Pilot("design", "show", "DSN-1")).Output.ShouldContain("terminals: 80x24 100x30");

        (await Pilot("design", "terminals", "DSN-1", "160x44", "250x60", "160x44")).Exit.ShouldBe(0);
        (await Pilot("design", "show", "DSN-1")).Output.ShouldContain("terminals: 160x44 250x60");

        (await Pilot("design", "terminals", "DSN-1")).Exit.ShouldBe(0);
        (await Pilot("design", "show", "DSN-1")).Output.ShouldNotContain("terminals:");
    }

    [Fact]
    public async Task ASizeThatIsNotColsXRowsIsRefusedAndTheTargetsStay()
    {
        await Pilot("design", "new", "--terminal", "80x24", "--", "brief");

        var (exit, said) = await Pilot("design", "terminals", "DSN-1", "wide");
        exit.ShouldBe(1);
        said.ShouldContain("'wide' is not a terminal size");
        (await Pilot("design", "new", "--terminal", "80", "--", "other")).Exit.ShouldBe(1);
        (await Pilot("design", "show", "DSN-1")).Output.ShouldContain("terminals: 80x24");
        (await Pilot("design", "terminals", "DSN-9", "80x24")).Output.ShouldContain("There is no design DSN-9");
    }

    [Fact]
    public async Task CheckAndRenderUseTheDesignsTerminalTargets()
    {
        await Pilot("design", "new", "--terminal", "80x24", "--", "brief");
        await Pilot("design", "draft", "DSN-1");
        var draft = Path.Combine(DesignFolder("DSN-1"), "draft");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Designs", "cells-overlap.prototype.json"), Path.Combine(draft, "prototype.json"), overwrite: true);

        var (checkExit, checkText) = await Pilot("design", "check", "DSN-1");
        checkExit.ShouldBe(1);
        checkText.ShouldContain("home | 80x24 cells | ");

        (await Pilot("design", "render", "DSN-1")).Exit.ShouldBe(0);
        File.Exists(Path.Combine(draft, "renders", "home.80x24.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(draft, "renders", "home.80x24.png")).ShouldBeTrue();

        await Pilot("design", "terminals", "DSN-1");
        (await Pilot("design", "check", "DSN-1")).Output.ShouldNotContain(" cells | ");
    }

    [Fact]
    public async Task DesignsListsEveryDesignNewestFirst()
    {
        (await Pilot("designs")).Output.ShouldContain("No designs");

        await Pilot("design", "new", "--title", "older one", "--", "brief");
        await Pilot("design", "new", "--repo", "tianwen", "--title", "newer one", "--", "brief");

        var (exit, list) = await Pilot("designs");
        exit.ShouldBe(0);
        var lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBe(2);
        lines[0].ShouldStartWith("DSN-2");
        lines[0].ShouldContain("newer one");
        lines[0].ShouldContain("tianwen");
        lines[1].ShouldStartWith("DSN-1");
        lines[1].ShouldContain("no version");
    }
}
