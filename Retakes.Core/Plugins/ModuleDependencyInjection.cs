using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retakes.Allocator;
using Retakes.Announcement;
using Retakes.Bomb;
using Retakes.Breaker;
using Retakes.Config;
using Retakes.Database;
using Retakes.Defuse;
using Retakes.Modules;
using Retakes.Player;
using Retakes.Queue;
using Retakes.RoundFlow;
using Retakes.Spawn;
using Retakes.Zones;

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

        // D1: RoundTypeManager — depends on ConfigModule (singleton, safe after config Init)
        services.AddSingleton<RoundTypeManager>();

        // B2: round flow orchestration (now injects RoundTypeManager)
        services.AddSingleton<RoundFlowModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<RoundFlowModule>());

        // B2: fallback weapon allocator — no-op when AllocatorModule is enabled
        services.AddSingleton<FallbackAllocationModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<FallbackAllocationModule>());

        services.AddSingleton<BreakerModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<BreakerModule>());

        services.AddSingleton<AnnouncementModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<AnnouncementModule>());

        services.AddSingleton<DefuseModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<DefuseModule>());

        services.AddSingleton<ZonesModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<ZonesModule>());

        // C2: bomb auto-plant (must be after RoundFlowModule which sets PlanterSteamId)
        services.AddSingleton<BombModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<BombModule>());

        // D1: RetakesDatabase — created lazily via static factory (reads DB connection string from config)
        services.AddSingleton<RetakesDatabase>(sp =>
        {
            var cfg    = sp.GetRequiredService<ConfigModule>().Config;
            var logger = sp.GetRequiredService<ILogger<RetakesDatabase>>();
            return RetakesDatabase.Create(cfg.Database.ConnectionString, logger);
        });

        // D1: real allocator — subscribes to OnAllocate in OAM
        services.AddSingleton<AllocatorModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<AllocatorModule>());

        // D2: weapon-pref commands + gun menu + !nextround vote
        services.AddSingleton<AllocatorCommandsModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<AllocatorCommandsModule>());

        // D3: mid-round buy control (PlayerCanAcquire hook) + preference save + stray cleanup
        services.AddSingleton<BuyControlModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<BuyControlModule>());

        // E: in-game spawn editor + map-config + admin commands
        services.AddSingleton<SpawnEditorModule>();
        services.AddSingleton<IModule>(sp => sp.GetRequiredService<SpawnEditorModule>());

        return services;
    }
}
