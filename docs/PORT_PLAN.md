# Retakes (ModSharp) — Port Plan & Design

Authoritative design doc. Migrates 4 CounterStrikeSharp (CSS) plugins into ONE ModSharp plugin, upstream-grade quality (no pointer-entity / manual-lifecycle anti-patterns), so ModSharp maintainers would accept it. **Public repo.**

## Sources being ported
- **b3none/cs2-retakes** (core, ~4k LOC) — bombsite round flow, queue/team mgmt, spawn teleport, auto-plant bomb, spawn editor, breakables, announce, fallback alloc, event bus.
- **yonilerner/cs2-retakes-allocator** (~6k LOC) — round types, weapon/nade/armor allocation, per-player prefs (DB), gun menus (chat + WASD center), round-type voting, mid-round CanAcquire buy-blocking, bombsite announce.
- **oscar-wos/Retakes-Zones** (~337 LOC) — runtime green/red AABB zones; bounce players who leave green / enter red. NOT an editor (no beams). Subscribes to AnnounceBombsite.
- **TICHOJEBEC-SK/cs2-RetakeDefuseFix** (~47 LOC) — on bomb_planted, give every CT a defuser. Folds into Core (not its own module).

## Target solution structure (mirror MonsterMod / TTT-private)
```
Retakes.slnx
Directory.Build.props        # net10.0; .build/modules|shared output; ModSharp.Sharp.Shared compile-only
.gitignore
Retakes.Shared/             # PUBLIC contract — NO ORM / 3rd-party types
Retakes.Core/               # IModSharpModule entry + all internal modules
Retakes.Vip/                # optional module: implements IRetakesVipProvider via Vip.Shared
.assets/                    # gamedata (if needed), configs.example, lang
docs/
```
> **As-built note:** the originally-planned `Retakes.Database` (SqlSugar prefs) was dropped — weapon prefs
> now use `IClientPreference` cookies (commit 189662a), so there is no ORM and no separate DB project. A
> `Retakes.Vip` module was added during hardening (docs/HARDENING.md chunk 3) to keep Core VIP-agnostic.
Why one plugin (not 4): originals couple via a CSS PluginCapability event bus; in-process we make that bus INTERNAL. Keep `Retakes.Shared` as a public `IRetakesService` so external plugins (custom HUD etc.) can still consume retake events — preserves the original extensibility.

## Retakes.Shared (public contract)
- `enum Bombsite { A = 0, B = 1 }`
- `enum RoundType { Pistol, HalfBuy, FullBuy }`
- `interface IRetakesService` (Identity const) — the event bus:
  - `event Action<Bombsite> OnAnnounceBombsite;`
  - `event Action OnAllocate;`  // fired post-round-start when weapons should be given
  - read-only round state: `Bombsite CurrentBombsite { get; }`, `RoundType CurrentRoundType { get; }`
- DTOs only if a consumer needs them. Spawn/Zone data stays internal to Core. **No SqlSugar/EF types here.**

## Retakes.Core modules (house `IModule`: Init / OnPostInit / OnAllSharpModulesLoaded / Shutdown; static `InterfaceBridge` holds managers; DI via Microsoft.Extensions.DependencyInjection)
1. **ConfigModule** — load `appsettings`→config; hot-reloadable.
2. **PlayerLifecycleModule** — connect/disconnect/spawn/death/team via framework client lifecycle. **Key all player sets by SteamID64 or slot, NEVER store IGameClient/pawn across rounds** (re-resolve in callbacks).
3. **QueueModule** (QueueManager + GameManager) — active/queue sets, max players, priority/immunity bump, spectator eject, team balance (SwitchTeam preserves score / ChangeTeam resets), win-streak scramble.
4. **SpawnModule** (SpawnManager + MapConfigService) — load per-map spawn JSON; partition by [Bombsite][Team]; per-round teleport (planter spawn first). Reuse CSS JSON format verbatim.
5. **RoundFlowModule** (RoundEventHandlers) — orchestrates round prestart/start/poststart/freezeend/end + bomb events; selects bombsite; fires OnAnnounceBombsite + OnAllocate on the bus.
6. **BombModule** (BombService) — auto-plant: create `planted_c4`, set schema, fire bomb_planted. **HIGH RISK** — verify schema fields via mcp__modsharp__get_schema_class("CPlantedC4") + the fire-event API; if not feasible, fall back to give-planter-bomb mode.
7. **BreakerModule** — round-start break func_breakable / open prop_door_rotating / kill func_button via safe entity enumeration + AcceptInput. Map-specific (nuke/vertigo/mirage).
8. **AnnouncementModule** — bombsite chat/center/voice announce + per-player `!voices` mute.
9. **DefuseModule** — bomb_planted → give every kit-less CT `item_defuser` (the DefuseFix port).
10. **ZonesModule** — load zone JSON per map; on OnAnnounceBombsite assign per-player zones; per-frame (PlayerPostThink / frame action) AABB check → bounce (SetAbsVelocity reversal + up-kick via Teleport/ApplyAbsVelocityImpulse).
11. **AllocatorModule** (+ RoundTypeManager, WeaponHelpers, NadeHelpers, menus, votes, CanAcquire) — the heavy one; consumes OnAllocate. Prefs via `IClientPreference` cookies (as-built; the `Retakes.Database` SqlSugar plan below was dropped). See allocator notes below.
12. **SpawnEditorModule** — `!showspawns/!add/!remove/!nearest/!done`; viz via prop_dynamic + point_worldtext (editor-only; degraded viz acceptable).
13. **EventBusModule** — implements + publishes `IRetakesService` (RegisterSharpModuleInterface in PostInit).

