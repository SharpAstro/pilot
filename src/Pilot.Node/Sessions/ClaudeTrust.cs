using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pilot.Node.Sessions;

/// <summary>
/// Claude Code's folder trust (<c>projects["C:/…"].hasTrustDialogAccepted</c> in <c>~/.claude.json</c>), read and, before
/// the node starts a background session in a folder, set: a background session has no one to accept the trust prompt,
/// so it refused to start ("Workspace not trusted"), and the fix needed a person at a terminal in each folder. The node
/// is starting the session for this machine's person, in a folder they made a flight or a watchtower in.
/// </summary>
/// <remarks>
/// The file is Claude Code's own, written by every session it runs, so a change is made with care: the file's bytes are
/// hashed as read, the edit touches that one folder's record, the new file is written beside it, the bytes are hashed
/// again just before the swap and the edit thrown away and redone when anyone wrote in between, and the swap is
/// <see cref="File.Replace(string, string, string)"/>, which keeps the file it replaced as <c>.claude.json.pilot-bak</c>.
/// Seen on SE1, 2026-10-24: the file was rewritten without its projects (238 KB to 72 KB) and every flight's worker and
/// the watchtower stopped starting until each folder's prompt was accepted by hand.
/// </remarks>
public sealed class ClaudeTrust(string file)
{
    /// <summary>How often the edit is redone when the file changed while it was being made.</summary>
    public const int Attempts = 5;

    public string File { get; } = file;

    /// <summary>The folder whose trust covers <paramref name="folder"/> (itself or one above it), or null when none does.</summary>
    public string? Trusted(string folder)
    {
        try
        {
            return System.IO.File.Exists(File) && JsonNode.Parse(System.IO.File.ReadAllBytes(File)) is JsonObject root ? Covering(root, folder) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Trusts <paramref name="folder"/> unless a folder covers it already: null when it is trusted now, else why not.</summary>
    public string? Ensure(string folder)
    {
        string? why = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var temp = File + ".pilot-tmp";
            try
            {
                if (!System.IO.File.Exists(File))
                {
                    return $"there is no {File} to record the trust in";
                }
                var read = System.IO.File.ReadAllBytes(File);
                if (JsonNode.Parse(read) is not JsonObject root)
                {
                    return $"{File} is not a JSON object";
                }
                if (Covering(root, folder) is not null)
                {
                    return null;
                }

                if (root["projects"] is not JsonObject projects)
                {
                    root["projects"] = projects = new JsonObject();
                }
                var key = Key(folder);
                if (projects[key] is not JsonObject project)
                {
                    projects[key] = project = new JsonObject();
                }
                project["hasTrustDialogAccepted"] = true;

                using (var stream = System.IO.File.Create(temp))
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, IndentSize = 2 }))
                {
                    root.WriteTo(writer);
                }

                // Anyone who wrote the file since it was read would lose their write to this swap: redo it on what is there.
                if (!SHA256.HashData(System.IO.File.ReadAllBytes(File)).AsSpan().SequenceEqual(SHA256.HashData(read)))
                {
                    why = $"{File} changed while the trust was being recorded";
                    System.IO.File.Delete(temp);
                    continue;
                }
                System.IO.File.Replace(temp, File, File + ".pilot-bak");
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                why = ex.Message;
                try
                {
                    System.IO.File.Delete(temp);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // A temp file left behind is overwritten by the next attempt.
                }
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        return why;
    }

    /// <summary>Claude Code keys a project by its path with forward slashes (<c>C:/Users/…</c>), a drive root as <c>C:/</c>.</summary>
    public static string Key(string folder)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).Replace('\\', '/');
        return key.EndsWith(':') ? key + "/" : key;
    }

    // Trust in a folder covers those under it.
    private static string? Covering(JsonObject root, string folder)
    {
        if (root["projects"] is not JsonObject projects)
        {
            return null;
        }
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            var key = Key(dir.FullName);
            var project = projects.FirstOrDefault(p => string.Equals(p.Key, key, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;
            if (project?["hasTrustDialogAccepted"] is JsonValue accepted && accepted.TryGetValue<bool>(out var yes) && yes)
            {
                return dir.FullName;
            }
        }
        return null;
    }
}
