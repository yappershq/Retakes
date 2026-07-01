using Sharp.Shared.Units;

namespace Retakes.Shared;

/// <summary>
/// Registerable VIP lookup so Retakes.Core stays VIP-agnostic. A separate optional
/// module (future Retakes.Vip, CHUNK 3) implements this against the house VIP service.
/// Default (no provider registered) = nobody is VIP.
/// </summary>
public interface IRetakesVipProvider
{
    bool IsVip(SteamID steamId);
}
