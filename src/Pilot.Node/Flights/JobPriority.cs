namespace Pilot.Node.Flights;

/// <summary>
/// How much CPU a job's or a check's process tree yields to other work on the machine (#629). Normal is the priority the
/// node itself runs at; the others are held for the whole tree, children included.
/// </summary>
public enum JobPriority
{
    Idle,
    BelowNormal,
    Normal,
}

public static class JobPriorities
{
    /// <summary>The Windows priority class: IDLE_PRIORITY_CLASS 0x40, BELOW_NORMAL_PRIORITY_CLASS 0x4000; 0 for Normal (none is set).</summary>
    public static uint WindowsClass(this JobPriority priority) => priority switch
    {
        JobPriority.Idle => 0x40,
        JobPriority.BelowNormal => 0x4000,
        _ => 0,
    };

    /// <summary>The nice value: 19 for Idle, 10 for BelowNormal, null for Normal (nice is not run).</summary>
    public static int? Nice(this JobPriority priority) => priority switch
    {
        JobPriority.Idle => 19,
        JobPriority.BelowNormal => 10,
        _ => null,
    };

    /// <summary>The path of <c>nice</c> on this machine, or null where there is none.</summary>
    public static string? NicePath() => new[] { "/usr/bin/nice", "/bin/nice" }.FirstOrDefault(File.Exists);
}
