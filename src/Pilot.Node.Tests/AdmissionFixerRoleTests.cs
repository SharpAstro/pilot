using Pilot.Contracts;
using Pilot.Node.Flights;
using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>The admission gate counts a PR fixer as a fixer by its role (PIL1008), and an older stored ad hoc row by its PR fix start (#912).</summary>
public sealed class AdmissionFixerRoleTests
{
    private static SessionRow Row(string id, string name, SessionRole role, string? reason) => new()
    {
        Id = id, SessionId = id + "-0000-4000-8000-000000000000", Name = name, Kind = SessionKind.Background, Cwd = "c", Role = role,
        StartReason = reason, State = SessionState.Working, ContextTokens = 0, ContextWindow = 200_000, SpendWeighted = 0, StartedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void AFixerRowAndAnOldAdHocPrFixRowBothCountAsFixers()
    {
        var state = new NodeState(new InMemoryJournal(), TimeProvider.System);
        state.PutSession(Row("aaaa1111", "de1-fix-pilot-1", SessionRole.Fixer, SessionStartReasons.PrFix));
        state.PutSession(Row("bbbb2222", "de1-fix-pilot-2", SessionRole.AdHoc, SessionStartReasons.PrFix));
        state.PutSession(Row("cccc3333", "a person's own", SessionRole.AdHoc, null));
        var models = new RoleModels(new NodeSettings().Settings ?? NodeSettings.With(new NodeSettings(), "opus-cap", ["5"]).Settings!);
        var gate = new AdmissionGate(state, () => models, "DE1");

        gate.OwnSessions().Select(s => (s.Label, s.Role)).OrderBy(s => s.Label)
            .ShouldBe([("de1-fix-pilot-1", (AdmissionRole?)AdmissionRole.Fixer), ("de1-fix-pilot-2", AdmissionRole.Fixer)]);
    }
}
