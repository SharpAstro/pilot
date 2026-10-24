using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The views place by arrangement, not arithmetic, as tianwen's do (its widgets-and-controls.md, rule 5, and its
/// DeclaredLayoutTakesDesignUnitsTests): a tree declares what goes where and the layout engine works out the pixels, once,
/// with the DPI. A view that adds up heights (<c>RowH(30f + 18f * lines)</c>), divides by <c>DpiScale</c>, or switches
/// columns on a width it measured itself is laying itself out by hand, and every such sum drifts the first time a font,
/// a line or a padding changes. Rows size to their content (the default, Auto), columns share a <c>TableColumn</c> span,
/// and what drops on a narrow window says so with <c>CollapseBelow</c>.
/// </summary>
/// <remarks>
/// A RATCHET, as tianwen's ChromeMeasuresThroughTheEngineTests: each file may keep what it had when this was written, never
/// more, and a new file none. Lower a number in the same commit as the work that earns it; an allowance no longer used
/// fails too, so a paid debt cannot be spent again. It reads the source by pattern, so a sum carried through a local gets
/// past it: what a pattern cannot see is for review.
/// </remarks>
public sealed class LayoutDrivenTests
{
    /// <summary>What each file may still do, as of 2026-10-21. Lower a number when the work lands; never raise one.</summary>
    private static readonly Dictionary<string, int> Allowed = new(StringComparer.Ordinal)
    {
    };

    // A sizing call; its argument, read to the matching parenthesis, must not compute (an operator, a Math call).
    private static readonly Regex SizingCall = new(@"\.(?:RowH|HFixed|WFixed|ColW|Pad|PadX|WClamp|HClamp|WithGap|CollapseBelow)\(", RegexOptions.Compiled);

    private static readonly Regex Arithmetic = new(@"[+*/]|(?<![\w.])-(?!\d)|\bMath\.", RegexOptions.Compiled);

    // The window's scale applied by hand, a size times or over it: the engine applies it once. Handing it on is not.
    private static readonly Regex DpiScale = new(@"[*/]\s*(?:Math\.\w+\(\s*)?DpiScale\b|\bDpiScale\s*[*/]", RegexOptions.Compiled);

    // A breakpoint, a width held against a named threshold (width >= WideWidth): what CollapseBelow says declaratively.
    private static readonly Regex WidthBreakpoint = new(@"[<>]=?\s*[A-Z]\w*Width\b|\b[A-Z]\w*Width\s*[<>]=?", RegexOptions.Compiled);

    // Literals and comments blanked, so a word in a string or a doc comment is not code.
    private static readonly Regex Literal = new(
        @"'(?:[^'\\\n]|\\.)'|@?\$?""(?:[^""\\\n]|\\.)*""|//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void NoViewLaysItselfOutByHandMoreThanItIsAllowedTo()
    {
        if (ViewSources() is not { } sources)
        {
            return;
        }

        var over = sources
            .Select(s => (s.Name, Count: Offences(s.Text).Count, Allowed: Allowed.GetValueOrDefault(s.Name)))
            .Where(f => f.Count > f.Allowed)
            .Select(f => $"{f.Name}: {f.Count} (allowed {f.Allowed}): {string.Join("; ", Offences(sources.Single(s => s.Name == f.Name).Text))}")
            .ToList();
        over.ShouldBeEmpty("a view computes a size, reads DpiScale, or breaks on a width it measured: declare it in the tree instead "
            + "(Auto rows, a TableColumn span, CollapseBelow), or lower nothing and say why in review");
    }

    // What may wait: a lock, a blocking wait on a task, a sleep. The UI's code is the render thread's, or shares state
    // with it, and the render thread never waits on anyone: state that crosses threads is an immutable record swapped
    // whole (Volatile, ImmutableInterlocked), work that takes time goes to the pool (SE1, 2026-10-24: a window that froze).
    private static readonly Regex Waits = new(
        @"\block\s*\(|\.Wait\s*\(|\.Result\b|GetAwaiter\s*\(\s*\)\s*\.GetResult|Thread\.Sleep|\bSemaphoreSlim\b|\bMonitor\.|ReaderWriterLock|\.WaitAll\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void NothingInTheUiWaits()
    {
        if (ViewSources() is not { } sources)
        {
            return;
        }

        var waits = sources
            .SelectMany(s => Waits.Matches(Literal.Replace(s.Text, m => new string(' ', m.Length))).Select(m => $"{s.Name}: {m.Value}"))
            .ToList();
        waits.ShouldBeEmpty("the UI never waits: swap an immutable record (Volatile.Read/Write, ImmutableInterlocked) instead of a lock, "
            + "and run what takes time on the pool (tracker.Run, Task.Run) instead of waiting for it");
    }

    [Fact]
    public void TheAllowanceListHasNoEntriesThatAreNoLongerEarned()
    {
        if (ViewSources() is not { } sources)
        {
            return;
        }

        var stale = Allowed
            .Select(a => (a.Key, a.Value, Count: sources.FirstOrDefault(s => s.Name == a.Key).Text is { } text ? Offences(text).Count : 0))
            .Where(a => a.Count < a.Value)
            .Select(a => $"{a.Key}: allowed {a.Value}, uses {a.Count}")
            .ToList();
        stale.ShouldBeEmpty("an allowance is higher than the file needs: lower it to what it uses, so the paid debt cannot be spent again");
    }

    /// <summary>What a source does by hand, a line each: the matched text.</summary>
    internal static List<string> Offences(string source)
    {
        var code = Literal.Replace(source, m => new string(' ', m.Length));
        var found = new List<string>();
        foreach (Match call in SizingCall.Matches(code))
        {
            var start = call.Index + call.Length;
            var depth = 1;
            var end = start;
            while (end < code.Length && depth > 0)
            {
                depth += code[end] switch { '(' => 1, ')' => -1, _ => 0 };
                end++;
            }
            if (Arithmetic.IsMatch(code[start..(end - 1)]))
            {
                found.Add(LineOf(code, call.Index));
            }
        }
        found.AddRange(DpiScale.Matches(code).Select(m => LineOf(code, m.Index)));
        found.AddRange(WidthBreakpoint.Matches(code).Select(m => LineOf(code, m.Index)));
        return found;
    }

    private static string LineOf(string code, int index)
    {
        var start = code.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = code.IndexOf('\n', index);
        return code[start..(end < 0 ? code.Length : end)].Trim();
    }

    /// <summary>The views' sources by their path under src (<c>Pilot.Ui/CardsView.cs</c>): every .cs of Pilot.Ui, Pilot.Gui and Pilot.Tui; null when the sources are not beside the tests.</summary>
    private static (string Name, string Text)[]? ViewSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pilot.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            return null;
        }
        return [.. new[] { "Pilot.Ui", "Pilot.Gui", "Pilot.Tui" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(dir.FullName, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(dir.FullName, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(f)))];
    }
}
