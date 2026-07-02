using System;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.RoundFlow;
using Retakes.Shared;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace Retakes.Bomb;

/// <summary>
/// Handles bomb auto-plant at freeze-end for the retakes round loop.
///
/// Timing: hooks <c>cs_round_start_beep</c> (fires when freeze period ends, equivalent to
/// CSS <c>round_freeze_end</c>). No typed event wrapper exists, so we check by name.
///
/// Auto-plant path (IsAutoPlantEnabled = true):
///   1. Resolve designated planter from RoundFlowModule.PlanterSteamId.
///   2. Synthetic plant — create planted_c4 via CreateEntityByName (bypasses precache;
///      planted_c4 is not a weapon entity, no crash risk), set schema fields, DispatchSpawn.
///   3. Mark CCSGameRules.m_bBombPlanted + m_bBombDefused via SetNetVar.
///   4. Fire synthetic bomb_planted event — this routes through the native event pipeline
///      so DefuseModule's IEventBombPlanted listener fires automatically. No extra wiring needed.
///
/// Fallback (planter not found OR entity creation fails):
///   Give the planter weapon_c4 so they can plant manually. This is the safe default —
///   it never risks a server crash and preserves normal CS2 defuse mechanics.
///
/// Decision log (Phase C2):
///   - FULL synthetic plant is used — all required APIs are verified in ModSharp:
///       CPlantedC4 schema fields (m_nBombSite, m_bBombTicking, m_bHasExploded, m_bCannotBeDefused),
///       CCSGameRules.m_bBombPlanted, IGameEvent.Fire(serverOnly), IEntityManager.CreateEntityByName.
///   - DefuseModule needs no extra wiring — Fire(false) routes through native CS2 event pipeline.
///   - The ONLY unproven assumption is whether DispatchSpawn on a pre-configured planted_c4
///     entity starts the countdown server-side without additional gamedata calls. If the bomb
///     spawns but the timer doesn't run, Phase D can investigate CPlantedC4 initialization or
///     use a timer to fire TerminateRound(TerroristsWin) as a safety net.
/// </summary>
internal sealed class BombModule : IModule, IEventListener
{
    private readonly ILogger<BombModule> _logger;
    private readonly InterfaceBridge      _bridge;
    private readonly ConfigModule         _config;
    private readonly RoundFlowModule      _roundFlow;

    // cs_round_start_beep fires once per countdown beep (3-2-1), not once per round — guard
    // against planting 2-3 bombs per round. Reset at round_poststart (new round begins).
    private bool _plantedThisRound;

    // ── IEventListener ─────────────────────────────────────────────────────
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 10; // after RoundFlowModule (priority 0)

    public BombModule(
        ILogger<BombModule> logger,
        InterfaceBridge      bridge,
        ConfigModule         config,
        RoundFlowModule      roundFlow)
    {
        _logger    = logger;
        _bridge    = bridge;
        _config    = config;
        _roundFlow = roundFlow;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
    {
        // FireGameEvent only fires for hooked events; cs_round_start_beep = freeze-end timing.
        _bridge.EventManager.HookEvent("cs_round_start_beep");
        _bridge.EventManager.HookEvent("round_poststart");
        _bridge.EventManager.InstallEventListener(this);
    }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.EventManager.RemoveEventListener(this);

    // ── IEventListener impl ────────────────────────────────────────────────

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (@event.Name.Equals("round_poststart", StringComparison.Ordinal))
        {
            _plantedThisRound = false;
            return;
        }

        // cs_round_start_beep fires once per countdown beep (3-2-1), not once per freeze-end —
        // No typed ModSharp interface for this event; check by name.
        if (@event.Name.Equals("cs_round_start_beep", StringComparison.Ordinal))
            OnFreezeEnd();
    }

    // ── freeze-end handler ─────────────────────────────────────────────────

    private void OnFreezeEnd()
    {
        if (_plantedThisRound)
            return; // already handled this round — cs_round_start_beep fires 2-3x per round

        if (!_config.Config.Bomb.IsAutoPlantEnabled)
        {
            _logger.LogDebug("[Retakes][Bomb] Auto-plant disabled in config.");
            return;
        }

        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod)
        {
            _logger.LogDebug("[Retakes][Bomb] freeze-end skipped (warmup or no game rules).");
            return;
        }

        // Resolve planter set during round_poststart
        var planterSteamId = _roundFlow.PlanterSteamId;
        if (planterSteamId is null)
        {
            _logger.LogWarning("[Retakes][Bomb] No planter designated — skipping auto-plant.");
            return;
        }

        // Re-resolve client fresh — never hold pawn pointer across round boundary.
        var client = _bridge.ClientManager.GetGameClient((SteamID)planterSteamId.Value);
        if (client is not { IsInGame: true })
        {
            _logger.LogWarning("[Retakes][Bomb] Planter {Id} not in-game at freeze-end.", planterSteamId.Value);
            return;
        }

        var controller = client.GetPlayerController();
        if (controller is null || !controller.IsValid())
        {
            _logger.LogWarning("[Retakes][Bomb] Planter controller invalid at freeze-end.");
            return;
        }

        var pawn = controller.GetPlayerPawn();
        if (pawn is null || !pawn.IsAlive)
        {
            _logger.LogWarning("[Retakes][Bomb] Planter pawn not alive at freeze-end — no auto-plant.");
            return;
        }

