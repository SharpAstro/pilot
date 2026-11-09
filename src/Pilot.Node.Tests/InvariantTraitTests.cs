using System.Reflection;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The repo-wide guard tests carry <c>[Trait("Category", "Invariant")]</c>, so one filter runs them all in seconds. This pins
/// the set: a guard that loses its tag fails here instead of silently dropping out of the filter.
/// </summary>
public sealed class InvariantTraitTests
{
    /// <summary>The value of the Category trait on a repo-wide guard.</summary>
    public const string Invariant = "Invariant";

    private static readonly Type[] Guards =
    [
        typeof(CardKindTests),
        typeof(SplitFileSizeTests),
        typeof(FixtureTests),
        typeof(CommitNotDropTests),
        typeof(WaitBudgetTests),
    ];

    [Fact]
    public void EveryNamedGuardClassCarriesTheInvariantTrait()
    {
        var missing = Guards
            .Where(t => !t.GetCustomAttributesData().Any(a =>
                a.AttributeType == typeof(TraitAttribute)
                && a.ConstructorArguments.Count == 2
                && a.ConstructorArguments[0].Value as string == "Category"
                && a.ConstructorArguments[1].Value as string == Invariant))
            .Select(t => t.Name)
            .ToList();

        missing.ShouldBeEmpty("these guard classes lost [Trait(\"Category\", \"Invariant\")]");
    }
}
