using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// pilot-tui's keys are declarations (a KeyChord shortcut or a list row on the arranged tree), routed by DIR.Lib's InputRouter.
/// A switch or an if on a key in Pilot.Tui or TerminalInput would be a second, hand-rolled routing that a focused box can swallow.
/// </summary>
[Trait("Category", "Invariant")]
public sealed class TuiKeyDeclarationTests
{
    private static string RepoSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        dir.ShouldNotBeNull("no folder above the test holds src/Pilot.slnx");
        return Path.Combine(dir.FullName, "src");
    }

    // A case label on a key type, or a switch or if over a key, a key-down or a console key.
    private static readonly Regex Offence = new(
        @"\bcase\s+(InputKey|ConsoleKey|InputEvent\.KeyDown|InputEvent\.KeyDown\s*\(|KeyDown\b)|\bcase\s+\w+\.(Key|KeyChar)\b|\bis\s+InputEvent\.KeyDown\s*\{[^}]*Key\s*:|\b(if|switch|when|while)\b[^\n]*(\bInputKey\.|\bConsoleKey\b|\bConsoleKeyInfo\b|InputEvent\.KeyDown|\.Key\s*(==|!=|is\b))|=>\s*[^\n]*\bInputKey\.\w+\s*=>",
        RegexOptions.Compiled);

    // A per-page list switch: an arm per page that names its list or steps a list cursor by hand (the old ShownList, SurfaceCursor and
    // CellListMoved glue). A page declares its list on its rows (Node.ListOf), and the router steps it. PilotChrome's history places
    // name ListId too but never step a cursor, so only the stepping members and the page-to-list tuple are offences.
    private static readonly Regex ListSwitchOffence = new(
        @"\b(ShownList|SurfaceCursor|CellListMoved|MoveListCursor|HandleListKey)\b|\.Step\(|\bPilotPage\.\w+\b[^\n]*=>\s*\(\s*\w+(<\w+>)?\.ListId\b",
        RegexOptions.Compiled);

    [Fact]
    public void NoPerPageListSwitchRemainsInPilotChromeOrTerminalInput()
    {
        var src = RepoSrc();
        var found = new List<string>();
        foreach (var file in new[] { Path.Combine(src, "Pilot.Ui", "PilotChrome.cs"), Path.Combine(src, "Pilot.Ui", "TerminalInput.cs") })
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (!line.StartsWith("//") && ListSwitchOffence.IsMatch(line))
                {
                    found.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {line}");
                }
            }
        }
        found.ShouldBeEmpty("a page declares its list with its own ListCursor (Node.ListOf), not a switch on the page:\n" + string.Join("\n", found));
    }

    [Fact]
    public void NoSwitchOrIfOnAKeyRemainsInPilotTuiOrTerminalInput()
    {
        var src = RepoSrc();
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(Path.Combine(src, "Pilot.Tui"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .Append(Path.Combine(src, "Pilot.Ui", "TerminalInput.cs"))
            .ToList();
        files.Count.ShouldBeGreaterThan(1);

        var found = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (!line.StartsWith("//") && Offence.IsMatch(line))
                {
                    found.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: {line}");
                }
            }
        }
        found.ShouldBeEmpty("key routing belongs to a declared KeyChord on the tree, not a switch or if:\n" + string.Join("\n", found));
    }
}
