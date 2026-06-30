using SqlSugar;

namespace Retakes.Database.Models;

/// <summary>
/// Per-player weapon preference store.
/// <see cref="UserId"/> is the SteamID64 stored as <c>long</c> (ulong→long at the CLR boundary).
/// </summary>
[SugarTable("retakes_user_settings")]
public sealed class UserSetting
{
    [SugarColumn(IsPrimaryKey = true)]
    public long UserId { get; set; }

    /// <summary>JSON-serialised weapon preferences blob (populated by AllocatorModule in Phase D).</summary>
    [SugarColumn(ColumnDataType = "text")]
    public string WeaponPreferencesJson { get; set; } = "{}";
}
