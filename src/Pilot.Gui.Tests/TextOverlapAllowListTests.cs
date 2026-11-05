using Shouldly;
using Xunit;

namespace Pilot.Gui.Tests;

/// <summary>The allow-list of <see cref="PageTextOverlapTests"/> is the sweep's to-do: no entry stands without a reason, and none outlives its fault.</summary>
public sealed class TextOverlapAllowListTests
{
    [Fact]
    public void EveryEntryHasAReasonAndAnIssue()
    {
        foreach (var entry in TextOverlapAllowList.Entries)
        {
            entry.Reason.ShouldNotBeNullOrWhiteSpace(entry.Case);
            entry.Issue.ShouldBeGreaterThan(0, entry.Case);
        }
    }

    /// <summary>An entry that matches no fault of the cases it names is stale: its fault was fixed, so it goes.</summary>
    [Fact]
    public async Task NoEntryOutlivesItsFault()
    {
        foreach (var entry in TextOverlapAllowList.Entries)
        {
            var found = false;
            foreach (var (page, state, width, dpi, height) in PageTextOverlapTests.CaseTuples())
            {
                var key = PageTextOverlapTests.Key(page, state, width, dpi, height);
                if (!entry.CoversCase(key))
                {
                    continue;
                }
                var faults = await PageTextOverlapTests.FaultsOf(page, state, width, dpi, height);
                if (faults.Any(f => entry.Covers(key, f.Line)))
                {
                    found = true;
                    break;
                }
            }
            found.ShouldBeTrue($"the allow-list entry '{entry.Case}' / '{entry.Fault}' matches no fault any more: remove it");
        }
    }
}
