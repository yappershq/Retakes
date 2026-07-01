<div align="center">
  <h1><strong>Retakes</strong></h1>
  <p>From-scratch ModSharp port of CS2 retakes — bombsite round flow, weapon allocation, zones enforcement, and defuse-fix in one plugin.</p>
</div>

<p align="center">
  <a href="https://github.com/Kxnrl/modsharp-public"><img src="https://img.shields.io/badge/framework-ModSharp-5865F2?logo=github" alt="ModSharp"></a>
  <img src="https://img.shields.io/badge/game-CS2-orange" alt="CS2">
  <img src="https://img.shields.io/github/stars/yappershq/Retakes?style=flat&logo=github" alt="Stars">
</p>

---

Retakes unifies four upstream CounterStrikeSharp plugins into a single ModSharp module — no CSS runtime required. All inter-system communication runs in-process; an `IRetakesService` interface is published on the ModSharp module bus so external plugins (custom HUDs, stat trackers, etc.) can subscribe to retakes events without coupling to internals.

## Features

- **Round flow** — picks a random bombsite each round, teleports players to spawns, auto-plants the bomb (optional), and enforces proper T/CT ratios.
- **Queue system** — rotates players through an active roster, supports priority/immunity flags (VIP queue jump), and ejects idle spectators automatically.
- **Weapon allocation** — per-player weapon preferences saved to MySQL; pistol, half-buy, and full-buy rounds with configurable weights; preferred-weapon (AWP) lottery with VIP bonus chances; nade budgets per team, round type, and map.
- **Round-type voting** — end-of-round vote lets players choose the next round type.
- **Zones** — per-map green/red AABB zones loaded from JSON; players who leave green zones or enter red zones are bounced back.
- **Breakables & doors** — optionally break `func_breakable` entities and open `prop_door_rotating` at round start (for maps like nuke/vertigo).
- **Bombsite announcement** — centre-screen text + native CS2 voice lines when the site is selected; per-player `!voices` mute.
- **Defuse fix** — every CT automatically receives a defuse kit when the bomb is planted.
- **Spawn editor** — in-game editor to add, remove, and inspect spawns; saves directly to the map config JSON.
- **Win-streak scramble** — scrambles teams after N consecutive T wins.

## Install

Copy build output and config to your ModSharp installation (`<sharp>` = your `sharp` directory):

| From | To |
|------|-----|
| `.build/modules/Retakes.Core/` | `<sharp>/modules/Retakes.Core/` |
| `.assets/configs.example/retakes/retakes.json` | `<sharp>/configs/retakes/retakes.json` |
| `.assets/data/retakes/map_config/*.json` | `<sharp>/data/retakes/map_config/` |
| `.assets/locales/retakes.json` | `<sharp>/locales/retakes.json` |

Fill in `database.connection_string` in the deployed config and create the `retakes` MySQL database before starting the server. The `retakes_user_settings` table is created automatically on first connect.

## Configuration

`<sharp>/configs/retakes/retakes.json` — auto-written with defaults on first load if absent.

### `game`

| Key | Default | Notes |
|-----|---------|-------|
| `max_players` | `9` | Active T + CT combined. Players beyond this sit in the queue. |
| `should_break_breakables` | `false` | Break `func_breakable` entities at round start. |
| `should_open_doors` | `false` | Open `prop_door_rotating` entities at round start. |
| `enable_fallback_allocation` | `false` | Give random rifles/pistols when `allocator.enabled` is false. |

### `queue`

| Key | Default | Notes |
|-----|---------|-------|
| `priority_flags` | `[{VIP, @css/vip, 0}]` | Players with these flags jump the queue. Higher `priority` = earlier. |
| `immunity_flags` | `[]` | Players with these flags are never rotated to spectator. |
| `should_remove_spectators` | `true` | Move idle spectators back into the waiting queue. |
| `should_auto_join_spectators` | `true` | New connects land in spectator (queued) rather than joining mid-round. |

### `teams`

| Key | Default | Notes |
|-----|---------|-------|
| `terrorist_ratio` | `0.45` | Fraction of active players assigned as T. |
| `rounds_to_scramble` | `5` | Consecutive T wins before teams are scrambled. 0 = disabled. |
| `is_scramble_enabled` | `true` | Enable win-streak scramble. |
| `is_balance_enabled` | `true` | Rebalance teams each round as players join/leave. |
| `should_prevent_team_changes_mid_round` | `true` | Block `jointeam` during live round. |

### `bomb`

| Key | Default | Notes |
|-----|---------|-------|
| `is_auto_plant_enabled` | `true` | Auto-plant at round start. Requires a `CanBePlanter` T spawn for the selected site. |

### `commands`

| Key | Default | Flag controls |
|-----|---------|---------------|
| `spawn_editor_flag` | `@css/root` | `!showspawns`, `!edit`, `!add`, `!remove`, `!nearest`, `!hidespawns`, `!done` |
| `map_config_flag` | `@css/root` | `!mapconfig`, `!mapconfigs` |
| `admin_flag` | `@css/admin` | `!forcebombsite`, `!forcebombsitestop`, `!scramble`, `!debugqueues` |

### `allocator`

