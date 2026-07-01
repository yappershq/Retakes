using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Sharp.Modules.ClientPreferences.Shared;
using Sharp.Shared.Units;

namespace Retakes.Allocator;

/// <summary>
/// Resolves the optional <see cref="IClientPreference"/> cookie service once and exposes a
/// simple get/set of the raw weapon-preferences JSON blob per player.
///
/// Cookie key: "retakes_weapon_prefs". Value is the same JSON blob shape consumed by
/// <see cref="WeaponPrefsHelper"/> — { "TE": { "FullBuyPrimary": "weapon_ak47" }, ... }.
///
/// The cookie system caches in-memory after <see cref="IClientPreference.IsLoaded"/>, so reads
/// are safe on the game thread with no async prefetch/eviction needed.
/// When IClientPreference isn't installed, Get returns null (== no prefs) and Set is a no-op —
/// logged once, matching AnnouncementModule's optional-service degrade pattern.
/// </summary>
internal sealed class WeaponPrefsStore : IModule
{
    private const string CookieKey = "retakes_weapon_prefs";

    private readonly ILogger<WeaponPrefsStore> _logger;
    private readonly InterfaceBridge           _bridge;

    private IClientPreference? _clientPreference;

    public WeaponPrefsStore(ILogger<WeaponPrefsStore> logger, InterfaceBridge bridge)
    {
        _logger = logger;
        _bridge = bridge;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        _clientPreference = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IClientPreference>(IClientPreference.Identity)
            ?.Instance;

        if (_clientPreference is null)
            _logger.LogInformation("[Retakes] WeaponPrefsStore: IClientPreference not available, weapon preferences will not persist.");
    }

    public void Shutdown() { }

    // ── cookie access ───────────────────────────────────────────────────────

    /// <summary>Get the raw weapon-preferences JSON blob for a player, or null if unset/unavailable.</summary>
    public string? GetJson(ulong steamId)
    {
        if (_clientPreference is null) return null;
        var cookie = _clientPreference.GetCookie((SteamID)steamId, CookieKey);
        return cookie?.GetString();
    }

    /// <summary>Persist the raw weapon-preferences JSON blob for a player. No-op when unavailable.</summary>
    public void SetJson(ulong steamId, string json)
        => _clientPreference?.SetCookie((SteamID)steamId, CookieKey, json);
}
