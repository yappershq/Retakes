using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Retakes.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.Spawn;

internal sealed class SpawnManager
{
    private readonly ILogger<SpawnManager> _logger;
    private readonly InterfaceBridge       _bridge;

    // partitioned spawns[site][team]
    private Dictionary<Bombsite, Dictionary<CStrikeTeam, List<Spawn>>> _partitions = [];

    public SpawnManager(ILogger<SpawnManager> logger, InterfaceBridge bridge)
    {
        _logger = logger;
        _bridge = bridge;
    }

    public void Rebuild(MapConfigService mapConfig)
    {
        _partitions = new Dictionary<Bombsite, Dictionary<CStrikeTeam, List<Spawn>>>();

        foreach (Bombsite site in Enum.GetValues<Bombsite>())
        {
            _partitions[site] = new Dictionary<CStrikeTeam, List<Spawn>>
            {
                [CStrikeTeam.TE] = [],
                [CStrikeTeam.CT] = [],
            };
        }

        foreach (var spawn in mapConfig.GetSpawnsClone())
        {
            if (spawn.Team is not (CStrikeTeam.TE or CStrikeTeam.CT)) continue;
            if (!_partitions.TryGetValue(spawn.Bombsite, out var byTeam)) continue;
            byTeam[spawn.Team].Add(spawn);
        }

        _logger.LogInformation("[Retakes] SpawnManager rebuilt.");
    }

    /// <summary>
    /// Teleport all active players to their spawns for this round.
    /// Returns the SteamID64 of the T player assigned as bomb planter, or null.
    /// Phase B2 will call this; it exists here to be wired in.
    /// </summary>
    public ulong? HandleRoundSpawns(Bombsite bombsite, IEnumerable<ulong> activeSteamIds)
    {
        if (!_partitions.TryGetValue(bombsite, out var byTeam)) return null;

        var tSpawns  = new List<Spawn>(byTeam[CStrikeTeam.TE]);
        var ctSpawns = new List<Spawn>(byTeam[CStrikeTeam.CT]);

        var rng = Random.Shared;

        // shuffle both lists
        Shuffle(tSpawns,  rng);
        Shuffle(ctSpawns, rng);

        ulong? planterSteamId = null;
        var tSpawnIdx  = 0;
        var ctSpawnIdx = 0;

        // Pick a dedicated planter spawn. Prefer a CanBePlanter T spawn; if the site has none
        // configured, fall back to ANY T spawn so the bomb still auto-plants (a missing planter
        // means the round can never end). Never silently leave planterSpawn null when T spawns exist.
        var planterSpawns = tSpawns.Where(s => s.CanBePlanter).ToList();
        Spawn? planterSpawn;
        if (planterSpawns.Count > 0)
        {
            planterSpawn = planterSpawns[rng.Next(planterSpawns.Count)];
        }
        else if (tSpawns.Count > 0)
        {
            planterSpawn = tSpawns[rng.Next(tSpawns.Count)];
            _logger.LogWarning(
                "[Retakes] Bombsite {Site} has no CanBePlanter T spawn — falling back to an arbitrary T spawn as planter. Add a planter spawn via the editor (!add T Y).",
                bombsite);
        }
        else
        {
            planterSpawn = null; // no T spawns at all — nothing we can do; log below when a T needs one
        }

        foreach (var steamId in activeSteamIds)
        {
            var client = _bridge.ClientManager.GetGameClient((SteamID)steamId);
            if (client is not { IsInGame: true }) continue;

            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;

            // Planter-ROLE assignment must not depend on the pawn already being alive at
            // round_poststart — this fires right at round transition, before CS2 has necessarily
            // respawned everyone yet, so a live T here could easily have IsAlive still false for
            // one more tick. Assigning the role is cheap and BombModule re-validates aliveness
            // fresh at freeze-end anyway. Only the actual teleport below needs a live pawn.
            if (controller.Team == CStrikeTeam.TE && planterSpawn is not null && planterSteamId is null)
                planterSteamId = steamId;

            var pawn = controller.GetPlayerPawn();
            if (pawn is null || !pawn.IsAlive) continue;

            if (controller.Team == CStrikeTeam.TE)
            {
                Spawn? spawn;
                if (planterSteamId == steamId)
                {
                    spawn = planterSpawn;
                }
                else
                {
                    // Assign the next T spawn, skipping the one already used for the planter.
                    // If we run out (more Ts than spawns), reuse spawns round-robin instead of
                    // leaving overflow players stranded at engine default spawns.
                    spawn = NextSpawn(tSpawns, ref tSpawnIdx, planterSpawn);
                }

                if (spawn is not null)
                    TeleportToSpawn(pawn, spawn);
            }
            else if (controller.Team == CStrikeTeam.CT)
            {
                var spawn = NextSpawn(ctSpawns, ref ctSpawnIdx, skip: null);
                if (spawn is not null)
                    TeleportToSpawn(pawn, spawn);
            }
        }

        if (planterSpawn is null && tSpawns.Count == 0)
            _logger.LogWarning("[Retakes] Bombsite {Site} has NO T spawns configured — no bomb planter this round; round may not end.", bombsite);

        return planterSteamId;
    }

    /// <summary>
    /// Return the next spawn from <paramref name="spawns"/>, advancing <paramref name="idx"/>.
    /// Skips <paramref name="skip"/> (the planter spawn, already assigned). When the list is
    /// exhausted (more players than spawns) it wraps round-robin so overflow players still get a
    /// retakes spawn rather than being left at an engine default spawn.
    /// </summary>
    private static Spawn? NextSpawn(List<Spawn> spawns, ref int idx, Spawn? skip)
    {
        if (spawns.Count == 0) return null;

        for (var attempts = 0; attempts < spawns.Count; attempts++)
        {
            if (idx >= spawns.Count) idx = 0; // wrap: reuse spawns for overflow players
            var spawn = spawns[idx++];
            if (spawn != skip) return spawn;
        }

        // Only reachable when Count == 1: the loop above exhausts every element for Count > 1.
        // If that single spawn is the planter (skip), there's nothing else to hand out → null.
        return spawns.Count == 1 && spawns[0] == skip ? null : spawns[0];
    }

    private static void TeleportToSpawn(Sharp.Shared.GameEntities.IPlayerPawn pawn, Spawn spawn)
        => pawn.Teleport(spawn.Position, spawn.Angles, new Vector());

    private static void Shuffle<T>(List<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
