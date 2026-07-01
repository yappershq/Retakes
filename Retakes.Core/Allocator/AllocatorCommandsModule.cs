using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Database;
using Retakes.Plugins;
using Retakes.Queue;
using Retakes.Shared;
using Retakes.Utils;
using Sharp.Modules.CommandCenter.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.Allocator;

/// <summary>
/// Phase D2 — weapon-pref commands (!gun, !awp, !removegun),
/// !guns menu via IMenuManager, !nextround vote via PushTimer.
/// </summary>
internal sealed class AllocatorCommandsModule : IModule
{
    private readonly ILogger<AllocatorCommandsModule> _logger;
    private readonly InterfaceBridge                  _bridge;
    private readonly ConfigModule                     _config;
    private readonly QueueModule                      _queueModule;
    private readonly EventBus                         _bus;
    private readonly RetakesDatabase                  _db;
    private readonly RoundTypeManager                 _roundTypeManager;
    private readonly NextRoundVoteManager             _voteManager;

    private IMenuManager? _menuManager;

    private readonly Action _onAllocate;

    public AllocatorCommandsModule(
        ILogger<AllocatorCommandsModule> logger,
        InterfaceBridge                  bridge,
        ConfigModule                     config,
        QueueModule                      queueModule,
        EventBus                         bus,
        RetakesDatabase                  db,
        RoundTypeManager                 roundTypeManager)
    {
        _logger           = logger;
        _bridge           = bridge;
        _config           = config;
        _queueModule      = queueModule;
        _bus              = bus;
        _db               = db;
        _roundTypeManager = roundTypeManager;
        _voteManager      = new NextRoundVoteManager(bridge, roundTypeManager);
        _onAllocate       = OnAllocate;
    }

    // ── IModule ────────────────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        _menuManager = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity)?.Instance;

        if (_menuManager is null)
            _logger.LogWarning("[Retakes] AllocatorCommandsModule: IMenuManager not available — gun menu disabled.");

        var cc = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<ICommandCenter>(ICommandCenter.Identity)?.Instance;

        if (cc is null)
        {
            _logger.LogWarning("[Retakes] AllocatorCommandsModule: ICommandCenter not available — allocator commands disabled.");
        }
        else
        {
            var reg = cc.GetRegistry("retakes");
            reg.RegisterClientCommand("guns",      OnGunsCommand);
            reg.RegisterClientCommand("gun",       OnGunCommand);
            reg.RegisterClientCommand("awp",       OnAwpCommand);
            reg.RegisterClientCommand("removegun", OnRemoveGunCommand);

            if (_config.Config.Allocator.EnableNextRoundTypeVoting)
                reg.RegisterClientCommand("nextround", OnNextRoundCommand);
        }

