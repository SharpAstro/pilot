using System.Text.Json;
using System.Text.Json.Serialization;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>
/// What the buddy is set to (DSN-2 v5 settings card), kept in <c>buddy-settings.json</c> beside the GUI's <c>gui-settings.json</c>. Every member is nullable
/// and the accessors below give the default, so a file written by another build, or a hand-edited one, reads (the repo's stored-records rule).
/// </summary>
public sealed record BuddySettings
{
    /// <summary>The "Sit in corner" choices.</summary>
    public static readonly IReadOnlyList<string> CornerChoices = ["Bottom right", "Bottom left", "Top right"];

    /// <summary>The "Placement" choices: the cat snaps to a corner when dropped, or stays where it is dropped.</summary>
    public static readonly IReadOnlyList<string> PlacementChoices = ["Corner", "Free"];

    /// <summary>The "Size" choices.</summary>
    public static readonly IReadOnlyList<string> SizeChoices = ["Small", "Medium", "Large"];

    /// <summary>The cat's scale per <see cref="SizeChoices"/>, against the first cat (1.0): Medium is about two thirds of it, Small and Large either side.</summary>
    public static readonly IReadOnlyList<float> SizeScales = [0.5f, 0.67f, 0.85f];

    /// <summary>The "Snooze" choices.</summary>
    public static readonly IReadOnlyList<string> SnoozeChoices = ["Off", "1 hour", "Until tomorrow"];

    /// <summary>A session asks me something wakes the cat; null is on.</summary>
    public bool? WakeAsk { get; init; }

    /// <summary>A PR merged or a flight landed wakes the cat; null is on.</summary>
    public bool? WakeMerged { get; init; }

    /// <summary>CI failed or a session got stuck wakes the cat; null is on.</summary>
    public bool? WakeFailed { get; init; }

    /// <summary>Index into <see cref="CornerChoices"/>; null is the bottom right.</summary>
    public int? Corner { get; init; }

    /// <summary>Clicks pass through to the window below while the cat sleeps; null is off, so the shape alone limits clicks and a sleeping cat can be hovered and clicked.</summary>
    public bool? ClickThrough { get; init; }

    /// <summary>Index into <see cref="SnoozeChoices"/>; null is off. The choice is what the card shows; <see cref="SnoozedUntil"/> is what holds.</summary>
    public int? Snooze { get; init; }

    /// <summary>The name of the monitor the cat sits on (SDL's display name), so a drag to another monitor is where it returns; null is the primary one.</summary>
    public string? Monitor { get; init; }

    /// <summary>Index into <see cref="PlacementChoices"/>; null is Corner.</summary>
    public int? Placement { get; init; }

    /// <summary>Index into <see cref="SizeChoices"/>; null is Medium.</summary>
    public int? Size { get; init; }

    /// <summary>Where the cat was dropped in Free placement, per monitor name (SDL's display name); null is none yet.</summary>
    public Dictionary<string, FreePosition>? FreePositions { get; init; }

    /// <summary>The moment the snooze ends; null or past is awake.</summary>
    public DateTimeOffset? SnoozedUntil { get; init; }

    /// <summary>How many minutes of quiet sleep before the cat goes away and leaves a badge at the edge (0 is never); null is <see cref="DefaultLongSleepMinutes"/>.</summary>
    public int? LongSleepMinutes { get; init; }

    /// <summary>Whether the cat shows inside a remote desktop session; null is off (it stays hidden there and the node's toasts carry on).</summary>
    public bool? ShowInRemoteSessions { get; init; }

    /// <summary>The default long sleep, in minutes.</summary>
    public const int DefaultLongSleepMinutes = 30;

    /// <summary>The "Go away after" choices, in minutes (0 is never): the first is the label's own words in <see cref="LongSleepLabels"/>.</summary>
    public static readonly IReadOnlyList<int> LongSleepChoices = [15, 30, 60, 120, 0];

    /// <summary>The words for <see cref="LongSleepChoices"/>.</summary>
    public static readonly IReadOnlyList<string> LongSleepLabels = ["15 min", "30 min", "1 hour", "2 hours", "Never"];

    /// <summary>See <see cref="LongSleepMinutes"/>.</summary>
    [JsonIgnore] public int LongSleepMinutesOrDefault => Math.Max(0, LongSleepMinutes ?? DefaultLongSleepMinutes);

