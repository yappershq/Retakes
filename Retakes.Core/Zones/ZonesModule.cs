using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.Zones;

/// <summary>
/// Loads per-map zone definitions and enforces player bounds each tick via PlayerPostThink.
/// Green zones restrict players to a planted-side area; red zones push players back.
/// </summary>
internal sealed class ZonesModule : IModule, IClientListener, IGameListener, IEventListener
{
    private readonly ILogger<ZonesModule> _logger;
    private readonly InterfaceBridge      _bridge;
    private readonly ConfigModule         _config;
    private readonly EventBus             _bus;

    private readonly Dictionary<Bombsite, List<ZoneData>> _zones = new()
    {
        [Bombsite.A] = new List<ZoneData>(),
        [Bombsite.B] = new List<ZoneData>(),
    };

    // Slot-indexed. PlayerPostThink is per-tick — avoid allocations here.
    private static readonly byte MaxSlots = PlayerSlot.MaxPlayerCount.AsPrimitive();
    private readonly PlayerZoneState?[] _playerStates = new PlayerZoneState?[MaxSlots];

    // Stored delegates for deterministic unregister.
    private readonly Action<Bombsite>                  _onBombsiteAnnounced;
    private readonly Action<IPlayerThinkForwardParams> _onPlayerThink;

    // ── IGameListener ──────────────────────────────────────────────────────
    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    // ── IClientListener ────────────────────────────────────────────────────
    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    // ── IEventListener ─────────────────────────────────────────────────────
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public ZonesModule(
        ILogger<ZonesModule> logger,
        InterfaceBridge      bridge,
        ConfigModule         config,
        EventBus             bus)
    {
        _logger = logger;
        _bridge = bridge;
        _config = config;
        _bus    = bus;

        _onBombsiteAnnounced = OnBombsiteAnnounced;
        _onPlayerThink       = OnPlayerThink;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
    {
        _bridge.ModSharp.InstallGameListener(this);
        _bridge.ClientManager.InstallClientListener(this);
        _bridge.HookManager.PlayerPostThink.InstallForward(_onPlayerThink);

        // Late/respawned players miss the one-shot OnBombsiteAnnounced pass (they had no alive
        // pawn at announce time) → they'd get no zone assignment for the round. Assigning on
        // player_spawned covers them. FireGameEvent only fires for explicitly-hooked events.
        _bridge.EventManager.HookEvent("player_spawned");
        _bridge.EventManager.InstallEventListener(this);
    }

    public void OnAllSharpModulesLoaded()
    {
        _bus.OnAnnounceBombsite += _onBombsiteAnnounced;

        // Don't call LoadZones() here — at OAM (cold boot) the engine's server instance isn't
        // valid yet and GetMapName() (called inside it) crashes the process. OnServerActivate
        // (below) fires once a map is actually active and covers the initial map too.
    }

    public void Shutdown()
    {
        _bus.OnAnnounceBombsite -= _onBombsiteAnnounced;
        _bridge.HookManager.PlayerPostThink.RemoveForward(_onPlayerThink);
        _bridge.EventManager.RemoveEventListener(this);
        _bridge.ClientManager.RemoveClientListener(this);
        _bridge.ModSharp.RemoveGameListener(this);
    }

    // ── IGameListener impl ─────────────────────────────────────────────────

    void IGameListener.OnServerActivate() => LoadZones();

    // ── zone loading ───────────────────────────────────────────────────────

    private void LoadZones()
    {
        _zones[Bombsite.A].Clear();
        _zones[Bombsite.B].Clear();

        var mapName = _bridge.ModSharp.GetMapName();
        if (mapName is null)
        {
            _logger.LogDebug("[Retakes] ZonesModule: map name not available yet, skipping zone load.");
            return;
        }

        var path = Path.Combine(_bridge.DataPath, "zones", $"{mapName}.json");
        if (!File.Exists(path))
        {
            _logger.LogInformation("[Retakes] ZonesModule: no zone file for map '{Map}' (expected: {Path})", mapName, path);
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            var json = JsonSerializer.Deserialize<JsonBombsiteZones>(text);
            if (json is null) return;

            foreach (var z in json.a) _zones[Bombsite.A].Add(BuildZone(z));
            foreach (var z in json.b) _zones[Bombsite.B].Add(BuildZone(z));

            _logger.LogInformation("[Retakes] ZonesModule: loaded {A} A-site and {B} B-site zones for '{Map}'",
                _zones[Bombsite.A].Count, _zones[Bombsite.B].Count, mapName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retakes] ZonesModule: failed to load zones for '{Map}'", mapName);
        }
    }

    private static ZoneData BuildZone(JsonZone z)
        => new()
        {
            Type  = (ZoneType)z.type,
            Teams = z.teams,
            MinX  = Math.Min(z.x[0], z.y[0]),
            MinY  = Math.Min(z.x[1], z.y[1]),
            MinZ  = Math.Min(z.x[2], z.y[2]),
            MaxX  = Math.Max(z.x[0], z.y[0]),
            MaxY  = Math.Max(z.x[1], z.y[1]),
            MaxZ  = Math.Max(z.x[2], z.y[2]),
        };

    // ── bombsite announcement handler ─────────────────────────────────────

    private void OnBombsiteAnnounced(Bombsite site)
    {
        foreach (var controller in _bridge.EntityManager.FindPlayerControllers(true))
        {
            if (controller is null || !controller.IsValid()) continue;
            if (controller.IsFakeClient)                     continue;

            var pawn = controller.GetPlayerPawn();
            if (pawn is null || !pawn.IsAlive)               continue;

            var client = controller.GetGameClient();
            if (client is not { IsInGame: true })            continue;

            AssignZones(site, client.Slot, (int)controller.Team);
        }
    }

    /// <summary>
    /// (Re)assign the round's zones to a single player, resolved by slot. Called once per player
    /// from <see cref="OnBombsiteAnnounced"/> (alive-at-announce) and again from player_spawned so
    /// dead/late/respawned players — who had no alive pawn when the site was announced — are still
    /// covered for the rest of the round.
    /// </summary>
    private void AssignZones(Bombsite site, PlayerSlot slot, int team)
    {
        if (!slot.IsValid()) return;

        var idx   = slot.AsPrimitive();
        var state = _playerStates[idx];
        if (state is null)
        {
            state = new PlayerZoneState();
            _playerStates[idx] = state;
        }

        state.Zones      = _zones[site].Where(z => z.Teams.Contains(team)).ToList();
        state.GreenZones = [];
    }

    // ── PlayerPostThink forward ───────────────────────────────────────────

    private void OnPlayerThink(IPlayerThinkForwardParams p)
    {
        var pawn = p.Pawn;
        if (!pawn.IsValid()) return;

        var client = p.Client;
        if (client.IsFakeClient) return;

        var state = _playerStates[client.Slot.AsPrimitive()];
        if (state is null)              return;
        if (state.Zones.Count == 0)     return;

        var pos     = pawn.GetAbsOrigin();
        var bounced = false;

        foreach (var zone in state.Zones)
        {
            var inZone = zone.IsInZone(pos.X, pos.Y, pos.Z);

            if (zone.Type == ZoneType.Red && inZone)
            {
                bounced = true;
                break;
            }

            if (zone.Type == ZoneType.Green)
            {
                if (inZone && !state.GreenZones.Contains(zone))
                {
                    state.GreenZones.Add(zone);
                }
                else if (!inZone && state.GreenZones.Remove(zone) && state.GreenZones.Count == 0)
                {
                    bounced = true;
                    break;
                }
            }
        }

        if (bounced)
            DoBounce(pawn);
    }

    private static void DoBounce(Sharp.Shared.GameEntities.IPlayerPawn pawn)
    {
        var vel   = pawn.GetAbsVelocity();
        var speed = Math.Sqrt((double)vel.X * vel.X + (double)vel.Y * vel.Y);
        if (speed < 1.0) return;

        var scale  = (float)(-350.0 / speed);
        var newVel = new Vector(
            vel.X * scale,
            vel.Y * scale,
            vel.Z <= 0f ? 150f : Math.Min(vel.Z, 150f));

        pawn.Teleport(null, null, newVel);
    }

    // ── IEventListener impl ────────────────────────────────────────────────

    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (!@event.GetName().Equals("player_spawned", StringComparison.Ordinal)) return;

        var controller = @event.GetPlayerController("userid");
        if (controller is null || !controller.IsValid() || controller.IsFakeClient) return;

        var pawn = controller.GetPlayerPawn();
        if (pawn is null || !pawn.IsAlive) return;

        var client = controller.GetGameClient();
        if (client is not { IsInGame: true }) return;

        // Assign the round's zones for the site already announced this round. EventBus.CurrentBombsite
        // holds the last-announced site, so a spectator-joiner or mid-round respawn gets the live site.
        AssignZones(_bus.CurrentBombsite, client.Slot, (int)controller.Team);
    }

    // ── IClientListener impl ──────────────────────────────────────────────

    void IClientListener.OnClientDisconnected(IGameClient client, Sharp.Shared.Enums.NetworkDisconnectionReason reason)
    {
        if (client.IsFakeClient) return;
        _playerStates[client.Slot.AsPrimitive()] = null;
    }

    void IClientListener.OnClientConnected(IGameClient client)   { }
    void IClientListener.OnClientPutInServer(IGameClient client) { }
}
