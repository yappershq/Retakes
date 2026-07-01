using Retakes.Shared;
using Sharp.Shared.Units;

namespace Retakes.Vip;

internal sealed class DefaultVipProvider : IRetakesVipProvider
{
    public bool IsVip(SteamID steamId) => false;
}
