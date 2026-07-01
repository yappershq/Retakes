# Retakes hardening pass — fix queue (2026-07-01)

After the port shipped, prefix reviewed and flagged a batch of "safe but not house-idiomatic" issues + real gaps. This is the authoritative fix list. Apply serially (all touch Retakes.Core — no parallel agents on the same project), build-verify each, commit, push. A 5-lens review workflow (`wrl7gz7r4`) + completeness audit (`afd87cf`) are producing additional verified findings to fold in.

## Direct directives from prefix (must-do)
1. **PlayerSlot arrays + entity lifecycle.** Convert all EPHEMERAL per-player state from `Dictionary<ulong>`/`HashSet<ulong>` to **`PlayerSlot`-indexed arrays** (`[64]`), cleared on disconnect. Keep SteamID64 ONLY where state must survive a map change / reconnect (after prefs→cookies, basically nothing). Add **`IEntityListener` (OnEntityCreated/OnEntityDestroyed)** + `EntityIndex` for entity lifecycle; evict per-player/entity state on disconnect + map change. Affected: QueueManager, GameManager scores, ZonesModule `_playerStates`, AnnouncementModule `_voicesMuted`, NextRoundVoteManager `_votes`, AllocatorModule preferred sets.
2. **VIP as a separate third-party module (Core stays VIP-agnostic).** Not everyone has our VIP. In `Retakes.Shared` define `IRetakesVipProvider` (registerable `bool IsVip(SteamID)` hook); default = nobody VIP (or a config-permission fallback). The allocator's preferred-weapon VIP weighting asks the provider. New SEPARATE optional module **`Retakes.Vip`** implements the provider against our `Vip.Shared` `IVipService` and registers it (ship the Vip.Shared contract so VIP-less servers don't FileNotFound — [[feedback_hextags_needs_vipshared_on_vipless_server]]). **Admins are NOT VIP** — remove the `@retakes/vip` AdminManager check; VIP is purely the provider.
3. **`Slay()` not `AcceptInput("Kill")`.** `PlayerLifecycleModule.cs:186` uses `pawn.AcceptInput("Kill")` which DESTROYS the pawn entity (→ the "weird alive spectator" the source guards against). Original used `CommitSuicide(false,true)`. ModSharp equivalent = `pawn.Slay(bool explode=false)`. RoundFlowModule already uses `.Slay()` — make it consistent everywhere.
4. **`PrintCenterHtml` for center announce.** AnnouncementModule does chat only; the allocator's rich center-HTML bombsite announce (site image + team counts) wasn't ported. Use `IGameClient.PrintCenterHtml(msg, duration)` / `ClientManager.PrintCenterHtmlToAll(...)` (confirmed in current Sharp.Shared).

