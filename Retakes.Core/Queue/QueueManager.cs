using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Units;

namespace Retakes.Queue;

internal sealed class QueueManager
{
    private readonly ILogger<QueueManager> _logger;
    private readonly InterfaceBridge       _bridge;
    private readonly ConfigModule          _config;

    public HashSet<ulong> ActivePlayers          { get; } = [];
    public HashSet<ulong> QueuePlayers           { get; } = [];
    private HashSet<ulong> _roundTerrorists      = [];
    private HashSet<ulong> _roundCounterTerrorists = [];

    private IAdminManager? _adminManager;

    public QueueManager(ILogger<QueueManager> logger, InterfaceBridge bridge, ConfigModule config)
    {
        _logger = logger;
        _bridge = bridge;
        _config = config;
    }

    public void SetAdminManager(IAdminManager? adminManager)
        => _adminManager = adminManager;

    // ── team sizing ────────────────────────────────────────────────────────

    public int GetTargetNumTerrorists()
    {
        var total = ActivePlayers.Count;
        return Math.Max(1, (int)MathF.Round(total * _config.Config.Teams.TerroristRatio));
    }

    public int GetTargetNumCounterTerrorists()
        => Math.Max(0, ActivePlayers.Count - GetTargetNumTerrorists());

    // ── queue management ───────────────────────────────────────────────────

    public void AddToQueue(ulong steamId64)
    {
        if (!ActivePlayers.Contains(steamId64))
            QueuePlayers.Add(steamId64);
    }

    public void AddToActive(ulong steamId64)
    {
        QueuePlayers.Remove(steamId64);
        ActivePlayers.Add(steamId64);
    }

    public bool IsActive(ulong steamId64) => ActivePlayers.Contains(steamId64);

    public void RemovePlayerFromQueues(ulong steamId64)
    {
        ActivePlayers.Remove(steamId64);
        QueuePlayers.Remove(steamId64);
    }

    /// <summary>
    /// Promote queued players into active slots up to MaxPlayers.
    /// Assigns promoted players to CT as their starting team.
    /// Called between rounds (Phase B2 will wire this).
    /// </summary>
    public void Update()
    {
        var maxActive    = _config.Config.Game.MaxPlayers;
        var priorityFlags = _config.Config.Queue.PriorityFlags;

        // sort by priority descending so VIPs get in first
        var sorted = QueuePlayers
            .Select(id => (steamId: id, priority: GetQueuePriority(id, _adminManager, priorityFlags)))
            .OrderByDescending(x => x.priority)
            .ToList();

        foreach (var (steamId, _) in sorted)
        {
            if (ActivePlayers.Count >= maxActive) break;

            QueuePlayers.Remove(steamId);
            ActivePlayers.Add(steamId);

            // move to CT as default starting team
            var client = _bridge.ClientManager.GetGameClient((SteamID)steamId);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;
            controller.ChangeTeam(CStrikeTeam.CT);
        }
    }

    // ── round team tracking ────────────────────────────────────────────────

    public void SetRoundTeams()
    {
        _roundTerrorists.Clear();
        _roundCounterTerrorists.Clear();

        foreach (var steamId in ActivePlayers)
        {
            var client = _bridge.ClientManager.GetGameClient((SteamID)steamId);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;

            switch (controller.Team)
            {
                case CStrikeTeam.TE: _roundTerrorists.Add(steamId);         break;
                case CStrikeTeam.CT: _roundCounterTerrorists.Add(steamId); break;
            }
        }
    }

    public void ClearRoundTeams()
    {
        _roundTerrorists.Clear();
        _roundCounterTerrorists.Clear();
    }

    public IReadOnlySet<ulong> RoundTerrorists       => _roundTerrorists;
    public IReadOnlySet<ulong> RoundCounterTerrorists => _roundCounterTerrorists;

    // ── team-change handler ────────────────────────────────────────────────

    /// <summary>Returns true if the event should be considered handled (caller may block/suppress).</summary>
    public bool HandlePlayerJoinedTeam(ulong steamId64, CStrikeTeam oldTeam, CStrikeTeam newTeam, bool isMidRound)
    {
        // spectator → any: enqueue if not already tracked
        if (oldTeam == CStrikeTeam.Spectator && newTeam != CStrikeTeam.Spectator)
        {
            if (!ActivePlayers.Contains(steamId64) && !QueuePlayers.Contains(steamId64))
                QueuePlayers.Add(steamId64);
            return false;
        }

        // active → spectator: remove from all queues
        if (newTeam == CStrikeTeam.Spectator && ActivePlayers.Contains(steamId64))
        {
            RemovePlayerFromQueues(steamId64);
            return false;
        }

        // mid-round active player tries to switch teams — force back to spectator
        if (isMidRound && _config.Config.Teams.ShouldPreventTeamChangesMidRound && ActivePlayers.Contains(steamId64))
        {
            var client = _bridge.ClientManager.GetGameClient((SteamID)steamId64);
            if (client is { IsInGame: true })
            {
                var controller = client.GetPlayerController();
                if (controller is not null && controller.IsValid())
                {
                    controller.ChangeTeam(CStrikeTeam.Spectator);
                    RemovePlayerFromQueues(steamId64);
                    QueuePlayers.Add(steamId64);
                    return true;
                }
            }
        }

        return false;
    }

    // ── priority ──────────────────────────────────────────────────────────

    private static int GetQueuePriority(ulong steamId64, IAdminManager? adminMgr, List<QueuePriorityFlagConfig> flags)
    {
        if (adminMgr is null || flags.Count == 0)
            return int.MinValue;

        var best = int.MinValue;
        foreach (var flag in flags)
        {
            if (adminMgr.GetAdmin((SteamID)steamId64)?.HasPermission(flag.Flag) == true)
                best = Math.Max(best, Math.Clamp(flag.Priority, 0, 100));
        }
        return best;
    }
}
