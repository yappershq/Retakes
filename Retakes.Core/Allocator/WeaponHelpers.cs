using Retakes.Shared;
using Sharp.Shared.Enums;

namespace Retakes.Allocator;

/// <summary>
/// Faithful port of RetakesAllocatorCore/WeaponHelpers.cs.
/// All weapon lists are explicit (no integer-range tricks from the CSS enum).
/// </summary>
public static class WeaponHelpers
{
    // ── Pistols ───────────────────────────────────────────────────────────────

    private static readonly HashSet<CsItem> _sharedPistols = new()
    {
        CsItem.Deagle, CsItem.P250, CsItem.CZ, CsItem.Dualies, CsItem.R8,
    };

    private static readonly HashSet<CsItem> _tPistols = new() { CsItem.Glock, CsItem.Tec9 };
    private static readonly HashSet<CsItem> _ctPistols = new() { CsItem.USPS, CsItem.P2000, CsItem.FiveSeven };

    private static readonly HashSet<CsItem> _pistolsForT = _sharedPistols.Concat(_tPistols).ToHashSet();
    private static readonly HashSet<CsItem> _pistolsForCt = _sharedPistols.Concat(_ctPistols).ToHashSet();

    // ── Mid-range (half-buy primaries) ────────────────────────────────────────

    private static readonly HashSet<CsItem> _sharedMidRange = new()
    {
        // SMGs
        CsItem.P90, CsItem.UMP45, CsItem.MP7, CsItem.Bizon, CsItem.MP5,
        // Shotguns
        CsItem.XM1014, CsItem.Nova,
        // Sniper
        CsItem.Scout,
    };

    private static readonly HashSet<CsItem> _tMidRange = new() { CsItem.Mac10, CsItem.SawedOff };
    private static readonly HashSet<CsItem> _ctMidRange = new() { CsItem.MP9, CsItem.MAG7 };

    private static readonly HashSet<CsItem> _midRangeForT = _sharedMidRange.Concat(_tMidRange).ToHashSet();
    private static readonly HashSet<CsItem> _midRangeForCt = _sharedMidRange.Concat(_ctMidRange).ToHashSet();

    // ── SMGs only (for random half-buy selection — excludes shotguns/Scout) ───
    // Mirrors the original's _maxSmgItemValue trick but explicit.

    private static readonly HashSet<CsItem> _smgsForT = new()
    {
        CsItem.P90, CsItem.UMP45, CsItem.MP7, CsItem.Bizon, CsItem.MP5, CsItem.Mac10,
    };

    private static readonly HashSet<CsItem> _smgsForCt = new()
    {
        CsItem.P90, CsItem.UMP45, CsItem.MP7, CsItem.Bizon, CsItem.MP5, CsItem.MP9,
    };

    // ── Full-buy primaries ────────────────────────────────────────────────────

    private static readonly HashSet<CsItem> _tRifles = new() { CsItem.AK47, CsItem.Galil, CsItem.Krieg };

    private static readonly HashSet<CsItem> _ctRifles = new()
    {
        CsItem.M4A1S, CsItem.M4A4, CsItem.Famas, CsItem.AUG,
    };

    private static readonly HashSet<CsItem> _heavys = new() { CsItem.M249, CsItem.Negev };

    private static readonly HashSet<CsItem> _fullBuyPrimaryForT = _tRifles.Concat(_heavys).ToHashSet();
    private static readonly HashSet<CsItem> _fullBuyPrimaryForCt = _ctRifles.Concat(_heavys).ToHashSet();

    // ── Preferred (AWP / auto-snipers) ────────────────────────────────────────

    private static readonly HashSet<CsItem> _sharedPreferred = new() { CsItem.AWP };
    private static readonly HashSet<CsItem> _tPreferred = new() { CsItem.AutoSniperT };
    private static readonly HashSet<CsItem> _ctPreferred = new() { CsItem.AutoSniperCT };

    private static readonly HashSet<CsItem> _preferredForT = _sharedPreferred.Concat(_tPreferred).ToHashSet();
    private static readonly HashSet<CsItem> _preferredForCt = _sharedPreferred.Concat(_ctPreferred).ToHashSet();
    private static readonly HashSet<CsItem> _allPreferred = _preferredForT.Concat(_preferredForCt).ToHashSet();