## Known completeness gaps (confirm/expand via audit `afd87cf`)
- Round-type `.cfg` execs + the shipped cfgs (Pistol/SmallBuy/FullBuy or cs2-retakes/*.cfg) — `execifexists` per round type.
- `retakes.cfg` server convars (mp_roundtime_defuse, mp_c4timer, mp_freezetime, mp_give_player_c4 0, …) — is it created/shipped?
- Per-map nade settings — D1 left `NadeHelpers.GetUtilForTeam(map=null)` → always GLOBAL. Pass the current map name.
- Bombsite-announce sounds / VO paths.
- Any allocator/core config key dropped from RetakesConfig/AllocatorConfig.

## From the review workflow (`wrl7gz7r4`) — fold in confirmed findings
logic flaws · misused/deprecated APIs · edge cases · lifecycle idioms · concurrency. Ranked list returned by the workflow.

## Process (avoid the original mistake)
Per [[feedback_porting_carries_source_idioms]]: encode conventions in the spec AND diff against a reference house plugin (mmosystem/TTT/MixScrims), not just the source. Build-verify the whole solution after each fix; commit small. Note: a concurrent session has been committing to this repo — `git fetch`/status before each commit to stay in sync.

---

## Consolidated findings → 5 sequential fix chunks (workflow `wrl7gz7r4` + audit `afd87cf`)

Each chunk = one agent, build-verify, commit, push. Serial (all touch Retakes.Core).

### CHUNK 1 — Queue/team correctness + PlayerSlot/EntityIndex refactor
- **BLOCKER** PlayerLifecycleModule.cs:176 — bots added to ActivePlayers with unguarded `AddToActive((ulong)controller.SteamId)`; bot SteamID64=0 → all bots collapse to key 0 in `HashSet<ulong>` → N bots = 1 active. Corrupts GetTargetNumT/CT, fill cap, scramble/balance; never evicted. FIX: key ephemeral active/queue by **PlayerSlot** (`bool[64]`/`HashSet<PlayerSlot>` via client.Slot), clear by slot on disconnect.
- HIGH GameManager.cs:123 — T-win flips BOTH whole teams every round. Source doesn't reassign on T-win; only CT-win promotes, and `BalanceTeams()` fills the T deficit (score>0 preferred). FIX: don't swap on T-win; add BalanceTeams deficit-fill.
- HIGH QueueManager.cs:73 — no VIP priority displacement when full + `ImmunityFlags` declared-but-unused. FIX: port HandleQueuePriority (uses the Chunk-3 VIP provider) + immunity.
- MED GameManager.cs:63 — win-streak scramble off-by-one (fires one round late).
- MED QueueManager.cs:37 — `ShouldForceEvenTeamsWhenPlayerCountIsMultipleOf10` dropped.
- MED QueueManager.cs:73 — no stale-entry pruning; disconnected ids linger.
- Convert GameManager `_playerScores`, ZonesModule `_playerStates`, AnnouncementModule `_voicesMuted`, NextRoundVoteManager `_votes` (LOW: vote-evict) to PlayerSlot-indexed + disconnect eviction. Add `IEntityListener` create/destroy for entity lifecycle.
- Audit: BalanceTeams, restart-on-zero-active, CheckRoundDone auto-terminate.

### CHUNK 2 — Critical convars + spawns + Slay
- **CRITICAL** audit — `retakes.cfg` server convars never applied; `mp_give_player_c4 0` load-bearing (else real C4 collides with synthetic auto-plant). Create/exec + ship it (mp_roundtime_defuse, mp_c4timer 40, mp_freezetime 1, …).
- audit/INCOMPLETE — per-map nade budgets dead: AllocatorModule.cs:165 passes `null` (`// TODO Phase E`); RoundTypeManager.cs:64 never re-inits Map on map change. FIX: thread current map name into NadeHelpers; set RoundTypeManager.Map on OnServerActivate.
- HIGH SpawnManager.cs:73 — no CanBePlanter spawn on site → bomb never plants, round can't end silently. FIX: guard + fallback (pick any T spawn as planter / log).
- MED SpawnManager.cs:107 — player count > spawns silently leaves overflow at default spawns. FIX: handle (reuse/log).
- Directive — PlayerLifecycleModule.cs:186 `AcceptInput("Kill")` → `pawn.Slay()`.

### CHUNK 3 — VIP as separate module (Core VIP-agnostic)
- Retakes.Shared `IRetakesVipProvider` (`bool IsVip(SteamID)`), default nobody/config-perm. Allocator preferred-weapon + Chunk-1 queue priority use it. Remove `@retakes/vip` AdminManager check (admins ≠ VIP).
- New `Retakes.Vip` project — implements provider via our `Vip.Shared` `IVipService`, registers with Retakes; ship Vip.Shared contract for VIP-less servers.

### CHUNK 4 — Announce + config parity
- audit#2/directive — center-HTML bombsite announce via `PrintCenterHtml`/`PrintCenterHtmlToAll` (site image PNGs + live team counts + timer) + the missing locale keys (BombSite.A/B, T/CT.Message, chatAsite/Bsite lines, menu images).
- LOW Loc.cs:27 — `Loc.Center` applies chat color escapes to Center HUD (wrong render); use HTML/plain for center.
- audit — round-type chat/center broadcast to ALL (not just voter); round-type `.cfg execifexists` hook.
- audit — restore ~13 dropped allocator config knobs (center-announce toggles, `EnableCanAcquireHook` opt-out, configurable menu triggers) + core knobs; fix 4 flipped defaults (voting on→off default per source, AllowAllocationAfterFreezeTime meaning, EnableFallbackAllocation, voices). CapabilityWeaponPaints — port or note.

### CHUNK 5 — Zones edge-cases + locale + docs
- LOW ZonesModule.cs:157 — dead/late player at announce gets no zone; assign on spawn too.
- Locale: fill 51/61 → all keys; keep en-US only (accept community PRs) but ship every key.
- Fix STATUS.md + PORT_PLAN.md to reflect real state (stop claiming DONE where stubbed).

REFUTED (don't touch): defuser one-vs-all-CTs (net effect identical). Per-map breakables, zone AABB, DefuseFix, round-type weighted-random = solid.
