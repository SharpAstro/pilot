using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;

namespace Pilot.Contracts;

/// <summary>
/// Where this user's node listens: a Unix domain socket, which is how every client on the machine reaches it. One
/// helper, shared by the node and its clients, so the two can never look in different places. Adapted from tianwen's
/// <c>NodeSocket</c>.
/// </summary>
/// <remarks>
/// <c>%LOCALAPPDATA%\pilot2\node.sock</c> on Windows; elsewhere under <c>$XDG_RUNTIME_DIR/pilot2</c> when the session
/// has one, else <c>~/.local/share/pilot2</c>. The folder's permissions are the access control: nothing else
/// authenticates a client on the socket.
/// </remarks>
public static class NodeSocket
{
    /// <summary>Points a client or a node at a socket instead of the default one: what <c>--node-socket</c> sets when no argument names it.</summary>
    public const string EnvironmentVariable = "PILOT_NODE_SOCKET";

    /// <summary>The socket's file name.</summary>
    public const string DefaultFileName = "node.sock";

    /// <summary>The folder pilot v2 keeps its files under, per user: v1's are in <c>~/.claude</c> and the repo.</summary>
    public const string FolderName = "pilot2";

    /// <summary>The socket every node binds and every client looks for, unless one is named.</summary>
    public static string DefaultPath { get; } = Path.Combine(DefaultSocketFolder(), DefaultFileName);

    /// <summary>The node's data folder unless <c>--data</c> names another: <c>%LOCALAPPDATA%\pilot2</c>, or <c>$XDG_DATA_HOME/pilot2</c> (<c>~/.local/share/pilot2</c>).</summary>
    public static string DefaultDataFolder { get; } = Path.Combine(DataHome(), FolderName);

    /// <summary>
    /// The address a request over the socket carries. Only its host reaches the node, as the <c>Host</c> header; the
    /// connection itself goes to the socket (<see cref="CreateHandler"/>).
    /// </summary>
    public static Uri BaseAddress { get; } = new Uri("http://localhost/");

    /// <summary>
    /// The longest socket path this system can bind, in bytes: <c>sun_path</c> holds 108 on Linux and Windows and 104
    /// on macOS and the BSDs, one of which is the terminator.
    /// </summary>
    public static int MaxPathBytes => OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 103 : 107;

    /// <summary>
    /// The lock beside a socket, which admits one node to it: <c>node.sock</c> is guarded by <c>node.lock</c>. Named
    /// after its socket, so a test's node on a socket of its own takes a lock of its own.
    /// </summary>
    public static string LockPathFor(string socketPath) => Path.ChangeExtension(socketPath, ".lock");

    /// <summary>
    /// Whether <paramref name="socketPath"/> fits in a socket address. A path that does not fit is refused with a
    /// message naming it, rather than bound truncated, which would put the node somewhere no client looks.
    /// </summary>
    public static bool TryValidate(string socketPath, [NotNullWhen(false)] out string? error)
    {
        var bytes = Encoding.UTF8.GetByteCount(socketPath);
        if (bytes > MaxPathBytes)
        {
            error = $"The node socket path is {bytes} bytes, longer than the {MaxPathBytes} a Unix domain socket can hold here: {socketPath}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// The socket to use: <paramref name="commandLineValue"/> (a <c>--node-socket</c> argument) first, then
    /// <see cref="EnvironmentVariable"/>, then <see cref="DefaultPath"/>.
    /// </summary>
    public static string Resolve(string? commandLineValue)
    {
        foreach (var candidate in (ReadOnlySpan<string?>)[commandLineValue, Environment.GetEnvironmentVariable(EnvironmentVariable)])
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return DefaultPath;
    }

    /// <summary>
    /// An HTTP handler whose every connection goes to the node's socket: under an <see cref="HttpClient"/> with
    /// <see cref="BaseAddress"/> for requests, and under an <see cref="HttpMessageInvoker"/> for the event stream's
    /// WebSocket.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(string socketPath) => new SocketsHttpHandler
    {
        ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    /// <summary>
    /// Whether anything accepts a connection on <paramref name="socketPath"/>. False when there is no socket file, or
    /// only a stale one nothing listens on, and for a path too long to be a socket address at all.
    /// </summary>
    public static async Task<bool> IsListeningAsync(string socketPath, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }
    }

    private static string DefaultSocketFolder()
    {
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime)
        {
            return Path.Combine(runtime, FolderName);
        }

        return Path.Combine(DataHome(), FolderName);
    }

    private static string DataHome()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
            ? dataHome
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }
}
