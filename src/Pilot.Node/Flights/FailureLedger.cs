using System.Globalization;
using System.Text.Json;

namespace Pilot.Node.Flights;

/// <summary>
/// One failure the node looked into: where it was seen, which test failed and how, and the investigation it belongs to.
/// </summary>
/// <param name="Repo">The repo whose code failed, by its folder name.</param>
/// <param name="Pr">The PR it hit; null for a leg check that ran before its flight had one.</param>
/// <param name="Flight">The flight whose PR or leg it hit; null for a hand PR.</param>
/// <param name="Head">The head (or queue entry) it failed at.</param>
/// <param name="Where">Where it was seen: <see cref="FailureWhere"/>.</param>
/// <param name="RunId">The run it was read from; 0 for a leg check, which no run holds.</param>
/// <param name="Test">The full name of the failed test; empty for an error with no test in it.</param>
/// <param name="Message">The assertion message, or the error line.</param>
/// <param name="Signature">What makes it the same failure as another (<see cref="FailureSignature"/>).</param>
/// <param name="Investigation">The code of the flight made to find its cause; empty when that flight could not be made.</param>
/// <param name="Issue">The issue that carries the investigation, in the repo.</param>
public sealed record FailureRecord(string Repo, int? Pr, string? Flight, string Head, string Where, long RunId, string Test, string Message,
    string Signature, string Investigation, int Issue, DateTimeOffset At);

/// <summary>Where a failure was seen.</summary>
public static class FailureWhere
{
    /// <summary>GitHub's merge queue took the PR out because its queue run failed.</summary>
    public const string Queue = "queue";

    /// <summary>The PR's own checks, a flight's or a hand PR pilot reviewed.</summary>
    public const string Ci = "CI";

    /// <summary>A leg's check, run by the node when the leg was finished.</summary>
    public const string LegCheck = "leg check";

    /// <summary>
    /// A leg's check whose failure the finish passed (#752: a test the leg does not touch). It is on record as an investigation's occurrence,
    /// but its flight is not hit: the leg passed, so an alert never holds it.
    /// </summary>
    public const string PassedLegCheck = "leg check passed";
}

/// <summary>
/// The failures the node has looked into, one record per failure per PR, in <c>failures.json</c> in the node's data folder, so a
/// restart keeps which signature already has an investigation and which PRs it hit. A node-private ledger beside the others
/// (<see cref="Sessions.StartOptionsBook"/>, the spend and session ledgers): nothing on the stream shows it, so the wire is unchanged.
/// Without a file it is kept in memory, as a test node has it.
/// </summary>
public sealed class FailureLedger(string? file = null)
{
    private readonly object _lock = new();
    private List<FailureRecord>? _records;

    /// <summary>Where this says what it would have said on stderr; nothing for one built without.</summary>
    public TextWriter Error { get; set; } = TextWriter.Null;

    /// <summary>The ledger's file, in <paramref name="dataFolder"/>.</summary>
    public static string FileIn(string dataFolder) => Path.Combine(dataFolder, "failures.json");

    /// <summary>Every record, oldest first.</summary>
    public FailureRecord[] All()
    {
        lock (_lock)
        {
            return [.. Load()];
        }
    }

    /// <summary>The first record of <paramref name="signature"/> in <paramref name="repo"/> that has an investigation (an issue), or null: the failure is new.</summary>
    public FailureRecord? InvestigationOf(string repo, string signature)
    {
        lock (_lock)
        {
            return Load().FirstOrDefault(r => r.Issue > 0 && string.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase) && r.Signature == signature);
        }
    }

    /// <summary>Whether this failure at this place is already on record: the same signature in the same PR (or flight), head and place.</summary>
    public bool Has(string repo, int? pr, string? flight, string head, string where, string signature)
    {
        lock (_lock)
        {
            return Load().Any(r => string.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase) && r.Pr == pr && r.Flight == flight
                && string.Equals(r.Head, head, StringComparison.OrdinalIgnoreCase) && r.Where == where && r.Signature == signature);
        }
    }

    /// <summary>
    /// Whether <paramref name="signature"/> in <paramref name="repo"/> was recorded for a flight or PR other than this one: a flight is itself by its
    /// code, a hand PR by its number.
    /// </summary>
    public bool OnAnother(string repo, string signature, string? flight, int? pr)
    {
        lock (_lock)
        {
            return Load().Any(r => string.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase) && r.Signature == signature && IdentityOf(r.Flight, r.Pr) != IdentityOf(flight, pr));
        }
    }

    /// <summary>The records of <paramref name="signature"/> in <paramref name="repo"/>, oldest first.</summary>
    public FailureRecord[] Of(string repo, string signature)
    {
        lock (_lock)
        {
            return [.. Load().Where(r => string.Equals(r.Repo, repo, StringComparison.OrdinalIgnoreCase) && r.Signature == signature)];
        }
    }

    /// <summary>What a failure's place is called when two are compared: the flight's code, else the PR's number.</summary>
    public static string IdentityOf(string? flight, int? pr) => flight is { Length: > 0 } ? flight : $"#{pr}";

    /// <summary>Keeps a record.</summary>
    public void Add(FailureRecord record)
    {
        lock (_lock)
        {
            var records = Load();
            records.Add(record);
            Save(records);
        }
    }

    private List<FailureRecord> Load()
    {
        if (_records is not null)
        {
            return _records;
        }
        var records = new List<FailureRecord>();
        try
        {
            if (file is not null && File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (Read(item) is { } record)
                    {
                        records.Add(record);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A broken or unreadable file is an empty ledger: the next failure writes it afresh.
        }
        return _records = records;
    }

    private static FailureRecord? Read(JsonElement item)
    {
        string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        int? Int(string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
        if (Text("repo") is not { } repo || Text("signature") is not { } signature || Text("at") is not { } at
            || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
        {
            return null;
        }
        return new FailureRecord(repo, Int("pr"), Text("flight"), Text("head") ?? "", Text("where") ?? "", item.TryGetProperty("run", out var run) && run.TryGetInt64(out var id) ? id : 0,
            Text("test") ?? "", Text("message") ?? "", signature, Text("investigation") ?? "", Int("issue") ?? 0, when);
    }

    private void Save(List<FailureRecord> records)
    {
        if (file is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var temp = file + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartArray();
                foreach (var r in records)
                {
                    json.WriteStartObject();
                    json.WriteString("repo", r.Repo);
                    if (r.Pr is { } pr)
                    {
                        json.WriteNumber("pr", pr);
                    }
                    if (r.Flight is not null)
                    {
                        json.WriteString("flight", r.Flight);
                    }
                    json.WriteString("head", r.Head);
                    json.WriteString("where", r.Where);
                    json.WriteNumber("run", r.RunId);
                    json.WriteString("test", r.Test);
                    json.WriteString("message", r.Message);
                    json.WriteString("signature", r.Signature);
                    json.WriteString("investigation", r.Investigation);
                    json.WriteNumber("issue", r.Issue);
                    json.WriteString("at", r.At.ToString("O", CultureInfo.InvariantCulture));
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error.WriteLine($"failures: could not write {file}: {ex.Message}");
        }
    }
}
