namespace Retakes.Allocator;

/// <summary>
/// CS2 item identifiers used by the allocator.
/// Replaces CounterStrikeSharp's CsItem enum — no integer range checks here;
/// membership is explicit via the per-category collections in WeaponHelpers.
/// </summary>
public enum CsItem
{
    // ── Pistols (T-side) ──────────────────────────────────────────────────
    Glock,
    Tec9,

    // ── Pistols (CT-side) ─────────────────────────────────────────────────
    USPS,
    P2000,
    FiveSeven,

    // ── Shared pistols ────────────────────────────────────────────────────
    Deagle,
    P250,
    CZ,
    Dualies,
    R8,

    // ── SMGs (shared) ─────────────────────────────────────────────────────
    P90,
    UMP45,
    MP7,
    Bizon,
    MP5,

    // ── SMGs (T-side) ─────────────────────────────────────────────────────
    Mac10,

    // ── SMGs (CT-side) ────────────────────────────────────────────────────
    MP9,

    // ── Shotguns (shared) ─────────────────────────────────────────────────
    XM1014,
    Nova,

    // ── Shotguns (T-side) ─────────────────────────────────────────────────
    SawedOff,

    // ── Shotguns (CT-side) ────────────────────────────────────────────────
    MAG7,

    // ── Sniper (mid-range) ────────────────────────────────────────────────
    Scout,

    // ── Rifles (T-side) ───────────────────────────────────────────────────
    AK47,
    Galil,
    Krieg,

    // ── Rifles (CT-side) ──────────────────────────────────────────────────
    M4A1S,
    M4A4,
    Famas,
    AUG,

    // ── Heavy ─────────────────────────────────────────────────────────────
    M249,
    Negev,

    // ── Preferred / Snipers ───────────────────────────────────────────────
    AWP,
    AutoSniperT,   // G3SG1
    AutoSniperCT,  // SCAR-20

    // ── Grenades ──────────────────────────────────────────────────────────
    Flashbang,
    HE,
    Smoke,
    Molotov,
    Incendiary,
    Decoy,

    // ── Utility ───────────────────────────────────────────────────────────
    Zeus,

    // ── Knives ────────────────────────────────────────────────────────────
    DefaultKnifeT,
    DefaultKnifeCT,
}

/// <summary>Maps CsItem enum values to their CS2 "weapon_*" / "item_*" entity name strings.</summary>
public static class CsItemNames
{
    private static readonly Dictionary<CsItem, string> _names = new()
    {
        // Pistols
        { CsItem.Glock,         "weapon_glock" },
        { CsItem.Tec9,          "weapon_tec9" },
        { CsItem.USPS,          "weapon_usp_silencer" },
        { CsItem.P2000,         "weapon_hkp2000" },
        { CsItem.FiveSeven,     "weapon_fiveseven" },
        { CsItem.Deagle,        "weapon_deagle" },
        { CsItem.P250,          "weapon_p250" },
        { CsItem.CZ,            "weapon_cz75a" },
        { CsItem.Dualies,       "weapon_elite" },
        { CsItem.R8,            "weapon_revolver" },
        // SMGs
        { CsItem.P90,           "weapon_p90" },
        { CsItem.UMP45,         "weapon_ump45" },
        { CsItem.MP7,           "weapon_mp7" },
        { CsItem.Bizon,         "weapon_bizon" },
        { CsItem.MP5,           "weapon_mp5sd" },
        { CsItem.Mac10,         "weapon_mac10" },
        { CsItem.MP9,           "weapon_mp9" },
        // Shotguns
        { CsItem.XM1014,        "weapon_xm1014" },
        { CsItem.Nova,          "weapon_nova" },
        { CsItem.SawedOff,      "weapon_sawedoff" },
        { CsItem.MAG7,          "weapon_mag7" },
        // Scout
        { CsItem.Scout,         "weapon_ssg08" },
        // Rifles
        { CsItem.AK47,          "weapon_ak47" },
        { CsItem.Galil,         "weapon_galil" },
        { CsItem.Krieg,         "weapon_sg556" },
        { CsItem.M4A1S,         "weapon_m4a1_silencer" },
        { CsItem.M4A4,          "weapon_m4a1" },
        { CsItem.Famas,         "weapon_famas" },
        { CsItem.AUG,           "weapon_aug" },
        // Heavy
        { CsItem.M249,          "weapon_m249" },
        { CsItem.Negev,         "weapon_negev" },
        // Preferred
        { CsItem.AWP,           "weapon_awp" },
        { CsItem.AutoSniperT,   "weapon_g3sg1" },
        { CsItem.AutoSniperCT,  "weapon_scar20" },
        // Grenades
        { CsItem.Flashbang,     "weapon_flashbang" },
        { CsItem.HE,            "weapon_hegrenade" },
        { CsItem.Smoke,         "weapon_smokegrenade" },
        { CsItem.Molotov,       "weapon_molotov" },
        { CsItem.Incendiary,    "weapon_incgrenade" },
        { CsItem.Decoy,         "weapon_decoy" },
        // Utility
        { CsItem.Zeus,          "weapon_taser" },
        // Knives
        { CsItem.DefaultKnifeT,  "weapon_knife_t" },
        { CsItem.DefaultKnifeCT, "weapon_knife" },
    };

    private static readonly Dictionary<string, CsItem> _byName =
        _names.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Returns the CS2 entity name for this item (e.g. "weapon_ak47").</summary>
    public static string GetName(this CsItem item)
        => _names.TryGetValue(item, out var name) ? name : item.ToString().ToLower();

    /// <summary>Reverse-lookup: entity name → CsItem, or null if not found.</summary>
    public static CsItem? TryGetFromName(string name)
        => _byName.TryGetValue(name, out var item) ? item : null;
}
