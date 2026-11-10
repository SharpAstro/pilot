using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Connections.Features;
using Node.Lib.Hosting;
using Pilot.Node.Flights;

namespace Pilot.Node;

/// <summary>Which of pilot's own long-lived programs a caller's chain passed through; a caller that ran under several has several bits.</summary>
[Flags]
public enum CallerVia
{
    None = 0,

    /// <summary>A <c>pilot mcp</c> server (a Claude Code session's tools), or one it handed over to.</summary>
    Mcp = 1,

    /// <summary>The GUI (<c>pilot-gui</c>).</summary>
    Gui = 2,

    /// <summary>The TUI (<c>pilot-tui</c>).</summary>
    Tui = 4,
}

/// <summary>What a counted request came from.</summary>
public enum CallerKind
{
    /// <summary>A process on this machine, over the node's socket.</summary>
    Process,

    /// <summary>A client on the LAN, known by its grant's label and no pid.</summary>
    Lan,

    /// <summary>A request whose peer could not be found out; counted all the same.</summary>
    Unknown,
}

/// <summary>
/// One caller's requests in one UTC clock hour. A process caller is the first ancestor of the peer that is not pilot's own
/// program (<see cref="CommandCallers"/>), keyed by (<see cref="Pid"/>, <see cref="Start"/>); a LAN caller is its grant's label.
/// </summary>
/// <param name="Hour">The clock hour, UTC, as <see cref="NodeTraffic.HourOf"/> cuts it.</param>
/// <param name="Kind">A process, a LAN client or an unresolved peer.</param>
/// <param name="Pid">The process id; 0 for a LAN client or an unresolved peer.</param>
/// <param name="Start">When that process started, so a reused pid is another caller; default where the OS would not say, and for a LAN client.</param>
/// <param name="Name">The program (<c>bash</c>, <c>pwsh.exe</c>), a LAN grant's label, or <c>unknown</c>.</param>
/// <param name="CommandLine">How the process was started; null where the OS would not say.</param>
/// <param name="ParentPid">The caller's own parent; 0 where there is none. A parent no longer there marks a process left behind.</param>
/// <param name="Via">Whether the requests came through the pilot MCP server, GUI or TUI.</param>
/// <param name="Count">The requests counted.</param>
public sealed record CallerRow(DateTimeOffset Hour, CallerKind Kind, int Pid, DateTimeOffset Start, string Name, string? CommandLine, int ParentPid, CallerVia Via, int Count)
{
    /// <summary>The command line kept in a log line.</summary>
    public const int LineClip = 80;

    /// <summary><c>name pid (command line, clipped) count</c>, on one line: a LAN client has no pid (<c>-</c>) and says so in place of a command line.</summary>
    public string Line(int clip = LineClip)
    {
        var what = Kind switch
        {
            CallerKind.Lan => "LAN grant",
            CallerKind.Unknown => "peer not resolved",
            _ => Clip(CommandLine, clip),
        };
        return string.Create(CultureInfo.InvariantCulture, $"{Name} {(Kind == CallerKind.Process ? Pid.ToString(CultureInfo.InvariantCulture) : "-")} ({what}) {Count}");
    }

    private static string Clip(string? line, int clip)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "no command line";
        }
        // One line in a log: any run of whitespace (a newline in an argument too) is one space.
        var flat = new StringBuilder(line.Length);
        var space = false;
        foreach (var c in line.Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = true;
                continue;
            }
            if (space)
            {
                flat.Append(' ');
                space = false;
            }
            flat.Append(c);
        }
        return flat.Length <= clip ? flat.ToString() : string.Concat(flat.ToString(0, Math.Max(0, clip - 3)), "...");
    }
}

/// <summary>The peer process of a request on the node's socket.</summary>
public static class CallerPeer
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    // _WSAIOR(IOC_VENDOR, 256): the AF_UNIX peer's process id, as a ULONG.
    private const int SioAfUnixGetPeerPid = 0x58000100;

    /// <summary>
    /// The process at the other end of a connected Unix domain socket: <c>SO_PEERCRED</c> (a <c>struct ucred</c>, the pid first) on
    /// Linux, the <c>SIO_AF_UNIX_GETPEERPID</c> ioctl on Windows. Null where the OS cannot say (another platform, a socket that is
    /// not a connected Unix one) and never throws.
    /// </summary>
    public static int? PidOf(Socket socket)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                Span<byte> ucred = stackalloc byte[12];
                return socket.GetRawSocketOption(SolSocket, SoPeerCred, ucred) >= sizeof(int) && BinaryPrimitives.ReadInt32LittleEndian(ucred) is > 0 and var pid ? pid : null;
            }
            if (OperatingSystem.IsWindows())
            {
                var output = new byte[sizeof(uint)];
                socket.IOControl(SioAfUnixGetPeerPid, null, output);
                return BinaryPrimitives.ReadUInt32LittleEndian(output) is > 0 and var pid && pid <= int.MaxValue ? (int)pid : null;
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException or InvalidOperationException or NotSupportedException)
        {
            // Not ours to ask, or the connection is already gone.
        }
        return null;
    }

    /// <summary>The socket of the connection that carried <paramref name="context"/>'s request, from Kestrel's own feature; null when the transport gives none.</summary>
    public static Socket? SocketOf(HttpContext context) => context.Features.Get<IConnectionSocketFeature>()?.Socket;
}

