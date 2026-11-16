namespace Pilot.Contracts;

/// <summary>One thing a maintenance step removed, or for a dry run would remove: its name, its size and why it may go.</summary>
public sealed record MaintenanceItem(string Name, long Bytes, string Reason)
{
    /// <summary>Bytes as a short size (<c>1.5 GB</c>, <c>340 KB</c>); the one formatter the CLI listing and the first-run card share.</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}

/// <summary>How many items a keep rule kept in one step (for example <c>newest version</c>: 12).</summary>
public sealed record MaintenanceKept(string Rule, int Count);

/// <summary>One step of a maintenance run: its total, its items and what its keep rules kept. <see cref="Stopped"/> says why it did not finish, or is null.</summary>
public sealed record MaintenanceStepList(string Name, long Bytes, MaintenanceItem[] Items, MaintenanceKept[] Kept, string? Stopped = null);

/// <summary>
/// The answer to <c>pilot node maintenance</c>, beside its one-line way: per step the items with their size and reason, and the kept counts.
/// Optional on <see cref="CommandResult.Maintenance"/>, so it does not raise <see cref="NodeWire.Version"/>.
/// </summary>
public sealed record MaintenanceReport(bool DryRun, MaintenanceStepList[] Steps)
{
    /// <summary>The bytes across the steps.</summary>
    public long Bytes => Steps.Sum(s => s.Bytes);
}
