using System.Text.Json;
using System.Text.Json.Serialization;
using Pilot.Contracts;

namespace Pilot.Buddy;

/// <summary>
/// What the buddy is set to (DSN-2 v5 settings card), kept in <c>buddy-settings.json</c> beside the GUI's <c>gui-settings.json</c>. Every member is nullable
/// and the accessors below give the default, so a file written by another build, or a hand-edited one, reads (the repo's stored-records rule).
/// </summary>
public sealed record BuddySettings
{
    /// <summary>The "Watch" choices: the watchtower sees every node, "this node only" reads the local node directly.</summary>
    public static readonly IReadOnlyList<string> SourceChoices = ["Watchtower (every node)", "This node only"];

    /// <summary>The "Sit in corner" choices.</summary>
    public static readonly IReadOnlyList<string> CornerChoices = ["Bottom right", "Bottom left", "Top right"];

    /// <summary>The "Snooze" choices.</summary>
    public static readonly IReadOnlyList<string> SnoozeChoices = ["Off", "1 hour", "Until tomorrow"];

    /// <summary>Index into <see cref="SourceChoices"/>; null is the watchtower.</summary>
    public int? Source { get; init; }

    /// <summary>A session asks me something wakes the cat; null is on.</summary>
    public bool? WakeAsk { get; init; }

    /// <summary>A PR merged or a flight landed wakes the cat; null is on.</summary>
    public bool? WakeMerged { get; init; }

    /// <summary>CI failed or a session got stuck wakes the cat; null is on.</summary>
    public bool? WakeFailed { get; init; }

    /// <summary>Index into <see cref="CornerChoices"/>; null is the bottom right.</summary>
    public int? Corner { get; init; }

    /// <summary>Clicks pass through to the window below while the cat sleeps; null is on.</summary>
    public bool? ClickThrough { get; init; }

    /// <summary>Index into <see cref="SnoozeChoices"/>; null is off. The choice is what the card shows; <see cref="SnoozedUntil"/> is what holds.</summary>
    public int? Snooze { get; init; }

    /// <summary>The name of the monitor the cat sits on (SDL's display name), so a drag to another monitor is where it returns; null is the primary one.</summary>
    public string? Monitor { get; init; }

    /// <summary>The moment the snooze ends; null or past is awake.</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }

    /// <summary>See <see cref="Source"/>.</summary>
    [JsonIgnore] public int SourceIndex => Math.Clamp(Source ?? 0, 0, SourceChoices.Count - 1);

    /// <summary>See <see cref="WakeAsk"/>.</summary>
    [JsonIgnore] public bool WakesForAsk => WakeAsk ?? true;

    /// <summary>See <see cref="WakeMerged"/>.</summary>
    [JsonIgnore] public bool WakesForMerged => WakeMerged ?? true;

    /// <summary>See <see cref="WakeFailed"/>.</summary>
    [JsonIgnore] public bool WakesForFailed => WakeFailed ?? true;

    /// <summary>See <see cref="Corner"/>.</summary>
    [JsonIgnore] public int CornerIndex => Math.Clamp(Corner ?? 0, 0, CornerChoices.Count - 1);

    /// <summary>See <see cref="ClickThrough"/>.</summary>
    [JsonIgnore] public bool ClicksPassThrough => ClickThrough ?? true;

    /// <summary>See <see cref="Snooze"/>.</summary>
    [JsonIgnore] public int SnoozeIndex => Math.Clamp(Snooze ?? 0, 0, SnoozeChoices.Count - 1);

    /// <summary>Whether the cat is snoozed at <paramref name="now"/>: nothing wakes it.</summary>
    public bool IsSnoozed(DateTimeOffset now) => SnoozedUntil is { } until && now < until;

    /// <summary>Chooses a snooze: "1 hour" ends an hour from <paramref name="now"/>, "Until tomorrow" at the next midnight of <paramref name="zone"/>.</summary>
    public BuddySettings WithSnooze(int index, DateTimeOffset now, TimeZoneInfo zone)
    {
        index = Math.Clamp(index, 0, SnoozeChoices.Count - 1);
        DateTimeOffset? until = index switch
        {
            1 => now.AddHours(1),
            2 => TomorrowMidnight(now, zone),
            _ => null,
        };
        return this with { Snooze = index, SnoozedUntil = until };
    }

    private static DateTimeOffset TomorrowMidnight(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    /// <summary>The file beside the GUI's settings.</summary>
    public static string DefaultPath => Path.Combine(NodeSocket.DefaultDataFolder, "buddy-settings.json");

    /// <summary>Reads the file; a missing or unreadable one gives the defaults (a damaged file must not stop the cat).</summary>
    public static BuddySettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), BuddyJson.Default.BuddySettings) ?? new BuddySettings() : new BuddySettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new BuddySettings();
        }
    }

    /// <summary>Writes the file, through a temporary one so a crash never leaves half of it. False when it could not be written.</summary>
    public bool Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, BuddyJson.Default.BuddySettings));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BuddySettings))]
internal sealed partial class BuddyJson : JsonSerializerContext;
