using System.Text.Json;
using System.Text.Json.Nodes;
using Pilot.Contracts;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The contract's sample snapshot, which the GUI leg draws from before a node fills real rows.</summary>
public sealed class FixtureTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "snapshot.sample.json");

    [Fact]
    public void TheSampleSnapshotDeserialisesWithEveryRoleAndState()
    {
        var snapshot = JsonSerializer.Deserialize(File.ReadAllText(FixturePath), PilotJsonContext.Default.NodeEvent).ShouldBeOfType<SnapshotEvent>();

        snapshot.Sessions.Length.ShouldBe(7);
        snapshot.Sessions.Select(s => s.Role).Distinct().Order().ShouldBe(Enum.GetValues<SessionRole>());
        snapshot.Sessions.Select(s => s.State).Distinct().Order().ShouldBe(Enum.GetValues<SessionState>());
        snapshot.Sessions.Select(s => s.Kind).Distinct().Order().ShouldBe(Enum.GetValues<SessionKind>());
        snapshot.Sessions.Select(s => s.Id).ShouldBeUnique();
        snapshot.Sessions.Single(s => s.State is SessionState.Waiting).WaitingFor.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TheSampleSnapshotRoundTripsThroughTheGeneratedContext()
    {
        var original = File.ReadAllText(FixturePath);

        var read = JsonSerializer.Deserialize(original, PilotJsonContext.Default.NodeEvent).ShouldNotBeNull();
        var written = JsonSerializer.Serialize(read, PilotJsonContext.Default.NodeEvent);

        JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(written)).ShouldBeTrue($"the fixture changed on its way through:\n{written}");
    }

    [Fact]
    public void ANullWhereTheContractHasNoneIsRefused()
    {
        var withoutName = File.ReadAllText(FixturePath).Replace("\"name\": \"pilot-12\"", "\"name\": null", StringComparison.Ordinal);

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(withoutName, PilotJsonContext.Default.NodeEvent));
    }
}
