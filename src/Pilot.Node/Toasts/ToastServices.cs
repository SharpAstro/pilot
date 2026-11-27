using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Pilot.Node.Toasts;

/// <summary>The node's registration of the desktop toasts of Ring cards and the <c>pilot:</c> scheme their click opens (#1356).</summary>
public static class ToastServices
{
    /// <summary>
    /// In process through the WinRT toast API on Windows (#1376), nowhere else. The runner (<see cref="IToastRunner"/>) and the registry of the scheme
    /// (<see cref="ISchemeRegistry"/>) are services, added only when none is registered yet, so a test node registers a recording runner and a
    /// fake registry first and never calls WinRT or writes HKCU. The scheme and pilot's AUMID are is per user (HKCU) and written only when the GUI sits beside
    /// this node.
    /// </summary>
    public static void AddToasts(this IServiceCollection services)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        services.TryAddSingleton<IToastRunner>(new WinRtToastRunner());
        services.TryAddSingleton<ToastHealth>();
        // Made here, inside the Windows guard (a lambda does not carry the guard to the analyzer).
        services.TryAddSingleton<ISchemeRegistry>(new UserSchemeRegistry());
        services.AddHostedService(sp => new Toaster(
            sp.GetRequiredService<NodeState>(), sp.GetRequiredService<IToastRunner>(), sp.GetRequiredService<TimeProvider>(),
            () => sp.GetRequiredService<RoleModels>().Toasts, () => sp.GetRequiredService<PageViews>().AnyShowsCards,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Toaster>(), sp.GetRequiredService<ToastHealth>()));
        services.AddHostedService(sp => new AumidRegistrar(sp.GetRequiredService<ISchemeRegistry>(),
            Environment.ProcessPath ?? "pilot-node", File.Exists, sp.GetRequiredService<ILoggerFactory>().CreateLogger<AumidRegistrar>()));
        services.AddHostedService(sp => new SchemeRegistrar(sp.GetRequiredService<ISchemeRegistry>(),
            Environment.ProcessPath ?? "pilot-node", File.Exists, sp.GetRequiredService<ILoggerFactory>().CreateLogger<SchemeRegistrar>()));
    }
}