    // ── All-weapon set (for IsWeapon checks) ──────────────────────────────────
    // Grenades, knife, zeus are NOT here; they're utility.

    private static readonly HashSet<CsItem> _allWeapons =
        _pistolsForT.Concat(_pistolsForCt)
            .Concat(_midRangeForT).Concat(_midRangeForCt)
            .Concat(_fullBuyPrimaryForT).Concat(_fullBuyPrimaryForCt)
            .Concat(_allPreferred)
            .ToHashSet();

    private static readonly HashSet<CsItem> _allUtil = new()
    {
        CsItem.Flashbang, CsItem.HE, CsItem.Molotov, CsItem.Incendiary, CsItem.Smoke, CsItem.Decoy,
    };

    // ── Valid allocation types per round type ──────────────────────────────────

    private static readonly Dictionary<RoundType, HashSet<WeaponAllocationType>> _validTypesForRound = new()
    {
        { RoundType.Pistol,  new() { WeaponAllocationType.PistolRound } },
        { RoundType.HalfBuy, new() { WeaponAllocationType.Secondary, WeaponAllocationType.HalfBuyPrimary } },
        { RoundType.FullBuy, new() { WeaponAllocationType.Secondary, WeaponAllocationType.FullBuyPrimary, WeaponAllocationType.Preferred } },
    };

    // ── Valid weapons per team + allocation type ───────────────────────────────

    private static readonly Dictionary<CStrikeTeam, Dictionary<WeaponAllocationType, HashSet<CsItem>>>
        _validWeaponsByTeamAndType = new()
        {
            {
                CStrikeTeam.TE, new()
                {
                    { WeaponAllocationType.PistolRound,   _pistolsForT },
                    { WeaponAllocationType.Secondary,     _pistolsForT },
                    { WeaponAllocationType.HalfBuyPrimary, _midRangeForT },
                    { WeaponAllocationType.FullBuyPrimary, _fullBuyPrimaryForT },
                    { WeaponAllocationType.Preferred,     _preferredForT },
                }
            },
            {
                CStrikeTeam.CT, new()
                {
                    { WeaponAllocationType.PistolRound,   _pistolsForCt },
                    { WeaponAllocationType.Secondary,     _pistolsForCt },
                    { WeaponAllocationType.HalfBuyPrimary, _midRangeForCt },
                    { WeaponAllocationType.FullBuyPrimary, _fullBuyPrimaryForCt },
                    { WeaponAllocationType.Preferred,     _preferredForCt },
                }
            },
        };

    // ── Built-in default weapon names (weapon_* strings) ─────────────────────

    /// <summary>
    /// Default weapon entity names per team (CStrikeTeam.ToString()) + allocation type.
    /// Exposed for AllocatorConfig default-value initialisation.
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, string>> DefaultWeaponNames = new()
    {
        {
            CStrikeTeam.TE.ToString(), new()
            {
                { WeaponAllocationType.FullBuyPrimary.ToString(),  CsItem.AK47.GetName() },
                { WeaponAllocationType.HalfBuyPrimary.ToString(),  CsItem.Mac10.GetName() },
                { WeaponAllocationType.Secondary.ToString(),       CsItem.Deagle.GetName() },
                { WeaponAllocationType.PistolRound.ToString(),     CsItem.Glock.GetName() },
            }
        },
        {
            CStrikeTeam.CT.ToString(), new()
            {
                { WeaponAllocationType.FullBuyPrimary.ToString(),  CsItem.M4A1S.GetName() },
                { WeaponAllocationType.HalfBuyPrimary.ToString(),  CsItem.MP9.GetName() },
                { WeaponAllocationType.Secondary.ToString(),       CsItem.Deagle.GetName() },
                { WeaponAllocationType.PistolRound.ToString(),     CsItem.USPS.GetName() },
            }
        },
    };

    /// <summary>All non-utility weapon entity names (for UsableWeapons config default).</summary>
    public static readonly List<string> AllWeaponNames = _allWeapons.Select(w => w.GetName()).ToList();

    // ── Public API ────────────────────────────────────────────────────────────

    public static bool IsWeapon(CsItem item) => _allWeapons.Contains(item);

