using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>What tidy and the repo choice tell Claude about an idea's images, and the put-back of one a tidy dropped.</summary>
public static class IdeaImagePrompt
{
    /// <summary>The local files of the images in <paramref name="body"/> that the node holds a copy of, each once, in the order they stand.</summary>
    public static string[] Copies(string? body, Attachments? attachments) =>
        attachments is null
            ? []
            : [.. IdeaImages.In(body)
                .Select(i => i.Copy is { } id ? attachments.FindIdeaImage(id)?.Path : null)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The <c>claude -p</c> arguments, with the ideas folder to read when there are copies to read there.</summary>
    public static string[] Args(string model, IReadOnlyList<string> copies, Attachments? attachments) =>
        copies.Count > 0 && attachments is not null ? ["-p", "--model", model, "--add-dir", attachments.IdeaFolder] : ["-p", "--model", model];

    private static string Count(int n) => n == 1 ? "1 image" : $"{n} images";

    private static string Local(IReadOnlyList<string> copies) =>
        copies.Count == 0
            ? ""
            : "\nThe node holds a local copy of " + (copies.Count == 1 ? "it" : "them") + "; Read the file to see what it shows:\n" + string.Join('\n', copies.Select(c => $"- {c}"));

    /// <summary>The lines about images in the tidy prompt; empty for an idea with none.</summary>
    public static string ForTidy(string body, IReadOnlyList<string> copies) =>
        IdeaImages.Spans(body).Length is var n and > 0
            ? $"- The idea holds {Count(n)}, each a Markdown image line. Each must come back in the body verbatim and in the same place relative to the text around it: never drop, reword or move one.{Local(copies)}\n"
            : "";

    /// <summary>The lines about images in the repo choice prompt; empty for an idea with none.</summary>
    public static string ForChoice(string body, IReadOnlyList<string> copies) =>
        IdeaImages.Spans(body).Length is var n and > 0
            ? $"The idea holds {Count(n)}, each a Markdown image line. They and the person's words are never changed by you.{Local(copies)}\n"
            : "";

    /// <summary>
    /// <paramref name="proposed"/> with every image of <paramref name="current"/> it lacks put back, after the paragraph the image followed
    /// (its last line of text found in the proposal), at the top (after the images already there) when nothing came before it, or at
    /// the end when that text is not found. A proposal that kept its images is returned as it is.
    /// </summary>
    public static string Restore(string current, string proposed)
    {
        var spans = IdeaImages.Spans(current);
        if (spans.Length == 0)
        {
            return proposed;
        }
        var have = IdeaImages.Spans(proposed).Select(s => s.Url).ToList();
        var body = proposed;
        foreach (var span in spans)
        {
            if (!have.Remove(span.Url))
            {
                body = Insert(body, span.Raw, LastTextBefore(current, span.At));
            }
        }
        return body;
    }

    private static bool OnlyImages(string line) => IdeaImages.Spans(line).Length > 0 && StripImages(line).Length == 0;

    private static string StripImages(string line)
    {
        foreach (var span in IdeaImages.Spans(line).Reverse())
        {
            line = line.Remove(span.At, span.Length);
        }
        return line.Trim();
    }

    private static string? LastTextBefore(string current, int at)
    {
        foreach (var line in current[..at].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Reverse())
        {
            if (StripImages(line) is { Length: > 0 } text)
            {
                return text;
            }
        }
        return null;
    }

    private static string Insert(string body, string image, string? anchor)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (anchor is null)
        {
            return Lead(lines, image);
        }
        // A tidy turns prose into bullets, so the anchor is found by its words, not its list marker.
        var words = anchor.TrimStart('-', '*', '•', ' ');
        var at = lines.FindIndex(l => StripImages(l).Contains(words, StringComparison.Ordinal));
        if (at < 0)
        {
            return body.TrimEnd().Length == 0 ? image : body.TrimEnd() + "\n\n" + image;
        }
        while (true)
        {
            while (at + 1 < lines.Count && lines[at + 1].Trim().Length > 0)
            {
                at++;
            }
            var next = at + 1;
            while (next < lines.Count && lines[next].Trim().Length == 0)
            {
                next++;
            }
            if (next < lines.Count && OnlyImages(lines[next]))
            {
                // Images already standing after this paragraph keep their order: this one goes after them.
                at = next;
                continue;
            }
            break;
        }
        lines.Insert(at + 1, "");
        lines.Insert(at + 2, image);
        return string.Join('\n', lines);
    }

    // An image with no text before it stood at the top, as a pasted screenshot does: it goes back there, after the images already
    // at the top, so leading images keep their order.
    private static string Lead(List<string> lines, string image)
    {
        var after = -1;
        for (var i = 0; i < lines.Count && (lines[i].Trim().Length == 0 || OnlyImages(lines[i])); i++)
        {
            if (lines[i].Trim().Length > 0)
            {
                after = i;
            }
        }
        if (after < 0)
        {
            var rest = string.Join('\n', lines).Trim();
            return rest.Length == 0 ? image : image + "\n\n" + rest;
        }
        lines.Insert(after + 1, "");
        lines.Insert(after + 2, image);
        return string.Join('\n', lines);
    }
}
