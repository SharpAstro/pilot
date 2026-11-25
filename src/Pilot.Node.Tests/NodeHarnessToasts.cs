using Pilot.Node.Toasts;

namespace Pilot.Node.Tests;

/// <summary>
/// What a test node uses for the desktop toasts and the <c>pilot:</c> scheme (#1356): the real runner starts Windows PowerShell and the real
/// registry writes HKCU, so a node a test starts must never get them (the process-start guard fails a test that starts a "toast" process).
/// </summary>
internal sealed class RecordingToastRunner : IToastRunner
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Tag, string Xml)> _shown = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _removed = new();

    public IReadOnlyCollection<(string Tag, string Xml)> Shown => _shown;

    public IReadOnlyCollection<string> Removed => _removed;

    public Task Show(string tag, string xml, CancellationToken cancel = default)
    {
        _shown.Enqueue((tag, xml));
        return Task.CompletedTask;
    }

    public Task Remove(string tag, CancellationToken cancel = default)
    {
        _removed.Enqueue(tag);
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory registry for the scheme: nothing a test node registers reaches HKCU.</summary>
internal sealed class MemorySchemeRegistry : ISchemeRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> _values = new();

    public string? Read(string subKey, string name) => _values.TryGetValue((subKey, name), out var value) ? value : null;

    public void Write(string subKey, string name, string value) => _values[(subKey, name)] = value;
}