/// <summary>
/// Counts every request the node takes by who made it, per UTC clock hour, so the hourly traffic line can say which caller is
/// hammering the node (#870: orphaned watcher loops). A caller is the first ancestor of the peer process that is not pilot's
/// own program: the short-lived <c>pilot</c> of a command, a <c>pilot mcp</c> server, the GUI or the TUI are walked past, so a
/// <c>bash</c> loop that runs <c>pilot status</c> every two minutes is one caller (the bash), not a new <c>pilot.exe</c> each time.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A caller is keyed by (pid, start), so a pid the OS hands to a new process is a new caller.</item>
/// <item>The resolution of a peer is cached per (pid, start) for <see cref="ResolutionLife"/>, and what a caller is (its command
/// line, read from the OS) per (pid, start) of the caller, so a loop's tenth <c>pilot</c> reads nothing about the bash again.</item>
/// <item>The table is read whole at most every <see cref="SnapshotEvery"/>; a process newer than the snapshot (every <c>pilot</c>
/// command is one) is read on its own (<see cref="IProcessTable.EntryOf"/>), never by reading the table again.</item>
/// <item>A LAN request has no process here: it is counted by its grant label (<see cref="CountLan"/>).</item>
/// <item>Nothing here throws at a request: a peer that cannot be resolved is counted as unknown.</item>
/// </list>
/// </remarks>
public sealed class CommandCallers(IProcessTable table, TimeProvider time)
{
    /// <summary>How many clock hours are kept, as <see cref="NodeTraffic"/> keeps them.</summary>
    public const int KeptHours = 48;

    /// <summary>How many callers the hourly line names.</summary>
    public const int TopCount = 3;

    private const int MaxHops = 32;
    private const int CacheCap = 4096;

