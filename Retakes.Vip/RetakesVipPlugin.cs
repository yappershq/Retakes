using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Retakes.Shared;
using Sharp.Shared;
using Sharp.Shared.Units;
using Vip.Shared;

namespace Retakes.Vip;

/// <summary>
/// Optional third-party module: bridges our house VIP plugin (<see cref="IVipShared"/>) into
/// Retakes' VIP-agnostic <see cref="IRetakesVipProvider"/> contract.
///
/// Retakes.Core ships a no-op <c>DefaultVipProvider</c> (nobody VIP) and, in its own
/// OnAllModulesLoaded, optionally looks up an externally-published <see cref="IRetakesVipProvider"/>
/// via <see cref="IRetakesVipProvider.Identity"/> — preferring it over the default when present.
/// This plugin is that external publisher. Deploy it only on servers that also run the Vip plugin;
/// on a VIP-less server this module still loads (Vip.Shared is an optional lookup, never a hard ref
/// at runtime) and Retakes.Core silently keeps using DefaultVipProvider.
///
/// Lifecycle: resolve Vip.Shared's IVipShared (optional) in OnAllModulesLoaded, then publish our
/// own IRetakesVipProvider so Retakes.Core can pick it up. Both plugins finish PostInit before any
/// OnAllModulesLoaded runs, so publishing this late is safe — Retakes.Core re-checks the registry
/// for a VIP provider override in its own OnAllModulesLoaded, which ModSharp does not order against
/// this plugin's, so we publish eagerly here and Retakes.Core treats "not found yet" as
/// "stay on default" (no retry) — acceptable since load order is deploy-controlled and both
/// plugins are expected to be present together on a VIP-enabled server.
/// </summary>
public sealed class RetakesVipPlugin : IModSharpModule
{
    public string DisplayName   => "Retakes.Vip";
    public string DisplayAuthor => "yappershq";

    private readonly ISharedSystem            _sharedSystem;
    private readonly ILogger<RetakesVipPlugin> _logger;

    private IVipShared? _vip;

    public RetakesVipPlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(sharedSystem);
        _sharedSystem = sharedSystem;
        _logger       = sharedSystem.GetLoggerFactory().CreateLogger<RetakesVipPlugin>();
    }

    public bool Init() => true;

    public void PostInit() { }

    /// <summary>
    /// Resolve Vip.Shared (optional — absent on a VIP-less server) then publish our
    /// IRetakesVipProvider bridge so Retakes.Core can adopt it.
    /// </summary>
    public void OnAllModulesLoaded()
    {
        var manager = _sharedSystem.GetSharpModuleManager();

        _vip = manager.GetOptionalSharpModuleInterface<IVipShared>(IVipShared.Identity)?.Instance;

        if (_vip is null)
        {
            _logger.LogWarning(
                "[Retakes.Vip] IVipShared not found — Vip plugin not installed/loaded. " +
                "Retakes.Core will keep using its no-op DefaultVipProvider.");
            return;
        }

        manager.RegisterSharpModuleInterface<IRetakesVipProvider>(
            this, IRetakesVipProvider.Identity, new VipBridgeProvider(_vip));

        _logger.LogInformation("[Retakes.Vip] Published IRetakesVipProvider bridging IVipShared.");
    }

    public void Shutdown() { }

    /// <summary>Delegates IsVip(SteamID) straight to IVipShared.IsVip(ulong).</summary>
    private sealed class VipBridgeProvider(IVipShared vip) : IRetakesVipProvider
    {
        public bool IsVip(SteamID steamId) => vip.IsVip((ulong)steamId);
    }
}
