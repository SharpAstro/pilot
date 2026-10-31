namespace Pilot.Gui.Tests;

/// <summary>A node that takes whatever it is asked at once: the answer a view's callback gives (null is "taken", text is the refusal in the node's words).</summary>
internal static class Taken
{
    /// <summary>A callback that records its argument and answers that the node took it.</summary>
    public static Func<T, Task<string?>> By<T>(Action<T> record) => value =>
    {
        record(value);
        return Task.FromResult<string?>(null);
    };
}
