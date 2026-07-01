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

    [JsonPropertyName("enable_next_round_type_voting")]
    public bool EnableNextRoundTypeVoting { get; set; } = true;

    [JsonPropertyName("allow_allocation_after_freeze_time")]
    public bool AllowAllocationAfterFreezeTime { get; set; } = false;

    // ── Helpers ───────────────────────────────────────────────────────────

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
