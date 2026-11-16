using System.Diagnostics;

namespace Pilot.Node;

/// <summary>
/// The one place every child process the node starts is seen (#933): the git, gh and claude starts (<see cref="NodeTraffic"/>), a
/// job's shell (<c>DetachedShell</c>), a leg's check, and setup's netsh, pwsh, gh and dotnet reads, its elevated scripts and its
/// trust window (<see cref="NodeSetup.SetupStartKind"/>). A test run sets <see cref="Observer"/> to learn which tests start real
/// processes, which a unit test must not (they belong in an integration category, or on a fake); the node never sets it.
/// </summary>
public static class ProcessStarts
{
    /// <summary>Told of each start, with its kind (<c>git</c>, <c>gh</c>, <c>claude</c>, <c>job</c>, <c>check</c>, <c>setup</c>) and what it runs; null in the node.</summary>
    public static Action<string, ProcessStartInfo>? Observer { get; set; }

    /// <summary>Tells the observer of a start about to be made; nothing when none is set.</summary>
    public static void Observe(string kind, ProcessStartInfo start) => Observer?.Invoke(kind, start);
}
