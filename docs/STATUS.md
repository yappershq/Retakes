# Retakes port — STATUS

Overnight migration of cs2-retakes (+allocator/zones/defuse) to ModSharp. Design: docs/PORT_PLAN.md. Sources cloned at /home/claude/retakes-research/. Repo: /home/claude/Retakes (local git; public GitHub repo to be created under yappershq).

Build check: `cd /home/claude/Retakes && env -u version dotnet build -c Release`. Commit each green phase.

## Phase status
- [x] **A. Scaffold** — Core/Shared/Database skeleton, IModule+DI, EventBus/IRetakesService. GREEN. commit 9c8d0d4.
- [x] **B1. State foundation** — config, spawn data, player lifecycle, queue/team. GREEN. (commit below)
- [x] **B2. Round flow** — RoundFlowModule + fallback alloc + assister scoring. GREEN. Core spine done.
- [x] **C. Combat round** — breaker/announce/defuse/zones + synthetic auto-plant (mcp-verified). GREEN.
  - CAVEAT (live-test): planted_c4 timer countdown init unproven headless; m_flC4Blow/m_flTimerLength settable; TerminateRound timer fallback if needed.
- [x] **D. Allocator** — round types, weapon/nade/armor alloc, SqlSugar prefs, menus, voting, mid-round buy-block (PlayerCanAcquire hard-block). GREEN.
- [x] **E. Spawn editor.** SpawnEditorModule: !showspawns/!edit, !add, !remove, !nearest, !hidespawns/!done, !mapconfig(s), !forcebombsite(stop), !scramble, !debugqueues. GREEN. Viz = team-colored prop_dynamic (RenderColor + best-effort glow) + point_worldtext label (IWorldText typed API); warmup-freeze via ServerCommand(mp_warmup_*); in-bomb-zone auto-planter via m_bInBombZone netvar. Instance-scoped entity cleanup (no static).
- [~] **F. Review** — anti-pattern pass, README, configs.example, lang, gamedata; create+push public GitHub repo.

## Notes / decisions
- Entry ctor + IModule (Init/OnPostInit/OnAllSharpModulesLoaded/Shutdown) + DI fan-out mirror mmosystem/MonsterMod.
- EventBus published via SharpModuleManager.RegisterSharpModuleInterface<IRetakesService>(this, Identity, _bus) in PostInit.
- SqlSugar: SqlSugarCoreNoDrive 5.1.4.211, MySqlConnector 2.5.0.
- HIGH-RISK open items: bomb auto-plant (planted_c4 + fire bomb_planted), allocator CanAcquire buy-blocking. Pick cleanest ModSharp path or v1 fallback + note.

## Phase C notes (from B2)
- round events via GLOBAL IEventListener.FireGameEvent (filter by name); typed: IEventRoundEnd(Winner CStrikeTeam), IEventBombPlanted(Controller,Site short), IEventBombDefused, IEventPlayerDeath(AssisterController).
- NO `round_freeze_end` event in ModSharp — use IGameListener.OnRoundRestarted() or cs_round_start_beep for auto-plant timing.
- RoundFlowModule.PlanterSteamId + TerminateRound(RoundEndReason) exposed for BombModule. OnBombPlanted has TODO.
- IGameRules.TerminateRound(float,RoundEndReason,bool,TeamRewardInfo[]?) via ModSharp.GetGameRules().

## Resume instructions (post-compaction)
1. Read this file + docs/PORT_PLAN.md.
2. `git log --oneline` to see last green phase; `dotnet build` to confirm state.
3. Launch the next unchecked phase as a subagent (port from /home/claude/retakes-research/<src> using PORT_PLAN APIs; verify ModSharp calls with mcp__modsharp__; build green; report). Build-verify yourself, commit, update this file, ping Discord channel 1485606501673336842 at each green phase.
