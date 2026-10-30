using Pilot.Contracts;
using Pilot.Node.Flights;

namespace Pilot.Node;

/// <summary>
/// The copies the node keeps of the images an artifact names, under its data folder at
/// <c>attachments/&lt;flight code or session&gt;/&lt;id&gt;.&lt;ext&gt;</c>, so a screenshot outlives the plane or folder it was
/// taken in and is shown by the app and the CLI from the node, not from a path on a machine.
/// </summary>
public sealed class Attachments(string dataFolder)
{
    private static readonly HashSet<string> ImageExtensions = new(["png", "jpg", "jpeg", "gif", "webp", "bmp"], StringComparer.OrdinalIgnoreCase);

    private string Root => Path.Combine(Path.GetFullPath(dataFolder), "attachments");

    /// <summary>The extension (no dot, lower case) of a path that names an image, else null; a URL names none.</summary>
    public static string? ImageExtensionOf(string location)
    {
        if (location.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        var ext = Path.GetExtension(location).TrimStart('.').ToLowerInvariant();
        return ImageExtensions.Contains(ext) ? ext : null;
    }

    /// <summary>The content type a copy is served with.</summary>
    public static string ContentType(string ext) => ext switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// <summary>Where a copy is, or would be.</summary>
    public string PathOf(string owner, string id, string ext) => Path.Combine(Root, Safe(owner), $"{id}.{ext}");

    /// <summary>
    /// <paramref name="artifact"/> with a copy of its file made for <paramref name="owner"/> when it is a local image; any other
    /// is returned as it is. A missing image file is a <see cref="FlightException"/> saying so. A copy <paramref name="replaced"/>
    /// goes when it was one.
    /// </summary>
    public Artifact Keep(string owner, Artifact artifact, Artifact? replaced)
    {
        if (ImageExtensionOf(artifact.Location) is not { } ext)
        {
            return artifact;
        }
        if (!File.Exists(artifact.Location))
        {
            throw new FlightException($"there is no file {artifact.Location} to keep: add-artifact copies an image when it is recorded");
        }
        var id = Guid.NewGuid().ToString("N")[..12];
        var target = PathOf(owner, id, ext);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(artifact.Location, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FlightException($"could not keep a copy of {artifact.Location}: {ex.Message}");
        }
        if (replaced is { Attachment: { } old, AttachmentExt: { } oldExt })
        {
            Delete(PathOf(owner, old, oldExt));
        }
        return artifact with { Attachment = id, AttachmentExt = ext };
    }

    /// <summary>The owner of the images pasted into ideas: no flight or session, so no archive removes them.</summary>
    public const string IdeaOwner = "ideas";

    /// <summary>The folder holding the idea images, which Claude is given to read.</summary>
    public string IdeaFolder => Path.GetDirectoryName(PathOf(IdeaOwner, "x", "png"))!;

    /// <summary><paramref name="ext"/> (no dot, lower case) when it names an image the node keeps, else null.</summary>
    public static string? IdeaImageExtension(string? ext) =>
        ext?.TrimStart('.').ToLowerInvariant() is { } e && ImageExtensions.Contains(e) ? e : null;

    private static bool IsId(string id) => id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit);

    /// <summary>Keeps <paramref name="bytes"/> as an idea's image under a new id, and answers the id.</summary>
    public string AddIdeaImage(byte[] bytes, string ext)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        SaveIdeaImage(id, ext, bytes);
        return id;
    }

    /// <summary>Keeps <paramref name="bytes"/> as the idea image <paramref name="id"/>.</summary>
    public void SaveIdeaImage(string id, string ext, byte[] bytes)
    {
        var target = PathOf(IdeaOwner, id, ext);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FlightException($"could not keep the image: {ex.Message}");
        }
    }

    /// <summary>The file and extension of idea image <paramref name="id"/>, when the node holds it.</summary>
    public (string Path, string Ext)? FindIdeaImage(string id)
    {
        if (!IsId(id))
        {
            return null;
        }
        foreach (var ext in ImageExtensions)
        {
            var path = PathOf(IdeaOwner, id, ext);
            if (File.Exists(path))
            {
                return (path, ext);
            }
        }
        return null;
    }

    /// <summary>Removes every copy kept for <paramref name="owner"/> (a flight's code or a session's id), and nothing else's.</summary>
    public void Remove(string owner)
    {
        var folder = Path.Combine(Root, Safe(owner));
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held open by a reader: the next archive of the owner tries again, and nothing shows it meanwhile.
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Safe(string owner) => string.Concat(owner.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
