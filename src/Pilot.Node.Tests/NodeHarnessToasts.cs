using Pilot.Node.Toasts;

namespace Pilot.Node.Tests;

/// <summary>
/// What a test node uses for the desktop toasts and the <c>pilot:</c> scheme (#1356): the real runner shows a toast through Windows and the real
/// identity and registry write HKCU, so a node a test starts must never get them.
/// </summary>
internal sealed class RecordingToastRunner : IToastRunner
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<ToastRequest> _shown = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _removed = new();

    public IReadOnlyCollection<ToastRequest> Shown => _shown;

    public IReadOnlyCollection<string> Removed => _removed;

    public Task<string?> Show(ToastRequest request, CancellationToken cancel = default)
    {
        _shown.Enqueue(request);
        return Task.FromResult<string?>(null);
    }

    public Task<string?> Withdraw(string tag, CancellationToken cancel = default)
    {
        _removed.Enqueue(tag);
        return Task.FromResult<string?>(null);
    }
}

/// <summary>Records the identity a node registers: nothing a test node registers reaches HKCU.</summary>
internal sealed class RecordingToastIdentity : IToastIdentity
{
    public List<(string AppId, string DisplayName, string? IconPath)> Registered { get; } = [];

    public string? Failure { get; set; }

    public string? Register(string appId, string displayName, string? iconPath)
    {
        Registered.Add((appId, displayName, iconPath));
        return Failure;
    }
}

/// <summary>An in-memory registry for the scheme: nothing a test node registers reaches HKCU.</summary>
internal sealed class MemorySchemeRegistry : ISchemeRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> _values = new();

    public string? Read(string subKey, string name) => _values.TryGetValue((subKey, name), out var value) ? value : null;

    public void Write(string subKey, string name, string value) => _values[(subKey, name)] = value;
}
