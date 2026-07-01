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
    private readonly ILogger<GameManager> _logger;
    private readonly InterfaceBridge      _bridge;
    private readonly ConfigModule         _config;
    private readonly QueueManager         _queue;

    private readonly Dictionary<ulong, PlayerScore> _playerScores = [];
    private          bool                           _scrambleNextRound;
    private          int                            _consecutiveTerroristWins;

    public GameManager(ILogger<GameManager> logger, InterfaceBridge bridge, ConfigModule config, QueueManager queue)
    {
        _logger = logger;
        _bridge = bridge;
        _config = config;
        _queue  = queue;
    }

    // ── score tracking ─────────────────────────────────────────────────────

    public void AddKill(ulong steamId64)
    {
        EnsureScore(steamId64).AddKill();
    }

    public void AddAssist(ulong steamId64)
    {
        EnsureScore(steamId64).AddAssist();
    }

    public void AddDefuse(ulong steamId64)
    {
        EnsureScore(steamId64).AddDefuse();
    }

    public void ResetPlayerScores() => _playerScores.Clear();

    /// <summary>Drop a disconnected player's score so it can't linger until the next round reset.</summary>
    public void RemovePlayerScore(ulong steamId64) => _playerScores.Remove(steamId64);

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
            _scrambleNextRound          = false;
            _consecutiveTerroristWins  = 0;
            return;
        }

        if (winningTeam == CStrikeTeam.CT)
        {
            _consecutiveTerroristWins = 0;
            CounterTerroristRoundWin();
        }
        else if (winningTeam == CStrikeTeam.TE)
        {
            _consecutiveTerroristWins++;
            TerroristRoundWin();
        }
    }

    public void ScrambleNextRound(ulong? adminSteamId)
    {
        _scrambleNextRound = true;
        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager,
            adminSteamId is null ? "Retakes_Teams_ScrambleNext" : "Retakes_Teams_ScrambleNextAdmin");
    }

    // ── team query ─────────────────────────────────────────────────────────

    public CStrikeTeam? GetCurrentTeam(ulong steamId64)
    {
        var client = _bridge.ClientManager.GetGameClient((SteamID)steamId64);
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
        var active           = _queue.ActivePlayers.ToList();

        var terrorists = active
            .Where(id => GetCurrentTeam(id) == CStrikeTeam.CT)
            .OrderByDescending(id => _playerScores.TryGetValue(id, out var s) ? s.Score : 0)
            .Take(targetTerrorists)
            .ToList();

        var counterTerrorists = active
            .Except(terrorists)
            .ToList();

        SetTeams(terrorists, counterTerrorists);
    }

    private void TerroristRoundWin()
    {
        // T wins: swap sides (CTs become Ts)
        var active = _queue.ActivePlayers.ToList();

        var newTerrorists = active
            .Where(id => GetCurrentTeam(id) == CStrikeTeam.CT)
            .ToList();

        var newCounterTerrorists = active
            .Except(newTerrorists)
            .ToList();

        SetTeams(newTerrorists, newCounterTerrorists);

        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Teams_CtSwapped",
            _consecutiveTerroristWins, _config.Config.Teams.RoundsToScramble);
    }

    private void ScrambleTeams()
    {
        var active = _queue.ActivePlayers.OrderBy(_ => Random.Shared.Next()).ToList();
        var targetT = _queue.GetTargetNumTerrorists();

        var terrorists        = active.Take(targetT).ToList();
        var counterTerrorists = active.Skip(targetT).ToList();

        SetTeams(terrorists, counterTerrorists);
        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Teams_Scrambled");
    }

    private void SetTeams(IReadOnlyList<ulong> terrorists, IReadOnlyList<ulong> counterTerrorists)
    {
        foreach (var id in terrorists)
            SwitchTeamFor(id, CStrikeTeam.TE);
        foreach (var id in counterTerrorists)
            SwitchTeamFor(id, CStrikeTeam.CT);
    }

    private void SwitchTeamFor(ulong steamId64, CStrikeTeam team)
    {
        var client = _bridge.ClientManager.GetGameClient((SteamID)steamId64);
        if (client is not { IsInGame: true }) return;
        var controller = client.GetPlayerController();
        if (controller is null || !controller.IsValid()) return;
        controller.SwitchTeam(team);
    }

    private PlayerScore EnsureScore(ulong steamId64)
    {
        if (!_playerScores.TryGetValue(steamId64, out var score))
            _playerScores[steamId64] = score = new PlayerScore();
        return score;
    }
}
