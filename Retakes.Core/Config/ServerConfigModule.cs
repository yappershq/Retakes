using System;
using System.IO;
using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;

namespace Retakes.Config;

/// <summary>
/// Applies the retakes server convars — a faithful port of the source's
/// <c>ServerHelper.ExecuteRetakesConfiguration</c> (b3none/cs2-retakes).
///
/// The source ships a <c>cfg/cs2-retakes/retakes.cfg</c> (creating it on first run if absent) and
/// <c>exec</c>s it on map start. We mirror that exactly: write the cfg to the game cfg dir if it
/// doesn't exist, then <c>exec cs2-retakes/retakes.cfg</c> on every map load.
///
/// This is LOAD-BEARING: <c>mp_give_player_c4 0</c> stops the engine handing a real C4 that would
/// collide with the synthetic auto-plant. The other convars set defuse round time, freeze time,
/// c4 timer, drop-on-death, disable warmup pausetimer, etc. so the retakes loop behaves correctly.
/// Shipping a real .cfg (rather than setting each convar via IConVarManager) matches the source and
/// lets server operators tune the "things you can change" block without a plugin rebuild.
/// </summary>
internal sealed class ServerConfigModule : IModule, IGameListener
{
    // Path the engine execs (relative to csgo/cfg/) — matches the source's `exec cs2-retakes/retakes.cfg`.
    private const string CfgExecPath = "cs2-retakes/retakes.cfg";

    private readonly InterfaceBridge             _bridge;
    private readonly ILogger<ServerConfigModule> _logger;

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public ServerConfigModule(InterfaceBridge bridge, ILogger<ServerConfigModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init()
    {
        EnsureCfgExists();
        return true;
    }

    public void OnPostInit()
        => _bridge.ModSharp.InstallGameListener(this);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.ModSharp.RemoveGameListener(this);

    // ── IGameListener ────────────────────────────────────────────────────────

    /// <summary>Exec the retakes config on every map load (mirrors the source's OnMapStart hook).</summary>
    void IGameListener.OnServerActivate()
    {
        EnsureCfgExists();
        // ServerCommand is buffered; a small delay lets the map finish loading before the exec runs
        // (the source uses a 1.0s AddTimer before ExecuteRetakesConfiguration for the same reason).
        _bridge.ModSharp.PushTimer(
            () =>
            {
                _bridge.ModSharp.ServerCommand($"exec {CfgExecPath}");
                _logger.LogInformation("[Retakes] Executed server config: {Cfg}", CfgExecPath);
            },
            1.0,
            GameTimerFlags.StopOnMapEnd);
    }

    // ── cfg file management ────────────────────────────────────────────────────

    private void EnsureCfgExists()
    {
        try
        {
            // GetGamePath() → game root (contains csgo/). Engine execs are relative to csgo/cfg/.
            var cfgDir  = Path.Combine(_bridge.ModSharp.GetGamePath(), "csgo", "cfg", "cs2-retakes");
            var cfgFile = Path.Combine(cfgDir, "retakes.cfg");

            if (File.Exists(cfgFile)) return;

            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(cfgFile, RetakesCfgContents);
            _logger.LogInformation("[Retakes] Created server config: {File}", cfgFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Retakes] Failed to create retakes.cfg — server convars will not be applied.");
        }
    }

    // Faithful port of the source's retakes.cfg contents (b3none/cs2-retakes ServerHelper.cs).
    private const string RetakesCfgContents =
        """
        // Things you shouldn't change:
        bot_kick
        bot_quota 0
        mp_autoteambalance 0
        mp_forcecamera 1
        mp_give_player_c4 0
        mp_halftime 0
        mp_ignore_round_win_conditions 0
        mp_join_grace_time 0
        mp_match_can_clinch 0
        mp_maxmoney 0
        mp_playercashawards 0
        mp_respawn_on_death_ct 0
        mp_respawn_on_death_t 0
        mp_solid_teammates 1
        mp_teamcashawards 0
        mp_warmup_pausetimer 0
        sv_skirmish_id 0

        // Things you can change, and may want to:
        mp_roundtime_defuse 0.25
        mp_autokick 0
        mp_c4timer 40
        mp_freezetime 1
        mp_friendlyfire 0
        mp_round_restart_delay 2
        sv_talk_enemy_dead 0
        sv_talk_enemy_living 0
        sv_deadtalk 1
        spec_replay_enable 0
        mp_maxrounds 30
        mp_match_end_restart 0
        mp_timelimit 0
        mp_match_restart_delay 10
        mp_death_drop_gun 1
        mp_death_drop_defuser 1
        mp_death_drop_grenade 1
        mp_warmuptime 15

        echo [Retakes] Config loaded!

        """;
}
