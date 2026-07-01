using System;
using Microsoft.Extensions.Logging;
using Retakes.Allocator;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.Queue;
using Retakes.Shared;
using Retakes.Spawn;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace Retakes.RoundFlow;

/// <summary>
/// Orchestrates the retakes round loop.
///
/// Event mapping vs CSS originals:
///   round_prestart  → QueueManager admission + GameManager balance/scramble
///   round_poststart → select bombsite, SpawnManager.HandleRoundSpawns, fire OnAnnounceBombsite + OnAllocate
///   round_end       → record winner for next-round balance
///   bomb_planted    → (Phase C: auto-plant already fired; B2: log + announce TODO)
///   bomb_defused    → record defuse into GameManager
///   player_death    → record assist into GameManager
/// </summary>
internal sealed class RoundFlowModule : IModule, IEventListener
{
    private readonly ILogger<RoundFlowModule> _logger;
    private readonly InterfaceBridge          _bridge;
    private readonly ConfigModule             _config;
    private readonly QueueModule              _queueModule;
    private readonly SpawnModule              _spawnModule;
    private readonly EventBus                 _bus;
    private readonly RoundTypeManager         _roundTypeManager;

    // ── round state ────────────────────────────────────────────────────────
    private Bombsite    _currentBombsite = Bombsite.A;
    private CStrikeTeam _lastRoundWinner = CStrikeTeam.CT; // default: CT won, so Ts get bumped next
    private Bombsite?   _forcedBombsite;

    /// <summary>SteamID64 of the T player designated as bomb planter this round (null = none found).</summary>
    public ulong? PlanterSteamId { get; private set; }

    /// <summary>Currently selected bombsite — read by AnnouncementModule / ZonesModule (Phase C/D).</summary>
    public Bombsite CurrentBombsite => _currentBombsite;

    // ── IEventListener ─────────────────────────────────────────────────────
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public RoundFlowModule(
        ILogger<RoundFlowModule> logger,
        InterfaceBridge          bridge,
        ConfigModule             config,
        QueueModule              queueModule,
        SpawnModule              spawnModule,
        EventBus                 bus,
        RoundTypeManager         roundTypeManager)
    {
        _logger           = logger;
        _bridge           = bridge;
        _config           = config;
        _queueModule      = queueModule;
        _spawnModule      = spawnModule;
        _bus              = bus;
        _roundTypeManager = roundTypeManager;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init()
    {
        // Initialise round-type sequencing once config is loaded (ConfigModule.Init ran first).
        _roundTypeManager.Initialize();
        return true;
    }

    public void OnPostInit()
        => _bridge.EventManager.InstallEventListener(this);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.EventManager.RemoveEventListener(this);

    // ── admin API (wired by CommandsModule in Phase E) ─────────────────────

    /// <summary>Force the next round to use a specific bombsite. Pass null to clear.</summary>
    public void SetForcedBombsite(Bombsite? site)
    {
        _forcedBombsite = site;
        _logger.LogInformation("[Retakes] Forced bombsite set to {Site}", site?.ToString() ?? "none");
    }

    // ── IEventListener impl ────────────────────────────────────────────────

    // HookFireEvent has a default impl (returns true) — we never need to block in B2.

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        switch (@event)
        {
            case IEventRoundEnd roundEnd:
                OnRoundEnd(roundEnd);
                break;

            case IEventBombPlanted bombPlanted:
                OnBombPlanted(bombPlanted);
                break;

            case IEventBombDefused bombDefused:
                OnBombDefused(bombDefused);
                break;

            case IEventPlayerDeath playerDeath:
                OnPlayerDeath(playerDeath);
                break;

            default:
                if (@event.Name.Equals("round_prestart", StringComparison.Ordinal))
                    OnRoundPreStart();
                else if (@event.Name.Equals("round_poststart", StringComparison.Ordinal))
                    OnRoundPostStart();
                break;
        }
    }

    // ── round_prestart ─────────────────────────────────────────────────────
    // Admitted BEFORE players respawn; safe to switch teams + update queues.

    private void OnRoundPreStart()
    {
        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod)
        {
            _logger.LogDebug("[Retakes] round_prestart skipped (warmup or no rules).");
            return;
        }

        _queueModule.QueueManager.ClearRoundTeams();
        _queueModule.QueueManager.Update();
        _queueModule.GameManager.OnRoundPreStart(_lastRoundWinner);
        _queueModule.QueueManager.SetRoundTeams();

