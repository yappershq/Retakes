using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Retakes.Queue;
using Retakes.Shared;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace Retakes.Allocator;

/// <summary>
/// Phase D1 — real weapon / nade / armor allocator.
///
/// Subscribes to <see cref="EventBus.OnAllocate"/>; reads <see cref="EventBus.CurrentRoundType"/>
/// (set by RoundFlowModule before firing) to determine the economy tier.
///
/// Per-round flow (faithful port of OnRoundPostStartHelper):
///   1. Partition active players → T list / CT list.
///   2. Batch-load weapon prefs from DB (one WHERE-IN query).
///   3. Roll preferred-weapon chance (AWP queue); apply VIP weighting + per-team cap.
///   4. Build team nade pools via NadeHelpers; distribute round-robin.
///   5. For each alive active player: strip → armor → defuser → give weapons + nades + zeus.
///
/// DEFERRED to later phases:
///   - TODO D2: gun-selection menus (!guns / WASD center)
///   - TODO D2: round-type voting
///   - TODO D3: mid-round CanAcquire buy-blocking (CSS hook equiv unavailable; investigate per Phase D notes)
///   - TODO D2: in-round weapon-change via !guns after freeze time
/// </summary>
internal sealed class AllocatorModule : IModule
{
    private const string VipPermission = "retakes:vip";
    private const string ModuleIdentity = "Retakes";

    private readonly ILogger<AllocatorModule> _logger;
    private readonly InterfaceBridge          _bridge;
    private readonly ConfigModule             _config;
    private readonly QueueModule              _queueModule;
    private readonly EventBus                 _bus;
    private readonly WeaponPrefsStore         _prefsStore;
    private readonly RoundTypeManager         _roundTypeManager;

    private IAdminManager? _adminManager;
    private readonly Action _onAllocate;

    /// <summary>
    /// True while HandleAllocate() is running (cleared ~0.5s after allocation completes).
    /// BuyControlModule reads this to skip the CanAcquire block during server-side GiveNamedItem,
    /// guarding against engine builds where GiveNamedItem flows through CanAcquire.
    /// </summary>
    internal bool IsAllocating { get; private set; }

    public AllocatorModule(
        ILogger<AllocatorModule> logger,
        InterfaceBridge          bridge,
        ConfigModule             config,
        QueueModule              queueModule,
        EventBus                 bus,
        WeaponPrefsStore         prefsStore,
        RoundTypeManager         roundTypeManager)
    {
        _logger           = logger;
        _bridge           = bridge;
        _config           = config;
        _queueModule      = queueModule;
        _bus              = bus;
        _prefsStore       = prefsStore;
        _roundTypeManager = roundTypeManager;
        _onAllocate       = HandleAllocate;
    }

    // ── IModule lifecycle ──────────────────────────────────────────────────

