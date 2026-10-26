using System.Text.RegularExpressions;

namespace Pilot.Contracts;

/// <summary>
/// Whether a session's name carries its machine's code (<c>se1-</c>, <c>de1-</c>), and the name a list shows: as it is,
/// or with the node's code written before it, so an unprefixed ad hoc session on one node can be told from one on another.
/// </summary>
public static partial class MachineName
{
    [GeneratedRegex("^[a-z]{2}[0-9]+-", RegexOptions.CultureInvariant)]
    private static partial Regex Prefixed();

    /// <summary>Whether <paramref name="name"/> starts with a machine code and a dash (shape <c>[a-z]{2}[0-9]+-</c>).</summary>
    public static bool HasPrefix(string name) => Prefixed().IsMatch(name);

    /// <summary>
    /// The name to show: <paramref name="name"/> as it is when it carries a machine prefix or the node's code is not known,
    /// else <c>code-name</c> (the code in lower case).
    /// </summary>
    public static string Shown(string name, string? code) =>
        string.IsNullOrEmpty(code) || HasPrefix(name) ? name : $"{code.ToLowerInvariant()}-{name}";
}
