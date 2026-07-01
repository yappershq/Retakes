# Retakes port — STATUS

Overnight migration of cs2-retakes (+allocator/zones/defuse) to ModSharp. Design: docs/PORT_PLAN.md.
Sources cloned at /home/claude/retakes-research/. Repo: /home/claude/Retakes → PUBLISHED public:
https://github.com/yappershq/Retakes (branch main).

Build check: `cd /home/claude/Retakes && env -u version dotnet build -c Release`. Commit each green phase.

> **Honesty note (2026-07-01):** an earlier revision of this file claimed blanket "DONE / GREEN / full
> parity". That was overstated. The initial port compiled and shipped, but prefix's review + a 5-chunk
> hardening pass (**docs/HARDENING.md**) found and fixed a BLOCKER and a batch of real gaps (see below).
> The code now builds green and the fixed items are code-complete, but the runtime behaviours listed under
> **"Needs live testing"** have NOT been verified on a live server. Treat those as unproven until tested.

## Port phases (initial build — code-complete, compiles green)
- [x] **A. Scaffold** — Core/Shared skeleton, IModule+DI, EventBus/IRetakesService.
- [x] **B. Core spine** — config, spawn data, player lifecycle, queue/team, round flow, fallback alloc.
- [x] **C. Combat round** — breaker/announce/defuse/zones + synthetic auto-plant.
- [x] **D. Allocator** — round types, weapon/nade/armor alloc, weapon prefs, menus, voting, mid-round
      buy-block (PlayerCanAcquire hard-block).
- [x] **E. Spawn editor** — !showspawns/!edit, !add, !remove, !nearest, !hidespawns/!done, !mapconfig(s),
      !forcebombsite(stop), !scramble, !debugqueues. Viz = team-colored prop_dynamic + point_worldtext label.
- [x] **F. Review + publish** — anti-pattern pass; assets/README/map configs shipped; published to GitHub.

## Hardening pass (docs/HARDENING.md) — chunks 1–5, all code-complete + green
The initial port carried source-framework idioms and had real logic gaps. Fixed serially:

- **Chunk 1 — Queue/team + PlayerSlot/EntityIndex refactor.**
  - **BLOCKER fixed:** bots were added to ActivePlayers keyed by SteamID64 (=0 for bots) → every bot
    collapsed to key 0, so N bots counted as 1 active. Now keyed by **PlayerSlot** (`bool[64]`), evicted
    by slot on disconnect.
  - **Dead HookEvents fixed:** round/bomb/defuse events were `InstallEventListener`'d without matching
    `HookEvent` → `FireGameEvent` never fired. Added the `HookEvent` calls.
  - T-win no longer swaps both teams every round; added `BalanceTeams` deficit-fill. Queue priority +
    immunity ported. Win-streak scramble off-by-one fixed. Persistent per-player state (scores, zone
    states, voice-mute, votes) converted to slot-indexed + disconnect eviction.
