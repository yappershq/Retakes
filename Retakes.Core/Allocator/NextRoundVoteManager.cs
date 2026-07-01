using Retakes.Shared;
using Sharp.Shared.Enums;

namespace Retakes.Allocator;

/// <summary>
/// Tracks !nextround votes for one round.
/// Holds per-steamId votes; a PushTimer fires CompleteVote after 30s.
/// Reset() is called each round start (via OnAllocate).
/// </summary>
internal sealed class NextRoundVoteManager
{
    private const double VoteTimeoutSeconds   = 30.0;
    private const double EnoughVotesThreshold = 0.5;

    private readonly InterfaceBridge  _bridge;
    private readonly RoundTypeManager _roundTypeManager;

    private readonly Dictionary<ulong, RoundType> _votes = new();
    private Guid? _timerHandle;
    private int   _activePlayers; // snapshot when vote started

    public NextRoundVoteManager(InterfaceBridge bridge, RoundTypeManager roundTypeManager)
    {
        _bridge           = bridge;
        _roundTypeManager = roundTypeManager;
    }

    public bool IsActive => _timerHandle is not null;

    /// <summary>Cast or change a player's vote. Starts the timer on first vote.</summary>
    public void CastVote(ulong steamId, RoundType roundType, int currentActivePlayers)
    {
        if (!IsActive)
        {
            _activePlayers = currentActivePlayers;
            _timerHandle   = _bridge.ModSharp.PushTimer(
                CompleteVote,
                VoteTimeoutSeconds,
                GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd
            );

            BroadcastChat("[Retakes] Vote started! Type !nextround to vote for the next round type.");
        }

        _votes[steamId] = roundType;
    }

    public void CompleteVote()
    {
        if (_timerHandle is not null)
        {
            _bridge.ModSharp.StopTimer(_timerHandle.Value);
            _timerHandle = null;
        }

        if (_votes.Count == 0 || _activePlayers == 0)
        {
            BroadcastChat("[Retakes] Vote ended — no votes cast.");
            _votes.Clear();
            return;
        }

        // Tally
        var tally = new Dictionary<RoundType, int>();
        foreach (var (_, vote) in _votes)
        {
            tally.TryGetValue(vote, out var count);
            tally[vote] = count + 1;
        }

        var highestCount = tally.Values.Max();
        if ((double)highestCount / _activePlayers < EnoughVotesThreshold)
        {
            BroadcastChat("[Retakes] Vote failed — not enough players voted.");
            _votes.Clear();
            return;
        }

        // Break ties randomly
        var winners = tally.Where(kv => kv.Value == highestCount).Select(kv => kv.Key).ToList();
        var winner  = winners[Random.Shared.Next(winners.Count)];

        _roundTypeManager.SetNextRoundTypeOverride(winner);
        BroadcastChat($"[Retakes] Vote complete! Next round will be {winner}.");
        _votes.Clear();
    }

    /// <summary>Call each round start to clear state from the previous round.</summary>
    public void Reset()
    {
        if (_timerHandle is not null)
        {
            _bridge.ModSharp.StopTimer(_timerHandle.Value);
            _timerHandle = null;
        }
        _votes.Clear();
        _activePlayers = 0;
    }

    private void BroadcastChat(string msg)
    {
        foreach (var client in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (client.IsFakeClient) continue;
            client.Print(HudPrintChannel.Chat, msg);
        }
    }
}