## Allocator specifics
- Round types: Random(weighted)/RandomFixedCounts/ManualOrdering + admin override + vote. `RoundTypeManager` singleton.
- Give items: `pawn.GiveNamedItem("weapon_ak47" | "item_assaultsuit" | "weapon_taser" | …)`. Armor: `pawn.ArmorValue = N` + `pawn.GetItemService().HasHelmet = true`. Defuser: `pawn.GetItemService().HasDefuser = true` (schema, not give). Money: not set (core handles). `mp_max_armor 0` via convar.
- Nade allocation: team budget (MaxTeamNades) + per-type caps (MaxNades), round-robin to shuffled team.
- Preferred (AWP) queue: chance roll + VIP weighting + per-team cap.
- Prefs (**as-built**): `IClientPreference` cookies keyed by SteamID64 — no ORM, no DB project. (The
  original plan — a `Retakes.Database` SqlSugar table `retakes_user_settings` batch-loaded per round — was
  dropped in commit 189662a in favour of the cookie store.)
- CanAcquire buy-blocking: **HARDEST.** CSS hooks native `CCSPlayer_ItemServices_CanAcquire`. Investigate ModSharp options in order: (a) an item-pickup/purchase forward we can block via HookFireEvent→false; (b) convar-based weapon restriction; (c) gamedata signature + detour. Pick the cleanest; if none clean, ship allocator without hard buy-blocking v1 and note it.
- Menus: `IMenuManager` fluent (chat). WASD center menu: port via frame action + button polling, or use IMenuManager center menu if available.

## Key ModSharp APIs (verified by cookbook)
- Give item: `IPlayerPawn.GiveNamedItem(string)`.
- Teleport: `IBaseEntity.Teleport(Vector? pos, Vector? angles, Vector? velocity)`. Velocity: `GetAbsVelocity/SetAbsVelocity`, `ApplyAbsVelocityImpulse` (bare `AbsVelocity` is `[Obsolete]`).
- Per-tick: `InvokeFrameAction` / `PushTimer` / `PlayerPreThink|PostThink` forwards (no CSS OnTick).
- Events: typed forwards on `IHookManager` (PlayerSpawn/Killed) vs named events (bomb_planted/round_start/item_pickup) via `IEventListener`; block with `HookFireEvent`→false.
- Money: `controller.GetInGameMoneyService().Account`. Armor: `pawn.ArmorValue`. Helmet/defuser: `pawn.GetItemService().HasHelmet/HasDefuser`.
- Team/respawn: `controller.SwitchTeam(CStrikeTeam.TE)`, `controller.Respawn()`, teleport BEFORE respawn.
- Entity: `EntityManager.SpawnEntitySync(classname, keyValues)` (already dispatches — DON'T double-dispatch). Beam: spawn `"beam"` + `SetNetVar("m_vecEndPos")`.
- Menus: `IMenuManager` fluent `Menu.Create()...Build()`. Prefs: `IClientPreference` cookies (light) / own DB (structured).
- Lifecycle: entry ctor `(ISharedSystem, string dllPath, string sharpPath, Version, IConfiguration, bool hotReload)`. Publish `RegisterSharpModuleInterface<T>(this, T.Identity, impl)` in PostInit; consume `GetOptionalSharpModuleInterface<T>(T.Identity)` in OnAllModulesLoaded (cache wrapper).

## Anti-pattern checklist (verified in the hardening convention sweep — docs/HARDENING.md chunk 5)
- [x] No CSS APIs (AddTimer/RegisterEventHandler/GetPlayers/PlayerPawn.Value) — all nonexistent in ModSharp.
- [x] No storing IGameClient/pawn pointers across callbacks/rounds — re-resolve by slot/SteamID64.
- [x] No raw `PointerTo<T>` entity dereference — IEntityManager safe enumeration; entities held as `EntityIndex`.
- [x] No direct Health writes for damage.
- [x] No ORM / 3rd-party types in Retakes.Shared (TypeLoad).
- [x] Locale colors `{{double-brace}}`.
- [x] No SpawnEntitySync double-dispatch.
- [x] Publishers PostInit, consumers OnAllModulesLoaded.
- [x] No `css_` command prefixes; all user-facing text via ILocalizerManager; no `.Next()` menu chaining;
      every `IEventListener` has matching `HookEvent`.

## Spawn data model (reuse CSS format)
`map_config/<map>.json`: `{ "Spawns": [ { "Vector":"X Y Z", "QAngle":"P Y R", "Team":2|3, "Bombsite":0|1, "CanBePlanter":bool } ] }`. Team 2=T,3=CT. At least one CanBePlanter T spawn per site. 11 shipped configs to copy from cs2-retakes.

## Zone data model (reuse Retakes-Zones format)
`zones/<map>.json`: `{ "a":[JsonZone], "b":[JsonZone] }`, JsonZone `{ "type":0|1, "teams":[2,3], "x":[x,y,z], "y":[x,y,z] }`. type 0=green(must stay in),1=red(must stay out).

## Phase plan (each phase builds GREEN before next)
- **A. Scaffold** — repo, slnx, Directory.Build.props, .gitignore, 3 projects, Core entry + InterfaceBridge + IModule + DI, Shared contract stub, configs.example. Empty build green.
- **B. Core spine** — ConfigModule, PlayerLifecycle, Queue/Game, Spawn (data+teleport), RoundFlow, fallback alloc, EventBus/IRetakesService. Green.
- **C. Combat round** — Bomb autoplant, Breaker, Announcement, Defuse, Zones. Green.
- **D. Allocator** — round types, weapon/nade/armor alloc, prefs DB, menus, votes, CanAcquire. Green.
- **E. Spawn editor.** Green.
- **F. Review** — anti-pattern pass, README, configs, lang, gamedata. Green + reviewed.

Status tracked in docs/STATUS.md.
