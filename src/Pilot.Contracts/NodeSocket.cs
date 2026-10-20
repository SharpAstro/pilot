using System.Diagnostics.CodeAnalysis;

namespace Pilot.Contracts;

/// <summary>
/// Where this user's node listens: a Unix domain socket, which is how every client on the machine reaches it. One
/// helper, shared by the node and its clients, so the two can never look in different places: pilot's names over
/// Node.Lib's <see cref="Node.Lib.NodeSocketLocation"/>, which tianwen's copy is to move onto too.
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
    public const string DefaultFileName = Node.Lib.NodeSocketLocation.DefaultFileName;

    /// <summary>The folder pilot v2 keeps its files under, per user: v1's are in <c>~/.claude</c> and the repo.</summary>
    public const string FolderName = "pilot2";

    /// <summary>pilot's node location, on the org's shared node pieces (Node.Lib).</summary>
    public static Node.Lib.NodeSocketLocation Location { get; } = new(FolderName, EnvironmentVariable);

    /// <summary>The socket every node binds and every client looks for, unless one is named.</summary>
    public static string DefaultPath { get; } = Location.DefaultPath;

    /// <summary>The node's data folder unless <c>--data</c> names another: <c>%LOCALAPPDATA%\pilot2</c>, or <c>$XDG_DATA_HOME/pilot2</c> (<c>~/.local/share/pilot2</c>).</summary>
    public static string DefaultDataFolder { get; } = Location.DefaultDataFolder;

    /// <summary>
    /// The address a request over the socket carries. Only its host reaches the node, as the <c>Host</c> header; the
    /// connection itself goes to the socket (<see cref="CreateHandler"/>).
    /// </summary>
    public static Uri BaseAddress => Node.Lib.NodeSocketLocation.BaseAddress;

    /// <inheritdoc cref="Node.Lib.NodeSocketLocation.MaxPathBytes"/>
    public static int MaxPathBytes => Node.Lib.NodeSocketLocation.MaxPathBytes;

    /// <inheritdoc cref="Node.Lib.NodeSocketLocation.LockPathFor"/>
    public static string LockPathFor(string socketPath) => Node.Lib.NodeSocketLocation.LockPathFor(socketPath);

    /// <inheritdoc cref="Node.Lib.NodeSocketLocation.TryValidate"/>
    public static bool TryValidate(string socketPath, [NotNullWhen(false)] out string? error) => Node.Lib.NodeSocketLocation.TryValidate(socketPath, out error);

    /// <summary>
    /// The socket to use: <paramref name="commandLineValue"/> (a <c>--node-socket</c> argument) first, then
    /// <see cref="EnvironmentVariable"/>, then <see cref="DefaultPath"/>.
    /// </summary>
    public static string Resolve(string? commandLineValue) => Location.Resolve(commandLineValue);

    /// <inheritdoc cref="Node.Lib.NodeSocketLocation.CreateHandler"/>
    public static SocketsHttpHandler CreateHandler(string socketPath) => Node.Lib.NodeSocketLocation.CreateHandler(socketPath);

    /// <inheritdoc cref="Node.Lib.NodeSocketLocation.IsListeningAsync"/>
    public static Task<bool> IsListeningAsync(string socketPath, CancellationToken cancellationToken) => Node.Lib.NodeSocketLocation.IsListeningAsync(socketPath, cancellationToken);
}
