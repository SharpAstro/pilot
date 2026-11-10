namespace Pilot.Node.Flights;

/// <summary>
/// The marker lines of a benchmark's log, the same whether it runs in a worktree on the node's machine (<see cref="BenchRun"/>) or on a cloud
/// VM (<see cref="CloudBenchScript"/>), so a log reads the same in sections either way.
/// </summary>
internal static class BenchMarkers
{
    /// <summary>The line that opens a ref's section: its label, the ref as given and its short commit.</summary>
    public static string Start(string label, string reference, string sha) => $"=== bench {label}: {reference} ({CommitRefs.Short(sha)}) ===";

    /// <summary>The line that says a ref's fetch step failed and so its benchmark was not run.</summary>
    public const string FetchFailed = "=== the fetch step failed, the benchmark was not run ===";
}