        _bus.OnAllocate += _onAllocate;
    }

    public void Shutdown()
    {
        _bus.OnAllocate -= _onAllocate;
        _voteManager.Reset();
    }

    // ── OnAllocate ─────────────────────────────────────────────────────────

    private void OnAllocate()
    {
        _voteManager.Reset();
    }

    // ── !guns ──────────────────────────────────────────────────────────────

    private void OnGunsCommand(IGameClient client, StringCommand _)
    {
        if (!client.IsInGame) return;

        if (_menuManager is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_MenuUnavailable");
            return;
        }

        _menuManager.DisplayMenu(client, BuildGunMenuChain(client));
    }

    // ── !gun / !removegun ──────────────────────────────────────────────────

    private void OnGunCommand(IGameClient client, StringCommand cmd)
        => HandleWeaponCommand(client, cmd, remove: false);

    private void OnRemoveGunCommand(IGameClient client, StringCommand cmd)
        => HandleWeaponCommand(client, cmd, remove: true);

    private void HandleWeaponCommand(IGameClient client, StringCommand cmd, bool remove)
    {
        if (!client.IsInGame) return;

        if (!_config.Config.Allocator.CanPlayersSelectWeapons())
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_SelectionDisabled");
            return;
        }

        if (cmd.ArgCount < 1)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_UsageGun");
            return;
        }

        var controller  = client.GetPlayerController();
        var currentTeam = controller?.Team ?? CStrikeTeam.UnAssigned;

        CStrikeTeam team;
        if (cmd.ArgCount >= 2)
        {
            var teamStr = cmd.GetArg(2).Trim().ToUpperInvariant();
            team = teamStr switch
            {
                "T"  or "TE"               => CStrikeTeam.TE,
                "CT" or "COUNTERTERRORIST" => CStrikeTeam.CT,
                _                          => CStrikeTeam.UnAssigned,
            };
            if (team == CStrikeTeam.UnAssigned)
            {
                Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_InvalidTeam", cmd.GetArg(2));
                return;
            }
        }
        else if (currentTeam is CStrikeTeam.TE or CStrikeTeam.CT)
        {
            team = currentTeam;
        }
        else
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_JoinTeamFirst");
            return;
        }

        var weaponInput = cmd.GetArg(1).Trim().ToLowerInvariant();
        var weapon      = CsItemNames.TryGetFromName(weaponInput)
                       ?? CsItemNames.TryGetFromName("weapon_" + weaponInput);

        if (weapon is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_UnknownWeapon", weaponInput);
            return;
        }

        if (!WeaponHelpers.IsWeapon(weapon.Value))
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_NotSelectable", weaponInput);
            return;
        }

        if (!_config.Config.Allocator.IsUsableWeapon(weapon.Value))
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_NotAllowed", weaponInput);
            return;
        }

        var allocType = WeaponHelpers.GetAllocationTypeForWeapon(team, weapon.Value);
        if (allocType is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_NotValidForTeam", weaponInput, team);
            return;
        }

        var steamId = (ulong)client.SteamId;
        var setting = _db.GetCachedUserSettings(steamId);

        if (remove)
        {
            setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
                setting.WeaponPreferencesJson, team, allocType.Value, null);
            _db.SetCachedWeaponPreference(steamId, setting.WeaponPreferencesJson);
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_PrefRemoved",
                weapon.Value.GetName(), allocType.Value, team);
        }
        else
        {
            setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
                setting.WeaponPreferencesJson, team, allocType.Value, weapon.Value);
            _db.SetCachedWeaponPreference(steamId, setting.WeaponPreferencesJson);
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_PrefSet",
                weapon.Value.GetName(), allocType.Value, team);

            // Immediate re-give if in-round, team matches, and not a preferred (AWP-queue) weapon
            if (team == currentTeam && allocType.Value != WeaponAllocationType.Preferred)
                TryGiveImmediately(client, weapon.Value, allocType.Value);
        }
    }

    private void TryGiveImmediately(IGameClient client, CsItem weapon, WeaponAllocationType allocType)
    {
        if (!_config.Config.Allocator.AllowAllocationAfterFreezeTime) return;

        var gameRules = _bridge.ModSharp.GetGameRules();
        if (gameRules is null || gameRules.IsFreezePeriod || gameRules.IsWarmupPeriod) return;

        var currentRoundType = _roundTypeManager.CurrentRoundType;
        if (currentRoundType is null) return;

        if (!WeaponHelpers.IsAllocationTypeValidForRound(allocType, currentRoundType.Value)) return;

        var controller = client.GetPlayerController();
        if (controller is null || !controller.IsValid()) return;
        var pawn = controller.GetPlayerPawn();
        if (pawn is null || !pawn.IsAlive) return;

        pawn.GiveNamedItem(weapon.GetName());
    }

    // ── !awp ────────────────────────────────────────────────────────────────

    private void OnAwpCommand(IGameClient client, StringCommand _)
    {
        if (!client.IsInGame) return;

        var steamId = (ulong)client.SteamId;
        var setting = _db.GetCachedUserSettings(steamId);

        var currentAwpPref = WeaponPrefsHelper.GetPreference(setting, CStrikeTeam.TE, WeaponAllocationType.Preferred);

        if (currentAwpPref is not null)
        {
            setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
                setting.WeaponPreferencesJson, CStrikeTeam.TE, WeaponAllocationType.Preferred, null);
            _db.SetCachedWeaponPreference(steamId, setting.WeaponPreferencesJson);
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_AwpRemoved");
        }
        else
        {
            setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
                setting.WeaponPreferencesJson, CStrikeTeam.TE, WeaponAllocationType.Preferred, CsItem.AWP);
            _db.SetCachedWeaponPreference(steamId, setting.WeaponPreferencesJson);
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_AwpSet");
        }
    }

    // ── !nextround ──────────────────────────────────────────────────────────

    private void OnNextRoundCommand(IGameClient client, StringCommand _)
    {
        if (!client.IsInGame) return;
        if (_menuManager is null)
        {
            Loc.Chat(_bridge.LocalizerManager, client, "Retakes_Alloc_VoteMenuUnavailable");
            return;
        }
        _menuManager.DisplayMenu(client, BuildVoteMenu(client));
    }

    // ── Gun menu chain (built bottom-up, localized to the requesting client) ─

    private Menu BuildGunMenuChain(IGameClient client)
    {
        var cfg = _config.Config.Allocator;

        var awpMenu      = BuildAwpMenu(client);
        var ctHalfMenu   = BuildWeaponPageMenu(client, "Retakes_Menu_Page_CtHalf",      CStrikeTeam.CT, WeaponAllocationType.HalfBuyPrimary, cfg, awpMenu);
        var tHalfMenu    = BuildWeaponPageMenu(client, "Retakes_Menu_Page_THalf",       CStrikeTeam.TE, WeaponAllocationType.HalfBuyPrimary, cfg, ctHalfMenu);
        var ctPistolMenu = BuildWeaponPageMenu(client, "Retakes_Menu_Page_CtPistol",    CStrikeTeam.CT, WeaponAllocationType.PistolRound,    cfg, tHalfMenu);
        var tPistolMenu  = BuildWeaponPageMenu(client, "Retakes_Menu_Page_TPistol",     CStrikeTeam.TE, WeaponAllocationType.PistolRound,    cfg, ctPistolMenu);
        var ctSecMenu    = BuildWeaponPageMenu(client, "Retakes_Menu_Page_CtSecondary", CStrikeTeam.CT, WeaponAllocationType.Secondary,      cfg, tPistolMenu);
        var tSecMenu     = BuildWeaponPageMenu(client, "Retakes_Menu_Page_TSecondary",  CStrikeTeam.TE, WeaponAllocationType.Secondary,      cfg, ctSecMenu);
        var ctPrimMenu   = BuildWeaponPageMenu(client, "Retakes_Menu_Page_CtPrimary",   CStrikeTeam.CT, WeaponAllocationType.FullBuyPrimary, cfg, tSecMenu);
        return              BuildWeaponPageMenu(client, "Retakes_Menu_Page_TPrimary",    CStrikeTeam.TE, WeaponAllocationType.FullBuyPrimary, cfg, ctPrimMenu);
    }

    private Menu BuildWeaponPageMenu(
        IGameClient client, string titleKey, CStrikeTeam team, WeaponAllocationType allocType, AllocatorSettings cfg, Menu nextMenu)
    {
        var lm      = _bridge.LocalizerManager;
        var weapons = WeaponHelpers.GetPossibleWeaponsForAllocationType(allocType, team, cfg);
        var builder = Menu.Create().Title(Loc.Str(lm, client, titleKey));
        foreach (var weapon in weapons)
        {
            var capturedWeapon = weapon;
            var capturedTeam   = team;
            var capturedAlloc  = allocType;
            builder.Item(DisplayName(capturedWeapon), ctrl =>
            {
                SavePref(ctrl.Client, capturedTeam, capturedAlloc, capturedWeapon);
                ctrl.Next(nextMenu);
            });
        }
        builder.Item(Loc.Str(lm, client, "Retakes_Menu_Skip"), ctrl => ctrl.Next(nextMenu));
        return builder.Build();
    }

    private Menu BuildAwpMenu(IGameClient client)
    {
        var lm = _bridge.LocalizerManager;
        return Menu.Create()
            .Title(Loc.Str(lm, client, "Retakes_Menu_SniperPref"))
            .Item(Loc.Str(lm, client, "Retakes_Menu_RequestAwp"), ctrl =>
            {
                SavePref(ctrl.Client, CStrikeTeam.TE, WeaponAllocationType.Preferred, CsItem.AWP);
                Loc.Chat(_bridge.LocalizerManager, ctrl.Client, "Retakes_Alloc_PrefsSaved");
                ctrl.Exit();
            })
            .Item(Loc.Str(lm, client, "Retakes_Menu_NoPreferredSniper"), ctrl =>
            {
                ClearPref(ctrl.Client, CStrikeTeam.TE, WeaponAllocationType.Preferred);
                Loc.Chat(_bridge.LocalizerManager, ctrl.Client, "Retakes_Alloc_PrefsSaved");
                ctrl.Exit();
            })
            .ExitItem(Loc.Str(lm, client, "Retakes_Menu_SkipDone"))
            .Build();
    }

    // ── Vote menu ────────────────────────────────────────────────────────────

    private Menu BuildVoteMenu(IGameClient client)
    {
        var lm            = _bridge.LocalizerManager;
        var activePlayers = _queueModule.QueueManager.ActivePlayers.Count;

        return Menu.Create()
            .Title(Loc.Str(lm, client, "Retakes_Menu_VoteTitle"))
            .Item(Loc.Str(lm, client, "Retakes_RoundType_Pistol"), ctrl =>
            {
                _voteManager.CastVote((ulong)ctrl.Client.SteamId, RoundType.Pistol, activePlayers);
                Loc.Chat(_bridge.LocalizerManager, ctrl.Client, "Retakes_Alloc_VoteCast",
                    Loc.Str(_bridge.LocalizerManager, ctrl.Client, "Retakes_RoundType_Pistol"));
                ctrl.Exit();
            })
            .Item(Loc.Str(lm, client, "Retakes_RoundType_HalfBuy"), ctrl =>
            {
                _voteManager.CastVote((ulong)ctrl.Client.SteamId, RoundType.HalfBuy, activePlayers);
                Loc.Chat(_bridge.LocalizerManager, ctrl.Client, "Retakes_Alloc_VoteCast",
                    Loc.Str(_bridge.LocalizerManager, ctrl.Client, "Retakes_RoundType_HalfBuy"));
                ctrl.Exit();
            })
            .Item(Loc.Str(lm, client, "Retakes_RoundType_FullBuy"), ctrl =>
            {
                _voteManager.CastVote((ulong)ctrl.Client.SteamId, RoundType.FullBuy, activePlayers);
                Loc.Chat(_bridge.LocalizerManager, ctrl.Client, "Retakes_Alloc_VoteCast",
                    Loc.Str(_bridge.LocalizerManager, ctrl.Client, "Retakes_RoundType_FullBuy"));
                ctrl.Exit();
            })
            .ExitItem(Loc.Str(lm, client, "Retakes_Menu_Cancel"))
            .Build();
    }

    // ── DB helpers ────────────────────────────────────────────────────────────

    private static string DisplayName(CsItem item)
        => item.GetName().Replace("weapon_", "").Replace("_", " ").ToUpperInvariant();

    private void SavePref(IGameClient client, CStrikeTeam team, WeaponAllocationType allocType, CsItem weapon)
    {
        var steamId = (ulong)client.SteamId;
        var setting = _db.GetCachedUserSettings(steamId);
        setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(setting.WeaponPreferencesJson, team, allocType, weapon);
        _db.SetCachedWeaponPreference(steamId, setting.WeaponPreferencesJson);
    }

    private void ClearPref(IGameClient client, CStrikeTeam team, WeaponAllocationType allocType)
    {
        var steamId = (ulong)client.SteamId;
        var setting = _db.GetCachedUserSettings(steamId);
        setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(setting.WeaponPreferencesJson, team, allocType, null);
        _db.SetCachedWeaponPreference(steamId, setting.WeaponPreferencesJson);
    }
}
