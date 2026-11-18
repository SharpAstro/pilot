namespace Pilot.Contracts;

/// <summary>
/// The title a design gets from its brief when none is given (#1160). It lives here, beside <see cref="DesignRow"/>, so the node's store
/// and the page's tests make a title by the one rule.
/// </summary>
public static class DesignTitle
{
    /// <summary>The longest title taken from a brief's first line. It is generous on purpose: the page draws the title on one line, ending
    /// in an ellipsis with the whole on hover, so the store keeps the line whole and cuts only a very long one.</summary>
    public const int MaxLength = 200;

    /// <summary>The brief's first non-empty line, whole up to <see cref="MaxLength"/> characters. A longer one is cut after the last word
    /// that fits (at the limit when its first 199 characters hold no space, as a pasted URL or hash) and ends in an ellipsis, so a cut always says so.</summary>
    public static string Of(string brief)
    {
        var line = brief.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (line.Length <= MaxLength)
        {
            return line;
        }
        var room = MaxLength - 1;
        var space = line.LastIndexOf(' ', room);
        var cut = space > 0 ? line[..space] : line[..room];
        return cut.TrimEnd(' ', ',', ';', ':', '.') + "…";
    }
}
