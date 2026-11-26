namespace Pilot.Contracts;

/// <summary>
/// The line a tell's picture adds to the words (PIL1366): the picture's absolute path on the node, then a line telling the watchtower to open it
/// with Read. Built by the node and read back by a client that shows the watchtower's chat, so both know one shape. Its lead differs from the
/// design chat's "A picture came with this message:" line, so that line is never read as a node attachment.
/// </summary>
public static class TellPictures
{
    /// <summary>What starts the line, kept apart from the design chat's own lead.</summary>
    public const string Lead = "A picture is attached to this tell, held on the node at:";

    private const string OpenWith = "Open it with the Read tool to see it.";

    /// <summary>What is appended after the words for the picture held at <paramref name="path"/>.</summary>
    public static string Line(string path) => $"\n\n{Lead} {path}\n{OpenWith}";

    /// <summary>
    /// The words of <paramref name="text"/> without any picture lines, and the id and extension of each picture they named
    /// (read from the path's file name, with / or a backslash as separator: a Linux client reads a Windows node's path).
    /// </summary>
    public static (string Words, IReadOnlyList<(string Id, string Ext)> Pictures) Split(string text)
    {
        var pictures = new List<(string Id, string Ext)>();
        var kept = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith(Lead, StringComparison.Ordinal))
            {
                var path = line[Lead.Length..].Trim();
                var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
                var dot = name.LastIndexOf('.');
                if (dot > 0 && dot < name.Length - 1)
                {
                    pictures.Add((name[..dot], name[(dot + 1)..]));
                }
                if (i + 1 < lines.Length && lines[i + 1].TrimEnd('\r') == OpenWith)
                {
                    i++;
                }
                continue;
            }
            kept.Add(lines[i]);
        }
        return (string.Join('\n', kept).TrimEnd(), pictures);
    }
}
