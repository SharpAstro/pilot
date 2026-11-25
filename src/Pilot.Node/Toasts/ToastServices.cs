using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pilot.Node.Toasts;

/// <summary>The node's registration of the desktop toasts of Ring cards and the <c>pilot:</c> scheme their click opens (#1356).</summary>
public static class ToastServices
{
    /// <summary>
    /// Through Windows PowerShell on Windows, nowhere else. Tests inject a fake runner instead. The scheme is per user (HKCU) and written
    /// only when the GUI sits beside this node; a test host has none beside it, so writes nothing.
    /// </summary>
    public static void AddToasts(this IServiceCollection services)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        services.AddHostedService(sp => new Toaster(
            sp.GetRequiredService<NodeState>(), new PowerShellToastRunner(), sp.GetRequiredService<TimeProvider>(),
            () => sp.GetRequiredService<RoleModels>().Toasts, () => sp.GetRequiredService<PageViews>().AnyShowsCards,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Toaster>()));
        // Made here, inside the Windows guard (a lambda does not carry the guard to the analyzer).
        var schemeRegistry = new UserSchemeRegistry();
        services.AddHostedService(sp => new SchemeRegistrar(schemeRegistry,
            Environment.ProcessPath ?? "pilot-node", File.Exists, sp.GetRequiredService<ILoggerFactory>().CreateLogger<SchemeRegistrar>()));
    }
}
