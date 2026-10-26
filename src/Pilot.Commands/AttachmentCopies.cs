using Pilot.Contracts;

namespace Pilot.Commands;

/// <summary>
/// The copies the node keeps of the images artifacts name: which there are, and where to look at each from here. On this
/// machine that is the file under the node's data folder; from another node it is fetched with the grant into a temp folder.
/// </summary>
internal static class AttachmentCopies
{
    internal sealed record Copy(string Owner, string? Leg, string Title, DateTimeOffset At, string Id, string Ext);

    /// <summary>Every artifact of the snapshot that has a copy, oldest first; <paramref name="owner"/> narrows it to a flight's code or a session's id or name.</summary>
    internal static List<Copy> Of(SnapshotEvent snapshot, string? owner = null)
    {
        var copies = new List<Copy>();
        foreach (var leg in snapshot.Legs)
        {
            copies.AddRange(Of(leg.Flight, leg.Name, leg.Artifacts));
        }
        foreach (var session in snapshot.Sessions)
        {
            copies.AddRange(Of(session.Id, null, session.Artifacts ?? []));
        }
        return [.. copies
            .Where(c => owner is null || string.Equals(c.Owner, owner, StringComparison.OrdinalIgnoreCase)
                || snapshot.Sessions.Any(s => s.Id == c.Owner && string.Equals(s.Name, owner, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.At)];

        static IEnumerable<Copy> Of(string owner, string? leg, Artifact[] artifacts) =>
            artifacts.Where(a => a.Attachment is not null && a.AttachmentExt is not null)
                .Select(a => new Copy(owner, leg, a.Title, a.At, a.Attachment!, a.AttachmentExt!));
    }

    /// <summary>Where the node keeps a copy under <paramref name="dataFolder"/>, as <c>Attachments.PathOf</c> names it.</summary>
    internal static string LocalPath(string dataFolder, Copy copy) =>
        Path.Combine(Path.GetFullPath(dataFolder), "attachments",
            string.Concat(copy.Owner.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), $"{copy.Id}.{copy.Ext}");

    /// <summary>Where a copy can be looked at from here, by id: the file in the data folder, or on another node a temp file fetched with the grant. A copy that cannot be reached is left out.</summary>
    internal static async Task<Dictionary<string, string>> ResolveAsync(IEnumerable<Copy> copies, NodeClient client, string? dataFolder, CancellationToken cancellationToken)
    {
        var paths = new Dictionary<string, string>();
        foreach (var copy in copies)
        {
            if (!client.IsRemote)
            {
                if (dataFolder is not null && LocalPath(dataFolder, copy) is var local && File.Exists(local))
                {
                    paths[copy.Id] = local;
                }
                continue;
            }
            var temp = Path.Combine(Path.GetTempPath(), "pilot-attachments", $"{copy.Id}.{copy.Ext}");
            try
            {
                if (!File.Exists(temp))
                {
                    var (status, _, bytes) = await client.GetAttachmentAsync(copy.Id, cancellationToken);
                    if (status != System.Net.HttpStatusCode.OK)
                    {
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                    await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
                }
                paths[copy.Id] = temp;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                // Not shown: the artifact's own location is the fallback.
            }
        }
        return paths;
    }

    /// <summary>The text of <c>pilot attachments</c>: one line per copy, with its path, or its URL on another node.</summary>
    internal static string List(IReadOnlyList<Copy> copies, IReadOnlyDictionary<string, string> where, NodeClient client, string? dataFolder, string? owner)
    {
        if (copies.Count == 0)
        {
            return owner is null ? "No attachments.\n" : $"No attachments for {owner}.\n";
        }
        var text = new System.Text.StringBuilder();
        foreach (var copy in copies)
        {
            var path = client.IsRemote
                ? $"http://{client.SocketPath}{NodeWire.AttachmentRoute(copy.Id)}"
                : where.TryGetValue(copy.Id, out var found) ? found : dataFolder is null ? "(missing)" : LocalPath(dataFolder, copy);
            text.AppendLine($"{copy.Owner}{(copy.Leg is null ? "" : "/" + copy.Leg)}  {copy.At.ToLocalTime():yyyy-MM-dd HH:mm}  {copy.Title}  {path}");
        }
        return text.ToString();
    }
}