    /// <summary>The oldest the table snapshot gets before the next resolution reads it again.</summary>
    public TimeSpan SnapshotEvery { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a peer's resolution is trusted before its chain is walked again (a parent that died re-parents its children).</summary>
    public TimeSpan ResolutionLife { get; init; } = TimeSpan.FromMinutes(10);

    private readonly record struct Identity(int Pid, DateTimeOffset Start);

    private sealed record Caller(int Pid, DateTimeOffset Start, string Name, string? CommandLine, int ParentPid);

    private sealed record Resolved(Caller Caller, CallerVia Via, DateTimeOffset At);

    private readonly record struct Key(CallerKind Kind, int Pid, DateTimeOffset Start, string Label);

    private sealed class Tally
    {
        public required CallerKind Kind;
        public required int Pid;
        public required DateTimeOffset Start;
        public required string Name;
        public string? CommandLine;
        public int ParentPid;
        public CallerVia Via;
        public int Count;
    }

    private readonly Lock _resolving = new();
    private readonly Dictionary<int, ProcessEntry> _snapshot = [];
    private DateTimeOffset? _snapshotAt;
    private readonly Dictionary<Identity, Resolved> _peers = [];
    private readonly Dictionary<Identity, Caller> _callers = [];

    private readonly Lock _counting = new();
    private readonly SortedDictionary<DateTimeOffset, Dictionary<Key, Tally>> _hours = [];

    /// <summary>How many times the whole table was read: a test (and the node's curiosity) reads it to see the snapshot is shared.</summary>
    public int SnapshotsTaken { get; private set; }

    /// <summary>Counts one request from the process <paramref name="peerPid"/> (the other end of the node's socket); null counts as unknown.</summary>
    public void CountPeer(int? peerPid)
    {
        Resolved? resolved = null;
        if (peerPid is > 0 and var pid)
        {
            try
            {
                lock (_resolving)
                {
                    resolved = Resolve(pid);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A table that fails is an unresolved peer, never a failed request.
                resolved = null;
            }
        }
        if (resolved is null)
        {
            Add(new Key(CallerKind.Unknown, 0, default, "unknown"), null, 0, CallerVia.None);
            return;
        }
        var caller = resolved.Caller;
        Add(new Key(CallerKind.Process, caller.Pid, caller.Start, ""), caller, caller.ParentPid, resolved.Via);
    }

    /// <summary>Counts one request from the LAN, under the label of the grant it came with (no pid: the caller is another machine's).</summary>
    public void CountLan(string label) =>
        Add(new Key(CallerKind.Lan, 0, default, label), null, 0, CallerVia.None);

    /// <summary>The requests of the clock hour starting at <paramref name="hour"/>, most first (then by pid); empty when none were counted.</summary>
    public IReadOnlyList<CallerRow> Rows(DateTimeOffset hour)
    {
        lock (_counting)
        {
            return _hours.TryGetValue(hour, out var tallies) ? Order(hour, tallies) : [];
        }
    }

    /// <summary>Every kept hour's rows, oldest hour first and each hour's most first: what the node's ring of callers reads.</summary>
    public IReadOnlyList<CallerRow> Rows()
    {
        lock (_counting)
        {
            var rows = new List<CallerRow>();
            foreach (var (hour, tallies) in _hours)
            {
                rows.AddRange(Order(hour, tallies));
            }
            return rows;
        }
    }

    /// <summary>The <paramref name="count"/> callers with the most requests in the hour starting at <paramref name="hour"/>.</summary>
    public IReadOnlyList<CallerRow> Top(DateTimeOffset hour, int count = TopCount) => [.. Rows(hour).Take(count)];

    /// <summary><c>top callers: &lt;name&gt; &lt;pid&gt; (&lt;command line, clipped&gt;) &lt;n&gt;, ...</c> for the top 3 of an hour, or <c>top callers: none</c>.</summary>
    public string TopLine(DateTimeOffset hour) =>
        Top(hour) is { Count: > 0 } top ? "top callers: " + string.Join(", ", top.Select(r => r.Line())) : "top callers: none";

    private static List<CallerRow> Order(DateTimeOffset hour, Dictionary<Key, Tally> tallies) =>
    [
        .. tallies.Values
            .OrderByDescending(t => t.Count).ThenBy(t => t.Pid).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new CallerRow(hour, t.Kind, t.Pid, t.Start, t.Name, t.CommandLine, t.ParentPid, t.Via, t.Count)),
    ];

    private void Add(Key key, Caller? caller, int parentPid, CallerVia via)
    {
        var hour = NodeTraffic.HourOf(time.GetUtcNow());
        lock (_counting)
        {
            if (!_hours.TryGetValue(hour, out var tallies))
            {
                _hours[hour] = tallies = [];
                while (_hours.Count > KeptHours)
                {
                    _hours.Remove(_hours.Keys.First());
                }
            }
            if (!tallies.TryGetValue(key, out var tally))
            {
                tallies[key] = tally = new Tally
                {
                    Kind = key.Kind,
                    Pid = key.Pid,
                    Start = key.Start,
                    Name = caller?.Name ?? key.Label,
                    CommandLine = caller?.CommandLine,
                    ParentPid = parentPid,
                };
            }
            tally.Count++;
            tally.Via |= via;
        }
    }

    // ---- Resolving a peer ----

    private Resolved? Resolve(int peerPid)
    {
        var now = time.GetUtcNow();
        var peer = new Identity(peerPid, table.StartOf(peerPid) ?? default);
        if (_peers.TryGetValue(peer, out var cached) && now - cached.At < ResolutionLife)
        {
            return cached;
        }
        if (_snapshotAt is not { } taken || now - taken >= SnapshotEvery)
        {
            TakeSnapshot(now);
        }
        if (Walk(peerPid) is not { } found)
        {
            // Not cached: the next request from it looks again.
            return null;
        }
        var resolved = new Resolved(found.Caller, found.Via, now);
        if (_peers.Count >= CacheCap)
        {
            _peers.Clear();
        }
        _peers[peer] = resolved;
        return resolved;
    }

    private void TakeSnapshot(DateTimeOffset now)
    {
        _snapshot.Clear();
        foreach (var entry in table.Snapshot())
        {
            _snapshot[entry.Pid] = entry;
        }
        _snapshotAt = now;
        SnapshotsTaken++;
    }

    private ProcessEntry? Lookup(int pid) =>
        _snapshot.TryGetValue(pid, out var entry) ? entry : table.EntryOf(pid);

    /// <summary>The first ancestor of <paramref name="peerPid"/> (itself included) that is not one of pilot's own programs.</summary>
    private (Caller Caller, CallerVia Via)? Walk(int peerPid)
    {
        var via = CallerVia.None;
        ProcessEntry? top = null;
        var seen = new HashSet<int>();
        var pid = peerPid;
        // A pid the OS reused can close a loop in the parents, so the walk is bounded and never visits one twice.
        for (var hop = 0; hop < MaxHops && pid > 0 && seen.Add(pid); hop++)
        {
            if (Lookup(pid) is not { } entry)
            {
                break;
            }
            if (PilotProgram(entry) is not { } added)
            {
                // Git Bash (MSYS) forks a bash before each external command, so the pilot's parent is a one-off bash
                // under the loop's own bash: walk up to the outermost bash of the run, the loop.
                if (IsShell(entry) && Lookup(entry.ParentPid) is { } up && IsShell(up) && SameProgram(entry, up) && !seen.Contains(up.Pid))
                {
                    pid = up.Pid;
                    continue;
                }
                return (CallerOf(entry), via);
            }
            via |= added;
            top = entry;
            pid = entry.ParentPid;
        }
        // Nothing but pilot's own programs up to a parent that is gone: the topmost of them is the caller.
        return top is { } last ? (CallerOf(last), via) : null;
    }

    private static string ProgramOf(ProcessEntry entry) => Path.GetFileNameWithoutExtension(entry.Name);

    private static bool IsShell(ProcessEntry entry) =>
        ProgramOf(entry).ToLowerInvariant() is "bash" or "sh";

    private static bool SameProgram(ProcessEntry a, ProcessEntry b) =>
        ProgramOf(a).Equals(ProgramOf(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>What <paramref name="entry"/> adds to the chain when it is one of pilot's own programs (<see cref="CallerVia.None"/> for the CLI of a command); null when it is not.</summary>
    private CallerVia? PilotProgram(ProcessEntry entry)
    {
        // A program an install moved aside is <c>pilot.exe~old-1</c>.
        var program = Path.GetFileNameWithoutExtension(entry.Name.Split('~')[0]);
        if (program.Equals("pilot-gui", StringComparison.OrdinalIgnoreCase))
        {
            return CallerVia.Gui;
        }
        if (program.Equals("pilot-tui", StringComparison.OrdinalIgnoreCase))
        {
            return CallerVia.Tui;
        }
        if (!program.Equals("pilot", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return IsMcp(table.DetailsOf(entry.Pid)?.CommandLine) ? CallerVia.Mcp : CallerVia.None;
    }

    /// <summary>Whether a <c>pilot</c> command line is the MCP server's: one of its arguments is exactly <c>mcp</c>.</summary>
    internal static bool IsMcp(string? commandLine) =>
        commandLine is not null && commandLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Any(a => a.Trim('"') == "mcp");

    private Caller CallerOf(ProcessEntry entry)
    {
        var key = new Identity(entry.Pid, table.StartOf(entry.Pid) ?? default);
        if (_callers.TryGetValue(key, out var known))
        {
            return known;
        }
        var caller = new Caller(entry.Pid, key.Start, entry.Name, table.DetailsOf(entry.Pid)?.CommandLine, entry.ParentPid);
        if (_callers.Count >= CacheCap)
        {
            _callers.Clear();
        }
        _callers[key] = caller;
        return caller;
    }
}

/// <summary>The one middleware that counts every request by its caller, ahead of every route.</summary>
public static class CommandCallersMiddleware
{
    /// <summary>
    /// Counts each request into <see cref="CommandCallers"/> before the route runs: by the peer process over the socket, by the
    /// grant's label (or the address) from the LAN. A failure to count is logged at debug and never fails the request.
    /// Placed after the access gate, so a LAN request's grant label is known.
    /// </summary>
    public static IApplicationBuilder UseCommandCallers(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                var callers = context.RequestServices.GetRequiredService<CommandCallers>();
                if (NodeAccessGate.CameOverTheSocket(context))
                {
                    callers.CountPeer(CallerPeer.SocketOf(context) is { } socket ? CallerPeer.PidOf(socket) : null);
                }
                else
                {
                    callers.CountLan(context.Items[NodeLan.GrantLabelKey] is string grant
                        ? grant
                        : $"lan {context.Connection.RemoteIpAddress}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                context.RequestServices.GetService<ILogger<CommandCallers>>()?.LogDebug(ex, "a request could not be counted by its caller");
            }
            await next(context);
        });
}
