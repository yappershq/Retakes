using System.Text.Json.Serialization;
using Retakes.Shared;
using Sharp.Shared.Enums;

namespace Retakes.Allocator;

// ── Enums (port of CSS ConfigData/NadeHelpers enums) ──────────────────────────

public enum WeaponSelectionType { PlayerChoice, Random, Default }
public enum RoundTypeSelectionOption { Random, RandomFixedCounts, ManualOrdering }
public enum ZeusPreference { Never, Always }

public enum MaxTeamNadesSetting
{
    None, One, Two, Three, Four, Five, Six, Seven, Eight, Nine, Ten,
    AveragePointFivePerPlayer, AverageOnePerPlayer, AverageOnePointFivePerPlayer, AverageTwoPerPlayer,
}

public record RoundTypeManualOrderingItem(RoundType Type, int Count);

// ── AllocatorSettings ─────────────────────────────────────────────────────────

/// <summary>
/// Allocator-specific configuration section — mirrors the upstream ConfigData record.
/// Serialized under "allocator" in retakes.jsonc.
/// </summary>
public class AllocatorSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    // ── Weapon selection ──────────────────────────────────────────────────

    [JsonPropertyName("allowed_weapon_selection_types")]
    public List<WeaponSelectionType> AllowedWeaponSelectionTypes { get; set; } =
        Enum.GetValues<WeaponSelectionType>().ToList();

    [JsonPropertyName("usable_weapons")]
    public List<string> UsableWeapons { get; set; } = WeaponHelpers.AllWeaponNames;

    [JsonPropertyName("default_weapons")]
    public Dictionary<string, Dictionary<string, string>> DefaultWeapons { get; set; } =
        WeaponHelpers.DefaultWeaponNames;

    // ── Round type selection ───────────────────────────────────────────────

    [JsonPropertyName("round_type_selection")]
    public RoundTypeSelectionOption RoundTypeSelection { get; set; } = RoundTypeSelectionOption.Random;

    [JsonPropertyName("round_type_percentages")]
    public Dictionary<RoundType, int> RoundTypePercentages { get; set; } = new()
    {
        { RoundType.Pistol,  15 },
        { RoundType.HalfBuy, 25 },
        { RoundType.FullBuy, 60 },
    };

    [JsonPropertyName("round_type_random_fixed_counts")]
    public Dictionary<RoundType, int> RoundTypeRandomFixedCounts { get; set; } = new()
    {
        { RoundType.Pistol,  5 },
        { RoundType.HalfBuy, 10 },
        { RoundType.FullBuy, 15 },
    };

    [JsonPropertyName("round_type_manual_ordering")]
    public List<RoundTypeManualOrderingItem> RoundTypeManualOrdering { get; set; } =
    [
        new(RoundType.Pistol,  5),
        new(RoundType.HalfBuy, 10),
        new(RoundType.FullBuy, 15),
    ];

    // ── Nade settings ─────────────────────────────────────────────────────

    /// <summary>
    /// Per-map, per-team, per-type nade count cap.
    /// Key: map name (or "GLOBAL"), then CStrikeTeam enum name, then CsItem weapon name.
    /// </summary>
    [JsonPropertyName("max_nades")]
    public Dictionary<string, Dictionary<string, Dictionary<string, int>>> MaxNades { get; set; } =
        new()
        {
            {
                NadeHelpers.GlobalSettingName, new()
                {
                    {
                        CStrikeTeam.TE.ToString(), new()
                        {
                            { CsItem.Flashbang.GetName(), 2 },
                            { CsItem.Smoke.GetName(),     1 },
                            { CsItem.Molotov.GetName(),   1 },
                            { CsItem.HE.GetName(),        1 },
                        }
                    },
                    {
                        CStrikeTeam.CT.ToString(), new()
                        {
                            { CsItem.Flashbang.GetName(),  2 },
                            { CsItem.Smoke.GetName(),      1 },
                            { CsItem.Incendiary.GetName(), 2 },
                            { CsItem.HE.GetName(),         1 },
                        }
                    },
                }
            }
        };

    /// <summary>
    /// Per-map, per-team, per-round-type team nade budget.
    /// Key: map name (or "GLOBAL"), then CStrikeTeam enum name, then RoundType.
    /// </summary>
    [JsonPropertyName("max_team_nades")]
    public Dictionary<string, Dictionary<string, Dictionary<RoundType, MaxTeamNadesSetting>>> MaxTeamNades { get; set; } =
        new()
        {
            {
                NadeHelpers.GlobalSettingName, new()
                {
                    {
                        CStrikeTeam.TE.ToString(), new()
                        {
                            { RoundType.Pistol,  MaxTeamNadesSetting.AverageOnePerPlayer },
                            { RoundType.HalfBuy, MaxTeamNadesSetting.AverageOnePointFivePerPlayer },
                            { RoundType.FullBuy, MaxTeamNadesSetting.AverageOnePointFivePerPlayer },
                        }
                    },
                    {
                        CStrikeTeam.CT.ToString(), new()
                        {
                            { RoundType.Pistol,  MaxTeamNadesSetting.AverageOnePerPlayer },
                            { RoundType.HalfBuy, MaxTeamNadesSetting.AverageOnePointFivePerPlayer },
                            { RoundType.FullBuy, MaxTeamNadesSetting.AverageOnePointFivePerPlayer },
                        }
                    },
                }
            }
        };

    // ── Preferred weapon (AWP) settings ──────────────────────────────────

    [JsonPropertyName("chance_for_preferred_weapon")]
    public double ChanceForPreferredWeapon { get; set; } = 100;

    [JsonPropertyName("allow_preferred_weapon_for_everyone")]
    public bool AllowPreferredWeaponForEveryone { get; set; } = false;

    [JsonPropertyName("max_preferred_weapons_per_team")]
    public Dictionary<string, int> MaxPreferredWeaponsPerTeam { get; set; } = new()
    {
        { CStrikeTeam.TE.ToString(), 1 },
        { CStrikeTeam.CT.ToString(), 1 },
    };

    [JsonPropertyName("min_players_per_team_for_preferred")]
    public Dictionary<string, int> MinPlayersPerTeamForPreferredWeapon { get; set; } = new()
    {
        { CStrikeTeam.TE.ToString(), 1 },
        { CStrikeTeam.CT.ToString(), 1 },
    };

    [JsonPropertyName("number_of_extra_vip_chances_for_preferred")]
    public int NumberOfExtraVipChancesForPreferredWeapon { get; set; } = 1;

    // ── Zeus ─────────────────────────────────────────────────────────────

    [JsonPropertyName("zeus_preference")]
    public ZeusPreference ZeusPreference { get; set; } = ZeusPreference.Never;

    // ── D2 features ───────────────────────────────────────────────────────

    // Source default = false (Configs.cs:246). Voting off unless an operator opts in.
    [JsonPropertyName("enable_next_round_type_voting")]
    public bool EnableNextRoundTypeVoting { get; set; } = false;

    // Source default = true (Configs.cs:234). Allows the allocator to (re-)give weapons after
    // freeze time ends, i.e. mid-round !guns re-allocation; gates BuyControlModule's post-freeze give.
    [JsonPropertyName("allow_allocation_after_freeze_time")]
    public bool AllowAllocationAfterFreezeTime { get; set; } = true;

    // ── Round-type announcement (Configs.cs:237-238) ──────────────────────

    [JsonPropertyName("enable_round_type_announcement")]
    public bool EnableRoundTypeAnnouncement { get; set; } = true;

    [JsonPropertyName("enable_round_type_announcement_center")]
    public bool EnableRoundTypeAnnouncementCenter { get; set; } = false;

    // ── Bombsite center/chat announcement (Configs.cs:236, 239-245) ────────

    // Master toggle for the rich center-HTML bombsite announce (site image + live team counts).
    [JsonPropertyName("enable_bomb_site_announcement_center")]
    public bool EnableBombSiteAnnouncementCenter { get; set; } = false;

    // When center-announce is on, show it only to CTs (retakers) — Ts get nothing.
    [JsonPropertyName("bomb_site_announcement_center_to_ct_only")]
    public bool BombSiteAnnouncementCenterToCtOnly { get; set; } = false;

    // Suppress the engine's default "Bomb has been planted at site X" center message.
    [JsonPropertyName("disable_default_bomb_planted_center_message")]
    public bool DisableDefaultBombPlantedCenterMessage { get; set; } = false;

    // When the bomb is planted, immediately stop the center-HTML announce refresh loop.
    [JsonPropertyName("force_close_bomb_site_announcement_center_on_plant")]
    public bool ForceCloseBombSiteAnnouncementCenterOnPlant { get; set; } = true;

    // Delay (seconds) after the planter spawns before the center-HTML announce starts.
    [JsonPropertyName("bomb_site_announcement_center_delay")]
    public float BombSiteAnnouncementCenterDelay { get; set; } = 1.0f;

    // How long (seconds) the center-HTML announce stays on screen (refreshed each tick to keep counts live).
    [JsonPropertyName("bomb_site_announcement_center_show_timer")]
    public float BombSiteAnnouncementCenterShowTimer { get; set; } = 5.0f;

    // ASCII-art bombsite lines printed to chat (source default off).
    [JsonPropertyName("enable_bomb_site_announcement_chat")]
    public bool EnableBombSiteAnnouncementChat { get; set; } = false;

    // ── CanAcquire buy-block hook (Configs.cs:264) ────────────────────────

    // Master switch for the mid-round CanAcquire buy-blocking hook. Opt-out-able per source.
    [JsonPropertyName("enable_can_acquire_hook")]
    public bool EnableCanAcquireHook { get; set; } = true;

    // ── Skin support (Configs.cs:236) ─────────────────────────────────────

    // Source gives allocated weapons via a paint-capable path (WeaponPaints/skins plugin capability).
    // ModSharp has no equivalent capability API; this flag is a documented no-op — allocated weapons
    // are given via GiveNamedItem, which a skins plugin can still decorate on its own item hooks.
    [JsonPropertyName("capability_weapon_paints")]
    public bool CapabilityWeaponPaints { get; set; } = true;

    // ── Menu triggers (Configs.cs:270-273) ────────────────────────────────

    // Comma-separated chat/console triggers that open the WASD/center gun menu.
    [JsonPropertyName("in_game_gun_menu_center_commands")]
    public string InGameGunMenuCenterCommands { get; set; } =
        "gunsmenu,gunmenu,gunsmenu,menugun,menuguns";

    // Comma-separated chat triggers that open the chat-based !guns menu.
    [JsonPropertyName("in_game_gun_menu_chat_commands")]
    public string InGameGunMenuChatCommands { get; set; } = "guns";

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Parse a comma-separated command trigger string into bare command names (css_/!/ stripped).</summary>
    public static IEnumerable<string> ParseMenuTriggers(string commands)
        => commands.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(c => c.TrimStart('!', '/'))
                   .Where(c => c.Length > 0)
                   .Distinct(StringComparer.OrdinalIgnoreCase);


    public double GetRoundTypePercentage(RoundType roundType)
        => Math.Round(RoundTypePercentages.GetValueOrDefault(roundType, 0) / 100.0, 2);

    public bool CanPlayersSelectWeapons()
        => AllowedWeaponSelectionTypes.Contains(WeaponSelectionType.PlayerChoice);

    public bool CanAssignRandomWeapons()
        => AllowedWeaponSelectionTypes.Contains(WeaponSelectionType.Random);

    public bool CanAssignDefaultWeapons()
        => AllowedWeaponSelectionTypes.Contains(WeaponSelectionType.Default);

    /// <summary>Returns true if weaponName is in the usable weapons list.</summary>
    public bool IsUsableWeapon(string weaponName)
        => UsableWeapons.Contains(weaponName);

    /// <summary>Returns true if item is in the usable weapons list.</summary>
    public bool IsUsableWeapon(CsItem item)
        => IsUsableWeapon(item.GetName());

    /// <summary>Gets the configured default weapon name for team/allocationType, or null.</summary>
    public string? GetDefaultWeaponName(string team, WeaponAllocationType allocationType)
    {
        if (!DefaultWeapons.TryGetValue(team, out var teamDefaults)) return null;
        return teamDefaults.TryGetValue(allocationType.ToString(), out var w) ? w : null;
    }
}
