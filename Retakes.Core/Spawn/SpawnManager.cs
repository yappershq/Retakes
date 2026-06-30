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

        // pick a dedicated planter spawn if available
        var planterSpawns = tSpawns.Where(s => s.CanBePlanter).ToList();
        Spawn? planterSpawn = planterSpawns.Count > 0 ? planterSpawns[rng.Next(planterSpawns.Count)] : null;

        foreach (var steamId in activeSteamIds)
        {
            var client = _bridge.ClientManager.GetGameClient((SteamID)steamId);
            if (client is not { IsInGame: true }) continue;

            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;

            var pawn = controller.GetPlayerPawn();
            if (pawn is null || !pawn.IsAlive) continue;

            if (controller.Team == CStrikeTeam.TE)
            {
                Spawn? spawn;
                if (planterSpawn is not null && planterSteamId is null)
                {
                    spawn         = planterSpawn;
                    planterSteamId = steamId;
                }
                else if (tSpawnIdx < tSpawns.Count)
                {
                    // skip the planterSpawn if we already used it
                    spawn = tSpawns[tSpawnIdx++];
                    if (spawn == planterSpawn) spawn = tSpawnIdx < tSpawns.Count ? tSpawns[tSpawnIdx++] : null;
                }
                else spawn = null;

                if (spawn is not null)
                    TeleportToSpawn(pawn, spawn);
            }
            else if (controller.Team == CStrikeTeam.CT)
            {
                if (ctSpawnIdx < ctSpawns.Count)
                    TeleportToSpawn(pawn, ctSpawns[ctSpawnIdx++]);
            }
        }

        return planterSteamId;
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
