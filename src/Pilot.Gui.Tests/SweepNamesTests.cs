using System.Reflection;
using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>
/// The documented filters find every sweep class (pilot #1157): <c>FullyQualifiedName~TerminalWireframeSweep</c> runs the terminal sweep and
/// <c>FullyQualifiedName~WireframeSweep</c> the pixel sweep. A class renamed past its term would pass a filtered run without running.
/// </summary>
[Trait("Category", "Invariant")]
public sealed class SweepNamesTests
{
    private static readonly Type[] Terminal = SweepClasses(typeof(string), typeof(string), typeof(int), typeof(int));
    private static readonly Type[] Pixel = SweepClasses(typeof(string), typeof(string), typeof(int), typeof(float), typeof(int));

    private static Type[] SweepClasses(params Type[] parameters) =>
        [.. typeof(SweepNamesTests).Assembly.GetTypes().Where(t => t.IsClass && t.IsPublic && HasSweepTheory(t, parameters))];

    private static bool HasSweepTheory(Type t, Type[] parameters) =>
        t.GetMethod("Cases", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes) is not null
        && t.GetMethods().Any(m => m.GetCustomAttributes<TheoryAttribute>().Any()
            && m.GetCustomAttributes<MemberDataAttribute>().Any(a => a.MemberName == "Cases")
            && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters));

    [Fact]
    public void EveryTerminalSweepClassIsFoundByTheTerminalFilter() =>
        Terminal.Where(t => !t.FullName!.Contains("TerminalWireframeSweep", StringComparison.Ordinal))
            .Select(t => $"{t.FullName} escapes FullyQualifiedName~TerminalWireframeSweep")
            .ShouldBeEmpty();

    [Fact]
    public void EveryPixelSweepClassIsFoundByThePixelFilter() =>
        Pixel.Where(t => !t.FullName!.Contains("WireframeSweep", StringComparison.Ordinal))
            .Select(t => $"{t.FullName} escapes FullyQualifiedName~WireframeSweep")
            .ShouldBeEmpty();

    [Fact]
    public void TerminalSweepClassesAreTheHelpersSizesTimesStates() =>
        Terminal.Length.ShouldBe(TerminalWireframeSweepTests.Sizes.Length * TerminalWireframeSweepTests.States.Length,
            $"terminal sweep classes: {string.Join(", ", Terminal.Select(t => t.Name))}");

    [Fact]
    public void PixelSweepClassesAreTheHelpersSizeGroups()
    {
        var groups = WireframeSweepTests.LongSizes.Select(s => (State: "long", s.Height, s.Dpi))
            .Concat(WireframeSweepTests.ManySizes.Select(s => (State: "many", s.Height, s.Dpi))).Distinct().Count();
        groups.ShouldBe(6);
        Pixel.Length.ShouldBe(groups, $"pixel sweep classes: {string.Join(", ", Pixel.Select(t => t.Name))}");
    }
}
