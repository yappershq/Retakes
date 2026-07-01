namespace Retakes.Allocator;

public enum WeaponAllocationType
{
    FullBuyPrimary,
    HalfBuyPrimary,
    Secondary,
    PistolRound,

    /// <summary>
    /// Preferred slot (e.g. AWP, auto-snipers).
    /// Only ever active on FullBuy rounds and subject to per-team caps + chance roll.
    /// </summary>
    Preferred,
}
