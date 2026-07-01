using System.Collections.Frozen;

namespace Retakes.Allocator;

/// <summary>
/// Maps CS2 CEconItemView ItemDefinitionIndex values to <see cref="CsItem"/>.
///
/// Used by the PlayerCanAcquire hook to determine weapon validity without a server-side
/// native lookup (ModSharp does not expose EconItemDefinitionsById in the shared API).
///
/// Indices are stable across CS2 patches; this table covers all buyable weapons.
/// Knives (indices 500-526, 42, 59) and grenades are absent — they are passed through
/// as "unknown" and treated as allowed by default in the acquire hook.
///
/// PROVEN: indices match CS2 items.txt and are consistent with CSS RetakesAllocator,
/// which uses GetCSWeaponDataFromKey internally to resolve the same mapping.
/// </summary>
internal static class ItemDefIndexLookup
{
    // pontail: frozen at module init, no per-call allocation
    private static readonly FrozenDictionary<ushort, CsItem> _map =
        new Dictionary<ushort, CsItem>
        {
            // ── Pistols ─────────────────────────────────────────────────────
            { 1,  CsItem.Deagle },
            { 2,  CsItem.Dualies },
            { 3,  CsItem.FiveSeven },
            { 4,  CsItem.Glock },
            { 30, CsItem.Tec9 },
            { 32, CsItem.P2000 },
            { 36, CsItem.P250 },
            { 61, CsItem.USPS },
            { 63, CsItem.CZ },
            { 64, CsItem.R8 },
            // ── SMGs ─────────────────────────────────────────────────────────
            { 17, CsItem.Mac10 },
            { 19, CsItem.P90 },
            { 23, CsItem.MP5 },
            { 24, CsItem.UMP45 },
            { 26, CsItem.Bizon },
            { 33, CsItem.MP7 },
            { 34, CsItem.MP9 },
            // ── Shotguns ─────────────────────────────────────────────────────
            { 25, CsItem.XM1014 },
            { 27, CsItem.MAG7 },
            { 29, CsItem.SawedOff },
            { 35, CsItem.Nova },
            // ── Rifles ───────────────────────────────────────────────────────
            { 7,  CsItem.AK47 },
            { 8,  CsItem.AUG },
            { 10, CsItem.Famas },
            { 13, CsItem.Galil },
            { 16, CsItem.M4A4 },
            { 39, CsItem.Krieg },
            { 60, CsItem.M4A1S },
            // ── Snipers ──────────────────────────────────────────────────────
            { 9,  CsItem.AWP },
            { 11, CsItem.AutoSniperT },
            { 38, CsItem.AutoSniperCT },
            { 40, CsItem.Scout },
            // ── Heavy ────────────────────────────────────────────────────────
            { 14, CsItem.M249 },
            { 28, CsItem.Negev },
            // ── Zeus ─────────────────────────────────────────────────────────
            { 31, CsItem.Zeus },
        }.ToFrozenDictionary();

    /// <summary>
    /// Returns null for unknowns (grenades, knives, etc.) — caller should treat unknown as "allow".
    /// </summary>
    public static CsItem? TryGet(ushort index)
        => _map.TryGetValue(index, out var item) ? item : null;
}
