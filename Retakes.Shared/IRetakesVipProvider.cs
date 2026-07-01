using Sharp.Shared.Units;

namespace Retakes.Shared;

/// <summary>
/// Registerable VIP lookup so Retakes.Core stays VIP-agnostic. Retakes.Core ships a no-op
/// <c>DefaultVipProvider</c> (nobody is VIP) via DI by default.
///
/// The separate optional <c>Retakes.Vip</c> module bridges this against the house VIP plugin
/// (<c>Vip.Shared</c>'s <c>IVipShared</c>) and publishes an implementation via
/// <see cref="Sharp.Shared.Managers.ISharpModuleManager.RegisterSharpModuleInterface{T}"/> using
/// <see cref="Identity"/>. Retakes.Core optionally looks that up in its own
/// <c>OnAllModulesLoaded</c> and, when found, prefers it over the DI default — see
/// <c>Retakes.Plugins.VipProviderModule</c>.
///
/// Admins are NOT VIP — this is purely a VIP lookup, never backed by an admin permission check.
/// </summary>
public interface IRetakesVipProvider
{
    static string Identity => typeof(IRetakesVipProvider).FullName!;

    bool IsVip(SteamID steamId);
}