        _plantedThisRound = true;

        if (TrySyntheticPlant(pawn, controller, rules, _roundFlow.CurrentBombsite))
        {
            _logger.LogInformation("[Retakes][Bomb] Synthetic bomb planted at {Site}.", _roundFlow.CurrentBombsite);
        }
        else
        {
            // Fallback: give planter the C4 so they can plant manually.
            // ponytail: deliberate fallback — synthetic plant failed (entity creation returned null).
            // Does not crash server; player plants normally; defuse works natively.
            GiveBombFallback(pawn, planterSteamId.Value);
        }
    }

    // ── synthetic plant ────────────────────────────────────────────────────

    /// <summary>
    /// Attempts a full synthetic plant: creates planted_c4, configures schema fields,
    /// updates game rules, and fires bomb_planted. Returns true on success.
    /// </summary>
    private bool TrySyntheticPlant(
        IPlayerPawn       pawn,
        IPlayerController controller,
        IGameRules        rules,
        Bombsite          bombsite)
    {
        // 1. Get planter position (bomb spawns at planter's feet)
        var pos = pawn.GetAbsOrigin();

        // 2. Create planted_c4 without spawning.
        //    planted_c4 is not a weapon entity, so CreateEntityByName (no precache) is safe.
        //    SpawnEntitySync would also work but has extra precache cost; CSS uses CreateEntityByName.
        var c4 = _bridge.EntityManager.CreateEntityByName<IBaseEntity>("planted_c4");
        if (c4 is null)
        {
            _logger.LogError("[Retakes][Bomb] CreateEntityByName(planted_c4) returned null — falling back to give-C4.");
            return false;
        }

        // 3. Position the bomb at the planter's feet (before DispatchSpawn, mirrors CSS)
        c4.SetAbsOrigin(pos);

        // 4. Configure schema fields (verified via mcp__modsharp__get_schema_class("CPlantedC4"))
        //    All fields confirmed networked (except where noted) and settable via SetNetVar.
        c4.SetNetVar("m_nBombSite",       (int)bombsite); // int32, networked — bombsite index
        c4.SetNetVar("m_bBombTicking",    true);          // bool, networked — starts timer countdown
        c4.SetNetVar("m_bHasExploded",    false);         // bool, networked — not yet exploded
        c4.SetNetVar("m_bCannotBeDefused",false);         // bool, networked — CTs can defuse

        // 5. Spawn — equivalent to CSS DispatchSpawn() call; DO NOT call SpawnEntitySync after this.
        c4.DispatchSpawn();

        // 6. Mark CCSGameRules so the engine knows a bomb is planted.
        //    m_bBombPlanted (offset 2399, networked) — verified via mcp__modsharp__get_schema_field.
        //    m_bBombDefused (offset 3841, NOT networked) — reset each plant.
        rules.SetNetVar("m_bBombPlanted", true);
        rules.SetNetVar("m_bBombDefused", false);

        // 7. Fire synthetic bomb_planted event.
        //    Fire(false) = broadcast to clients, routes through native event pipeline.
        //    DefuseModule's IEventBombPlanted listener receives it automatically — no extra wiring.
        FireBombPlantedEvent(controller, bombsite);

        // Synthetic plant skips the native weapon_c4 plant sequence entirely, so the plant
        // confirmation sound never fires on its own — emit it explicitly. Name confirmed from
        // CS:GO source (weapon_c4.cpp PrecacheScriptSound("c4.plant")); CS2 kept the legacy name.
        foreach (var client in _bridge.ClientManager.GetGameClients(true))
            client.GetPlayerController()?.EmitSoundClient("c4.plant");

        return true;
    }

    // ── event firing ───────────────────────────────────────────────────────

    private void FireBombPlantedEvent(IPlayerController controller, Bombsite bombsite)
    {
        var @event = _bridge.EventManager.CreateEvent("bomb_planted", true);
        if (@event is null)
        {
            // ponytail: if event creation fails, DefuseModule won't give defusers this round
            // and the client HUD bomb indicator won't appear.
            // A future phase can call DefuseModule.GiveDefusers() directly as fallback.
            _logger.LogWarning("[Retakes][Bomb] CreateEvent(bomb_planted) returned null. " +
                               "DefuseModule will not receive planted signal this round.");
            return;
        }

        // bomb_planted fields: "userid" (player_controller_and_pawn) + "site" (short)
        @event.SetPlayer("userid", controller);
        @event.SetInt("site", (int)bombsite);

        // Fire(false) = not serverOnly → broadcasts to clients (CS2 HUD shows bomb timer).
        // IGameEvent.Fire() auto-disposes the event object — do NOT call Dispose() after.
        @event.Fire(false);
    }

    // ── give-bomb fallback ─────────────────────────────────────────────────

    /// <summary>
    /// Safety fallback: give the planter a C4 weapon so they can plant manually.
    /// Used when the synthetic plant entity could not be created.
    /// </summary>
    private void GiveBombFallback(IPlayerPawn pawn, ulong planterSteamId)
    {
        pawn.GiveNamedItem("weapon_c4");
        _logger.LogInformation("[Retakes][Bomb] Fallback: gave weapon_c4 to planter {Id} for manual plant.", planterSteamId);
    }
}
