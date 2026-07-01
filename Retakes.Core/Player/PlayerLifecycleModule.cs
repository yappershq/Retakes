using System;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Database;
using Retakes.Plugins;
using Retakes.Queue;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace Retakes.Player;

internal sealed class PlayerLifecycleModule : IModule, IClientListener, IEventListener
{
    private readonly ILogger<PlayerLifecycleModule> _logger;
    private readonly InterfaceBridge                _bridge;
    private readonly ConfigModule                   _config;
    private readonly QueueModule                    _queueModule;
    private readonly RetakesDatabase                _db;

    private QueueManager QueueManager => _queueModule.QueueManager;
    private GameManager  GameManager  => _queueModule.GameManager;

    // forward callbacks — stored as fields so we can unregister them
    private readonly Action<IPlayerSpawnForwardParams>  _onSpawnPost;
    private readonly Action<IPlayerKilledForwardParams> _onKilledPost;

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
        QueueModule                    queueModule,
        RetakesDatabase                db)
    {
        _logger      = logger;
        _bridge      = bridge;
        _config      = config;
        _queueModule = queueModule;
        _db          = db;

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
        _bridge.EventManager.HookEvent("player_team");
        _bridge.EventManager.InstallEventListener(this);
    }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
    {
        _bridge.EventManager.RemoveEventListener(this);
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
            QueueManager.AddToQueue((ulong)client.SteamId);
    }

    void IClientListener.OnClientPutInServer(IGameClient client)
    {
        if (client.IsFakeClient) return;
        // Prefetch prefs async so game-thread allocations always read from cache.
        _db.PrefetchUserAsync((ulong)client.SteamId);
    }

    void IClientListener.OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        if (client.IsFakeClient) return;
        _db.EvictUser((ulong)client.SteamId);
        QueueManager.RemovePlayerFromQueues((ulong)client.SteamId);
    }

    // ── spawn forward ──────────────────────────────────────────────────────

    private void OnSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client     = @params.Client;
        var controller = @params.Controller;
        var pawn       = @params.Pawn;

        if (client.IsFakeClient)
        {
            // bots always counted as active
            QueueManager.AddToActive((ulong)controller.SteamId);
            return;
        }

        var steamId = (ulong)client.SteamId;
        if (!steamId.IsValidSteamId()) return;

        if (!QueueManager.IsActive(steamId))
        {
            // not in queue — kill and move to spectator
            pawn.AcceptInput("Kill");
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
                {
                    var killerSteamId = (ulong)attackerClient.SteamId;
                    if (killerSteamId.IsValidSteamId())
                        GameManager.AddKill(killerSteamId);
                }
            }
        }
        // No assister info available in ITakeDamageInfoParams — tracked in Phase B2 via damage service
    }

    // ── IEventListener impl ────────────────────────────────────────────────

    bool IEventListener.HookFireEvent(IGameEvent @event, ref bool serverOnly)
    {
        if (@event is not IEventPlayerTeam evt) return true;

        // always suppress team-change UI noise
        evt.Silent = true;

        // let disconnect events through unmodified
        if (evt.Disconnect) return true;

        var controller = evt.Controller;
        if (controller is null || !controller.IsValid()) return true;

        // bots — don't queue-manage them here
        if (evt.Bot) return true;

        var client = controller.GetGameClient();
        if (client is null) return true;

        var steamId    = (ulong)client.SteamId;
        if (!steamId.IsValidSteamId()) return true;

        var gameRules  = _bridge.ModSharp.GetGameRules();
        var isMidRound = gameRules is not null && !gameRules.IsWarmupPeriod && !gameRules.IsFreezePeriod;

        QueueManager.HandlePlayerJoinedTeam(steamId, evt.OldTeam, evt.NewTeam, isMidRound);

        return true; // never block the event itself
    }

    void IEventListener.FireGameEvent(IGameEvent @event) { }
}
