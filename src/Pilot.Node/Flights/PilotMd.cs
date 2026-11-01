using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A repo's <c>PILOT.md</c>: its short rules for pilot, read as it stands at the flight's base (so a flight cannot rewrite its
/// own rules) and quoted whole into the planner, worker and reviewer briefs. A file over <see cref="MaxLines"/> lines is not
/// quoted: a rule list that long is no longer short, and a brief should not carry it.
/// </summary>
public static class PilotMd
{
    public const string FileName = "PILOT.md";

    public const int MaxLines = 150;

    /// <summary>The card kind raised once per flight and line count when a repo's PILOT.md is over the limit.</summary>
    public const string TooLongKind = "pilot-md-too-long";

    /// <summary>What reading the file gave: its text, nothing (absent), or a refusal with the line count it was over.</summary>
    public readonly record struct Read(string? Text, int? TooLongBy)
    {
        public static Read None { get; } = new(null, null);
    }

    public static async Task<Read> ReadAsync(IGit git, FlightRow flight, CancellationToken cancellationToken)
    {
        GitRun run;
        try
        {
            run = await git.RunAsync(["show", $"{flight.Base}:{FileName}"], flight.Worktree, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No plane to read from (git cannot start in a folder that is not there): the brief goes out without rules.
            return Read.None;
        }
        if (!run.Ok || string.IsNullOrWhiteSpace(run.Output))
        {
            return Read.None;
        }
        var lines = run.Output.TrimEnd().Split('\n').Length;
        return lines > MaxLines ? new Read(null, lines) : new Read(run.Output.TrimEnd(), null);
    }
}
