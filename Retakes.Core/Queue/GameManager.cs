using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Utils;
using Sharp.Shared.Enums;
using Sharp.Shared.Units;

namespace Retakes.Queue;

internal sealed class GameManager
{
    private static readonly byte MaxSlots = PlayerSlot.MaxPlayerCount.AsPrimitive();

    private readonly ILogger<GameManager> _logger;
    private readonly InterfaceBridge      _bridge;
    private readonly ConfigModule         _config;
    private readonly QueueManager         _queue;

    private readonly PlayerScore?[] _playerScores = new PlayerScore?[MaxSlots];
    private          bool           _scrambleNextRound;
    private          int            _consecutiveTerroristWins;

    public GameManager(ILogger<GameManager> logger, InterfaceBridge bridge, ConfigModule config, QueueManager queue)
    {
        _logger = logger;
        _bridge = bridge;
        _config = config;
        _queue  = queue;
    }

    // ── score tracking ─────────────────────────────────────────────────────

    public void AddKill(PlayerSlot slot)
    {
        EnsureScore(slot).AddKill();
    }

    public void AddAssist(PlayerSlot slot)
    {
        EnsureScore(slot).AddAssist();
    }

    public void AddDefuse(PlayerSlot slot)
    {
        EnsureScore(slot).AddDefuse();
    }

    public void ResetPlayerScores() => Array.Clear(_playerScores);

    /// <summary>Drop a disconnected player's score so it can't linger until the next round reset.</summary>
    public void ClearSlot(PlayerSlot slot)
    {
        if (!slot.IsValid()) return;
        _playerScores[slot.AsPrimitive()] = null;
    }

    // ── round transition ───────────────────────────────────────────────────

    /// <summary>
    /// Balance/scramble teams based on who won the round.
    /// Called from Phase B2 round-pre-start hook.
    /// </summary>
    public void OnRoundPreStart(CStrikeTeam winningTeam)
    {
        if (!_config.Config.Teams.IsBalanceEnabled) return;

        if (_scrambleNextRound || (_config.Config.Teams.IsScrambleEnabled &&
            _consecutiveTerroristWins >= _config.Config.Teams.RoundsToScramble))
        {
            ScrambleTeams();
            _scrambleNextRound        = false;
            _consecutiveTerroristWins = 0;
        }
        else if (winningTeam == CStrikeTeam.CT)
        {
            _consecutiveTerroristWins = 0;
            CounterTerroristRoundWin();
        }
        else if (winningTeam == CStrikeTeam.TE)
        {
            // Increment BEFORE checking the scramble threshold (matches source semantics:
            // TerroristRoundWin() increments then checks == RoundsToScramble).
            _consecutiveTerroristWins++;
            TerroristRoundWin();

            if (_config.Config.Teams.IsScrambleEnabled
                && _consecutiveTerroristWins == _config.Config.Teams.RoundsToScramble)
            {
                ScrambleTeams();
                _scrambleNextRound        = false;
                _consecutiveTerroristWins = 0;
            }
        }

        BalanceTeams();
    }

    public void ScrambleNextRound(ulong? adminSteamId)
    {
        _scrambleNextRound = true;
        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager,
            adminSteamId is null ? "Retakes_Teams_ScrambleNext" : "Retakes_Teams_ScrambleNextAdmin");
    }

    // ── team query ─────────────────────────────────────────────────────────

    public CStrikeTeam? GetCurrentTeam(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);
        if (client is not { IsInGame: true }) return null;
        var controller = client.GetPlayerController();
        if (controller is null || !controller.IsValid()) return null;
        return controller.Team;
    }

    // ── internal balance helpers ───────────────────────────────────────────

    private void CounterTerroristRoundWin()
    {
        // Promote top-scoring CTs to Ts for next round
        var targetTerrorists = _queue.GetTargetNumTerrorists();
        var active           = _queue.ActiveSlots.ToList();

        var terrorists = active
            .Where(slot => GetCurrentTeam(slot) == CStrikeTeam.CT)
            .OrderByDescending(slot => GetScore(slot))
            .Take(targetTerrorists)
            .ToList();

        var counterTerrorists = active
            .Except(terrorists)
            .ToList();

        SetTeams(terrorists, counterTerrorists);
    }

    private void TerroristRoundWin()
    {
        // T win: source semantics — no team reassignment here, just the streak announcement.
        // (BalanceTeams(), called unconditionally afterward, handles side-count correction.)
        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Teams_CtSwapped",
            _consecutiveTerroristWins, _config.Config.Teams.RoundsToScramble);
    }

    private void ScrambleTeams()
    {
        var active  = _queue.ActiveSlots.OrderBy(_ => Random.Shared.Next()).ToList();
        var targetT = _queue.GetTargetNumTerrorists();

        var terrorists        = active.Take(targetT).ToList();
        var counterTerrorists = active.Skip(targetT).ToList();

        SetTeams(terrorists, counterTerrorists);
        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Teams_Scrambled");
    }

    /// <summary>
    /// Adjust the live T count toward GetTargetNumTerrorists() by switching CTs to T.
    /// Queried live (controller.Team) rather than from the round-team snapshot, since
    /// RoundFlowModule clears/repopulates that snapshot around this call.
    /// </summary>
    private void BalanceTeams()
    {
        var activeSlots = _queue.ActiveSlots.ToList();

        var currentT = activeSlots.Count(slot => GetCurrentTeam(slot) == CStrikeTeam.TE);
        var numTerroristsNeeded = _queue.GetTargetNumTerrorists() - currentT;
        if (numTerroristsNeeded <= 0) return;

        var cts = activeSlots.Where(slot => GetCurrentTeam(slot) == CStrikeTeam.CT).ToList();

        // Prefer scoring CTs first, then backfill from the shuffled remainder.
        var scored    = cts.Where(slot => GetScore(slot) > 0).OrderByDescending(GetScore).ToList();
        var remaining = cts.Except(scored).OrderBy(_ => Random.Shared.Next()).ToList();

        var chosen = scored.Concat(remaining).Take(numTerroristsNeeded).ToList();

        foreach (var slot in chosen)
            SwitchTeamFor(slot, CStrikeTeam.TE);
    }

    private void SetTeams(IReadOnlyList<PlayerSlot> terrorists, IReadOnlyList<PlayerSlot> counterTerrorists)
    {
        foreach (var slot in terrorists)
            SwitchTeamFor(slot, CStrikeTeam.TE);
        foreach (var slot in counterTerrorists)
            SwitchTeamFor(slot, CStrikeTeam.CT);
    }

    private void SwitchTeamFor(PlayerSlot slot, CStrikeTeam team)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);
        if (client is not { IsInGame: true }) return;
        var controller = client.GetPlayerController();
        if (controller is null || !controller.IsValid()) return;
        controller.SwitchTeam(team);
    }

    private int GetScore(PlayerSlot slot)
        => slot.IsValid() ? _playerScores[slot.AsPrimitive()]?.Score ?? 0 : 0;

    private PlayerScore EnsureScore(PlayerSlot slot)
    {
        var i = slot.AsPrimitive();
        return _playerScores[i] ??= new PlayerScore();
    }
}
