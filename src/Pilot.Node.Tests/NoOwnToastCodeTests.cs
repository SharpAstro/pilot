using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The node's toasts are AppShell's (#1384): no WinRT toast code of pilot's own, no PowerShell on the toast path, no -windows target.
/// Reads source text only, in milliseconds.
/// </summary>
public sealed class NoOwnToastCodeTests
{
    // Built by concatenation so this file does not match its own needles.
    private static readonly string[] Needles =
    [
        "Windows.UI." + "Notifications",
        "ToastNotification" + "Manager",
        "RoGetActivation" + "Factory",
    ];

    private static string SrcFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pilot.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("Pilot.slnx was not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Sources(string folder) =>
        Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    [Trait("Category", "Invariant")]
    public void NoSourceHoldsWinRtToastCode()
    {
        var src = SrcFolder();
        var offenders = Sources(src)
            .Where(f => File.ReadAllText(f) is var text && Needles.Any(text.Contains))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        offenders.ShouldBeEmpty("toasts go through AppShell's DesktopNotification: " + string.Join(", ", offenders));
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public void NoToastFileMentionsPowerShell()
    {
        var src = SrcFolder();
        var offenders = Sources(Path.Combine(src, "Pilot.Node", "Toasts"))
            .Where(f => File.ReadAllText(f).Contains("PowerShell", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        offenders.ShouldBeEmpty("no PowerShell on the toast path: " + string.Join(", ", offenders));
    }

    [Fact]
    [Trait("Category", "Invariant")]
    public void TheNodeHasNoWindowsTargetFramework()
    {
        var csproj = File.ReadAllText(Path.Combine(SrcFolder(), "Pilot.Node", "Pilot.Node.csproj"));
        csproj.ShouldNotContain("-windows", Case.Insensitive);
    }
}
