using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Retakes.Config;
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

    public QueueManager(ILogger<QueueManager> logger, InterfaceBridge bridge, ConfigModule config)
    {
        _logger = logger;
        _bridge = bridge;
        _config = config;
    }

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

        var maxActive = _config.Config.Game.MaxPlayers;

        foreach (var slot in QueueSlots.ToList())
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

}
