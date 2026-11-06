using System.Text.Json;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// A journal written by an older node replays into the newer one: an admission row with no <c>waitingOnPerson</c>
/// reads back as empty and serializes again, where it once stopped the node at start (beta 1718, 2026-11-07).
/// </summary>
public sealed class AdmissionRowJournalTests
{
    [Theory]
    [InlineData("""{"opusCount":1,"opusCap":3,"oneFlightPerRepo":true,"counted":["a"],"priority":[]}""")]
    public void AnAdmissionRowFromAnOlderNodeReadsBackEmptyAndSerializesAgain(string json)
    {
        var row = JsonSerializer.Deserialize(json, PilotJsonContext.Default.AdmissionRow)!;

        row.WaitingOnPerson.ShouldBeEmpty();
        Should.NotThrow(() => JsonSerializer.Serialize(row, PilotJsonContext.Default.AdmissionRow));
    }
}
