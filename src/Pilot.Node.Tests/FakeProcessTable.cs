using Pilot.Node.Flights;

namespace Pilot.Node.Tests;

/// <summary>
/// An in-memory process table for the tests of who called the node: processes with a parent, a start and a command line, and a
/// count of what was read from it, so a test can show a snapshot is shared. No real process is read.
/// </summary>
internal sealed class FakeProcessTable : IProcessTable
{
    private readonly List<ProcessEntry> _entries = [];
    private readonly Dictionary<int, DateTimeOffset> _starts = [];
    private readonly Dictionary<int, string?> _lines = [];
    private readonly Dictionary<int, ProcessEntry> _newer = [];

    /// <summary>Whole-table reads.</summary>
    public int Snapshots { get; private set; }

    /// <summary>Reads of one process's command line.</summary>
    public int DetailReads { get; private set; }

    /// <summary>Reads of one process's start.</summary>
    public int StartReads { get; private set; }

    /// <summary>Reads of one process on its own.</summary>
    public int EntryReads { get; private set; }

    /// <summary>A table that cannot be read: every call throws.</summary>
    public bool Broken { get; set; }

    /// <summary>Adds a process to the table, and to its snapshots from now on.</summary>
    public FakeProcessTable Add(int pid, int parent, string name, DateTimeOffset start, string? commandLine = null)
    {
        _entries.Add(new ProcessEntry(pid, parent, name));
        _starts[pid] = start;
        _lines[pid] = commandLine;
        return this;
    }

    /// <summary>Adds a process the whole-table read does not show yet (it started after the last snapshot): only a read of it on its own finds it.</summary>
    public FakeProcessTable AddNewer(int pid, int parent, string name, DateTimeOffset start, string? commandLine = null)
    {
        _newer[pid] = new ProcessEntry(pid, parent, name);
        _starts[pid] = start;
        _lines[pid] = commandLine;
        return this;
    }

    public FakeProcessTable Remove(int pid)
    {
        _entries.RemoveAll(e => e.Pid == pid);
        _newer.Remove(pid);
        _starts.Remove(pid);
        _lines.Remove(pid);
        return this;
    }

    public IReadOnlyList<ProcessEntry> Snapshot()
    {
        ThrowIfBroken();
        Snapshots++;
        return [.. _entries];
    }

    public DateTimeOffset? StartOf(int pid)
    {
        ThrowIfBroken();
        StartReads++;
        return _starts.TryGetValue(pid, out var start) ? start : null;
    }

    public ProcessDetail? DetailsOf(int pid)
    {
        ThrowIfBroken();
        DetailReads++;
        return _lines.TryGetValue(pid, out var line) ? new ProcessDetail(line, null) : null;
    }

    public ProcessEntry? EntryOf(int pid)
    {
        ThrowIfBroken();
        EntryReads++;
        if (_newer.TryGetValue(pid, out var newer))
        {
            return newer;
        }
        foreach (var entry in _entries)
        {
            if (entry.Pid == pid)
            {
                return entry;
            }
        }
        return null;
    }

    public void Kill(int pid) => throw new NotSupportedException("a test of who called the node kills nothing");

    private void ThrowIfBroken()
    {
        if (Broken)
        {
            throw new InvalidOperationException("the fake table is broken");
        }
    }
}
