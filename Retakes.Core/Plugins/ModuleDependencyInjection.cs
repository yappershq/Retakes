using Microsoft.Extensions.DependencyInjection;
using Retakes.Config;
using Retakes.Modules;
using Retakes.Player;
using Retakes.Queue;
using Retakes.RoundFlow;
using Retakes.Spawn;

namespace Retakes.Plugins;

internal static class ModuleDependencyInjection
{
    /// <summary>Register all Core services and modules into the DI container.</summary>
    internal static IServiceCollection AddModules(this IServiceCollection services)
    {
        // BootstrapModule kept as proof-of-life log
        services.AddSingleton<BootstrapModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<BootstrapModule>());

        services.AddSingleton<ConfigModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<ConfigModule>());

        services.AddSingleton<SpawnModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<SpawnModule>());

        services.AddSingleton<QueueModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<QueueModule>());

        services.AddSingleton<PlayerLifecycleModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<PlayerLifecycleModule>());

        // B2: round flow orchestration
        services.AddSingleton<RoundFlowModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<RoundFlowModule>());

        // B2: fallback weapon allocator (subscribes to OnAllocate in OAM)
        services.AddSingleton<FallbackAllocationModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<FallbackAllocationModule>());

        return services;
    }
}