    public bool Init()
    {
        // Set mp_max_armor 0 so the engine doesn't cap armor we give via ArmorValue.
        var mpMaxArmor = _bridge.ConVarManager.FindConVar("mp_max_armor");
        mpMaxArmor?.Set(0);
        return true;
    }

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        // Resolve IAdminManager (optional — VIP weighting degrades gracefully without it).
        var adminIface = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);
        _adminManager = adminIface?.Instance;

        if (_adminManager is not null)
        {
            // Register "retakes:vip" as a known permission so wildcard admins ("*") resolve it.
            _adminManager.MountAdminManifest(ModuleIdentity, () => new AdminTableManifest(
                PermissionCollection: new() { { VipPermission, new HashSet<string>() } },
                Roles:  [],
                Admins: []
            ));
        }

        _bus.OnAllocate += _onAllocate;
        _logger.LogInformation("[Retakes] AllocatorModule subscribed to OnAllocate.");
    }

    public void Shutdown()
    {
        _bus.OnAllocate -= _onAllocate;
    }

    // ── Allocation handler ─────────────────────────────────────────────────

    private void HandleAllocate()
    {
        if (!_config.Config.Allocator.Enabled) return;

        // Signal BuyControlModule to pass-through CanAcquire while we give weapons.
        IsAllocating = true;
        _bridge.ModSharp.PushTimer(
            () => { IsAllocating = false; },
            0.5,
            GameTimerFlags.StopOnMapEnd | GameTimerFlags.StopOnRoundEnd
        );

        var roundType = _bus.CurrentRoundType;
        var allocCfg  = _config.Config.Allocator;

        // ── 1. Partition active players by team ────────────────────────────
        var tIds  = new List<ulong>();
        var ctIds = new List<ulong>();

        foreach (var slot in _queueModule.QueueManager.ActiveSlots)
        {
            var client = _bridge.ClientManager.GetGameClient(slot);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;

            var steamId = (ulong)client.SteamId;
            if (controller.Team == CStrikeTeam.TE) tIds.Add(steamId);
            else if (controller.Team == CStrikeTeam.CT) ctIds.Add(steamId);
        }

        // ── 2. Read weapon prefs from the cookie cache (no I/O on game thread) ──────────────────────
        var allIds  = tIds.Concat(ctIds).ToList();
        var prefMap = allIds.ToDictionary(id => id, id => _prefsStore.GetJson(id));

        // ── 3. Preferred-weapon (AWP) queue ───────────────────────────────
        HashSet<ulong> tPreferred  = [];
        HashSet<ulong> ctPreferred = [];

        if (roundType == RoundType.FullBuy)
        {
            var roll = Random.Shared.NextDouble() * 100;
            if (roll <= allocCfg.ChanceForPreferredWeapon)
            {
                // Only players who have a Preferred weapon pref set are eligible
                tPreferred = WeaponHelpers.SelectPreferredPlayers(
                    tIds.Where(id => HasPreferredPref(prefMap, id, CStrikeTeam.TE)),
                    IsVip, CStrikeTeam.TE, allocCfg);
                ctPreferred = WeaponHelpers.SelectPreferredPlayers(
                    ctIds.Where(id => HasPreferredPref(prefMap, id, CStrikeTeam.CT)),
                    IsVip, CStrikeTeam.CT, allocCfg);
            }
        }

        // ── 4. Nade pools (team-level, then distribute per player) ─────────
        // Thread the real map name so per-map nade budgets resolve (falls back to GLOBAL inside
        // NadeHelpers when the map has no override). RoundTypeManager.Map is set on map change.
        var map     = _roundTypeManager.Map;
        var tNades  = NadeHelpers.GetUtilForTeam(map, roundType, CStrikeTeam.TE, tIds.Count, allocCfg);
        var ctNades = NadeHelpers.GetUtilForTeam(map, roundType, CStrikeTeam.CT, ctIds.Count, allocCfg);

        var nadesByPlayer = new Dictionary<ulong, List<CsItem>>();
        NadeHelpers.AllocateNadesToPlayers(tNades,  tIds,  nadesByPlayer);
        NadeHelpers.AllocateNadesToPlayers(ctNades, ctIds, nadesByPlayer);

        // One CT gets a defuser on pistol rounds; on non-pistol all CTs get one
        ulong? pistolDefuser = ctIds.Count > 0
            ? ctIds[Random.Shared.Next(ctIds.Count)]
            : null;

        // ── 5. Give loadout per player ─────────────────────────────────────
        var count = 0;
        foreach (var steamId in allIds)
        {
            var client = _bridge.ClientManager.GetGameClient((SteamID)steamId);
            if (client is not { IsInGame: true }) continue;
            var controller = client.GetPlayerController();
            if (controller is null || !controller.IsValid()) continue;
            var pawn = controller.GetPlayerPawn();
            if (pawn is null || !pawn.IsAlive) continue;

            var team   = controller.Team;
            prefMap.TryGetValue(steamId, out var prefsJson);
            var prefs  = WeaponPrefsHelper.GetAllPreferences(prefsJson, team);

            var givePreferred = team == CStrikeTeam.TE
                ? tPreferred.Contains(steamId)
                : ctPreferred.Contains(steamId);

            var isDefuser = roundType != RoundType.Pistol || pistolDefuser == steamId;

            nadesByPlayer.TryGetValue(steamId, out var playerNades);

            GiveLoadout(pawn, team, roundType, prefs, givePreferred, isDefuser, playerNades, allocCfg);
            count++;
        }

        _logger.LogInformation("[Retakes] Allocated {Count} player(s), round={RoundType}.", count, roundType);
    }

    // ── Loadout ────────────────────────────────────────────────────────────

    private static void GiveLoadout(
        Sharp.Shared.GameEntities.IPlayerPawn pawn,
        CStrikeTeam team,
        RoundType roundType,
        Dictionary<WeaponAllocationType, CsItem> prefs,
        bool givePreferred,
        bool isDefuser,
        List<CsItem>? nades,
        AllocatorSettings cfg)
    {
        // 1. Strip everything (removeSuit=true so we control ArmorValue ourselves)
        pawn.RemoveAllItems(removeSuit: true);

        // 2. Armor — pistol rounds give Kevlar only; buy rounds give Kevlar+Helmet
        pawn.ArmorValue = 100;
        var svc = pawn.GetItemService();
        if (svc is not null)
        {
            svc.HasHelmet = roundType != RoundType.Pistol;

            // 3. Defuser
            if (team == CStrikeTeam.CT && isDefuser)
                svc.HasDefuser = true;
        }

        // 4. Knife
        pawn.GiveNamedItem(team == CStrikeTeam.TE ? "weapon_knife_t" : "weapon_knife");

        // 5. Primary + secondary
        var weapons = WeaponHelpers.GetWeaponsForRoundType(roundType, team, prefs, givePreferred, cfg);
        foreach (var w in weapons)
            pawn.GiveNamedItem(w.GetName());

        // 6. Nades
        if (nades is not null)
            foreach (var n in nades)
                pawn.GiveNamedItem(n.GetName());

        // 7. Zeus
        if (cfg.ZeusPreference == ZeusPreference.Always)
            pawn.GiveNamedItem(CsItem.Zeus.GetName());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private bool IsVip(ulong steamId)
    {
        if (_adminManager is null) return false;
        var admin = _adminManager.GetAdmin((SteamID)steamId);
        return admin?.HasPermission(VipPermission) == true;
    }

    private static bool HasPreferredPref(
        Dictionary<ulong, string?> prefMap, ulong id, CStrikeTeam team)
    {
        prefMap.TryGetValue(id, out var prefsJson);
        return WeaponPrefsHelper.GetPreference(prefsJson, team, WeaponAllocationType.Preferred) is not null;
    }
}
