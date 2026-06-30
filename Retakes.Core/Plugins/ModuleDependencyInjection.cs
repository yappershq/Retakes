using Microsoft.Extensions.DependencyInjection;
using Retakes.Modules;

namespace Retakes.Plugins;

internal static class ModuleDependencyInjection
{
    /// <summary>Register all Core services and modules into the DI container.</summary>
    internal static IServiceCollection AddModules(this IServiceCollection services)
    {
        // Phase A: single bootstrap module. Add real modules here in Phase B.
        services.AddSingleton<BootstrapModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<BootstrapModule>());

        return services;
    }
}
