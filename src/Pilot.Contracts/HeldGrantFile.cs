using System.Text.Json;

namespace Pilot.Contracts;

/// <summary>
/// One grant this user holds: the node's machine name (its <c>MachinePrefix</c>, <c>SE1</c>), the token, and the address the node
/// last answered at, as a hint. An entry read from the old flat file has no name yet: <see cref="Name"/> is null and
/// <see cref="Address"/> is the key it was saved under, until the address answers and the entry is re-keyed by name.
/// </summary>
public sealed record HeldGrant(string? Name, string Address, string Token)
{
    /// <summary>The name, else the address: how the entry is shown and forgotten.</summary>
    public string Key => Name ?? Address;
}

/// <summary>
/// <c>client-grants.json</c>: an object of <c>name: { "token": ..., "address": ... }</c>. The old shape, <c>host:port: token</c>, is
/// read as it stands (an entry with no name) and written back as it was until it is re-keyed. Each call reads the file afresh, and a
/// write goes to a sibling temporary file moved over the old one, so a reader sees the old file or the new, never half.
/// </summary>
public sealed class HeldGrantFile(string path)
{
    private static readonly object Gate = new();

    public string Path { get; } = path;

    /// <summary>Every entry: named ones first by name, then the old address-keyed ones by address.</summary>
    public IReadOnlyList<HeldGrant> Read()
    {
        lock (Gate)
        {
            return ReadUnlocked();
        }
    }

    /// <summary>Changes the entries under one lock: <paramref name="change"/> gets the list and edits it, and says whether it did; the file is written when it did.</summary>
    public void Update(Func<List<HeldGrant>, bool> change)
    {
        lock (Gate)
        {
            var entries = ReadUnlocked();
            if (change(entries))
            {
                WriteUnlocked(entries);
            }
        }
    }

    private List<HeldGrant> ReadUnlocked()
    {
        var entries = new List<HeldGrant>();
        try
        {
            if (!File.Exists(Path))
            {
                return entries;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return entries;
            }
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } old)
                {
                    entries.Add(new HeldGrant(null, property.Name.ToLowerInvariant(), old));
                }
                else if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String && token.GetString() is { Length: > 0 } text)
                {
                    var address = property.Value.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
                    entries.Add(new HeldGrant(property.Name, address.ToLowerInvariant(), text));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read holds nothing: a token that cannot be read grants nothing, and the next save writes a good one.
            return [];
        }
        return entries;
    }

    private void WriteUnlocked(List<HeldGrant> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var entry in entries.OrderBy(e => e.Name is null).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Name is null)
                {
                    writer.WriteString(entry.Address, entry.Token);
                }
                else
                {
                    writer.WriteStartObject(entry.Name);
                    writer.WriteString("token", entry.Token);
                    writer.WriteString("address", entry.Address);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndObject();
        }
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }
        var temp = Path + "." + Environment.ProcessId + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var file = new FileStream(temp, options))
        {
            file.Write(stream.ToArray());
        }
        File.Move(temp, Path, overwrite: true);
    }
}