- **Chunk 2 — Convars + spawns + Slay.**
  - `retakes.cfg` server convars now created/exec'd (`mp_give_player_c4 0` load-bearing — real C4 would
    otherwise collide with the synthetic auto-plant); mp_roundtime_defuse, mp_c4timer, mp_freezetime, etc.
  - Per-map nade budgets threaded (was passing `null` → always global); RoundTypeManager.Map re-init on
    map change.
  - Planter-spawn fallback when no CanBePlanter spawn on site (bomb could never plant otherwise); spawn
    overflow handled.
  - `PlayerLifecycleModule` `AcceptInput("Kill")` → `pawn.Slay()` (Kill destroys the pawn → "weird alive
    spectator"). EKV editor entities via SpawnEntitySync.
- **Chunk 3 — VIP as a separate module.** Core is now VIP-agnostic: `IRetakesVipProvider` in
  Retakes.Shared (default = nobody VIP). Separate optional **Retakes.Vip** implements it against our
  `Vip.Shared` `IVipService`; Vip.Shared contract shipped so VIP-less servers don't FileNotFound. Admins
  are NOT VIP (removed the `@retakes/vip` AdminManager check).
- **Chunk 4 — Announce + config parity.** Rich center-HTML bombsite announce via `PrintCenterHtml` (site
  image + live per-team counts + DEFEND/RETAKE variant, refreshed on a timer). `Loc.Center` no longer runs
  chat color escapes on the Center channel. Round-type chat/center broadcast to ALL + `.cfg execifexists`
  hook. ~13 dropped allocator/core config knobs restored; 4 flipped defaults corrected.
- **Chunk 5 — Zones edge-case + locale + docs (this pass).**
  - Zones: a dead/late/respawned player had no alive pawn at the one-shot `OnAnnounceBombsite` pass → got
    no zone assignment for the round. Now also assigned on `player_spawned` (resolved by slot), so late
    joiners and respawns are covered.
  - Locale audit: every `Loc.*` / localizer key referenced in code is present in
    `.assets/locales/retakes.json` (en-US only by design; community PRs add cultures). **Zero missing keys.**
  - This doc + PORT_PLAN.md corrected to real state.

## Convention-conformance sweep (chunk 5 review) — CLEAN
Grepped Retakes.Core + Retakes.Vip for source-framework tells; all clean:
- No `css_` in any command registration — all bare names (CommandCenter house convention).
- No hardcoded user-facing English in Print/PrintCenterHtml/chat — everything routes through `Loc.*`.
- No `.Next(` menu page-chaining — gun menu is a cached nested SubMenu tree.
- Every `IEventListener` module has matching `HookEvent` calls (no dead-event trap).
- `Dictionary<ulong>`/`HashSet<ulong>` hits are all method-local allocator scratch maps (rebuilt per
  allocation pass), NOT stored per-player state — legitimate.
- No `AcceptInput("Kill")` on a pawn (Slay everywhere). BreakerModule's `AcceptInput(action)` is a
  configured input on a breakable entity, and it stores `EntityIndex` + re-resolves via
  `FindEntityByIndex` — exemplary.
- No raw `IBaseEntity`/pawn stored across callbacks.

## Needs LIVE TESTING (NOT verified — do not assume working)
- **Auto-plant timer** — `planted_c4` countdown init unproven headless; `m_flC4Blow`/`m_flTimerLength`
  settable but the visible bomb-timer HUD + explosion timing need a live round. TerminateRound timer
  fallback is in place if the schema path misbehaves.
- **Center-HTML render** — the bombsite announce panel (site image `<img>` + live counts) renders only for
  a real human client's PVS; confirm layout/flicker/duration on a live client.
- **Spawn editor viz** — prop_dynamic + point_worldtext label look/placement, glow, in-bomb-zone
  auto-planter detection.
- **Gameplay feel** — zone bounce strength, queue/balance/scramble across real joins/leaves, allocator
  buy-block (PlayerCanAcquire hard-block), round-type voting, per-map nade budgets.
- Not deployed to any server yet.

## Notes / decisions
- Weapon prefs use **`IClientPreference` cookies** (commit 189662a) — the earlier `Retakes.Database` /
  SqlSugar / MySQL plan was dropped; that project no longer exists. Prefs survive reconnect via the cookie
  store, not an ORM.
- Entry ctor + IModule (Init/OnPostInit/OnAllSharpModulesLoaded/Shutdown) + DI fan-out mirror
  mmosystem/MonsterMod.
- EventBus published via `RegisterSharpModuleInterface<IRetakesService>(this, Identity, _bus)` in PostInit.
- Round events via GLOBAL `IEventListener.FireGameEvent` (filter by name) + `HookEvent` per event; typed:
  IEventRoundEnd(Winner), IEventBombPlanted(Controller,Site), IEventBombDefused, IEventPlayerDeath.
- NO `round_freeze_end` event in ModSharp — auto-plant timing uses `cs_round_start_beep`.

## Resume instructions
1. Read this file + docs/PORT_PLAN.md + docs/HARDENING.md.
2. `git log --oneline` for the last green commit; `env -u version dotnet build -c Release` to confirm.
3. The next real work is LIVE TESTING (see above) on a server, not more code — deploy and exercise a full
   round loop before claiming any runtime behaviour works.