    public static bool IsAllocationTypeValidForRound(WeaponAllocationType allocType, RoundType roundType)
        => _validTypesForRound.TryGetValue(roundType, out var types) && types.Contains(allocType);

    public static bool IsPreferred(CStrikeTeam team, CsItem weapon) => team switch
    {
        CStrikeTeam.TE => _preferredForT.Contains(weapon),
        CStrikeTeam.CT => _preferredForCt.Contains(weapon),
        _              => false,
    };

    public static ICollection<CsItem> GetPossibleWeaponsForAllocationType(
        WeaponAllocationType allocType, CStrikeTeam team, AllocatorSettings config)
    {
        if (team != CStrikeTeam.TE && team != CStrikeTeam.CT) return [];
        return _validWeaponsByTeamAndType[team][allocType]
            .Where(config.IsUsableWeapon)
            .ToList();
    }

    /// <summary>
    /// Get the WeaponAllocationType for a weapon+team combination.
    /// A pistol lives in BOTH the PistolRound and Secondary sets, so when a <paramref name="round"/>
    /// is supplied we return the alloc type that is actually VALID for that round — otherwise a
    /// buy-round pistol would classify as PistolRound (first insertion) and get blocked/stripped
    /// (money loss) and prefs would save to the wrong slot. With no round we keep the first match.
    /// </summary>
    public static WeaponAllocationType? GetAllocationTypeForWeapon(CStrikeTeam team, CsItem weapon, RoundType? round = null)
    {
        if (team != CStrikeTeam.TE && team != CStrikeTeam.CT) return null;
        var byType = _validWeaponsByTeamAndType[team];
        WeaponAllocationType? firstMatch = null;
        foreach (var (allocType, set) in byType)
        {
            if (!set.Contains(weapon)) continue;
            firstMatch ??= allocType;
            if (round is null || IsAllocationTypeValidForRound(allocType, round.Value))
                return allocType;
        }
        return firstMatch;
    }

