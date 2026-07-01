using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Database;
using Retakes.Database.Models;
using Retakes.Plugins;
using Retakes.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace Retakes.Allocator;

/// <summary>
/// Phase D3 — mid-round buy control, preference saving, and stray-weapon cleanup.
///
/// OPTION A selected: IHookManager.PlayerCanAcquire is a first-class ModSharp hook for
/// CCSPlayer_ItemServices::CanAcquire. It fires BEFORE the purchase completes; returning
/// EHookAction.SkipCallReturnOverride + EAcquireResult.AlreadyOwned is a true hard block
/// (the player never receives the weapon, no money is deducted).
///
/// The hook needs ItemDefinitionIndex → CsItem to check validity; ModSharp does not expose
/// EconItemDefinitionsById in the shared API, so we use a static lookup table (ItemDefIndexLookup).
/// For unknown indices the hook passes through and the item_purchase fallback layer handles it.
///
/// item_purchase POST listener (fallback + preference save):
///   - Valid buy that passed CanAcquire → save as the player's weapon preference.
///   - Invalid buy that slipped through (unknown def-index) → strip from pawn.
///   - ALL buys → schedule stray-weapon cleanup 0.5s later (matches CSS behaviour).
///
/// LIMITATION vs CSS original:
///   - CanAcquire hard-blocks only weapons in ItemDefIndexLookup; unknown weapons get a
///     post-purchase strip instead. In practice the table covers every buyable CS2 weapon,
///     so this is equivalent for all normal play.
///   - No money refund on invalid buys (same as CSS: AlreadyOwned response leaves money
///     deducted in the rare no-refund case, but in retakes money is reset each round).
/// </summary>
internal sealed class BuyControlModule : IModule, IEventListener
{
    private readonly ILogger<BuyControlModule> _logger;
    private readonly InterfaceBridge           _bridge;
    private readonly ConfigModule              _config;
    private readonly EventBus                  _bus;
    private readonly RetakesDatabase           _db;
    private readonly AllocatorModule           _allocatorModule;

    // Cached delegate for symmetric Install/Remove
    private readonly Func<IPlayerCanAcquireHookParams,
                          HookReturnValue<EAcquireResult>,
                          HookReturnValue<EAcquireResult>> _canAcquirePre;

    // ── IEventListener identity ────────────────────────────────────────────
    int IEventListener.ListenerVersion  => IEventListener.ApiVersion;
    int IEventListener.ListenerPriority => 0;

    public BuyControlModule(
        ILogger<BuyControlModule> logger,
        InterfaceBridge            bridge,
        ConfigModule               config,
        EventBus                   bus,
        RetakesDatabase            db,
        AllocatorModule            allocatorModule)
    {
        _logger          = logger;
        _bridge          = bridge;
        _config          = config;
        _bus             = bus;
        _db              = db;
        _allocatorModule = allocatorModule;
        _canAcquirePre   = OnCanAcquirePre;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init() => true;

    public void OnPostInit()
    {
        _bridge.HookManager.PlayerCanAcquire.InstallHookPre(_canAcquirePre);
        _bridge.EventManager.HookEvent("item_purchase");
        _bridge.EventManager.InstallEventListener(this);
        _logger.LogInformation("[Retakes] BuyControlModule: PlayerCanAcquire hook + item_purchase listener installed.");
    }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerCanAcquire.RemoveHookPre(_canAcquirePre);
        _bridge.EventManager.RemoveEventListener(this);
    }

    // ── PlayerCanAcquire hook (Option A — hard block, PRE) ─────────────────

    private HookReturnValue<EAcquireResult> OnCanAcquirePre(
        IPlayerCanAcquireHookParams         p,
        HookReturnValue<EAcquireResult>     current)
    {
        static HookReturnValue<EAcquireResult> Allow()
            => new(EHookAction.Ignored, EAcquireResult.Allowed);

        static HookReturnValue<EAcquireResult> Block()
            => new(EHookAction.SkipCallReturnOverride, EAcquireResult.AlreadyOwned);

        // Pickups (ground, player-to-player) — never restricted.
        if (p.Method == EAcquireMethod.PickUp) return Allow();

        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod) return Allow();   // warmup: open market
        if (rules.IsFreezePeriod)                  return Allow();   // allocation phase

        // Guard: server-side GiveNamedItem may flow through CanAcquire in some engine versions;
        // skip our own allocation so we don't accidentally block the round loadout.
        if (_allocatorModule.IsAllocating) return Allow();

        if (!_config.Config.Allocator.Enabled) return Allow();

        // Resolve ItemDefinitionIndex → CsItem; unknown = allow (item_purchase fallback covers it).
        var csItem = ItemDefIndexLookup.TryGet(p.ItemDefinitionIndex);
        if (csItem is null) return Allow();

        // Zeus — exempt only when config says "always give zeus".
        if (csItem == CsItem.Zeus)
        {
            return _config.Config.Allocator.ZeusPreference == ZeusPreference.Always
                ? Allow()
                : Block();
        }

        // Non-weapon (shouldn't reach here via buy, but be safe).
        if (!WeaponHelpers.IsWeapon(csItem.Value)) return Allow();

        var team      = p.Controller.Team;
        var roundType = _bus.CurrentRoundType;

        // Preferred weapons (AWP / auto-snipers): block mid-round buys.
        // Allocation-time give already handles preferred via RNG/pref queue at round start.
        if (WeaponHelpers.IsPreferred(team, csItem.Value)) return Block();

