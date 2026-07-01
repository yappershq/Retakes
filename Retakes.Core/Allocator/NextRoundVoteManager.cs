using Retakes.Shared;
using Retakes.Utils;
using Sharp.Shared.Enums;
using Sharp.Shared.Units;

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

    private static readonly byte MaxSlots = PlayerSlot.MaxPlayerCount.AsPrimitive();
    private readonly RoundType?[] _votes = new RoundType?[MaxSlots];
    private Guid? _timerHandle;
    private int   _activePlayers; // snapshot when vote started

    public NextRoundVoteManager(InterfaceBridge bridge, RoundTypeManager roundTypeManager)
    {
        _bridge           = bridge;
        _roundTypeManager = roundTypeManager;
    }

    public bool IsActive => _timerHandle is not null;

    /// <summary>Cast or change a player's vote. Starts the timer on first vote.</summary>
    public void CastVote(PlayerSlot slot, RoundType roundType, int currentActivePlayers)
    {
        if (!slot.IsValid()) return;

        if (!IsActive)
        {
            _activePlayers = currentActivePlayers;
            _timerHandle   = _bridge.ModSharp.PushTimer(
                CompleteVote,
                VoteTimeoutSeconds,
                GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd
            );

            Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Vote_Started");
        }

        _votes[slot.AsPrimitive()] = roundType;
    }

    public void CompleteVote()
    {
        if (_timerHandle is not null)
        {
            _bridge.ModSharp.StopTimer(_timerHandle.Value);
            _timerHandle = null;
        }

        var castVotes = _votes.Where(v => v is not null).Select(v => v!.Value).ToList();

        if (castVotes.Count == 0 || _activePlayers == 0)
        {
            Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Vote_NoVotes");
            Array.Clear(_votes);
            return;
        }

        // Tally
        var tally = new Dictionary<RoundType, int>();
        foreach (var vote in castVotes)
        {
            tally.TryGetValue(vote, out var count);
            tally[vote] = count + 1;
        }

        var highestCount = tally.Values.Max();
        if ((double)highestCount / _activePlayers < EnoughVotesThreshold)
        {
            Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Vote_Failed");
            Array.Clear(_votes);
            return;
        }

        // Break ties randomly
        var winners = tally.Where(kv => kv.Value == highestCount).Select(kv => kv.Key).ToList();
        var winner  = winners[Random.Shared.Next(winners.Count)];

        _roundTypeManager.SetNextRoundTypeOverride(winner);
        var winnerName = Loc.Format(_bridge.LocalizerManager, RoundTypeKey(winner));
        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Vote_Complete", winnerName);
        Array.Clear(_votes);
    }

    internal static string RoundTypeKey(RoundType roundType) => roundType switch
    {
        RoundType.Pistol  => "Retakes_RoundType_Pistol",
        RoundType.HalfBuy => "Retakes_RoundType_HalfBuy",
        _                 => "Retakes_RoundType_FullBuy",
    };

    /// <summary>Call each round start to clear state from the previous round.</summary>
    public void Reset()
    {
        if (_timerHandle is not null)
        {
            _bridge.ModSharp.StopTimer(_timerHandle.Value);
            _timerHandle = null;
        }
        Array.Clear(_votes);
        _activePlayers = 0;
    }
}
