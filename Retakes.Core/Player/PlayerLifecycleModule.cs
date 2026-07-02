using System;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.Queue;
using Retakes.Utils;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.Player;

internal sealed class PlayerLifecycleModule : IModule, IClientListener, IEventListener
{
    private readonly ILogger<PlayerLifecycleModule> _logger;
    private readonly InterfaceBridge                _bridge;
    private readonly ConfigModule                   _config;
    private readonly QueueModule                    _queueModule;

    private QueueManager QueueManager => _queueModule.QueueManager;
    private GameManager  GameManager  => _queueModule.GameManager;

    // ── forward / hook callbacks (cached as fields so they can be unregistered) ──
    private readonly Action<IPlayerSpawnForwardParams>  _onSpawnPost;
    private readonly Action<IPlayerKilledForwardParams> _onKilledPost;

    // HandleCommandJoinTeam pre-hook — typed delegate field required by HookManager.
    private Func<IHandleCommandJoinTeamHookParams, HookReturnValue<bool>, HookReturnValue<bool>>? _joinTeamHook;

    // ── IEventListener ─────────────────────────────────────────────────────
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    // ── IClientListener ────────────────────────────────────────────────────
    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    public PlayerLifecycleModule(
        ILogger<PlayerLifecycleModule> logger,
        InterfaceBridge                bridge,
        ConfigModule                   config,
        QueueModule                    queueModule)
    {
        _logger      = logger;
        _bridge      = bridge;
        _config      = config;
        _queueModule = queueModule;

        _onSpawnPost  = OnSpawnPost;
        _onKilledPost = OnKilledPost;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
    {
        _bridge.ClientManager.InstallClientListener(this);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(_onSpawnPost);
        _bridge.HookManager.PlayerKilledPost.InstallForward(_onKilledPost);

        // HandleCommandJoinTeam pre-hook: gate team joins BEFORE the engine processes them.
        _joinTeamHook = OnHandleCommandJoinTeam;
        _bridge.HookManager.HandleCommandJoinTeam.InstallHookPre(_joinTeamHook);

        // player_team event: kept only to suppress team-change UI noise (Silent = true).
        _bridge.EventManager.HookEvent("player_team");
        _bridge.EventManager.InstallEventListener(this);
    }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
    {
        _bridge.EventManager.RemoveEventListener(this);

        if (_joinTeamHook is not null)
            _bridge.HookManager.HandleCommandJoinTeam.RemoveHookPre(_joinTeamHook);

        _bridge.HookManager.PlayerKilledPost.RemoveForward(_onKilledPost);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(_onSpawnPost);
        _bridge.ClientManager.RemoveClientListener(this);
    }

    // ── IClientListener impl ───────────────────────────────────────────────

    void IClientListener.OnClientConnected(IGameClient client)
    {
        if (client.IsFakeClient) return;

        _logger.LogInformation("[Retakes] Client connected: {SteamId}", (ulong)client.SteamId);

        if (_config.Config.Queue.ShouldAutoJoinSpectators)
        {
            QueueManager.AddToQueue(client.Slot);
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Queue_Joined");
        }
    }

    void IClientListener.OnClientPutInServer(IGameClient client) { }

    void IClientListener.OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        if (client.IsFakeClient) return;
        QueueManager.ClearSlot(client.Slot);
        GameManager.ClearSlot(client.Slot);
    }

    // ── HandleCommandJoinTeam pre-hook ─────────────────────────────────────
    //
    // Fires BEFORE the engine processes a jointeam command.  We evaluate queue
    // state here instead of reacting to the player_team event so we can block
    // or redirect the request cleanly without a post-hoc ChangeTeam call.

