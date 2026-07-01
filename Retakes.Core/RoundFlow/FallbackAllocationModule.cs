using System;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.Queue;
using Sharp.Shared.Enums;
using Sharp.Shared.Units;

namespace Retakes.RoundFlow;

/// <summary>
/// Fallback weapon allocator — active when Phase-D AllocatorModule is absent.
///
/// Subscribes to <see cref="EventBus.OnAllocate"/> and gives every active, alive player a
/// basic loadout: armor+helmet, a defuser kit for CT, an appropriate primary rifle,
/// a Desert Eagle, a knife, and one random grenade.
///
/// NOTE: Phase D AllocatorModule must set <c>config.Game.EnableFallbackAllocation = false</c>
/// (or override it via config) to suppress this module and avoid double-giving weapons.
/// </summary>
internal sealed class FallbackAllocationModule : IModule
{
    private readonly ILogger<FallbackAllocationModule> _logger;
    private readonly InterfaceBridge                   _bridge;
    private readonly ConfigModule                      _config;
    private readonly QueueModule                       _queueModule;
    private readonly EventBus                          _bus;

    // Stored so the same delegate reference can be removed in Shutdown.
    private readonly Action _onAllocate;

    public FallbackAllocationModule(
        ILogger<FallbackAllocationModule> logger,
        InterfaceBridge                   bridge,
        ConfigModule                      config,
        QueueModule                       queueModule,
        EventBus                          bus)
    {
        _logger      = logger;
        _bridge      = bridge;
        _config      = config;
        _queueModule = queueModule;
        _bus         = bus;
        _onAllocate  = HandleAllocate;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
        => _bus.OnAllocate += _onAllocate;

    public void Shutdown()
        => _bus.OnAllocate -= _onAllocate;

    // ── allocation handler ─────────────────────────────────────────────────

    private void HandleAllocate()
    {
        // Skip if the real AllocatorModule (Phase D) is active.
        if (_config.Config.Allocator.Enabled)
        {
            _logger.LogDebug("[Retakes] FallbackAllocation skipped (AllocatorModule is active).");
            return;
        }

        if (!_config.Config.Game.EnableFallbackAllocation)
        {
            _logger.LogDebug("[Retakes] FallbackAllocation skipped (disabled in config).");
            return;
        }

        var count = 0;
        foreach (var slot in _queueModule.QueueManager.ActiveSlots)
        {
            // Re-resolve every player fresh — never store pawn pointers across rounds.
            var client = _bridge.ClientManager.GetGameClient(slot);
            if (client is not { IsInGame: true }) continue;

            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;

            var pawn = controller.GetPlayerPawn();
            if (pawn is null || !pawn.IsAlive) continue;

            GiveLoadout(pawn, controller.Team);
            count++;
        }

        _logger.LogInformation("[Retakes] FallbackAllocation: gave loadout to {Count} player(s).", count);
    }

    /// <summary>
    /// Strip all weapons then give armor + rifle + deagle + knife + 1 random grenade.
    /// Order: strip → armor → defuser → primary → sidearm → knife → grenade.
    /// </summary>
    private static void GiveLoadout(Sharp.Shared.GameEntities.IPlayerPawn pawn, CStrikeTeam team)
    {
        // 1. Strip everything (removeSuit=true so we can set ArmorValue ourselves)
        pawn.RemoveAllItems(removeSuit: true);

        // 2. Armor + helmet via direct schema props (pawn.ArmorValue + IItemService.HasHelmet)
        pawn.ArmorValue = 100;
        var svc = pawn.GetItemService();
        if (svc is not null)
        {
            svc.HasHelmet = true;

            // 3. Defuser kit for CT (schema bool — no item_defuser entity needed)
            if (team == CStrikeTeam.CT)
                svc.HasDefuser = true;
        }

        // 4. Primary rifle (T: AK-47, CT: M4A1-S)
        pawn.GiveNamedItem(team == CStrikeTeam.TE ? "weapon_ak47" : "weapon_m4a1_silencer");

        // 5. Sidearm + knife
        pawn.GiveNamedItem("weapon_deagle");
        pawn.GiveNamedItem("weapon_knife");

        // 6. One random grenade
        switch (Random.Shared.Next(4))
        {
            case 0: pawn.GiveNamedItem("weapon_smokegrenade"); break;
            case 1: pawn.GiveNamedItem("weapon_flashbang");    break;
            case 2: pawn.GiveNamedItem("weapon_hegrenade");    break;
            case 3:
                // Molotov for T; Incendiary grenade for CT
                pawn.GiveNamedItem(team == CStrikeTeam.TE ? "weapon_molotov" : "weapon_incgrenade");
                break;
        }
    }
}
