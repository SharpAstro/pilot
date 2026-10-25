using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>A package a restore could not find: its id, the version it asked for (null for NU1101, which names none), and the line that said it.</summary>
public sealed record RestoreMiss(string Id, string? Version, string Line);

/// <summary>
/// The restore failures a leg can hold for, read out of a worker's failed report or a failed check's output: NU1101 (no package
/// of that id on any source) and NU1102 (the id exists, the version asked for does not), as <c>dotnet restore</c> words them.
/// </summary>
public static partial class RestoreMisses
{
    // error NU1102: Unable to find package Drawboard.Core with version (>= 1.2.7)
    [GeneratedRegex(@"NU1102:\s*Unable to find package (?<id>\S+) with version \((?:>=|=|>)\s*(?<version>\d+(?:\.\d+){1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex WithVersion();

    // error NU1101: Unable to find package Drawboard.Core. No packages exist with this id in source(s): nuget.org
    [GeneratedRegex(@"NU1101:\s*Unable to find package (?<id>\S+?)\.?\s+No packages exist", RegexOptions.CultureInvariant)]
    private static partial Regex NoVersion();

    /// <summary>Every miss <paramref name="text"/> names, in the order it says them, each package and version once.</summary>
    public static IReadOnlyList<RestoreMiss> In(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("NU110", StringComparison.Ordinal))
        {
            return [];
        }
        var found = new List<(int At, RestoreMiss Miss)>();
        foreach (Match m in WithVersion().Matches(text))
        {
            found.Add((m.Index, new RestoreMiss(m.Groups["id"].Value, m.Groups["version"].Value, Line(text, m.Index))));
        }
        foreach (Match m in NoVersion().Matches(text))
        {
            found.Add((m.Index, new RestoreMiss(m.Groups["id"].Value, null, Line(text, m.Index))));
        }
        return [.. found.OrderBy(f => f.At).Select(f => f.Miss)
            .DistinctBy(m => (m.Id.ToLowerInvariant(), m.Version))];
    }

    // The whole line the match sits on, verbatim, so a card and a report can quote it.
    private static string Line(string text, int at)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        var end = text.IndexOf('\n', at);
        return text[start..(end < 0 ? text.Length : end)].Trim();
    }
}
