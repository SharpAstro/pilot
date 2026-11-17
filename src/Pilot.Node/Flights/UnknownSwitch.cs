using System.Text.RegularExpressions;

namespace Pilot.Node.Flights;

/// <summary>
/// A check that failed with MSB1001 "Unknown switch": the command is wrong for the repo's test runner (pilot #1132), not an SDK or
/// environment problem. <see cref="Switch"/> is the switch the SDK did not know.
/// </summary>
public sealed partial record UnknownSwitch(string Switch)
{
    /// <summary>The words a report, message or card cause starts with.</summary>
    public string Text => $"This is a wrong command for this repo's test runner, not an SDK or environment problem: the SDK does not know the switch {Switch} " +
        "(a repo runs VSTest unless global.json or dotnet.config selects Microsoft.Testing.Platform; each runner has its own switches).";

    [GeneratedRegex(@"Unknown switch\.?\s*Switch:\s*(?<s>[^\s=]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SwitchLine();

    /// <summary>The unknown switch a check's output names; null when it carries no MSB1001 "Unknown switch".</summary>
    public static UnknownSwitch? In(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("MSB1001", StringComparison.Ordinal) || !text.Contains("Unknown switch", StringComparison.Ordinal))
        {
            return null;
        }
        return SwitchLine().Match(text) is { Success: true } m ? new UnknownSwitch(m.Groups["s"].Value) : new UnknownSwitch("(not named)");
    }
}