| Key | Default | Notes |
|-----|---------|-------|
| `enabled` | `true` | Master switch for the full allocator. |
| `allowed_weapon_selection_types` | all three | Remove entries to restrict: `PlayerChoice`, `Random`, `Default`. |
| `usable_weapons` | all rifles/SMGs/pistols | Weapons removed from this list cannot be given to anyone. |
| `default_weapons` | AK/M4/Deagle/Glock/USP | Fallback weapons per team + allocation type. |
| `round_type_selection` | `Random` | `Random`, `RandomFixedCounts`, or `ManualOrdering`. |
| `round_type_percentages` | `{Pistol:15, HalfBuy:25, FullBuy:60}` | Weights for `Random` mode. Keys are RoundType int values (0/1/2). |
| `round_type_random_fixed_counts` | `{5, 10, 15}` | Per-type block size for `RandomFixedCounts` mode. |
| `round_type_manual_ordering` | `[{0,5},{1,10},{2,15}]` | Ordered sequence for `ManualOrdering` mode. |
| `max_nades` | 2 flash / 1 smoke / 1 mol or incen / 1 HE | Per-type nade cap. Structure: `{map_or_GLOBAL: {TE|CT: {weapon: count}}}`. |
| `max_team_nades` | `AverageOnePointFivePerPlayer` | Team nade budget per round type. Enum: `None`…`Ten` or `AverageXPerPlayer` variants. |
| `chance_for_preferred_weapon` | `100` | 0–100 chance a player's preferred weapon is honoured. |
| `allow_preferred_weapon_for_everyone` | `false` | `true` = all players; `false` = VIP (`priority_flags`) only. |
| `max_preferred_weapons_per_team` | `{TE:1, CT:1}` | Max AWP/preferred slots per team per round. |
| `min_players_per_team_for_preferred` | `{TE:1, CT:1}` | Minimum team size before preferred slots activate. |
| `number_of_extra_vip_chances_for_preferred` | `1` | Extra raffle tickets for VIP players in the preferred-weapon lottery. |
| `zeus_preference` | `Never` | `Never` or `Always`. |
| `enable_next_round_type_voting` | `true` | End-of-round vote for next round type. |
| `allow_allocation_after_freeze_time` | `false` | Let players change weapon pref (`!guns`) after freeze time ends. |

## Commands

### Player commands

| Command | Description |
|---------|-------------|
| `!voices` | Toggle bombsite voice-over announcements on/off for yourself. |
| `!guns` | Open the weapon-preference menu. |
| `!gun <weapon> [T\|CT]` | Set preferred primary weapon via chat. |
| `!awp [T\|CT]` | Set AWP as preferred primary weapon. |
| `!removegun [T\|CT]` | Clear preferred weapon preference. |
| `!nextround` | Vote for the next round type (when voting is enabled). |

### Admin commands

| Command | Flag | Description |
|---------|------|-------------|
| `!forcebombsite <A\|B>` | `admin_flag` | Force the next round to use the specified bombsite. |
| `!forcebombsitestop` | `admin_flag` | Cancel the forced bombsite override. |
| `!scramble` | `admin_flag` | Immediately scramble teams. |
| `!debugqueues` | `admin_flag` | Print active/queued player lists to chat. |

### Spawn editor commands

All require the `spawn_editor_flag` permission.

| Command | Description |
|---------|-------------|
| `!showspawns` / `!edit` | Enter spawn-editor mode — spawns visualised as team-coloured props with labels. |
| `!hidespawns` / `!done` | Exit spawn-editor mode. |
| `!add [T\|CT] [A\|B] [planter]` | Add a spawn at your current position. Defaults: standing team, random site, non-planter. |
| `!remove` | Remove the nearest spawn to your position. |
| `!nearest` | Print info about the nearest spawn to chat. |
| `!mapconfig` | Print a summary of the current map's spawn counts. |
| `!mapconfigs` | List all maps that have a saved spawn config. |

## Spawn configs

Eleven default map configs are included under `data/retakes/map_config/`:

`de_ancient`, `de_ancient_night`, `de_anubis`, `de_cache`, `de_dust2`, `de_inferno`, `de_mirage`, `de_nuke`, `de_overpass`, `de_train`, `de_vertigo`

Each file is a JSON array of spawns: `Vector` ("X Y Z"), `QAngle` ("P Y R"), `Team` (2 = T, 3 = CT), `Bombsite` (0 = A, 1 = B), `CanBePlanter` (bool). At least one `CanBePlanter: true` T spawn per site is required for auto-plant to work.

Use the in-game spawn editor to create configs for additional maps. Configs are saved automatically on `!done`.

## Zones

Zone configs live at `<sharp>/data/retakes/zones/<map>.json`. Format (from [oscar-wos/Retakes-Zones](https://github.com/oscar-wos/Retakes-Zones)):

```json
{
  "a": [ { "type": 0, "teams": [2,3], "x": [x,y,z], "y": [x,y,z] } ],
  "b": [ { "type": 1, "teams": [2,3], "x": [x,y,z], "y": [x,y,z] } ]
}
```

`type 0` = green (players must stay inside); `type 1` = red (players must stay outside). No zone file = zones disabled for that map.

## Credits

This plugin is a from-scratch ModSharp port that unifies the following four upstream CounterStrikeSharp projects:

- **[b3none/cs2-retakes](https://github.com/b3none/cs2-retakes)** — core round flow, queue management, spawn teleport, auto-plant, breakables, spawn editor, and event bus design.
- **[yonilerner/cs2-retakes-allocator](https://github.com/yonilerner/cs2-retakes-allocator)** — weapon and nade allocation, round types, per-player preferences (DB), gun menus, round-type voting, and buy-blocking.
- **[oscar-wos/Retakes-Zones](https://github.com/oscar-wos/Retakes-Zones)** — runtime AABB zone loading and player-bounce enforcement.
- **[TICHOJEBEC-SK/cs2-RetakeDefuseFix](https://github.com/TICHOJEBEC-SK/cs2-RetakeDefuseFix)** — automatic defuse-kit distribution on bomb plant.

All ported to [ModSharp](https://github.com/Kxnrl/modsharp-public) with no CounterStrikeSharp runtime dependency.

## License

MIT — see [LICENSE](LICENSE).