    /// <summary>Get all RoundTypes where the given allocation type is valid.</summary>
    public static IReadOnlyList<RoundType> GetRoundTypesForAllocationType(WeaponAllocationType allocType)
    {
        return _validTypesForRound
            .Where(kvp => kvp.Value.Contains(allocType))
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Coerce an auto-sniper to the team-appropriate variant.
    /// AWP is team-agnostic; auto-snipers have T/CT variants.
    /// Returns null for any non-preferred weapon.
    /// </summary>
    public static CsItem? CoercePreferredTeam(CsItem? item, CStrikeTeam team)
    {
        if (item is null || !_allPreferred.Contains(item.Value)) return null;
        if (team != CStrikeTeam.TE && team != CStrikeTeam.CT) return null;
        if (item == CsItem.AWP) return item;
        // Auto-snipers: coerce to team-appropriate variant
        return team == CStrikeTeam.TE ? CsItem.AutoSniperT : CsItem.AutoSniperCT;
    }

    /// <summary>
    /// Select which players get a preferred weapon this round.
    /// Applies VIP extra-chance weighting + per-team cap.
    /// </summary>
    public static HashSet<ulong> SelectPreferredPlayers(
        IEnumerable<ulong> eligibleIds,
        Func<ulong, bool> isVip,
        CStrikeTeam team,
        AllocatorSettings config)
    {
        if (config.AllowPreferredWeaponForEveryone) return eligibleIds.ToHashSet();

        var playersList = eligibleIds.ToList();

        var teamKey = team.ToString();
        if (config.MinPlayersPerTeamForPreferredWeapon.TryGetValue(teamKey, out var minPlayers))
        {
            if (playersList.Count < minPlayers) return [];
        }

        if (!config.MaxPreferredWeaponsPerTeam.TryGetValue(teamKey, out var maxPerTeam)) maxPerTeam = 1;
        if (maxPerTeam == 0) return [];

        var pool = new List<ulong>();
        foreach (var id in playersList)
        {
            if (config.NumberOfExtraVipChancesForPreferredWeapon == -1)
            {
                if (isVip(id)) pool.Add(id);
            }
            else
            {
                pool.Add(id);
                if (isVip(id))
                {
                    for (var i = 0; i < config.NumberOfExtraVipChancesForPreferredWeapon; i++)
                        pool.Add(id);
                }
            }
        }

        CollectionUtils.Shuffle(pool);
        return pool.Distinct().Take(maxPerTeam).ToHashSet();
    }

    /// <summary>
    /// Returns the weapons to give a player for this round type.
    /// <paramref name="userPrefs"/> is the already-deserialized team preferences (may be empty).
    /// </summary>
    public static ICollection<CsItem> GetWeaponsForRoundType(
        RoundType roundType,
        CStrikeTeam team,
        Dictionary<WeaponAllocationType, CsItem> userPrefs,
        bool givePreferred,
        AllocatorSettings config)
    {
        WeaponAllocationType? primaryAlloc = givePreferred
            ? WeaponAllocationType.Preferred
            : roundType switch
            {
                RoundType.HalfBuy => WeaponAllocationType.HalfBuyPrimary,
                RoundType.FullBuy => WeaponAllocationType.FullBuyPrimary,
                _                 => null,
            };

        var secondaryAlloc = roundType == RoundType.Pistol
            ? WeaponAllocationType.PistolRound
            : WeaponAllocationType.Secondary;

        var weapons = new List<CsItem>();

        var secondary = GetWeaponForAllocationType(secondaryAlloc, team, userPrefs, config);
        if (secondary is not null) weapons.Add(secondary.Value);

        if (primaryAlloc is not null)
        {
            var primary = GetWeaponForAllocationType(primaryAlloc.Value, team, userPrefs, config);
            if (primary is not null) weapons.Add(primary.Value);
        }

        return weapons;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static CsItem? GetWeaponForAllocationType(
        WeaponAllocationType allocType,
        CStrikeTeam team,
        Dictionary<WeaponAllocationType, CsItem> userPrefs,
        AllocatorSettings config)
    {
        CsItem? weapon = null;

        // 1. Player preference
        if (config.CanPlayersSelectWeapons() && userPrefs.TryGetValue(allocType, out var pref))
        {
            if (config.IsUsableWeapon(pref)) weapon = pref;
        }

        // 2. Random
        if (weapon is null && config.CanAssignRandomWeapons())
            weapon = GetRandomWeaponForAllocationType(allocType, team, config);

        // 3. Default
        if (weapon is null && config.CanAssignDefaultWeapons())
            weapon = GetDefaultWeaponForAllocationType(allocType, team, config);

        return weapon;
    }

    private static CsItem? GetDefaultWeaponForAllocationType(
        WeaponAllocationType allocType, CStrikeTeam team, AllocatorSettings config)
    {
        if (allocType == WeaponAllocationType.Preferred) return null;
        if (team != CStrikeTeam.TE && team != CStrikeTeam.CT) return null;

        var configDefault = config.GetDefaultWeaponName(team.ToString(), allocType);
        CsItem? fallback = DefaultWeaponNames.TryGetValue(team.ToString(), out var td)
            && td.TryGetValue(allocType.ToString(), out var fn)
            ? CsItemNames.TryGetFromName(fn)
            : null;

        CsItem? chosen = null;
        if (configDefault is not null) chosen = CsItemNames.TryGetFromName(configDefault);
        chosen ??= fallback;

        return chosen is not null && config.IsUsableWeapon(chosen.Value) ? chosen : null;
    }

    private static CsItem GetRandomWeaponForAllocationType(
        WeaponAllocationType allocType, CStrikeTeam team, AllocatorSettings config)
    {
        var pool = allocType switch
        {
            WeaponAllocationType.PistolRound  => team == CStrikeTeam.TE ? _pistolsForT : _pistolsForCt,
            WeaponAllocationType.Secondary    => team == CStrikeTeam.TE ? _pistolsForT : _pistolsForCt,
            WeaponAllocationType.HalfBuyPrimary => team == CStrikeTeam.TE ? (IEnumerable<CsItem>)_smgsForT : _smgsForCt,
            WeaponAllocationType.FullBuyPrimary => team == CStrikeTeam.TE ? _tRifles : _ctRifles,
            WeaponAllocationType.Preferred    => team == CStrikeTeam.TE ? _preferredForT : _preferredForCt,
            _                                 => _sharedPistols,
        };

        var usable = pool.Where(config.IsUsableWeapon).ToList();
        return usable.Count > 0 ? CollectionUtils.Choice(usable) : CsItem.Deagle;
    }
}
