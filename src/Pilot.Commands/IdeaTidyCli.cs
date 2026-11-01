using System.Text;
using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>A line diff in the unified format, for showing what a tidy would change.</summary>
public static class IdeaDiff
{
    private const int Context = 2;

    /// <summary>The unified diff of <paramref name="was"/> to <paramref name="now"/>; empty when the two are the same text.</summary>
    public static string Unified(string was, string now, string wasName, string nowName)
    {
        var a = Lines(was);
        var b = Lines(now);
        if (a.SequenceEqual(b))
        {
            return "";
        }
        // Longest common subsequence of lines: ideas are a few lines, so the table is small.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        var ops = new List<(char Mark, string Line)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                ops.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                ops.Add(('-', a[x++]));
            }
            else
            {
                ops.Add(('+', b[y++]));
            }
        }

        var keep = new bool[ops.Count];
        for (var k = 0; k < ops.Count; k++)
        {
            if (ops[k].Mark != ' ')
            {
                for (var n = Math.Max(0, k - Context); n <= Math.Min(ops.Count - 1, k + Context); n++)
                {
                    keep[n] = true;
                }
            }
        }
        var text = new StringBuilder();
        text.Append("--- ").Append(wasName).Append('\n').Append("+++ ").Append(nowName).Append('\n');
        for (var k = 0; k < ops.Count; k++)
        {
            if (!keep[k])
            {
                if (k > 0 && keep[k - 1])
                {
                    text.Append("@@\n");
                }
                continue;
            }
            text.Append(ops[k].Mark).Append(ops[k].Line).Append('\n');
        }
        return text.ToString();
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
}

/// <summary>
/// The tidy the person was last shown for an idea, kept so that <c>tidy --save</c> saves what was seen and not a new answer
/// of Claude's. A file per idea in the temp folder: it holds only the proposal and the <c>updated_at</c> it was made from.
/// </summary>
public static class IdeaTidyCache
{
    public static string Joined(string title, string body) => body.Length > 0 ? title + "\n" + body : title;

    private static string FileOf(string target)
    {
        var name = new string([.. target.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_')]);
        return Path.Combine(Path.GetTempPath(), "pilot-tidy", name + ".json");
    }

    public static void Write(string target, IdeaTidyProposal proposal)
    {
        var file = FileOf(target);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(proposal, PilotJsonContext.Default.IdeaTidyProposal));
    }

    public static IdeaTidyProposal? Read(string target)
    {
        try
        {
            return File.Exists(FileOf(target)) ? JsonSerializer.Deserialize(File.ReadAllText(FileOf(target)), PilotJsonContext.Default.IdeaTidyProposal) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Forgets the proposal; whether there was one.</summary>
    public static bool Discard(string target)
    {
        var file = FileOf(target);
        if (!File.Exists(file))
        {
            return false;
        }
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        return true;
    }
}
