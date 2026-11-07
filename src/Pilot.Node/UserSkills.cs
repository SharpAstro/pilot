using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Pilot.Node;

/// <summary>
/// The Claude Code skills pilot carries for every session on the machine (<c>UserSkills/&lt;name&gt;/</c> in this project, embedded
/// in the binary so a native release needs no file beside it), and their install into the profile's <c>skills</c> folder:
/// by <c>pilot-node setup</c>, by the setup check's fix, and at every node start while the <c>user-skills</c> setting is on
/// (its default), so a release that changes a skill reaches each machine with its update.
/// <para>A skill pilot wrote carries a marker (<see cref="Marker"/>) holding the hash of what it wrote. A folder with no marker
/// is someone else's, and one whose files no longer hash to its marker was edited by hand: both are left alone and said so,
/// never overwritten. Only a skill pilot wrote and nobody changed is replaced by a newer one, or removed when the setting
/// goes off.</para>
/// </summary>
public static class UserSkills
{
    /// <summary>The file pilot leaves in a skill folder it wrote: the hash of the files as written.</summary>
    public const string Marker = ".pilot-installed";

    private const string ResourcePrefix = "UserSkills/";

    /// <summary>What a skill's folder holds against the one pilot carries.</summary>
    public enum State
    {
        /// <summary>No folder: the install writes it.</summary>
        Missing,
        /// <summary>Pilot's, unchanged, and the same as the one carried.</summary>
        Current,
        /// <summary>Pilot's, unchanged, from an older release: the install replaces it.</summary>
        Outdated,
        /// <summary>Pilot's, changed by hand since: kept.</summary>
        Edited,
        /// <summary>A folder of that name pilot never wrote: kept.</summary>
        NotOurs,
    }

    /// <summary>One skill pilot carries: its folder name and its files, by path relative to that folder (<c>/</c>-separated).</summary>
    public sealed record Skill(string Name, IReadOnlyDictionary<string, byte[]> Files)
    {
        public string Hash => HashOf(Files);
    }

    /// <summary>The <c>skills</c> folder of a Claude Code profile folder.</summary>
    public static string RootIn(string profileRoot) => Path.Combine(profileRoot, "skills");

    /// <summary>Every skill embedded in this binary, by name.</summary>
    public static IReadOnlyList<Skill> Carried() => FromAssembly(typeof(UserSkills).Assembly);

    internal static IReadOnlyList<Skill> FromAssembly(Assembly assembly)
    {
        var files = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            // The project names each one UserSkills/<skill>/<path>; RecursiveDir gives a backslash on Windows.
            var name = resource.Replace('\\', '/');
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var rest = name[ResourcePrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1)
            {
                continue;
            }
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var skill = rest[..slash];
            if (!files.TryGetValue(skill, out var of))
            {
                files[skill] = of = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            }
            of[rest[(slash + 1)..]] = copy.ToArray();
        }
        return [.. files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new Skill(f.Key, f.Value))];
    }

    /// <summary>Where <paramref name="skill"/> stands in <paramref name="root"/>.</summary>
    public static State StateOf(string root, Skill skill)
    {
        var folder = Path.Combine(root, skill.Name);
        if (!Directory.Exists(folder))
        {
            return State.Missing;
        }
        var marker = Path.Combine(folder, Marker);
        if (!File.Exists(marker))
        {
            return State.NotOurs;
        }
        var written = File.ReadAllText(marker).Trim();
        if (HashOf(Read(folder)) != written)
        {
            return State.Edited;
        }
        return written == skill.Hash ? State.Current : State.Outdated;
    }

    /// <summary>
    /// Writes every carried skill that is missing or outdated into <paramref name="root"/>, and gives a line for each one it
    /// wrote or kept as someone else's; nothing for those already current, so a second run says nothing.
    /// </summary>
    public static IReadOnlyList<string> Install(string root, IReadOnlyList<Skill>? skills = null)
    {
        var lines = new List<string>();
        foreach (var skill in skills ?? Carried())
        {
            var state = StateOf(root, skill);
            switch (state)
            {
                case State.Missing or State.Outdated:
                    Write(root, skill);
                    lines.Add($"{skill.Name}: {(state == State.Missing ? "installed" : "updated")} in {Path.Combine(root, skill.Name)}");
                    break;
                case State.Edited:
                    lines.Add($"{skill.Name}: kept, it was changed by hand since pilot wrote it (delete {Path.Combine(root, skill.Name)} to take pilot's)");
                    break;
                case State.NotOurs:
                    lines.Add($"{skill.Name}: kept, {Path.Combine(root, skill.Name)} is not pilot's (no {Marker})");
                    break;
            }
        }
        return lines;
    }

    /// <summary>Removes each carried skill pilot wrote and nobody changed; gives a line for each one removed or kept.</summary>
    public static IReadOnlyList<string> Remove(string root, IReadOnlyList<Skill>? skills = null)
    {
        var lines = new List<string>();
        foreach (var skill in skills ?? Carried())
        {
            switch (StateOf(root, skill))
            {
                case State.Current or State.Outdated:
                    Directory.Delete(Path.Combine(root, skill.Name), recursive: true);
                    lines.Add($"{skill.Name}: removed");
                    break;
                case State.Edited:
                    lines.Add($"{skill.Name}: kept, it was changed by hand");
                    break;
            }
        }
        return lines;
    }

    // Written beside the folder and moved into place, so a session never reads half a skill; the marker last, inside.
    private static void Write(string root, Skill skill)
    {
        Directory.CreateDirectory(root);
        var folder = Path.Combine(root, skill.Name);
        var temp = Path.Combine(root, $".{skill.Name}.pilot-tmp");
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }
        foreach (var (path, bytes) in skill.Files)
        {
            var file = Path.Combine(temp, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }
        File.WriteAllText(Path.Combine(temp, Marker), skill.Hash + "\n", new UTF8Encoding(false));
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
        Directory.Move(temp, folder);
    }

    // A folder's files but the marker, by /-separated relative path.
    private static Dictionary<string, byte[]> Read(string folder)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (relative != Marker)
            {
                files[relative] = File.ReadAllBytes(file);
            }
        }
        return files;
    }

    private static string HashOf(IReadOnlyDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData([0]);
            hash.AppendData(BitConverter.GetBytes((long)bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
