using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.Queue;
using Retakes.RoundFlow;
using Retakes.Shared;
using Retakes.Utils;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.CommandCenter.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.Spawn;

/// <summary>
/// Phase E — in-game spawn editor + map-config + admin commands.
///
/// Spawn-editor viz is EDITOR-ONLY and best-effort: each spawn is rendered as a team-colored
/// <c>prop_dynamic</c> (agent model, tinted via <see cref="IBaseModelEntity.RenderColor"/> + optional
/// glow) plus a <c>point_worldtext</c> (<see cref="IWorldText"/>) label. Model/glow decoration is
/// wrapped in try/catch so a fiddly netvar can never destabilise the server — the worldtext label is
/// the reliable indicator and always renders. Created entities are tracked in an INSTANCE list (no
/// static state) and killed on <c>!done</c>/shutdown.
///
/// Warmup-freeze while editing: enter warmup via <c>mp_warmup_pausetimer 1 / mp_warmuptime 999999 /
/// mp_warmup_start</c> (buffered ServerCommand), end via <c>mp_warmup_pausetimer 0 / mp_warmup_end</c>.
/// </summary>
internal sealed class SpawnEditorModule : IModule, IGameListener
{
    private const string ModuleIdentity = "Retakes.SpawnEditor";

    private const string TModel  = "agents/models/tm_leet/tm_leet_variantb.vmdl";
    private const string CtModel = "agents/models/ctm_sas/ctm_sas.vmdl";

    private static readonly Color32 ColorT       = new(255, 0,   0,   255); // red
    private static readonly Color32 ColorPlanter = new(255, 165, 0,   255); // orange
    private static readonly Color32 ColorCt      = new(0,   100, 255, 255); // blue

    private readonly ILogger<SpawnEditorModule> _logger;
    private readonly InterfaceBridge            _bridge;
    private readonly ConfigModule               _config;
    private readonly SpawnModule                _spawnModule;
    private readonly RoundFlowModule            _roundFlow;
    private readonly QueueModule                _queueModule;

    private IAdminManager? _adminManager;

    // ── editor session state (instance-scoped, never static) ─────────────────
    private Bombsite?              _editBombsite;   // null = not editing
    // ponytail: store indices not entity references — raw IBaseEntity pointers dangle after bulk-kill
    private readonly List<EntityIndex> _vizEntities = [];

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public SpawnEditorModule(
        ILogger<SpawnEditorModule> logger,
        InterfaceBridge            bridge,
        ConfigModule               config,
        SpawnModule                spawnModule,
        RoundFlowModule            roundFlow,
        QueueModule                queueModule)
    {
        _logger      = logger;
        _bridge      = bridge;
        _config      = config;
        _spawnModule = spawnModule;
        _roundFlow   = roundFlow;
        _queueModule = queueModule;
    }

    // ── IModule lifecycle ────────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
        => _bridge.ModSharp.InstallGameListener(this);

    public void OnAllSharpModulesLoaded()
    {
        _adminManager = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity)?.Instance;

        if (_adminManager is not null)
        {
            // Register the configured flags as known permissions so wildcard admins ("*") resolve them.
            var cmds = _config.Config.Commands;
            var perms = new Dictionary<string, HashSet<string>>();
            foreach (var flag in new[] { cmds.SpawnEditor, cmds.MapConfig, cmds.Admin })
                perms.TryAdd(flag, new HashSet<string>());

            _adminManager.MountAdminManifest(ModuleIdentity, () => new AdminTableManifest(
                PermissionCollection: perms,
                Roles:  [],
                Admins: []
            ));
        }
        else
        {
            _logger.LogWarning("[Retakes] SpawnEditor: AdminManager not available — editor/admin commands will be denied.");
        }

        var cc = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<ICommandCenter>(ICommandCenter.Identity)?.Instance;

        if (cc is null)
        {
            _logger.LogWarning("[Retakes] SpawnEditor: ICommandCenter not available — commands disabled.");
            return;
        }

        var reg = cc.GetRegistry("retakes");

        // spawn editor
        reg.RegisterClientCommand("showspawns",        OnShowSpawns);
        reg.RegisterClientCommand("edit",              OnShowSpawns);
        reg.RegisterClientCommand("add",               OnAddSpawn);
        reg.RegisterClientCommand("remove",            OnRemoveSpawn);
        reg.RegisterClientCommand("nearest",           OnNearestSpawn);
        reg.RegisterClientCommand("hidespawns",        OnHideSpawns);
        reg.RegisterClientCommand("done",              OnHideSpawns);

        // map config
        reg.RegisterClientCommand("mapconfig",         OnMapConfig);
        reg.RegisterClientCommand("mapconfigs",        OnMapConfigs);

