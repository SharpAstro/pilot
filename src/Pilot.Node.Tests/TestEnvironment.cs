using System.Runtime.CompilerServices;

namespace Pilot.Node.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// The tests' CLI is a human's unless a test says otherwise: whoever runs them (a Claude session's tool has CLAUDECODE in its
    /// environment, which would make every <c>pilot</c> the tests run an assistant's, its your-only commands waiting on a card).
    /// </summary>
    [ModuleInitializer]
    internal static void ClearClaudeCode() => Environment.SetEnvironmentVariable("CLAUDECODE", null);
}