        _logger.LogInformation("[Retakes] round_prestart: queue/balance complete. Active={Count}", _queueModule.QueueManager.ActivePlayers.Count);
    }

    // ── round_poststart ────────────────────────────────────────────────────
    // Fired after all round-restart actions — players are alive at default spawns.
    // We teleport to retakes spawns, announce bombsite, then fire OnAllocate.

    private void OnRoundPostStart()
    {
        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod)
        {
            _logger.LogDebug("[Retakes] round_poststart skipped (warmup or no rules).");
            return;
        }

        // 1. Select bombsite
        _currentBombsite = _forcedBombsite ?? (Random.Shared.Next(0, 2) == 0 ? Bombsite.A : Bombsite.B);
        _forcedBombsite  = null; // one-shot forced site; clear after use

        // 2. Reset per-round score
        _queueModule.GameManager.ResetPlayerScores();

        // 3. Teleport active players to retakes spawns; record the planter
        PlanterSteamId = _spawnModule.SpawnManager.HandleRoundSpawns(
            _currentBombsite,
            _queueModule.QueueManager.ActivePlayers);

        // 4. Publish bombsite selection to all subscribers (AnnouncementModule, ZonesModule, etc.)
        _bus.FireAnnounceBombsite(_currentBombsite);

        // 5. Ask RoundTypeManager for the economy tier; publish to bus + AllocatorModule.
        var roundType = _roundTypeManager.GetNextRoundType();
        _roundTypeManager.SetCurrentRoundType(roundType);
        _bus.FireAllocate(roundType);

        _logger.LogInformation("[Retakes] round_poststart: bombsite={Site}, planter={Planter}", _currentBombsite, PlanterSteamId);
    }

    // ── round_end ──────────────────────────────────────────────────────────

    private void OnRoundEnd(IEventRoundEnd evt)
    {
        _lastRoundWinner = evt.Winner;
        _logger.LogInformation("[Retakes] round_end: winner={Winner}", _lastRoundWinner);
    }

    // ── bomb_planted ───────────────────────────────────────────────────────
    // Phase C: BombModule auto-plants the bomb on freeze-end (if IsAutoPlantEnabled).
    // Phase C: AnnouncementModule fires a delayed bombsite re-announce here.
    // B2: placeholder only.

    private void OnBombPlanted(IEventBombPlanted evt)
    {
        _logger.LogInformation("[Retakes] Bomb planted at {Site}.", _currentBombsite);
        // TODO Phase C: trigger post-plant announcement + DefuseModule give-defuser pass
    }

    // ── bomb_defused ───────────────────────────────────────────────────────

    private void OnBombDefused(IEventBombDefused evt)
    {
        var controller = evt.Controller;
        if (controller is null || !controller.IsValid()) return;

        var steamId = (ulong)controller.SteamId;
        if (steamId.IsValidSteamId())
            _queueModule.GameManager.AddDefuse(steamId);
    }

    // ── player_death (assist scoring) ──────────────────────────────────────
    // Kills are already recorded in PlayerLifecycleModule via PlayerKilledPost forward.
    // That forward has no assister info, so we record assists here from the event.

    private void OnPlayerDeath(IEventPlayerDeath evt)
    {
        var assister = evt.AssisterController;
        if (assister is null || !assister.IsValid()) return;

        // skip bots
        if (assister.IsFakeClient) return;

        var steamId = (ulong)assister.SteamId;
        if (steamId.IsValidSteamId())
            _queueModule.GameManager.AddAssist(steamId);
    }

    // ── round termination helper (used by Phase C auto-plant fallback) ─────

    /// <summary>
    /// End the current round immediately.
    /// If IGameRules.TerminateRound is unavailable, slays all active alive players as fallback.
    /// </summary>
    internal void TerminateRound(RoundEndReason reason)
    {
        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is not null)
        {
            rules.TerminateRound(0.1f, reason);
            return;
        }

        // Fallback: slay everyone
        _logger.LogWarning("[Retakes] GetGameRules returned null — slaying all as TerminateRound fallback.");
        foreach (var steamId in _queueModule.QueueManager.ActivePlayers)
        {
            var client = _bridge.ClientManager.GetGameClient((SteamID)steamId);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;
            var pawn = controller.GetPlayerPawn();
            pawn?.Slay();
        }
    }
}

file static class SteamIdGuard
{
    internal static bool IsValidSteamId(this ulong id) => id > 76561197960265728UL;
}
