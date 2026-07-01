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
    private static readonly byte MaxSlots = PlayerSlot.MaxPlayerCount.AsPrimitive();

    private readonly ILogger<QueueManager> _logger;
    private readonly InterfaceBridge       _bridge;
    private readonly ConfigModule          _config;

    private readonly bool[] _activePlayers          = new bool[MaxSlots];
    private readonly bool[] _queuePlayers            = new bool[MaxSlots];
    private readonly bool[] _roundTerrorists         = new bool[MaxSlots];
    private readonly bool[] _roundCounterTerrorists  = new bool[MaxSlots];

    private IAdminManager? _adminManager;

    public QueueManager(ILogger<QueueManager> logger, InterfaceBridge bridge, ConfigModule config)
    {
        _logger = logger;
        _bridge = bridge;
        _config = config;
    }

    public void SetAdminManager(IAdminManager? adminManager)
        => _adminManager = adminManager;

    // ── slot-array accessors ──────────────────────────────────────────────

    public IEnumerable<PlayerSlot> ActiveSlots
    {
        get
        {
            for (byte i = 0; i < MaxSlots; i++)
                if (_activePlayers[i])
                    yield return new PlayerSlot(i);
        }
    }

    public IEnumerable<PlayerSlot> QueueSlots
    {
        get
        {
            for (byte i = 0; i < MaxSlots; i++)
                if (_queuePlayers[i])
                    yield return new PlayerSlot(i);
        }
    }

    public int ActiveCount
    {
        get
        {
            var count = 0;
            for (byte i = 0; i < MaxSlots; i++)
                if (_activePlayers[i])
                    count++;
            return count;
        }
    }

    public int QueueCount
    {
        get
        {
            var count = 0;
            for (byte i = 0; i < MaxSlots; i++)
                if (_queuePlayers[i])
                    count++;
            return count;
        }
    }

    // ── team sizing ────────────────────────────────────────────────────────

    public int GetTargetNumTerrorists()
    {
        var total = ActiveCount;
        var shouldForceEven = _config.Config.Queue.ShouldForceEvenTeamsWhenPlayerCountIsMultipleOf10
            && total % 10 == 0;
        var ratio = shouldForceEven ? 0.5f : _config.Config.Teams.TerroristRatio;
        return Math.Max(1, (int)MathF.Round(ratio * total));
    }

    public int GetTargetNumCounterTerrorists()
        => Math.Max(0, ActiveCount - GetTargetNumTerrorists());

    // ── queue management ───────────────────────────────────────────────────

    public void AddToQueue(PlayerSlot slot)
    {
        if (!slot.IsValid()) return;
        if (!_activePlayers[slot.AsPrimitive()])
            _queuePlayers[slot.AsPrimitive()] = true;
    }

    public void AddToActive(PlayerSlot slot)
    {
        if (!slot.IsValid()) return;
        _queuePlayers[slot.AsPrimitive()]  = false;
        _activePlayers[slot.AsPrimitive()] = true;
    }

    public bool IsActive(PlayerSlot slot)
        => slot.IsValid() && _activePlayers[slot.AsPrimitive()];

    public bool IsQueued(PlayerSlot slot)
        => slot.IsValid() && _queuePlayers[slot.AsPrimitive()];

    public void RemovePlayerFromQueues(PlayerSlot slot)
    {
        if (!slot.IsValid()) return;
        _activePlayers[slot.AsPrimitive()] = false;
        _queuePlayers[slot.AsPrimitive()]  = false;
    }

    /// <summary>Clears a slot from active/queue/round-team tracking. Used on disconnect and by PruneStale().</summary>
    public void ClearSlot(PlayerSlot slot)
    {
        if (!slot.IsValid()) return;
        var i = slot.AsPrimitive();
        _activePlayers[i]         = false;
        _queuePlayers[i]          = false;
        _roundTerrorists[i]       = false;
        _roundCounterTerrorists[i] = false;
    }

    /// <summary>Drops any tracked slot whose client is no longer connected/in-game.</summary>
    public void PruneStale()
    {
        for (byte i = 0; i < MaxSlots; i++)
        {
            if (!_activePlayers[i] && !_queuePlayers[i]) continue;

            var client = _bridge.ClientManager.GetGameClient(new PlayerSlot(i));
            if (client is not { IsInGame: true })
                ClearSlot(new PlayerSlot(i));
        }
    }

    /// <summary>
    /// Promote queued players into active slots up to MaxPlayers.
    /// Assigns promoted players to CT as their starting team.
    /// Called between rounds (Phase B2 will wire this).
    /// </summary>
    public void Update()
    {
        PruneStale();

        var maxActive     = _config.Config.Game.MaxPlayers;
        var priorityFlags = _config.Config.Queue.PriorityFlags;

        // sort by priority descending so VIPs get in first
        var sorted = QueueSlots
            .Select(slot => (slot, priority: GetQueuePriority(slot, _adminManager, priorityFlags)))
            .OrderByDescending(x => x.priority)
            .ToList();

        foreach (var (slot, _) in sorted)
        {
            if (ActiveCount >= maxActive) break;

            AddToActive(slot);

            // move to CT as default starting team
            var client = _bridge.ClientManager.GetGameClient(slot);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;
            controller.ChangeTeam(CStrikeTeam.CT);
        }

        HandleQueuePriority();
    }

    /// <summary>
    /// When the active roster is full, allow high-priority queued players to bump
    /// lower-priority (and non-immune) active players.
    /// </summary>
    private void HandleQueuePriority()
    {
        var maxActive = _config.Config.Game.MaxPlayers;
        if (ActiveCount < maxActive) return;

        var priorityFlags = _config.Config.Queue.PriorityFlags;
        var immunityFlags = _config.Config.Queue.ImmunityFlags;

        var queued = QueueSlots
            .Select(slot => (slot, priority: GetQueuePriority(slot, _adminManager, priorityFlags)))
            .Where(x => x.priority > int.MinValue)
            .OrderByDescending(x => x.priority)
            .ThenBy(x => x.slot.AsPrimitive())
            .ToList();

        foreach (var (queuedSlot, queuedPriority) in queued)
        {
            var candidates = ActiveSlots
                .Select(slot => (
                    slot,
                    priority: GetQueuePriority(slot, _adminManager, priorityFlags),
                    immunity: GetQueueImmunityPriority(slot, _adminManager, immunityFlags)))
                .Where(x => x.priority < queuedPriority && x.immunity < queuedPriority)
                .OrderBy(x => x.priority)
                .ThenByDescending(x => x.slot.AsPrimitive())
                .ToList();

            if (candidates.Count == 0) continue;

            var replaceable = candidates[0].slot;

            // bump the active player to spectator + queue
            var bumpedClient = _bridge.ClientManager.GetGameClient(replaceable);
            if (bumpedClient is { IsInGame: true })
            {
                var bumpedController = bumpedClient.GetPlayerController();
                bumpedController?.ChangeTeam(CStrikeTeam.Spectator);
            }
            RemovePlayerFromQueues(replaceable);
            AddToQueue(replaceable);

            // promote the queued player to active + CT
            AddToActive(queuedSlot);
            var promotedClient = _bridge.ClientManager.GetGameClient(queuedSlot);
            if (promotedClient is { IsInGame: true })
            {
                var promotedController = promotedClient.GetPlayerController();
                promotedController?.ChangeTeam(CStrikeTeam.CT);
            }
        }
    }

    // ── round team tracking ────────────────────────────────────────────────

    public void SetRoundTeams()
    {
        Array.Clear(_roundTerrorists);
        Array.Clear(_roundCounterTerrorists);

        foreach (var slot in ActiveSlots)
        {
            var client = _bridge.ClientManager.GetGameClient(slot);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;

            switch (controller.Team)
            {
                case CStrikeTeam.TE: _roundTerrorists[slot.AsPrimitive()]        = true; break;
                case CStrikeTeam.CT: _roundCounterTerrorists[slot.AsPrimitive()] = true; break;
            }
        }
    }

    public void ClearRoundTeams()
    {
        Array.Clear(_roundTerrorists);
        Array.Clear(_roundCounterTerrorists);
    }

    public IReadOnlyList<PlayerSlot> RoundTerrorists
    {
        get
        {
            var list = new List<PlayerSlot>();
            for (byte i = 0; i < MaxSlots; i++)
                if (_roundTerrorists[i])
                    list.Add(new PlayerSlot(i));
            return list;
        }
    }

    public IReadOnlyList<PlayerSlot> RoundCounterTerrorists
    {
        get
        {
            var list = new List<PlayerSlot>();
            for (byte i = 0; i < MaxSlots; i++)
                if (_roundCounterTerrorists[i])
                    list.Add(new PlayerSlot(i));
            return list;
        }
    }

    // ── priority ──────────────────────────────────────────────────────────

    private int GetQueuePriority(PlayerSlot slot, IAdminManager? adminMgr, List<QueuePriorityFlagConfig> flags)
    {
        if (adminMgr is null || flags.Count == 0)
            return int.MinValue;

        var steamId = _bridge.ClientManager.GetGameClient(slot)?.SteamId;
        if (steamId is null)
            return int.MinValue;

        var best = int.MinValue;
        foreach (var flag in flags)
        {
            if (adminMgr.GetAdmin(steamId.Value)?.HasPermission(flag.Flag) == true)
                best = Math.Max(best, Math.Clamp(flag.Priority, 0, 100));
        }
        return best;
    }

    private int GetQueueImmunityPriority(PlayerSlot slot, IAdminManager? adminMgr, List<QueuePriorityFlagConfig> flags)
    {
        if (adminMgr is null || flags.Count == 0)
            return int.MinValue;

        var steamId = _bridge.ClientManager.GetGameClient(slot)?.SteamId;
        if (steamId is null)
            return int.MinValue;

        var best = int.MinValue;
        foreach (var flag in flags)
        {
            if (adminMgr.GetAdmin(steamId.Value)?.HasPermission(flag.Flag) == true)
                best = Math.Max(best, Math.Clamp(flag.Priority, 0, 100));
        }
        return best;
    }
}
