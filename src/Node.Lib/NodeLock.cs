using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Node.Lib;

/// <summary>
/// The lock that admits ONE node to a socket, held for the node's whole life: <c>node.lock</c> beside
/// <c>node.sock</c>. Lifted from tianwen's <c>NodeLock</c> and pilot's copy of it.
/// </summary>
/// <remarks>
/// <para>
/// A file opened with no sharing: an exclusive open on Windows and <c>flock(LOCK_EX)</c> on Unix, both dropped by the
/// OS when the process dies, however it dies. The socket file cannot be the lock itself: it outlives a crash, and
/// clearing it by probe-then-delete lets two starting nodes both delete, the loser unlinking the winner's live socket.
/// Holding this lock is what entitles a node to clear a stale socket (<see cref="ClearStaleSocket"/>).
/// </para>
/// <para>
/// The file is never deleted, not even by its holder on a clean exit: between the holder closing it and deleting it,
/// a second node could open it, and a third would then create a new file under the same name and hold that one too.
/// </para>
/// </remarks>
public sealed class NodeLock : IDisposable
{
    private readonly FileStream _lock;

    private NodeLock(string socketPath, FileStream heldOpen)
    {
        SocketPath = socketPath;
        _lock = heldOpen;
    }

    /// <summary>The socket this lock admits its holder to.</summary>
    public string SocketPath { get; }

    /// <summary>
    /// Takes the lock on <paramref name="socketPath"/>, or answers why not: another process holds it, which is a running
    /// node unless something else has opened the file.
    /// </summary>
    public static bool TryAcquire(string socketPath, [NotNullWhen(true)] out NodeLock? held, [NotNullWhen(false)] out IOException? refusal)
    {
        var lockPath = NodeSocketLocation.LockPathFor(socketPath);
        if (Path.GetDirectoryName(lockPath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);

            // For a person reading the file. Nothing reads it back: a held lock cannot be opened on Unix, so a node that
            // is refused asks the running one instead (DescribeHolderAsync).
            stream.SetLength(0);
            stream.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
            stream.Flush();

            held = new NodeLock(socketPath, stream);
            stream = null;
            refusal = null;
            return true;
        }
        catch (IOException ex) when (stream is null && ex is not (FileNotFoundException or DirectoryNotFoundException))
        {
            // The sharing violation (Windows) or the refused flock (Unix) that another holder causes, which the runtime
            // reports only as an exception.
            held = null;
            refusal = ex;
            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>
    /// Removes the socket file a dead node left behind, which a bind onto the path would fail on. Only the lock's
    /// holder can call it, so no two nodes can ever both clear and bind.
    /// </summary>
    public void ClearStaleSocket()
    {
        if (File.Exists(SocketPath))
        {
            File.Delete(SocketPath);
        }
    }

    /// <summary>
    /// What a node refused the lock says about the one holding it: <paramref name="askHolder"/> asks it over the socket,
    /// within two seconds, since a held lock cannot be read on every OS; a holder that does not answer (still starting,
    /// or not a node at all) is described by what the lock refusal said.
    /// </summary>
    /// <param name="askHolder">Asks the node on the socket who it is, in the application's own words; it may throw.</param>
    public static async Task<string> DescribeHolderAsync(string socketPath, IOException refusal, Func<string, CancellationToken, Task<string>> askHolder, CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            return await askHolder(socketPath, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
        {
            // No node answered in time: describe the holder by the refusal alone.
        }

        return $"Another process holds the lock on {socketPath} and no node answered there: {refusal.Message}";
    }

    public void Dispose() => _lock.Dispose();
}