        // admin
        reg.RegisterClientCommand("forcebombsite",     OnForceBombsite);
        reg.RegisterClientCommand("forcebombsitestop", OnForceBombsiteStop);
        reg.RegisterClientCommand("scramble",          OnScramble);
        reg.RegisterClientCommand("debugqueues",       OnDebugQueues);
    }

    public void Shutdown()
    {
        ClearViz();
        _bridge.ModSharp.RemoveGameListener(this);
    }

    // ── IGameListener ────────────────────────────────────────────────────────

    /// <summary>On map change entities are already gone; just clear the index list.</summary>
    void IGameListener.OnServerActivate() => _vizEntities.Clear();

    void IGameListener.OnResourcePrecache()
    {
        _bridge.ModSharp.PrecacheResource(TModel);
        _bridge.ModSharp.PrecacheResource(CtModel);
    }

    // ── admin gate ───────────────────────────────────────────────────────────

    private bool Denied(IGameClient client, string flag)
    {
        if (_adminManager?.GetAdmin((SteamID)client.SteamId)?.HasPermission(flag) == true)
            return false;

        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_NoPermission");
        return true;
    }

    private static IPlayerPawn? GetAlivePawn(IGameClient client)
    {
        if (!client.IsInGame) return null;
        var controller = client.GetPlayerController();
        if (controller is null || !controller.IsValid()) return null;
        var pawn = controller.GetPlayerPawn();
        return pawn is { IsAlive: true } ? pawn : null;
    }

    private static Bombsite? ParseBombsite(string arg)
        => arg.Trim().ToUpperInvariant() switch
        {
            "A" => Bombsite.A,
            "B" => Bombsite.B,
            _   => null,
        };

    // ── !showspawns / !edit [A|B] ────────────────────────────────────────────

    private void OnShowSpawns(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.SpawnEditor)) return;

        if (cmd.ArgCount < 1)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_UsageShow");
            return;
        }

        var site = ParseBombsite(cmd.GetArg(1));
        if (site is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_SpecifyBombsite");
            return;
        }

        _editBombsite = site;

        // Freeze the game in indefinite warmup while editing.
        _bridge.ModSharp.ServerCommand("mp_warmup_pausetimer 1");
        _bridge.ModSharp.ServerCommand("mp_warmuptime 999999");
        _bridge.ModSharp.ServerCommand("mp_warmup_start");

        // Give warmup a beat to take before spawning viz entities (mirrors CSS 1s delay).
        _bridge.ModSharp.PushTimer(() =>
        {
            if (_editBombsite is not null)
                RefreshViz();
        }, 1.0, GameTimerFlags.StopOnMapEnd);

        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_Editing", site);
    }

    // ── !add [T|CT] [Y|N] ────────────────────────────────────────────────────

    private void OnAddSpawn(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.SpawnEditor)) return;

        if (_editBombsite is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_EnterEditFirst");
            return;
        }

        var pawn = GetAlivePawn(client);
        if (pawn is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_NeedAlivePawn");
            return;
        }

        if (cmd.ArgCount < 1)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_UsageAdd");
            return;
        }

        var teamStr = cmd.GetArg(1).Trim().ToUpperInvariant();
        CStrikeTeam team = teamStr switch
        {
            "T"  => CStrikeTeam.TE,
            "CT" => CStrikeTeam.CT,
            _    => CStrikeTeam.UnAssigned,
        };
        if (team == CStrikeTeam.UnAssigned)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_SpecifyTeam");
            return;
        }

        var planterArg = cmd.ArgCount >= 2 ? cmd.GetArg(2).Trim().ToUpperInvariant() : "";
        if (planterArg is not ("" or "Y" or "N"))
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_BadPlanter");
            return;
        }

        var pos    = pawn.GetAbsOrigin();
        var angles = pawn.GetAbsAngles();

        // Reject if within 72u of an existing spawn (this bombsite).
        var siteSpawns = _spawnModule.MapConfig.GetSpawnsClone()
            .Where(s => s.Bombsite == _editBombsite.Value)
            .ToList();
        if (siteSpawns.Any(s => s.Position.DistToSqr(pos) <= 72f * 72f))
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_TooClose");
            return;
        }

        // Auto CanBePlanter: only meaningful for T; if arg given use it, else read in-bomb-zone schema field.
        bool canBePlanter = team == CStrikeTeam.TE
            && (planterArg == "Y"
                || (planterArg == "" && pawn.GetNetVar<bool>("m_bInBombZone")));

        var spawn = new Spawn
        {
            Position     = pos,
            Angles       = angles,
            Team         = team,
            Bombsite     = _editBombsite.Value,
            CanBePlanter = canBePlanter,
        };

        if (_spawnModule.MapConfig.AddSpawn(spawn))
        {
            _spawnModule.SpawnManager.Rebuild(_spawnModule.MapConfig);
            RefreshViz();
            Loc.Chat(_bridge.LocalizerManager, client,
                canBePlanter ? "Retakes_Spawn_AddedPlanter" : "Retakes_Spawn_Added", team);
        }
        else
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_AddError");
        }
    }

    // ── !remove ──────────────────────────────────────────────────────────────

    private void OnRemoveSpawn(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.SpawnEditor)) return;

        if (_editBombsite is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_EnterEditFirst");
            return;
        }

        var pawn = GetAlivePawn(client);
        if (pawn is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_NeedAlivePawn");
            return;
        }

        var pos     = pawn.GetAbsOrigin();
        var nearest = FindNearest(pos, _editBombsite.Value, 128f);
        if (nearest is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_NoneWithin128");
            return;
        }

        if (_spawnModule.MapConfig.RemoveSpawn(nearest))
        {
            _spawnModule.SpawnManager.Rebuild(_spawnModule.MapConfig);
            RefreshViz();
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_Removed");
        }
        else
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_RemoveError");
        }
    }

    // ── !nearest ─────────────────────────────────────────────────────────────

    private void OnNearestSpawn(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.SpawnEditor)) return;

        if (_editBombsite is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_EnterEditFirst");
            return;
        }

        var pawn = GetAlivePawn(client);
        if (pawn is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_NeedAlivePawn");
            return;
        }

        var nearest = FindNearest(pawn.GetAbsOrigin(), _editBombsite.Value, float.MaxValue);
        if (nearest is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_NoneFound");
            return;
        }

        pawn.Teleport(nearest.Position, nearest.Angles, new Vector());
        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_Teleported");
    }

    // ── !hidespawns / !done ──────────────────────────────────────────────────

    private void OnHideSpawns(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.SpawnEditor)) return;

        _editBombsite = null;
        ClearViz();

        _bridge.ModSharp.ServerCommand("mp_warmup_pausetimer 0");
        _bridge.ModSharp.ServerCommand("mp_warmup_end");

        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_Exited");
    }

    // ── !mapconfig <name> ────────────────────────────────────────────────────

    private void OnMapConfig(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.MapConfig)) return;

        if (cmd.ArgCount < 1)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_UsageMapconfig");
            return;
        }

        var name = cmd.GetArg(1).Trim().Replace(".json", "");
        var path = Path.Combine(_bridge.DataPath, "map_config", $"{name}.json");
        if (!File.Exists(path))
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_MapconfigNotFound", name);
            return;
        }

        _spawnModule.MapConfig.LoadForMap(name);
        _spawnModule.SpawnManager.Rebuild(_spawnModule.MapConfig);

        if (_editBombsite is not null) RefreshViz();

        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_MapconfigLoaded", name);
        _logger.LogInformation("[Retakes] Map config '{Name}' hot-loaded by {Id}.", name, (ulong)client.SteamId);
    }

    // ── !mapconfigs ──────────────────────────────────────────────────────────

    private void OnMapConfigs(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.MapConfig)) return;

        var dir = Path.Combine(_bridge.DataPath, "map_config");
        if (!Directory.Exists(dir))
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_NoMapConfigs");
            return;
        }

        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToList();
        if (files.Count == 0)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_NoMapConfigs");
            return;
        }

        foreach (var file in files)
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_MapconfigItem", Path.GetFileNameWithoutExtension(file));

        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_MapconfigsListed");
    }

    // ── !forcebombsite [A|B] ─────────────────────────────────────────────────

    private void OnForceBombsite(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.Admin)) return;

        if (cmd.ArgCount < 1)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_UsageForce");
            return;
        }

        var site = ParseBombsite(cmd.GetArg(1));
        if (site is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_SpecifyBombsite");
            return;
        }

        _roundFlow.SetForcedBombsite(site.Value);
        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_BombsiteForced", site);
    }

    // ── !forcebombsitestop ───────────────────────────────────────────────────

    private void OnForceBombsiteStop(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.Admin)) return;

        _roundFlow.SetForcedBombsite(null);
        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_BombsiteUnforced");
    }

    // ── !scramble ────────────────────────────────────────────────────────────

    private void OnScramble(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.Admin)) return;

        _queueModule.GameManager.ScrambleNextRound((ulong)client.SteamId);
        _logger.LogInformation("[Retakes] Scramble requested by {Id}.", (ulong)client.SteamId);
    }

    // ── !debugqueues ─────────────────────────────────────────────────────────

    private void OnDebugQueues(IGameClient client, StringCommand cmd)
    {
        if (Denied(client, _config.Config.Commands.Admin)) return;

        var qm = _queueModule.QueueManager;
        _logger.LogInformation(
            "[Retakes][DebugQueues] Active({A})=[{ActiveIds}] Queue({Q})=[{QueueIds}] RoundT({T})=[{TIds}] RoundCT({C})=[{CIds}]",
            qm.ActivePlayers.Count,          string.Join(",", qm.ActivePlayers),
            qm.QueuePlayers.Count,           string.Join(",", qm.QueuePlayers),
            qm.RoundTerrorists.Count,        string.Join(",", qm.RoundTerrorists),
            qm.RoundCounterTerrorists.Count, string.Join(",", qm.RoundCounterTerrorists));

        Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Spawn_QueuesDumped",
            qm.ActivePlayers.Count, qm.QueuePlayers.Count);
    }

    // ── spawn lookup ─────────────────────────────────────────────────────────

    private Spawn? FindNearest(Vector pos, Bombsite site, float maxDist)
    {
        var maxSqr = maxDist >= float.MaxValue ? float.MaxValue : maxDist * maxDist;
        Spawn? best = null;
        var    bestSqr = float.MaxValue;

        foreach (var spawn in _spawnModule.MapConfig.GetSpawnsClone())
        {
            if (spawn.Bombsite != site) continue;
            var d = spawn.Position.DistToSqr(pos);
            if (d > maxSqr || d >= bestSqr) continue;
            bestSqr = d;
            best    = spawn;
        }
        return best;
    }

    // ── visualisation ────────────────────────────────────────────────────────

    private void RefreshViz()
    {
        ClearViz();
        if (_editBombsite is null) return;

        var site   = _editBombsite.Value;
        var spawns = _spawnModule.MapConfig.GetSpawnsClone().Where(s => s.Bombsite == site).ToList();
        foreach (var spawn in spawns)
            ShowSpawn(spawn);

        Loc.ChatAll(_bridge.LocalizerManager, _bridge.ClientManager, "Retakes_Spawn_Showing", spawns.Count, site);
    }

    private void ShowSpawn(Spawn spawn)
    {
        var color = spawn.Team == CStrikeTeam.TE
            ? (spawn.CanBePlanter ? ColorPlanter : ColorT)
            : ColorCt;

        // Prop model — decoration is best-effort; a failed netvar must never crash the server.
        var prop = _bridge.EntityManager.CreateEntityByName<IBaseModelEntity>("prop_dynamic");
        if (prop is not null)
        {
            try
            {
                prop.SetModel(spawn.Team == CStrikeTeam.TE ? TModel : CtModel);
                prop.DispatchSpawn();
                prop.RenderColor = color;

                // ponytail: glow is a nice-to-have; wrapped so a fiddly glow netvar degrades to plain tint.
                var glow = prop.GetGlowProperty();
                if (glow is not null)
                {
                    glow.Glowing            = true;
                    glow.GlowColorOverride  = color;
                    glow.GlowType           = 3;
                    glow.GlowRangeMax       = 2000;
                    glow.GlowRangeMin       = 25;
                }

                prop.Teleport(spawn.Position, spawn.Angles, new Vector());
                _vizEntities.Add(prop.Index);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Retakes] SpawnEditor: prop viz decoration failed — degrading to label only.");
                if (prop.IsValidEntity) prop.Kill();
            }
        }

        ShowLabel(spawn, color);
    }

    private void ShowLabel(Spawn spawn, Color32 color)
    {
        var text = _bridge.EntityManager.CreateEntityByName<IWorldText>("point_worldtext");
        if (text is null) return;

        try
        {
            var teamName = spawn.Team == CStrikeTeam.TE ? "T" : "CT";
            var planter  = spawn.CanBePlanter
                ? Loc.Format(_bridge.LocalizerManager, "Retakes_Spawn_LabelPlanter")
                : "";
            text.Message = Loc.Format(_bridge.LocalizerManager, "Retakes_Spawn_Label",
                teamName, planter, spawn.Bombsite,
                spawn.Position.X.ToString("F0"), spawn.Position.Y.ToString("F0"), spawn.Position.Z.ToString("F0"));
            text.Enabled          = true;
            text.FontSize         = 25f;
            text.Color            = color;
            text.FullBright       = true;
            text.SetNetVar("m_flWorldUnitsPerPx", 0.1f); // no typed property on IWorldText; verified netvar

            var textPos   = new Vector(spawn.Position.X, spawn.Position.Y, spawn.Position.Z + 80f);
            var textAngle = new Vector(spawn.Angles.X, spawn.Angles.Y + 90f, spawn.Angles.Z + 90f);
            text.Teleport(textPos, textAngle, new Vector());
            text.DispatchSpawn();

            _vizEntities.Add(text.Index);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Retakes] SpawnEditor: worldtext label failed.");
            if (text.IsValidEntity) text.Kill();
        }
    }

    private void ClearViz()
    {
        foreach (var idx in _vizEntities)
        {
            var ent = _bridge.EntityManager.FindEntityByIndex(idx);
            if (ent is { IsValidEntity: true })
                ent.Kill();
        }
        _vizEntities.Clear();
    }
}
