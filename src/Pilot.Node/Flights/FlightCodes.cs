using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// How a flight is coded: the repo's prefix, the issue number, and a suffix a to e for a further flight on the same
/// issue (TIA1611, TIA1611a). Unique on the machine for good, landed flights included, so a code names one flight
/// wherever it is read; unique on the LAN comes with the peers of phase 5.
/// </summary>
public static class FlightCodes
{
    /// <summary>The suffixes a further flight on an issue takes, in order.</summary>
    public const string Suffixes = "abcde";

    /// <summary>
    /// The prefix of <paramref name="repo"/>: the one its earlier flights have, else its first three letters in capitals
    /// (tianwen TIA, pilot PIL), lengthened while another repo has it (DIR.Lib DIR, then dirsync DIRS).
    /// </summary>
    public static string PrefixFor(string repo, IReadOnlyCollection<FlightRow> flights)
    {
        if (flights.FirstOrDefault(f => string.Equals(f.Repo, repo, StringComparison.OrdinalIgnoreCase)) is { } earlier)
        {
            return PrefixOf(earlier.Code);
        }

        var taken = flights.Select(f => PrefixOf(f.Code)).ToHashSet(StringComparer.Ordinal);
        var letters = new string([.. repo.Where(char.IsAsciiLetter).Select(char.ToUpperInvariant)]);
        if (letters.Length < 2)
        {
            letters = "FL" + letters;
        }
        for (var length = Math.Min(3, letters.Length); length <= letters.Length; length++)
        {
            if (!taken.Contains(letters[..length]))
            {
                return letters[..length];
            }
        }
        for (var extra = 'A'; ; extra++)
        {
            if (!taken.Contains(letters + extra))
            {
                return letters + extra;
            }
        }
    }

    /// <summary>The code for a new flight on <paramref name="issue"/>, or null when it and its five suffixes are all taken.</summary>
    public static string? Next(string prefix, int issue, IReadOnlyCollection<FlightRow> flights)
    {
        var codes = flights.Select(f => f.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plain = $"{prefix}{issue}";
        if (!codes.Contains(plain))
        {
            return plain;
        }
        foreach (var suffix in Suffixes)
        {
            if (!codes.Contains(plain + suffix))
            {
                return plain + suffix;
            }
        }
        return null;
    }

    /// <summary>The letters a code starts with: TIA of TIA1611a.</summary>
    public static string PrefixOf(string code)
    {
        var end = 0;
        while (end < code.Length && char.IsAsciiLetter(code[end]))
        {
            end++;
        }
        return code[..end];
    }
}
