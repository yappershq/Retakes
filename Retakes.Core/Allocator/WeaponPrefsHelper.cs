using System.Text.Json;
using Retakes.Database.Models;
using Sharp.Shared.Enums;

namespace Retakes.Allocator;

/// <summary>
/// Converts the raw JSON blob in <see cref="UserSetting.WeaponPreferencesJson"/> to/from
/// typed <see cref="CsItem"/> preferences without pulling ORM/SqlSugar types into the helper.
///
/// JSON shape: { "TE": { "FullBuyPrimary": "weapon_ak47" }, "CT": { "Secondary": "weapon_deagle" } }
/// Keys are CStrikeTeam.ToString() and WeaponAllocationType.ToString().
/// </summary>
internal static class WeaponPrefsHelper
{
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Get a single weapon preference from the JSON blob.</summary>
    public static CsItem? GetPreference(UserSetting? setting, CStrikeTeam team, WeaponAllocationType allocType)
    {
        if (setting is null) return null;
        var prefs = Deserialize(setting.WeaponPreferencesJson);
        var teamKey = team.ToString();
        if (!prefs.TryGetValue(teamKey, out var teamPrefs)) return null;
        if (!teamPrefs.TryGetValue(allocType.ToString(), out var weaponName)) return null;
        return CsItemNames.TryGetFromName(weaponName);
    }

    /// <summary>Get all preferences for a team from the JSON blob.</summary>
    public static Dictionary<WeaponAllocationType, CsItem> GetAllPreferences(
        UserSetting? setting, CStrikeTeam team)
    {
        var result = new Dictionary<WeaponAllocationType, CsItem>();
        if (setting is null) return result;
        var prefs = Deserialize(setting.WeaponPreferencesJson);
        var teamKey = team.ToString();
        if (!prefs.TryGetValue(teamKey, out var teamPrefs)) return result;
        foreach (var (typeKey, weaponName) in teamPrefs)
        {
            if (!Enum.TryParse<WeaponAllocationType>(typeKey, out var allocType)) continue;
            var item = CsItemNames.TryGetFromName(weaponName);
            if (item is not null) result[allocType] = item.Value;
        }
        return result;
    }

    /// <summary>Returns a new JSON blob with the preference set (or cleared when item is null).</summary>
    public static string SetPreference(string json, CStrikeTeam team, WeaponAllocationType allocType, CsItem? item)
    {
        var prefs = Deserialize(json);
        var teamKey = team.ToString();
        if (!prefs.ContainsKey(teamKey)) prefs[teamKey] = new();
        if (item is null)
            prefs[teamKey].Remove(allocType.ToString());
        else
            prefs[teamKey][allocType.ToString()] = item.Value.GetName();
        return JsonSerializer.Serialize(prefs);
    }

    private static Dictionary<string, Dictionary<string, string>> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json, _opts) ?? new(); }
        catch { return new(); }
    }
}
