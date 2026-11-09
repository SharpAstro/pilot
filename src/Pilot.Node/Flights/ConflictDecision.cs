namespace Pilot.Node.Flights;

/// <summary>
/// What a conflict worker reports when it finds the two sides of a PR's conflict change the same behaviour in opposite ways, so that
/// keeping both is impossible (PIL846). It finishes its leg failed with a report in this shape, one field a line after the header:
/// <code>
/// CONFLICT DECISION
/// files: a.cs, b.cs
/// base: what the base does there
/// branch: what the branch does there
/// keep base costs: what the branch loses if the base wins
/// keep branch costs: what the base loses if the branch wins
/// </code>
/// Only the last rung's report in this shape rings the person; any other failure is the leg-failed card's.
/// </summary>
public sealed record ConflictDecision(string[] Files, string OnBase, string OnBranch, string KeepBaseCosts, string KeepBranchCosts)
{
    /// <summary>The first line of the report.</summary>
    public const string Header = "CONFLICT DECISION";

    /// <summary>The shape a worker is told to report in, as the brief words it.</summary>
    public const string Shape =
        Header + "\nfiles: <the conflicting files, comma separated>\nbase: <the behaviour on the base>\nbranch: <the behaviour on the branch>\n" +
        "keep base costs: <what the branch loses if the base's behaviour is kept>\nkeep branch costs: <what the base loses if the branch's behaviour is kept>";

    /// <summary>The report read in the fixed shape, or null when it is not in it: no header, or a field missing or empty.</summary>
    public static ConflictDecision? TryParse(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return null;
        }
        var lines = report.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var start = Array.FindIndex(lines, l => l.Equals(Header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return null;
        }
        string? files = null, onBase = null, onBranch = null, baseCosts = null, branchCosts = null;
        foreach (var line in lines.Skip(start + 1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            var value = line[(colon + 1)..].Trim();
            switch (line[..colon].Trim().ToLowerInvariant())
            {
                case "files": files ??= value; break;
                case "base": onBase ??= value; break;
                case "branch": onBranch ??= value; break;
                case "keep base costs": baseCosts ??= value; break;
                case "keep branch costs": branchCosts ??= value; break;
            }
        }
        var names = (files ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0 || string.IsNullOrEmpty(onBase) || string.IsNullOrEmpty(onBranch) || string.IsNullOrEmpty(baseCosts) || string.IsNullOrEmpty(branchCosts))
        {
            return null;
        }
        return new ConflictDecision(names, onBase, onBranch, baseCosts, branchCosts);
    }
}