    /// <summary>The long sleep as a span, or null for never.</summary>
    [JsonIgnore] public TimeSpan? LongSleep => LongSleepMinutesOrDefault is > 0 and var m ? TimeSpan.FromMinutes(m) : null;

    /// <summary>The index into <see cref="LongSleepChoices"/> of the chosen minutes, or the nearest choice at or below it.</summary>
    [JsonIgnore] public int LongSleepIndex
    {
        get
        {
            var minutes = LongSleepMinutesOrDefault;
            var best = 0;
            for (var i = 0; i < LongSleepChoices.Count; i++)
            {
                if (LongSleepChoices[i] == minutes)
                {
                    return i;
                }
                if (LongSleepChoices[i] > 0 && LongSleepChoices[i] <= minutes && (LongSleepChoices[best] == 0 || LongSleepChoices[i] > LongSleepChoices[best]))
                {
                    best = i;
                }
            }
            return best;
        }
    }

    /// <summary>See <see cref="ShowInRemoteSessions"/>.</summary>
    [JsonIgnore] public bool ShowsInRemoteSessions => ShowInRemoteSessions ?? false;

    /// <summary>See <see cref="WakeAsk"/>.</summary>
    [JsonIgnore] public bool WakesForAsk => WakeAsk ?? true;

    /// <summary>See <see cref="WakeMerged"/>.</summary>
    [JsonIgnore] public bool WakesForMerged => WakeMerged ?? true;

    /// <summary>See <see cref="WakeFailed"/>.</summary>
    [JsonIgnore] public bool WakesForFailed => WakeFailed ?? true;

    /// <summary>See <see cref="Corner"/>.</summary>
    [JsonIgnore] public int CornerIndex => Math.Clamp(Corner ?? 0, 0, CornerChoices.Count - 1);

    /// <summary>See <see cref="Placement"/>.</summary>
    [JsonIgnore] public int PlacementIndex => Math.Clamp(Placement ?? 0, 0, PlacementChoices.Count - 1);

    /// <summary>Whether the cat stays where it is dropped (Free) instead of snapping to a corner.</summary>
    [JsonIgnore] public bool IsFree => PlacementIndex == 1;

    /// <summary>See <see cref="Size"/>.</summary>
    [JsonIgnore] public int SizeIndex => Math.Clamp(Size ?? 1, 0, SizeChoices.Count - 1);

    /// <summary>The cat's scale at the chosen size.</summary>
    [JsonIgnore] public float SizeScale => SizeScales[SizeIndex];

    /// <summary>The corner the scene draws the cat in for Corner placement: the chosen one. In Free the host works it out from where the cat sits (BuddyPlacement.FreeCorner), so the bubble and cards open towards the room there; this is then only the start.</summary>
    [JsonIgnore] public int SceneCorner => IsFree ? 0 : CornerIndex;

    /// <summary>The stored free position for the monitor <paramref name="name"/>, or null when none (or an incomplete one).</summary>
    public (double X, double Y)? FreeAt(string name) =>
        FreePositions is { } map && map.TryGetValue(name, out var p) && p.X is { } x && p.Y is { } y ? (x, y) : null;

    /// <summary>The settings with the free position of monitor <paramref name="name"/> set; the other monitors keep theirs.</summary>
    public BuddySettings WithFreeAt(string name, double x, double y) =>
        this with { FreePositions = new Dictionary<string, FreePosition>(FreePositions ?? []) { [name] = new FreePosition { X = x, Y = y } } };

    /// <summary>See <see cref="ClickThrough"/>.</summary>
    [JsonIgnore] public bool ClicksPassThrough => ClickThrough ?? false;

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

/// <summary>Where the window sits on a monitor in Free placement, as fractions (0 to 1) of the distance the window can travel across the work area. Members are nullable (stored-records rule).</summary>
public sealed record FreePosition
{
    /// <summary>Across: 0 is the work area's left edge, 1 its right.</summary>
    public double? X { get; init; }

    /// <summary>Down: 0 is the work area's top edge, 1 its bottom.</summary>
    public double? Y { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BuddySettings))]
[JsonSerializable(typeof(FreePosition))]
internal sealed partial class BuddyJson : JsonSerializerContext;
