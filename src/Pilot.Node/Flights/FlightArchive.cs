using System.Text.Json;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// The archive store (PIL1083): one file per landed flight under <c>archive/flights/&lt;code&gt;.json</c> in the node's data folder, holding the
/// flight's row (with its review and review rounds), its legs and its Handled cards, and when it was archived. A light index of
/// <see cref="ArchivedFlightRow"/> is loaded at start and kept in memory, so the lists and the code lookups never open a file.
/// </summary>
/// <remarks>
/// Archiving moves: the file is written beside and moved into place first, then the node's event takes the flight out of its state, so a
/// crash between leaves a file for a flight still live, which the next archive of it overwrites (<see cref="NodeState.ArchivedFlightRows"/>
/// leaves such a row out). Nothing is deleted but the file, by an undo, once the flight is back in the state.
/// </remarks>
public sealed class FlightArchive
{
    /// <summary>Where this says what it would have said on stderr: the node's log, nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    private readonly string _folder;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ArchivedFlightRow> _index = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The archive's folder in <paramref name="dataFolder"/>.</summary>
    public static string FolderIn(string dataFolder) => Path.Combine(dataFolder, "archive", "flights");

    /// <summary>Opens the archive in <paramref name="dataFolder"/>, reading the index from the files there; a file that cannot be read is left out and said.</summary>
    public FlightArchive(string dataFolder)
    {
        _folder = FolderIn(dataFolder);
        if (!Directory.Exists(_folder))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                var archived = JsonSerializer.Deserialize(File.ReadAllText(file), PilotJsonContext.Default.ArchivedFlight);
                if (archived is not null)
                {
                    _index[archived.Row.Code] = archived.Row;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Error.WriteLine($"archive: {file} is not read: {ex.Message}");
            }
        }
    }

    private string FileOf(string code)
    {
        if (code.Length == 0 || code.Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw new FlightException($"{code} is not a flight code");
        }
        return Path.Combine(_folder, code + ".json");
    }

    /// <summary>How many flights the archive holds.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>How many archived flights <paramref name="keep"/> takes by code, with no copy and no sort (#1090: the node row's count sorted every row).</summary>
    public int CountOf(Func<string, bool> keep)
    {
        lock (_lock)
        {
            var count = 0;
            foreach (var code in _index.Keys)
            {
                count += keep(code) ? 1 : 0;
            }
            return count;
        }
    }

    /// <summary>Every archived flight's light row, newest landed first.</summary>
    public ArchivedFlightRow[] Rows()
    {
        lock (_lock)
        {
            return [.. _index.Values.OrderByDescending(r => r.LandedAt ?? r.ArchivedAt).ThenBy(r => r.Code, StringComparer.Ordinal)];
        }
    }

    /// <summary>The light row of <paramref name="code"/>, or null when it is not archived.</summary>
    public ArchivedFlightRow? Find(string code)
    {
        lock (_lock)
        {
            return _index.GetValueOrDefault(code);
        }
    }

    /// <summary>Writes <paramref name="flight"/> to its file, beside and then moved into place, and into the index; one already there is replaced.</summary>
    public void Save(ArchivedFlight flight)
    {
        var file = FileOf(flight.Row.Code);
        Directory.CreateDirectory(_folder);
        var beside = file + ".tmp";
        File.WriteAllText(beside, JsonSerializer.Serialize(flight, PilotJsonContext.Default.ArchivedFlight));
        lock (_lock)
        {
            File.Move(beside, file, overwrite: true);
            _index[flight.Row.Code] = flight.Row;
        }
    }

    /// <summary>The whole flight of <paramref name="code"/> from its file, or null when it is not archived (or its file is gone).</summary>
    public ArchivedFlight? Read(string code)
    {
        if (Find(code) is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FileOf(code)), PilotJsonContext.Default.ArchivedFlight);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Error.WriteLine($"archive: {code} is not read: {ex.Message}");
            return null;
        }
    }

    /// <summary>Deletes the file of <paramref name="code"/> and drops it from the index: only once the flight is back in the state.</summary>
    public void Delete(string code)
    {
        var file = FileOf(code);
        lock (_lock)
        {
            _index.Remove(code);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>The light row a flight would be archived under, <paramref name="at"/> being the moment.</summary>
    public static ArchivedFlightRow RowOf(FlightRow flight, DateTimeOffset at) => new()
    {
        Code = flight.Code,
        Repo = flight.Repo,
        Title = flight.Title,
        Issue = flight.Issue,
        PullRequest = flight.PullRequest,
        PullRequestUrl = flight.PullRequestUrl,
        LandedAt = flight.LandedAt,
        ArchivedAt = at,
        FoldedInto = flight.FoldedInto,
        MovedTo = flight.MovedTo?.To,
        BranchDeletedAt = flight.BranchDeletedAt,
    };
}
