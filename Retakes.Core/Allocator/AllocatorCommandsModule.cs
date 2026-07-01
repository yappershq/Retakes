using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Database;
using Retakes.Database.Models;
using Retakes.Plugins;
using Retakes.Queue;
using Retakes.Shared;
using Sharp.Modules.CommandCenter.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Retakes.Allocator;

/// <summary>
/// Phase D2 — weapon-pref commands (!css_gun, !css_awp, !css_removegun),
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
            reg.RegisterClientCommand("guns",          OnGunsCommand);
            reg.RegisterClientCommand("css_gun",       OnCssGunCommand);
            reg.RegisterClientCommand("css_awp",       OnCssAwpCommand);
            reg.RegisterClientCommand("css_removegun", OnCssRemoveGunCommand);

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
            client.Print(HudPrintChannel.Chat, "[Retakes] Gun menu not available (MenuManager module missing).");
            return;
        }

        _menuManager.DisplayMenu(client, BuildGunMenuChain());
    }

    // ── !css_gun / !css_removegun ──────────────────────────────────────────

    private void OnCssGunCommand(IGameClient client, StringCommand cmd)
        => HandleWeaponCommand(client, cmd, remove: false);

    private void OnCssRemoveGunCommand(IGameClient client, StringCommand cmd)
        => HandleWeaponCommand(client, cmd, remove: true);

    private void HandleWeaponCommand(IGameClient client, StringCommand cmd, bool remove)
    {
        if (!client.IsInGame) return;

        if (!_config.Config.Allocator.CanPlayersSelectWeapons())
        {
            client.Print(HudPrintChannel.Chat, "[Retakes] Weapon selection is disabled.");
            return;
        }

        if (cmd.ArgCount < 1)
        {
            client.Print(HudPrintChannel.Chat, "[Retakes] Usage: !css_gun <weapon> [T|CT]");
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
                client.Print(HudPrintChannel.Chat, $"[Retakes] Invalid team '{cmd.GetArg(2)}'. Use T or CT.");
                return;
            }
        }
        else if (currentTeam is CStrikeTeam.TE or CStrikeTeam.CT)
        {
            team = currentTeam;
        }
        else
        {
            client.Print(HudPrintChannel.Chat, "[Retakes] Join a team first or specify T/CT.");
            return;
        }

        var weaponInput = cmd.GetArg(1).Trim().ToLowerInvariant();
        var weapon      = CsItemNames.TryGetFromName(weaponInput)
                       ?? CsItemNames.TryGetFromName("weapon_" + weaponInput);

        if (weapon is null)
        {
            client.Print(HudPrintChannel.Chat, $"[Retakes] Unknown weapon '{weaponInput}'.");
            return;
        }

        if (!WeaponHelpers.IsWeapon(weapon.Value))
        {
            client.Print(HudPrintChannel.Chat, $"[Retakes] '{weaponInput}' is not a selectable weapon.");
            return;
        }

        if (!_config.Config.Allocator.IsUsableWeapon(weapon.Value))
        {
            client.Print(HudPrintChannel.Chat, $"[Retakes] '{weaponInput}' is not allowed on this server.");
            return;
        }

        var allocType = WeaponHelpers.GetAllocationTypeForWeapon(team, weapon.Value);
        if (allocType is null)
        {
            client.Print(HudPrintChannel.Chat, $"[Retakes] '{weaponInput}' is not valid for team {team}.");
            return;
        }

        var steamId = (ulong)client.SteamId;
        var setting = _db.GetUserSettings(steamId) ?? new UserSetting { UserId = (long)steamId };

        string message;
        if (remove)
        {
            setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
                setting.WeaponPreferencesJson, team, allocType.Value, null);
            _db.SetWeaponPreference(steamId, setting.WeaponPreferencesJson);
            message = $"[Retakes] Removed {weapon.Value.GetName()} preference for {allocType.Value} ({team}).";
        }
        else
        {
            setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
                setting.WeaponPreferencesJson, team, allocType.Value, weapon.Value);
            _db.SetWeaponPreference(steamId, setting.WeaponPreferencesJson);
            message = $"[Retakes] Set {weapon.Value.GetName()} as your {allocType.Value} preference for {team}.";

            // Immediate re-give if in-round, team matches, and not a preferred (AWP-queue) weapon
            if (team == currentTeam && allocType.Value != WeaponAllocationType.Preferred)
                TryGiveImmediately(client, weapon.Value, allocType.Value);
        }

        client.Print(HudPrintChannel.Chat, message);
    }

    private void TryGiveImmediately(IGameClient client, CsItem weapon, WeaponAllocationType allocType)
    {
        if (!_config.Config.Allocator.AllowAllocationAfterFreezeTime) return;

        var gameRules = _bridge.ModSharp.GetGameRules();
        if (gameRules.IsFreezePeriod || gameRules.IsWarmupPeriod) return;

        var currentRoundType = _roundTypeManager.CurrentRoundType;
        if (currentRoundType is null) return;

        if (!WeaponHelpers.IsAllocationTypeValidForRound(allocType, currentRoundType.Value)) return;

        var controller = client.GetPlayerController();
        if (controller is null || !controller.IsValid()) return;
        var pawn = controller.GetPlayerPawn();
        if (pawn is null || !pawn.IsAlive) return;

        pawn.GiveNamedItem(weapon.GetName());
    }

    // ── !css_awp ────────────────────────────────────────────────────────────

    private void OnCssAwpCommand(IGameClient client, StringCommand _)
    {
        if (!client.IsInGame) return;

        var steamId = (ulong)client.SteamId;
        var setting = _db.GetUserSettings(steamId) ?? new UserSetting { UserId = (long)steamId };

        var currentAwpPref = WeaponPrefsHelper.GetPreference(setting, CStrikeTeam.TE, WeaponAllocationType.Preferred);

        if (currentAwpPref is not null)
        {
            setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
                setting.WeaponPreferencesJson, CStrikeTeam.TE, WeaponAllocationType.Preferred, null);
            _db.SetWeaponPreference(steamId, setting.WeaponPreferencesJson);
            client.Print(HudPrintChannel.Chat, "[Retakes] AWP preference removed — you will not be given a preferred sniper.");
        }
        else
        {
            setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
                setting.WeaponPreferencesJson, CStrikeTeam.TE, WeaponAllocationType.Preferred, CsItem.AWP);
            _db.SetWeaponPreference(steamId, setting.WeaponPreferencesJson);
            client.Print(HudPrintChannel.Chat, "[Retakes] AWP preference set — you will be considered for a preferred sniper.");
        }
    }

    // ── !nextround ──────────────────────────────────────────────────────────

    private void OnNextRoundCommand(IGameClient client, StringCommand _)
    {
        if (!client.IsInGame) return;
        if (_menuManager is null)
        {
            client.Print(HudPrintChannel.Chat, "[Retakes] Vote menu not available.");
            return;
        }
        _menuManager.DisplayMenu(client, BuildVoteMenu());
    }

    // ── Gun menu chain (built bottom-up) ────────────────────────────────────

    private Menu BuildGunMenuChain()
    {
        var cfg = _config.Config.Allocator;

        var awpMenu      = BuildAwpMenu();
        var ctHalfMenu   = BuildWeaponPageMenu("CT HalfBuy Primary",           CStrikeTeam.CT, WeaponAllocationType.HalfBuyPrimary,  cfg, awpMenu);
        var tHalfMenu    = BuildWeaponPageMenu("T HalfBuy Primary",             CStrikeTeam.TE, WeaponAllocationType.HalfBuyPrimary,  cfg, ctHalfMenu);
        var ctPistolMenu = BuildWeaponPageMenu("CT Pistol Round",               CStrikeTeam.CT, WeaponAllocationType.PistolRound,     cfg, tHalfMenu);
        var tPistolMenu  = BuildWeaponPageMenu("T Pistol Round",                CStrikeTeam.TE, WeaponAllocationType.PistolRound,     cfg, ctPistolMenu);
        var ctSecMenu    = BuildWeaponPageMenu("CT Secondary (Full/Half-buy)",  CStrikeTeam.CT, WeaponAllocationType.Secondary,       cfg, tPistolMenu);
        var tSecMenu     = BuildWeaponPageMenu("T Secondary (Full/Half-buy)",   CStrikeTeam.TE, WeaponAllocationType.Secondary,       cfg, ctSecMenu);
        var ctPrimMenu   = BuildWeaponPageMenu("CT Primary (Full-buy)",         CStrikeTeam.CT, WeaponAllocationType.FullBuyPrimary,  cfg, tSecMenu);
        return              BuildWeaponPageMenu("T Primary (Full-buy)",          CStrikeTeam.TE, WeaponAllocationType.FullBuyPrimary,  cfg, ctPrimMenu);
    }

    private Menu BuildWeaponPageMenu(
        string title, CStrikeTeam team, WeaponAllocationType allocType, AllocatorSettings cfg, Menu nextMenu)
    {
        var weapons = WeaponHelpers.GetPossibleWeaponsForAllocationType(allocType, team, cfg);
        var builder = Menu.Create().Title(title);
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
        builder.Item("Skip", ctrl => ctrl.Next(nextMenu));
        return builder.Build();
    }

    private Menu BuildAwpMenu()
    {
        return Menu.Create()
            .Title("Sniper Preference")
            .Item("Request AWP / Sniper (queue-based)", ctrl =>
            {
                SavePref(ctrl.Client, CStrikeTeam.TE, WeaponAllocationType.Preferred, CsItem.AWP);
                ctrl.Client.Print(HudPrintChannel.Chat, "[Retakes] Preferences saved!");
                ctrl.Exit();
            })
            .Item("No preferred sniper", ctrl =>
            {
                ClearPref(ctrl.Client, CStrikeTeam.TE, WeaponAllocationType.Preferred);
                ctrl.Client.Print(HudPrintChannel.Chat, "[Retakes] Preferences saved!");
                ctrl.Exit();
            })
            .ExitItem("Skip / Done")
            .Build();
    }

    // ── Vote menu ────────────────────────────────────────────────────────────

    private Menu BuildVoteMenu()
    {
        var activePlayers = _queueModule.QueueManager.ActivePlayers.Count;

        return Menu.Create()
            .Title("Vote: Next Round Type")
            .Item("Pistol", ctrl =>
            {
                _voteManager.CastVote((ulong)ctrl.Client.SteamId, RoundType.Pistol, activePlayers);
                ctrl.Client.Print(HudPrintChannel.Chat, "[Retakes] Vote cast: Pistol.");
                ctrl.Exit();
            })
            .Item("Half-Buy", ctrl =>
            {
                _voteManager.CastVote((ulong)ctrl.Client.SteamId, RoundType.HalfBuy, activePlayers);
                ctrl.Client.Print(HudPrintChannel.Chat, "[Retakes] Vote cast: Half-Buy.");
                ctrl.Exit();
            })
            .Item("Full-Buy", ctrl =>
            {
                _voteManager.CastVote((ulong)ctrl.Client.SteamId, RoundType.FullBuy, activePlayers);
                ctrl.Client.Print(HudPrintChannel.Chat, "[Retakes] Vote cast: Full-Buy.");
                ctrl.Exit();
            })
            .ExitItem("Cancel")
            .Build();
    }

    // ── DB helpers ────────────────────────────────────────────────────────────

    private static string DisplayName(CsItem item)
        => item.GetName().Replace("weapon_", "").Replace("_", " ").ToUpperInvariant();

    private void SavePref(IGameClient client, CStrikeTeam team, WeaponAllocationType allocType, CsItem weapon)
    {
        var steamId = (ulong)client.SteamId;
        var setting = _db.GetUserSettings(steamId) ?? new UserSetting { UserId = (long)steamId };
        setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(setting.WeaponPreferencesJson, team, allocType, weapon);
        _db.SetWeaponPreference(steamId, setting.WeaponPreferencesJson);
    }

    private void ClearPref(IGameClient client, CStrikeTeam team, WeaponAllocationType allocType)
    {
        var steamId = (ulong)client.SteamId;
        var setting = _db.GetUserSettings(steamId) ?? new UserSetting { UserId = (long)steamId };
        setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(setting.WeaponPreferencesJson, team, allocType, null);
        _db.SetWeaponPreference(steamId, setting.WeaponPreferencesJson);
    }
}
