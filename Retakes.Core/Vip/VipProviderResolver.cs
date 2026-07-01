using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Retakes.Shared;
using Sharp.Shared.Units;

namespace Retakes.Vip;

/// <summary>
/// DI-registered <see cref="IRetakesVipProvider"/> for Retakes.Core. Defaults to "nobody is VIP".
///
/// In <see cref="OnAllSharpModulesLoaded"/> it optionally looks up an externally-published
/// <see cref="IRetakesVipProvider"/> (identity <see cref="IRetakesVipProvider.Identity"/>) —
/// published by the separate, optional <c>Retakes.Vip</c> module, which bridges our house
/// <c>Vip.Shared</c> plugin. When found, every subsequent <see cref="IsVip"/> call delegates to it;
/// when absent (VIP-less server, or Retakes.Vip not deployed/loaded yet), it stays a no-op.
///
/// This is the consumer half of the publisher/consumer pair described on
/// <see cref="IRetakesVipProvider"/>. Everything inside Retakes.Core (allocator preferred-weapon
/// weighting, queue priority) takes <see cref="IRetakesVipProvider"/> via DI and never talks to
/// AdminManager or Vip.Shared directly — Core stays VIP-agnostic.
/// </summary>
internal sealed class VipProviderResolver : IRetakesVipProvider, IModule
{
    private readonly ILogger<VipProviderResolver> _logger;
    private readonly InterfaceBridge               _bridge;

    private IRetakesVipProvider? _external;

    public VipProviderResolver(ILogger<VipProviderResolver> logger, InterfaceBridge bridge)
    {
        _logger = logger;
        _bridge = bridge;
    }

    public bool IsVip(SteamID steamId) => _external?.IsVip(steamId) ?? false;

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        var iface = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IRetakesVipProvider>(IRetakesVipProvider.Identity);
        _external = iface?.Instance;

        _logger.LogInformation(_external is not null
            ? "[Retakes] External IRetakesVipProvider found — VIP weighting active."
            : "[Retakes] No external IRetakesVipProvider — running VIP-agnostic (nobody is VIP).");
    }

    public void Shutdown() { }
}
