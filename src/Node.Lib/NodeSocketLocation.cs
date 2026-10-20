using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;

namespace Node.Lib;

/// <summary>
/// Where one application's per-user node listens: a Unix domain socket, which is how every client on the machine reaches
/// it, and the data folder beside it. One instance per application (its folder name and the environment variable that
/// points elsewhere), shared by the node and its clients so the two never look in different places.
/// </summary>
/// <remarks>
/// <c>%LOCALAPPDATA%\&lt;folder&gt;\node.sock</c> on Windows; elsewhere under <c>$XDG_RUNTIME_DIR/&lt;folder&gt;</c> when the
/// session has one, else <c>~/.local/share/&lt;folder&gt;</c>. The folder's permissions are the access control: nothing
/// else authenticates a client on the socket. Lifted from tianwen's <c>NodeSocket</c> and pilot's copy of it.
/// </remarks>
/// <param name="folderName">The application's per-user folder (<c>pilot2</c>, <c>TianWen</c>).</param>
/// <param name="environmentVariable">The variable that names another socket when no argument does (<c>PILOT_NODE_SOCKET</c>).</param>
/// <param name="socketFolder">
/// Where the socket goes when the application keeps its files under a root of its own (tianwen's data root), read when
/// asked; null for the per-user default above.
/// </param>
public sealed class NodeSocketLocation(string folderName, string environmentVariable, Func<string>? socketFolder = null)
{
    /// <summary>The socket's file name.</summary>
    public const string DefaultFileName = "node.sock";

    public string FolderName { get; } = folderName;

    public string EnvironmentVariable { get; } = environmentVariable;

    /// <summary>The socket every node binds and every client looks for, unless one is named.</summary>
    public string DefaultPath => Path.Combine(socketFolder?.Invoke() ?? DefaultSocketFolder(), DefaultFileName);

    /// <summary>
    /// The socket a client uses, and whether it was NAMED: <paramref name="commandLineValue"/> first, then
    /// <see cref="EnvironmentVariable"/>. Named either way, a client connects there and never starts a node of its own;
    /// named neither way, it uses <see cref="DefaultPath"/> and may start one.
    /// </summary>
    public bool TryGetNamed(string? commandLineValue, out string socketPath)
    {
        foreach (var candidate in (ReadOnlySpan<string?>)[commandLineValue, Environment.GetEnvironmentVariable(EnvironmentVariable)])
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                socketPath = Path.GetFullPath(candidate);
                return true;
            }
        }

        socketPath = DefaultPath;
        return false;
    }

    /// <summary>The node's data folder unless it is told another: <c>%LOCALAPPDATA%\&lt;folder&gt;</c>, or <c>$XDG_DATA_HOME/&lt;folder&gt;</c>.</summary>
    public string DefaultDataFolder => Path.Combine(DataHome(), FolderName);

    /// <summary>
    /// The socket to use: <paramref name="commandLineValue"/> first, then <see cref="EnvironmentVariable"/>, then
    /// <see cref="DefaultPath"/>.
    /// </summary>
    public string Resolve(string? commandLineValue)
    {
        TryGetNamed(commandLineValue, out var socketPath);
        return socketPath;
    }

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
    /// An HTTP handler whose every connection goes to the node's socket: under an <see cref="HttpClient"/> with
    /// <see cref="BaseAddress"/> for requests, and under an <see cref="HttpMessageInvoker"/> for a WebSocket.
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

    private string DefaultSocketFolder()
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