    private HookReturnValue<bool> OnHandleCommandJoinTeam(
        IHandleCommandJoinTeamHookParams p,
        HookReturnValue<bool> prev)
    {
        var client = p.Client;
        if (client is null || !client.IsInGame || client.IsFakeClient)
            return new HookReturnValue<bool>(EHookAction.Ignored);

        var steamId = (ulong)client.SteamId;
        if (!steamId.IsValidSteamId())
            return new HookReturnValue<bool>(EHookAction.Ignored);

        var slot = client.Slot;

        var requestedTeam = (CStrikeTeam)p.Team;

        // Auto-assign (0 / UnAssigned) — let the engine pick a side.
        if (requestedTeam == CStrikeTeam.UnAssigned)
            return new HookReturnValue<bool>(EHookAction.Ignored);

        var currentTeam = client.GetPlayerController()?.Team ?? CStrikeTeam.Spectator;

        // spectator → active team: reject outright if that team (or the whole roster) is already
        // full instead of letting the engine seat them and relying on OnSpawnPost's after-the-fact
        // slay. Matches MixScrims' join-gate — capacity checked BEFORE any state changes.
        if (currentTeam == CStrikeTeam.Spectator && requestedTeam != CStrikeTeam.Spectator)
        {
            if (QueueManager.IsActive(slot))
                return new HookReturnValue<bool>(EHookAction.Ignored); // already seated, no-op

            if (!QueueManager.CanAcceptToTeam(requestedTeam))
            {
                if (!QueueManager.IsQueued(slot))
                    QueueManager.AddToQueue(slot);
                Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Team_Full");
                return new HookReturnValue<bool>(EHookAction.SkipCallReturnOverride, false);
            }

            QueueManager.AddToActive(slot);
            return new HookReturnValue<bool>(EHookAction.Ignored);
        }

        // active → spectator: remove from all queues, then let through.
        if (requestedTeam == CStrikeTeam.Spectator && QueueManager.IsActive(slot))
        {
            QueueManager.RemovePlayerFromQueues(slot);
            return new HookReturnValue<bool>(EHookAction.Ignored);
        }

        // Mid-round active player trying to switch teams — redirect to spectator + re-queue.
        var gameRules  = _bridge.ModSharp.GetGameRules();
        var isMidRound = gameRules is not null && !gameRules.IsWarmupPeriod && !gameRules.IsFreezePeriod;
        if (isMidRound && _config.Config.Teams.ShouldPreventTeamChangesMidRound && QueueManager.IsActive(slot))
        {
            p.OverrideTeam(1); // redirect to spectator (1)
            QueueManager.RemovePlayerFromQueues(slot);
            QueueManager.AddToQueue(slot);
            return new HookReturnValue<bool>(EHookAction.ChangeParamReturnDefault);
        }

        return new HookReturnValue<bool>(EHookAction.Ignored);
    }

    // ── spawn forward ──────────────────────────────────────────────────────

    private void OnSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client     = @params.Client;
        var controller = @params.Controller;
        var pawn       = @params.Pawn;

        if (client.IsFakeClient)
        {
            // bots always counted as active — slot-indexed so each bot counts individually
            // (SteamId is 0 for every bot, which used to collapse them into one HashSet key).
            QueueManager.AddToActive(client.Slot);
            return;
        }

        var steamId = (ulong)client.SteamId;
        if (!steamId.IsValidSteamId()) return;

        // RoundFlowModule.OnRoundPreStart skips QueueManager.Update() entirely during warmup, so
        // nobody who joined during warmup is ever promoted to "active" until the first real round
        // starts. Enforcing the active-roster slay here regardless would kill every warmup joiner
        // on their very first spawn for no reason — skip the check during warmup, same free period
        // the queue system itself treats as a no-op.
        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod) return;

        if (!QueueManager.IsActive(client.Slot))
        {
            // not in queue — slay (CommitSuicide equivalent) and move to spectator.
            // NOT AcceptInput("Kill"): that DESTROYS the pawn entity → the "weird alive spectator"
            // the source guards against. Slay() is the ModSharp CommitSuicide equivalent (matches RoundFlow).
            pawn.Slay();
            controller.ChangeTeam(CStrikeTeam.Spectator);
        }
    }

    // ── killed forward ─────────────────────────────────────────────────────

    private void OnKilledPost(IPlayerKilledForwardParams @params)
    {
        // victim
        var victimClient = @params.Client;
        if (victimClient.IsFakeClient) return; // skip bot kills for scoring

        // attacker — derive from AttackerPlayerSlot
        // IsPawn: true if killed by a player pawn (not world/trigger)
        if (@params.IsPawn && !@params.IsWorld)
        {
            var attackerSlot = @params.AttackerPlayerSlot;
            if (attackerSlot >= 0)
            {
                var attackerClient = _bridge.ClientManager.GetGameClient((PlayerSlot)(byte)attackerSlot);
                if (attackerClient is { IsInGame: true } && !attackerClient.IsFakeClient)
                    GameManager.AddKill(attackerClient.Slot);
            }
        }
        // No assister info available in ITakeDamageInfoParams — tracked in Phase B2 via damage service
    }

    // ── IEventListener impl ────────────────────────────────────────────────
    // Kept only to suppress the team-change chat notification (Silent = true).
    // All queue gating has moved to the HandleCommandJoinTeam pre-hook above.

    bool IEventListener.HookFireEvent(IGameEvent @event, ref bool serverOnly)
    {
        if (@event is IEventPlayerTeam evt)
            evt.Silent = true;
        return true;
    }

    void IEventListener.FireGameEvent(IGameEvent @event) { }
}