        var allocType = WeaponHelpers.GetAllocationTypeForWeapon(team, csItem.Value);
        var isValid   = allocType is not null
                     && WeaponHelpers.IsAllocationTypeValidForRound(allocType.Value, roundType);

        return isValid ? Allow() : Block();
    }

    // ── IEventListener: item_purchase POST ────────────────────────────────

    // HookFireEvent has a default impl (return true) — we never block item_purchase.
    void IEventListener.FireGameEvent(IGameEvent @event)
    {
        if (@event is not IEventItemPurchased evt) return;
        HandleItemPurchase(evt);
    }

    private void HandleItemPurchase(IEventItemPurchased evt)
    {
        var controller = evt.Controller;
        if (controller is null || !controller.IsValid()) return;

        var rules = _bridge.ModSharp.GetGameRules();
        if (rules is null || rules.IsWarmupPeriod) return;

        if (!_config.Config.Allocator.Enabled) return;

        var pawn = controller.GetPlayerPawn();

        // ── Stray-weapon cleanup (runs for ALL purchases, valid or not) ────
        // Mirrors CSS: any unclaimed weapon within 30u of the buy position is removed 0.5s later.
        if (pawn is not null && pawn.IsAlive)
        {
            // Capture Vector (value type, no stale-pointer risk) before the timer fires.
            var playerPos = pawn.GetAbsOrigin();
            _bridge.ModSharp.PushTimer(
                () => CleanupStrayWeapons(playerPos),
                0.5,
                GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd
            );
        }

        // ── Weapon resolution ──────────────────────────────────────────────
        // item_purchase "weapon" field may be "ak47" (short) or "weapon_ak47" (full classname).
        // Try both; CsItemNames only knows the "weapon_*" form.
        var weaponStr = evt.Weapon;
        var csItem    = CsItemNames.TryGetFromName(weaponStr)
                     ?? CsItemNames.TryGetFromName("weapon_" + weaponStr);

        if (csItem is null || !WeaponHelpers.IsWeapon(csItem.Value)) return;

        var team      = controller.Team;
        var roundType = _bus.CurrentRoundType;
        var allocType = WeaponHelpers.GetAllocationTypeForWeapon(team, csItem.Value);
        var isValid   = allocType is not null
                     && WeaponHelpers.IsAllocationTypeValidForRound(allocType.Value, roundType);
        var isPreferred = WeaponHelpers.IsPreferred(team, csItem.Value);

        var steamId = (ulong)controller.SteamId;

        if (!isPreferred && isValid && allocType is not null)
        {
            // ── Valid buy: save as new weapon preference ────────────────────
            SavePreference(steamId, team, allocType.Value, csItem.Value);
        }
        else if (pawn is not null && pawn.IsAlive)
        {
            // ── Invalid buy that CanAcquire missed (unknown def-index): strip ─
            StripInvalidWeapon(pawn, csItem.Value);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void SavePreference(ulong steamId, CStrikeTeam team, WeaponAllocationType allocType, CsItem weapon)
    {
        // Synchronous single-row read (same pattern as AllocatorCommandsModule).
        var setting = _db.GetUserSettings(steamId) ?? new UserSetting { UserId = (long)steamId };
        setting.WeaponPreferencesJson = WeaponPrefsHelper.SetPreference(
            setting.WeaponPreferencesJson, team, allocType, weapon);
        _db.SetWeaponPreference(steamId, setting.WeaponPreferencesJson);

        _logger.LogDebug("[Retakes][BuyControl] Saved pref: {SteamId} {Team} {Type} = {Weapon}",
            steamId, team, allocType, weapon);
    }

    private void StripInvalidWeapon(IPlayerPawn pawn, CsItem purchased)
    {
        var targetName = purchased.GetName();

        // Check primary then secondary slot for the invalid weapon.
        foreach (var slot in new[] { GearSlot.Rifle, GearSlot.Pistol })
        {
            var w = pawn.GetWeaponBySlot(slot);
            if (w is null || !w.IsValid()) continue;
            if (!w.Classname.Equals(targetName, StringComparison.OrdinalIgnoreCase)) continue;

            pawn.RemovePlayerItem(w);
            _logger.LogDebug("[Retakes][BuyControl] Stripped invalid buy: {Weapon} (CanAcquire miss)", purchased);
            return;
        }
    }

    private void CleanupStrayWeapons(Vector playerPos)
    {
        const float MaxDistSqr = 30f * 30f; // 30 units radius, squared avoids MathF.Sqrt

        IBaseEntity? ent = null;
        var removed = 0;

        // Fresh enumeration inside the timer — no captured entity pointers (stale-pointer safe).
        while ((ent = _bridge.EntityManager.FindEntityByClassname(ent, "weapon_*")) is not null)
        {
            if (!ent.IsValid()) continue;
            if (ent.Classname.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)) continue;
            if (ent.OwnerEntity is not null) continue;  // picked up by someone

            var distSqr = (ent.GetAbsOrigin() - playerPos).LengthSqr();
            if (distSqr >= MaxDistSqr) continue;

            ent.Kill();
            removed++;
        }

        if (removed > 0)
            _logger.LogDebug("[Retakes][BuyControl] Cleaned up {N} stray weapon(s) near buy point.", removed);
    }
}
