# Story Mode: colony management over the hive

Story Mode is a browser tab where Fox runs a colony of pawns, in the style of a colony-management game. Its mechanics are
the genre's usual ones:
- work priorities per pawn and work type
- a daily schedule
- characteristics that shape what a pawn is good at
- direct orders to one pawn or a group (drafting)
- zones and designations painted on a top-down map

It's an original design on top of MineClaw. It uses no other game's art, names or text. The established layout stays, but new
mechanics should fit MineClaw's implementation in Minecraft; they do not need to follow RimWorld's model. Pawn counts have no
gameplay cap: server slots and available process memory determine how many can join at once.

## Pawns are script-only bodies

- A **colony** is a dedicated group of `CloneKind.Script` bodies, script-only with their brains asleep. They are owned by
  main, under the hive id `story-<n>`, on managed `maid_` accounts that are whitelisted under the existing scoped exception.
  No LLM drives a pawn. The browser and the colony director do.
- Every pawn runs the same saved **pawn script** (`story_pawn`, JS SDK). It loops:
  1. Ask the director for its next job through a new SDK primitive.
  2. Run the job with the existing primitives: chop, dig/mine, farm/harvest/replant, hunt, deposit/stash (haul), build (a
     design), craft/smelt, fight, goto, eat.
  3. Report progress, done or failed.

  Code adds only the primitive. How a pawn chooses its job comes from the priorities and schedule Fox sets.
- This works the same on b1.8.1 and 1.12.2, and with in-process hives (the default).

## The colony director (C#, bot side)

There is one per colony. It holds:
- **Pawns.** Each has an id (body name), a display name, and **characteristics** generated once per pawn, seeded by its
  name:
  - skills 0–20 for each work type, with a passion flag
  - 1–3 traits, for example: night owl (works best at night), brave (stays in fights), skittish (flees early), industrious
    (shorter idle), green thumb (farm skill grows faster), steady hands (build skill grows faster)

  Skills grow with experience in the work. Traits and skills shape:
  - **Tie-breaking.** Among jobs with equal priority, the pawn picks the one it's most skilled at.
  - **Defaults.** New pawns get suggested priorities from their skills.
  - **Survival thresholds.** A brave pawn stays in a fight that a skittish one leaves.
- **Work types,** in a fixed order: guard, doctor (eat and heal others: give food), cook (smelt food), hunt, build,
  farm, mine, chop, craft, haul. Each pawn has a **priority** of 1–4 for each, or off. Like the genre convention, a lower
  number is done first, with ties broken by the column order.
- **Schedule.** 24 in-game hours per pawn, each one of `work`, `sleep`, `anything`, `free`:
  - `sleep` sends the pawn home to a bed or shelter.
  - `free` means idle near home.
  - `anything` means work, eat or rest as needed.

  The hour comes from the world time, at 1000 ticks per hour, with tick 0 = 06:00. A schedule is indexed by the clock hour
  (index 0 = 00:00, `StoryCharacter.Hour`); the colony state's `hour` is that clock hour, the column in force. (Until
  2026-11-03 the web labelled index 0 as 06:00: its schedule grid and HUD clock ran six hours ahead of the bot.)
- **Zones,** painted as areas: chop, mine, farm, home, forbidden. Storage is chests with their own settings (see "Storage").

  The hour comes from the world time, at 1000 ticks per hour, with 0 = 06:00.
- **Zones,** painted on the map RimWorld-style as cell sets (see "Zones"): chop, mine, farm (growing zones), fish (see
  "Fishing") are work zones;
  home, forbidden, dump (the drop spot when the colony has no storage, see "Hauling") and preserve (see "Preserve zones") are overlay layers. Storage is chests with their own settings (see "Storage").
- **Designations,** per block or area: chop this tree, mine these blocks, harvest, haul these drops, build design N here,
  deconstruct.
- **Orders,** per pawn or group: move to, attack, chop, mine, haul, build, hold position, stop, and queued orders
  (shift).
  - **Drafted** pawns ignore work and the schedule and only follow orders.
  - **Undrafted** pawns go back to the job picker.
- **Job picker** (per pawn, whenever idle or after a job finishes):
  1. Urgent needs win. hp ≤ 8 or food ≤ 6 means eat or flee; a skittish pawn's thresholds are higher. Drafted pawns skip
     this step.
  2. The schedule hour comes next.
  3. Then the highest-priority work type that has an available job: a designation, a zone task or a standing task.
  4. Ties go to skill, then distance.

  Jobs are leased so that two pawns never take the same tree, block or drop. The existing crew lease logic can be reused.
- **State** is persisted in the agent db (new tables, `story_*`). It survives restarts, and the colony resumes.
- **Pause.** "Pause colony" makes every pawn hold. There is no time control: the Minecraft server owns the clock. Fast and Faster
  speed up the pawns' movement instead (see "Speed").

## Supervisor routes (bot)

They sit behind `Supervisor:Viewer:Enabled` (else 404) and the token in the `X-Supervisor-Token` header (else 403). Writes
(every POST) also need `Supervisor:Viewer:AllowStory`, which defaults to true when the viewer is on (false → 403; GETs still
work, so viewers can watch). JSON is snake_case with nulls written. Errors are `{error}` with 400 (validation), 403, 404
(no such colony or route), 405, 413 (body over 64 KB) or 503 (no world view yet).

| Method | Path | Body / result |
|---|---|---|
| GET | `/api/story` | `{colonies:[{id, name, created, paused, active, pawns:[name], version}], ai:[Ai], directives:[Directive], time_control:{available, by}}` |
| POST | `/api/story/time` | `{time: 0..23999}` (whole number) → `{time, by}`; 400 on a bad body, 409 `{error:"no op account"}` when no account the bot controls is op, 429 for more than one change a second. See "Time of day" |
| POST | `/api/story/colonies` | `{name (1..40 chars), pawns (1 or more: no upper limit), home?:{x,y,z}}` → `{id}` (`story-<n>`). Spawns script-only bodies running `story_pawn`: as many as there is room for now, the rest wait (see "How many pawns") |
| POST | `/api/story/colonies/{id}/release` | `{}` → `{id, released}`. The pawns deposit and log out; the saved state is kept (`active:false`) |
| POST | `/api/story/colonies/{id}/resume` | `{}` → `{id, pawns, note}`. A released colony spawns its pawns again, their old accounts first |
| POST | `/api/story/colonies/{id}/online` | `{pawns:[name]\|null, over_cap?:bool}` → `{started:[account], blocked:[{pawn, reason}]}`; 400 on a bad body, 404 for an unknown colony or pawn. Offline pawns are whitelisted again and (re)started now, the player cap permitting (see "Offline pawns") |
| POST | `/api/story/colonies/{id}/pawns` | `{op:"add", count ≥ 1}` \| `{op:"remove", pawn}` → `{pawns:[name], wanted, pending:[{reason, count}], leaving:[name], blocked:[{reason}]}`; 400 on a bad body or the last pawn;\| `{op:"remove", pawn}` → `{pawns:[name], wanted, pending:[{reason}], leaving:[name], blocked:[{reason}]}`; 400 on a bad body, past 12, or the last pawn; 404 for an unknown colony or pawn. See "Adding and removing pawns" |
| POST | `/api/story/colonies/{id}/pause` | `{paused}` → `{paused}` |
| POST | `/api/story/colonies/{id}/home` | `{home:{x,y,z}}` (whole numbers) → `{home:[x,y,z]}`; 400 on a bad body, 404 for an unknown colony. Moves and saves the colony's home; pawns far from it are brought there (see "Home") |
| GET | `/api/story/colonies/{id}?since=rev` | The state (below): full, or a delta when `since` is given |
| POST | `/api/story/colonies/{id}/priorities` | `{pawns:[name], work:{type: 0..4}}` (0 = off; a pawn with a role turns Custom) or `{pawns:[name], role: id\|null}` (null = Custom, the values kept) → `{ok}`; 400 for both at once, 404 for an unknown role. See "Roles" |
| POST | `/api/story/colonies/{id}/roles` | `{op:"add", role:{name, icon?, priorities?}}` \| `{op:"update", role:{id, name?, icon?, priorities?}}` \| `{op:"remove", id}` \| `{op:"reset_defaults"}` → `{id, roles:[Role], removed?}`; 400 on a bad body (unknown op, work type or role, a name taken or not 1..40 chars, a priority past 0..4, an icon not `[a-z0-9-]{1,24}`). See "Roles" |
| POST | `/api/story/colonies/{id}/schedule` | `{pawns:[name], hours:{"0".."23": "work"|"sleep"|"anything"|"free"}}` → `{ok}` |
| POST | `/api/story/colonies/{id}/draft` | `{pawns:[name], drafted}` → `{ok}` |
| POST | `/api/story/colonies/{id}/orders` | `{pawns:[name], order:{kind: move|attack|chop|mine|haul|build|hold|stop, target?:{x,y,z}|{entity:id}|{player:name}|{area:[x1,y1,z1,x2,y2,z2]}, design?:id, confirm?:bool}, queue?:bool}` → `{ids:[order id]}`; an attack on a player or bot without `confirm:true` → 409 `{error, confirm:{entity, name, kind}}` (see "Drafted combat") |
| POST | `/api/story/colonies/{id}/zones` | `{op: add, kind: chop|mine|farm|fish|home|forbidden|dump|preserve, area, into?, mode? (mine: quarry|ores), name?, plant?, sow?, harvest?, below?, above? (preserve)}` paints the area (see "Zones"); `{op: remove, kind?: kind|any, area}` erases its cells; both → `{id?, created, merged:[{into, from}], grown, shrunk, split:[{from, into}], deleted, removed (= deleted), warnings?}`; `{op: remove, id}` → `{removed:[id], deleted:[id]}` (the whole zone); `{op: rename, id, name}` → `{id, name}` (name ≤ 40 chars, empty clears it); `{op: update, id, plant?, sow?, harvest?}` → `{id, warnings}`; `{op: seek_seeds, id}` → `{count, kind: cut|bill}`; `{op: irrigation, id}` → `{id, cells, dig, axis, lines, note}` (see "Water"); `{op: bill_add, id, plant, mode?, target?, paused?}` → `{id, bill, ids, active_bill, warnings}`, `bill_update {id, bill, plant?, mode?, target?, paused?}`, `bill_remove|bill_pause|bill_resume {id, bill}`, `bill_reorder {id, ids}` (see "Farming"); `{op: preserve, id, on?, below?, above?, retake?}` → `{id, preserve, warnings?}` (see "Preserve zones") |
| POST | `/api/story/colonies/{id}/beds` | `{op:"assign", bed:{x,y,z}, pawn}` or `{op:"unassign", bed:{x,y,z}}` → `{beds:[Bed]}`; 400 on a bad body or no bed there, 404 for an unknown colony or pawn. See "Homes and beds" |
| POST | `/api/story/colonies/{id}/designations` | See "Designations" → `{count, targets}` or `{count, removed}` |
| POST | `/api/story/colonies/{id}/builds` | See "Construction" |
| POST | `/api/story/colonies/{id}/storage` | See "Storage" |
| POST | `/api/story/colonies/{id}/bills` | See "Workbenches & bills" |
| POST | `/api/story/colonies/{id}/policies` | See "Tools and gear" |
| POST | `/api/story/colonies/{id}/material-rules` | See "Tools and gear" |
| POST | `/api/story/colonies/{id}/gear` | See "Tools and gear" (bulk gear ops) |
| GET | `/api/story/recipes?workbench=<kind>` | `[Recipe]` (every bench when omitted) |
| POST | `/api/story/directives` | See "AI hives" |
| GET | `/api/story/directives` · `/api/story/directives/{id}` | `{directives:[Directive]}` · one directive with its `line`, `delivered` and `detail` |

`pawns:[name]` takes the account (`maid_chan_101`), its number (`101`) or the display name. A zone of kind `stockpile` is refused:
storage is chests (see "Storage").

### The colony state

```
{id, rev, since, full, at, time_of_day, time_frozen, hour, paused, active, home:[x,y,z]|null, tie_break: "column"|"skill", version,
 work_types:[guard, doctor, cook, hunt, build, farm, mine, chop, craft, haul],
 pawns:[Pawn], seats:{online, wanted, pending, waiting, waiting_reason, cap_free, kept_for_regulars, kept_for, slots, memory_room}, zones:[{id, kind, area, rows, cells, name, plant?, active_plant?, bills?, active_bill?, sow?, harvest?, status?, seeds?, needs_hoe?, preserve?}], plants:[Plant] (full only), beds:[{pos:[x,y,z], dim, pawn, zone}], designations:[Designation], orders:[{id, pawn, kind, target, design, given, left}],
 alerts:[{at, level: info|warn, text, pawn}], builds:[Build], ghosts:{rev, blocks:[[x,y,z,id,meta,build_id,state,kind]]},
 storages:[Storage], workbenches:[Workbench], categories:[Category] (full only), ai:[Ai], directives:[Directive],
 time_control:{available: bool, by: account|null}, policies:[Policy], material_rules:[MaterialRule], materials:[MaterialInfo] (full only),
 removed:{zones:[id], designations:[id], builds:[id], storages:["x,y,z"], ghosts:["build|x,y,z"]} (delta only)}
```

A delta (`since=rev`) carries only the objects with a newer revision, plus every pawn (they move all the time) and `removed`.
That holds for `ghosts.blocks` too: a delta lists only the ghosts that changed (often none), so the client merges them into
what it holds by build and cell, and a build in `removed.builds` takes its ghosts with it (fixed 2026-11-04: the web replaced
the whole list with each delta's, so the holograms and flat ghosts vanished a few seconds after the page loaded).
Pass the answer's `rev` back as the next `since`. This is the `story` event source the bridge polls, at 2 Hz while a client
watches. Every answer also carries `architect_key` (the Architect palette's inputs as one number: fetch the palette again when it
moves). The page's own `since=rev` catch-up (every 3 s) runs only while no `story` frame arrived for 5 s (a quiet colony or a
reconnecting stream); while frames flow it would fetch the same delta again.

The **Pawn** object:

```
{name, display, kind:"pawn", online, hp, food, pos:[x,y,z], dim, drafted, state: idle|working|moving|eating|sleeping|fleeing|fighting|drafted|offline, leaving, offline_reason,
 job:{type, label, target?, progress?, since}|null, queue:[Order],
 skills:{type:{level, xp, passion}}, traits:[{id, label, effect}], priorities:{type:0..4}, role, schedule:[24 × block], held, inventory:[[id,meta,count]],
 xp_level,
 equipment:{held, armor, tools, weapons}, policy, gear_tasks:[{id, op, label}] }
```

- `job.type` is the work type, or one of `need` (eating, fleeing), `order`, `schedule` (sleep, free time) or `idle`.
- Traits are `night_owl`, `brave`, `skittish`, `industrious`, `green_thumb`, `steady_hands` and `quick_study`.
- Skills grow with each finished job: passion gives ×1.5 and some traits multiply it. The experience to the next level is
  `100 + 50 × level`.

### Job picking (as built)

1. A paused colony holds. A drafted pawn runs its first queued order, else it holds; it skips needs and work.
2. An undrafted pawn runs its queued orders first.
3. Urgent needs:
   - hp ≤ 8 (brave 4, skittish 12) with a monster within 12 blocks: flee home, or away from it.
   - food ≤ 6 (skittish 8): eat. With no food on it, the pawn fetches some from storage first; with none anywhere, an alert.
4. With ≤ 4 free slots, or after a gathering job once that run is over (see "Keeping loot while gathering"), the pawn takes what it
   carries to storage.
5. The schedule hour (1000 ticks an hour, tick 0 = 06:00):
   - sleep: to its bed, else its own spot at home (see "Homes and beds").
   - free: wander the home area (see "Idle behaviour").
   - anything: eat when food ≤ 14, else work.
   - work: work.
6. Work. The work types with a priority of 1–4 go lowest number first. Equal priorities go in column order
   (`tie_break:"column"`, the default) or by the pawn's skill first (`"skill"`). Within a type, the nearest open target wins.
   With nothing to do, the pawn wanders the home area (see "Idle behaviour") and the colony gets an alert, at most every 5
   minutes.

Every target is leased: `b:x,y,z`, `e:<entity>`, `g:<build>|x,y,z` or `bench:x,y,z`.
- A lease lasts 90 s; progress reports renew it, and so does the tick while the pawn's script still runs (up to 10 minutes per job). It goes back when the job ends, fails or times out.
- A pawn keeps its job until it is done, fails, or an order (or draft, pause, stop) replaces it. New work never cuts a working pawn.
- A failed target rests before anyone takes it again: 30 s, then 1 min, then 10 min after each further failure. A failed delivery
  rests the same way for that pawn.
- A pawn whose last 3 jobs failed within 20 s rests 15 s before it picks again, but only when it has nothing else to do: the back-off
  rests the failed targets, never the pawn (Fox 11-04: "never idle on a work schedule if there are things to be done"). Orders and
  urgent needs still come first. It rests on its feet: idle legs about home (see "Idle behaviour"). Work for it ends a resting leg at
  once.
- A designation that failed is kept on the map with `reason` and `fails`. Its first failure raises an alert saying why. After 3
  failures its `status` is `failed` and an alert says so (again every 3 failures). It is still retried after its rest until it is
  done or cancelled.
- A designation is done only when the colony's view agrees: the trunk's base log is gone (chop), the plant is gone (cut), the crop
  isn't ripe any more (harvest), the block is gone (mine, deconstruct), or the drop or animal is gone (haul, hunt). Otherwise a "done"
  report counts as a failure. Live 11-03: harvest and cut designations vanished without a line in the log. The script's dig on a block
  outside the pawn's view did nothing and said ok.
- The pawn script walks to a far target before working on it, because a block outside its body's view reads as air. A chop goes at
  exactly the designated tree (`chop_tree` with `at:"x,y,z only"`) and never at the next free tree near the pawn. The director already
  gave each pawn its own tree.
- Hunger and storage alerts never hold work back. A hungry pawn with no food anywhere keeps working. Loot that no chest accepts stays
  with the pawn.
- New work (a designation, zone, build, bill, storage, or priorities that really changed) doesn't restart idle pawns' scripts: an
  idle hold lasts a few seconds and the pawn picks again by itself. Only a pawn held idle for 8 s or more (walking home from far)
  is cut for it, at most once every 30 s. (Live 11-03: cutting every idle script on each work-panel click restarted ten scripts per
  click, several "cancelled … (new work)" lines a second, and no pawn got anywhere.)

### Roles (added 2026-11-04)

Fox 11-04: "It would be a lot easier to assign priorities if I could make predefined roles that have specific priorities — e.g. a
Farmer whose biggest priority is farming, plus a couple of other things … I like the 1–4 priority grid, but I'd like to be able to
set some of those rows to just predefined roles."

- **A role** is a named priority template, saved with the colony: `Role = {id, name, icon, builtin, priorities:{type: 0..4}, pawns:[name]}`
  (`pawns` = who has it; a work type missing from a role is off). The state carries `roles` whole with every answer, and each pawn's
  `role` (its id, or null = **Custom**: its own priorities, the behaviour before roles).
- **Effective priorities.** A pawn with a role works by the role's priorities. The director keeps them in the pawn's `priorities`
  (rewritten whenever the role or the assignment changes, and again on load), so the job picker, hauling, gear and every other reader
  use the effective values unchanged (`StoryColony.Roles.cs`, `EffectivePriorities`).
- **Built-in roles** (seeded on first use, editable and removable; `reset_defaults` brings them back as shipped, re-adding removed
  ones and resetting edited ones, and keeps own roles):

  | role | 1 | 2 | 3 | 4 |
  |---|---|---|---|---|
  | Farmer (`wheat`) | farm | | cook, haul | doctor |
  | Miner (`pickaxe`) | mine | | haul | build |
  | Woodcutter (`axe`) | chop | | haul | |
  | Builder (`brick`) | build | haul | craft | |
  | Hauler (`package`) | haul | | chop (plant cutting) | |
  | Crafter (`hammer`) | craft | cook | haul | |
  | Cook (`pot`) | cook | | farm, haul | doctor |
  | Fisher (`fish`) | fish | | cook, haul | |
  | Guard / Hunter (`shield`) | guard, hunt | | haul | |
  | Generalist (`shapes`) | | haul | everything else | |

  Every other work type is off. Brewing is Cook and enchanting/anvil work is Craft, so they need no column of their own.
- **Assigning.** `POST …/priorities {pawns, role}` gives the pawns the role (their values become the role's); `role: null` makes
  them Custom with the values they have.
- **Editing a role** (`update`) changes every pawn with it at once; `priorities` merge (only the given work types change).
- **Customising a role row.** A cell edit (`POST …/priorities {pawns, work}`) on a pawn with a role detaches it: the role is cleared
  and the pawn keeps the role's values plus that change (RimWorld-style; the grid offers an Undo, which sends the role again).
- **Deleting a role** turns its pawns Custom with the same values.
- Ids: built-ins keep theirs (`farmer`, `miner`, …); an added role's id comes from its name (`Night miner` → `night-miner`, `-2` when
  taken), never a built-in's. Names are unique (case-insensitive); a reset whose built-in name an own role took names it
  "… (built-in)".

### Homes and beds

- **Homes.** A colony has one primary home (its anchor: `home`, moved with `POST …/home`, which is a migration: far pawns are
  brought there) and any number of houses, which are zones of kind `home`. They can be added, renamed and removed at any time. "At
  home" means inside any home zone's columns, or within 24 blocks of the primary home. Guarding and hauling loose drops use that union;
  so do workbench and bed finding. Pawns work in a far house by walking there. Only a migration or a rejoin teleports them.
- **Beds.** Beds are found in every home zone and within 32 blocks of the primary home, rescanned at most every 10 s. A bed's two
  halves are one bed, named by its foot. Bed: `{pos, dim, pawn, zone}`, where `zone` is the home zone it stands in (null = by the
  primary home) and `pawn` is who sleeps there. One pawn per bed and one bed per pawn: assigning a pawn moves it from its old bed,
  and takes the bed from whoever had it. Assignments are saved with the colony. The head half's position works as the bed too.
- **Sleep.** In a pawn's `sleep` hours it goes to its own bed (while that is still a bed), else to the nearest free one (leased, so
  two pawns don't take the same one). At night it lies down in it (`use_block`), and by day it rests by it. With no bed it rests at
  home, and the colony hears "X has no bed" once. That spot is its own (see "Idle behaviour"): resting pawns never stack on one
  cell, and each keeps its spot between its rests.

### Time of day

The minecraft server owns the clock, but an op account can set it. `time_control` (in `GET /api/story` and in every colony state)
is `{available, by}`. It is available when an account this bot controls is op: main first, else the first op body. `by` names it.

- **One clock on the page** (added 2026-11-03, `features/story/clock.ts`). The server's time is the only truth: the HUD
  clock (HH:MM), its sun/moon, the time slider, the schedule grid's current column and the map's day/night light all read one
  anchor, the newest server sample by receipt time: the colony state's `time_of_day` or the world index's `time` (the map's
  viewer polls it every ~0.9 s). Between samples it advances at 20 ticks/s; each new sample re-anchors it, and when the colony
  state is newer than the viewer's index the viewer is fed from it (and the other way round), so the light and the clock
  agree to the second. Never the browser's wall clock. A sample older than a minute is stale: the clock dims (with a
  tooltip) and stops advancing. A catch-up with no newer revision still carries the time (a paused colony's clock runs).
- **Day and night** follow the bot: night is 19:00–05:59 (`StoryCharacter.Night`), about ticks 13000–23000, where vanilla's sky
  goes dark. The map darkens with the sky light (`skylightSubtracted`, 0 at noon, 11 at midnight; torches and other block
  light stay), the sky and fog colours with it.
- **Frozen time.** A 1.12.2 server sends the time of day negative while `doDaylightCycle` is false (the absolute value is the
  time). The bot reads it so (`TimeUpdatePacket.Frozen`, `WorldState.TimeFrozen`; `WorldState.TimeOfDay` never maps -6000 to
  18000) and says `time_frozen` in the colony state and the world index. The web does not advance a frozen time and shows a
  snowflake on the clock and "Time is frozen" under the slider.
- **Op detection.** A 1.12.2 server tells each account its op level as Entity Status 24..28 on its own player, on login and on
  every /op and /deop (level = status − 24). Op means level 2 or more. A b1.8.1 server never says, so there an account is op only
  when `Supervisor:Viewer:OpAccounts` lists it. Otherwise it's unknown, and an unknown account doesn't count.
- **Main not op.** Main should be op, because it runs the whitelist. When the server says main isn't, the bot logs a warning once and
  each colony gets one alert.
- **The command.** `POST /api/story/time {time}` sends `/time set <time>` from that account. Main's goes through the command gate's
  scoped exception (only that exact shape, never when the config blocks `/time`, audited), and another body's after the same
  blocklist check, audited. The request is the authenticated story-role owner's. At most one change a second (429 otherwise,
  with `Retry-After: 1`). Each change logs one line.

### Offline pawns (added 2026-11-03)

Live 11-03 (CT 114, `story-1`, 10 pawns, 1.12.2): after a restart only 5 pawns came (the player cap) and four of those were then
kicked "You are not white-listed" every 60 s for good. Three causes, all fixed:

- **The startup reconcile** (crash cleanup: managed accounts main left whitelisted but that aren't running are released and taken
  off the whitelist) judged "running" by the supervisor's clone processes only, so every in-process body, story pawns included, looked
  stale. Now an account is kept when it is a node of the ownership tree or claimed by an owner (`HiveHierarchy.AddOwner`: Story Mode
  registers "a pawn of an active colony", spawned or still waiting for a seat), and the reconcile waits until the owners have loaded
  (`ExpectClaims` / `ClaimsReady`, AppHost calls it once the supervisor and its Story wiring are up; at most 2 min).
- **A "not white-listed" kick** was retried forever without a re-add (the add ran only before a body's first login). Now the body
  asks main to add it again (`HiveHierarchy.RewhitelistAsync`: managed accounts of the tree or an owner only, through the gate's
  scoped, armed `/whitelist` exception) and retries after 2, 4, 8, 16, then 30 s; after three tries in a row the body's own reconnect
  backoff takes over and each later login re-adds again. When main can't (it isn't op), the pawn's reason says so, the log warns once
  per 5 min and the colony raises one alert.
- **The player cap.** Pawns that didn't fit ("only 2 slot(s) free and 2 kept for offline regulars") were never tried again. Now an
  active colony retries every 15 s, once main is in game: the pawns that aren't hosted are brought in as seats free up, the same
  accounts only (never a new one); a colony founded short grows to its wanted size the same way. A pawn that yielded its seat to a
  regular comes back the same way.

Each pawn carries `offline_reason` (null while online): `released`, `connecting`, `reconnecting`, `logging out`,
`not whitelisted — re-adding`, `not whitelisted — main (<main>) isn't op, so it can't whitelist <pawn>: op <main> on the server`,
`server full (player cap: 2 free, 2 kept for regulars)` (the regulars' seats would fit it), `server full (7/7 seats taken)`
(nothing would), `someone else is logged in as it`, `retired for this session (crashed too often)`, `waiting for a seat`. The colony
state carries `seats: {online, wanted, cap_free, kept_for_regulars, …}` (the last two null when unknown; the rest under "How many
pawns").

`POST /api/story/colonies/{id}/online` `{pawns:[name]|null, over_cap?:bool}` → 200 `{started:[account], blocked:[{pawn, reason}]}`.
`null` means every offline pawn. Each one is whitelisted again (through main) and (re)started now: a hosted body's reconnect wait is
cut short, another logs in, the player cap permitting. An online pawn named explicitly counts as started. A released colony's pawns
are blocked with `the colony is released: resume it first`. 404 for an unknown colony or pawn, 400 for a bad body (`pawns` not a
non-empty list of names or null, `over_cap` not a boolean).

**`over_cap: true`** is the owner's explicit choice to use the seats kept for offline regulars. It can lock a regular out (the
server is full when they come) until a pawn leaves: such a pawn is spared by the yield check that otherwise logs bodies out to make
room for a regular, until it logs out for good. Even over the cap a pawn never takes a seat that isn't free.

Web: offline pawns are greyed in the colonist bar with an offline badge, the reason on hover and in the inspector; an offline pawn's
inspector has "Bring online", the Colony tab has "Bring all online (k offline)" and the colonist bar a small button for it whenever
a pawn is offline. When the answer is blocked by the cap, the reason is shown with a secondary "Use regulars' seats" action and a
one-line warning.

### A pawn's own claim (added 2026-11-03)

Live 11-03 22:08: the only pawn online couldn't cut a single plant: "double_plant at 208,64,412: another hive bot is working on that
part right now". Its dig takes a hive lease on the block under its own name (`ClaimAwareGameClient`), then the one shared protection
policy checked leases through main's claim view and saw the pawn's own lease as someone else's. Every in-process body now checks
leases through its own view (`Protection.IsProtected/Explain(p, id, claims)`), so its own lease is free to it and another body's still
isn't. Also: a cut over a tall plant (1.12.2 `double_plant`) is one designation on its lower half (breaking it drops the upper), leased
once; a pawn that goes offline gives its job and leases back at once (not after the 90 s lease), as a failed or cancelled job does.

Designations carry `retry_at` (when a resting, failed designation is tried again; null when open), and
`POST .../designations {op:"retry", ids}` → `{count, retried}` tries them again now. The map shows a failed designation's reason as a
short label (one per cluster of adjacent designations with the same reason), the full text on hover and in its inspector (reason,
fail count, next retry, Retry now / Cancel).

### Digging that sticks (added 2026-11-03, late)

Live 11-03 after 23:41 (`story-1`, 1.12.2, 10 pawns): "reported done, but the tallgrass is still there" ×54 (flowers ×13, dirt/grass
×27), a tall plant "busy: another hive bot is working on that part" ×11, "can't reach dirt/grass" ×46, "won't dig: someone is standing
on it" ×6, and the bot at ~70% CPU. Fixed:

- **Done is checked where the dig happened.** A pawn's "done" is checked against its own body's view (its connection carried the
  server's answer) when that view holds the cell, else main's; main's copy of a block change can come a moment later and is no failure.
  The check waits up to 1.5 s for the world to agree (`DoneGrace`; the pawn's script thread waits, never the director's lock). A block
  that is still there after that is a failure whose reason says which view saw it ("… still there (in its own view)").
- **The dig primitive finishes the block.** A block that turned into its other form while being dug (exposed dirt → grass, grass → dirt,
  redstone ore lighting up when punched) used to count as broken; the client now waits until it is really gone. `story_pawn` digs a
  block that an ok dig left in place once more after a second.
- **A tall plant is one target for every kind.** Cut, mine and deconstruct designations over a 1.12.2 double plant are all made on
  its lower half and lease the same key, so a cut box and a mine box over one plant never send two pawns to its two halves.
- **Leases last as long as the job.** The pawn script reports no progress, so a lease ran out after 90 s while the pawn was still
  walking there or digging and a second pawn was sent to the same target. A pawn whose script still runs keeps its lease (renewed by
  the tick) up to 10 minutes per job.
- **Only exposed blocks are handed out.** A mine or deconstruct target is offered only when a face of it touches an open cell (air,
  a plant, a liquid, snow), so a multi-layer box is dug top-down and from the outside in; a buried cell just waits, uncounted. Among
  open targets the nearest wins, higher cells a little earlier.
- **Nobody digs a block someone stands on.** A block under another pawn's or a player's feet is skipped; a pawn's own floor comes
  last, and when it is the pawn's target anyway the job carries `stand` (a cell beside it) and the script steps off first.
- **Transient refusals aren't failures.** "can't reach", "someone is standing on it", "busy"/a hive claim and "not loaded" rest the
  target 15 s with no fail count and no alert; only the 5th in a row counts as a failure (with its reason and alert as before).
- **CPU.** A pick reads designations from an index rebuilt only when a designation or zone changed (by kind, with lease keys and
  targets; forbidden cells, drops and zones as sets) and the world's entities once per pick. Benchmark (`StoryPickerBenchmarkTests`,
  10 pawns, 500 mine designations): a pick round of all 10 pawns 15.7 → 2.0 ms; a director tick with every pawn finishing a job, the
  watchdog tick and a state delta 6.7 → 3.1 ms.

### Stuck pawns (added 2026-11-04)

Live 11-04 (release 20261103-204549, `story-1`): maid_chan_114 stood in a hole two deep ("no path … it's 2 blocks up; unreachable even
by pillaring/bridging" ×5) and failed grass after grass. Each failure rested that grass for the whole colony (and counted against it),
so the other pawns stood idle in front of a field of work. Fox also saw pawns stuck in waterfalls and in caves.

- **A failure is the pawn's own** when the target is out of reach from where it stands: "no path", "no route", "unreachable", or a
  monster it couldn't find ("no skeleton within 16"). The target rests for that pawn only (30 s, 1 min, then 5 min) and the others take
  it. Only when 3 different pawns couldn't reach it (fewer when fewer are online) is it the target's failure, with its reason, fail
  count and alert as before. "can't reach" at the target (a soft refusal) rests it 15 s for that pawn only. A stuck pawn's failures
  never count against a target, and an escape job never counts as a failure.
- **Getting out is the pawn's.** `story.go(at, d)` is the usual `go`; when it finds no way, `story.escape(at)` runs before the job's own
  retry, and then the walk is tried again. `story.escape` reads `storyPit` (zero tokens): **pit** (nothing over the body, and walking
  reaches at most 30 cells and never 8 blocks away; a room under a build's roof is never a pit), **cave** (natural ground over the
  body) or **water**. Then, in order:
  1. walk to the exit, if there is one (the nearest bank out of the water; the nearest cell with open sky in a cave);
  2. dig a staircase: the blocks in the way of a 1-block jump onto a neighbour, toward the target first. Never below the feet, never a
     block with liquid beside or over it, never one with sand or gravel on top, never a container or anything as hard as obsidian;
  3. nerd-pole with blocks it carries (cobblestone, dirt, planks, stone, netherrack, sandstone, stone bricks, logs): `place(block,
     <its own feet>)` is a pillar step (jump, place under itself, land on it);
  4. with no blocks, dig the pit's walls for some (dirt by hand; stone, cobble, netherrack with a pickaxe), head height first, then
     pole;
  5. ask the colony for help (`story.stuck`).
- **The colony sees what the pawn can't.** Three unreachable targets in a row within a minute, or a pawn under ground (three blocks of
  natural ground over it) with no mining, deconstructing or build within 16 blocks, outside a home zone and with no queued order: the
  pawn's next job is `escape` (label "stuck: getting out (attempt N)", "stuck? heading home", "under ground: heading back up").
- **Stuck is sticky (live 11-04, a ravine at y 49 under home at y 63).** Pawns stuck in the ravine went back to work after a few
  blocks' walk along its floor and failed task after task, about 100 failures each in 20 minutes. A stuck pawn now gets only escape work
  (never a regular task) until its escape walks it home. That walk is the path check: the `escape` job is done only when `go(home)`
  succeeds. The state carries `stuck: {since, at, reason, step, attempts}`; `step` is what it tries now. Each step goes to the log
  ("stuck: no way home (…): trying a staircase toward home", "teleporting home (rescue_teleport)").
- **The escape job:**
  1. out of a pit, a cave or water by itself (above);
  2. walk home (a ravine with a walkable slope ends here);
  3. with no walkable way home, whatever the shape (a ravine, a canyon, a deep hole), climb toward home (`storyPit(home, climb)`):
     walk to the floor cell nearest home (`approach`), then cut a staircase up the wall toward home, one step up a block, under the
     same safe rules as above. Pillar when the wall can't be cut, dig for blocks when it has none. It stops level with home, and then
     walks home again;
  4. nothing works: `story.stuck`.
- **Help, fast (Fox 11-04: "they should teleport sooner").** A stuck pawn gets an escape budget: option `stuck_teleport_after_s`
  (default 20 s, at least 5) from when the colony first saw it stuck, or 2 failed escape steps, whichever comes first. Then the colony
  **teleports it home** (option `rescue_teleport`, default on) through the op command path (never on b1.8.1, whose `/tp` takes no
  coordinates). Blocks are tossed into a pit (kind `give`, 8 blocks, fetched from storage first when needed) only by a pawn that is idle
  and within 16 blocks, and only within the budget. Otherwise the teleport comes straight away, as it does for a pawn with no way home
  from a ravine or a canyon. A teleport counts once the pawn shows up at home. When it hasn't within 10 s (the server refused the
  `/tp`), one warning says so and the colony doesn't try again. A teleport that can't be sent (option off, no op path) is said at most
  every 10 minutes, and the pawn keeps trying to get out by itself. Each teleport is logged and said in an alert.
- **Getting out beats a drop.** The no-tool rule ("needs a wooden pickaxe: digging stone … would lose the drop") is for work targets.
  An escape that is refused for it digs with whatever the pawn holds (the hidden `escape_dig{at}`). Every other rule (protection, the
  floor under someone, lava, the void) still holds, and the client logs the lost drop. In a pit, a pillar step with the blocks it
  carries comes before any stair dig. Live 11-04: three pawns waited for the teleport in a stone hole they could have dug out of.
- **Stuck spots keep work away.** For 5 minutes after a pawn gets stuck, no other pawn is sent to a target within 3 blocks of the spot
  (4 up or down). Live 11-04: maid_chan_126, _118 and _105 were stuck in one hole, each sent after the work by it.
- **Idle with work (the idle audit).** Every second the director checks each pawn in a work or anything hour that holds, wanders or
  rests. The audit never searches for work itself. It reads what the pawn's own last search saw: every pick and every wake check keeps
  that, along with what held work of its kinds back. A pawn between two jobs is not idle. An idle pawn gets a reason (`idle_reason` on
  its row):
  - `stuck`
  - `work not taken yet`: its last search found work that an idle leg still holds
  - from its designations: `all targets leased`, `tool waiting`, `targets backing off`, `no reachable target`, or `back-off rest`.
    `no reachable target` (a mark free and not resting that its search still passed over) says which and why when its last search
    knows: `no reachable target: mine 181,57,366: buried (dug once a cell over or beside it is gone)`. The reasons: buried, under
    someone's feet, by a spot where a pawn got stuck, forbidden, out of sight (a haul or hunt mark whose entity no body sees). The
    mark named is the first such one in the colony's list, so pawns idle for the same reason group together. What the picker skips
    on purpose is no blocked work and gives no reason: a cave plant the colony marked itself, and trees an under-ground pawn leaves
    to a pawn up top.
  - `waiting: …`: what its last search found held back. Examples: `waiting: build b3: need 48 block of iron (short: 432 iron ore)`,
    `waiting: bill Iron Ingot suspended: no iron ore …`, `waiting: Torch for build b5: no coal …`,
    `waiting: build b2: the level below is still being built`, `waiting: build b2: its open cells are taken or backing off`,
    `waiting: the furnace at 6,64,0 is in use`
  - `priority 0`: there is work, but only of kinds it doesn't do

  A bill whose count is met, or whose stock is there, is no work. A furnace that is still smelting is no work either. A pawn with
  nothing else is simply idle. The colony's state carries `blocked_work`: idle pawns grouped by reason, most first. The web shows it
  as one condition, such as "4 pawns idle: build b3: need 48 block of iron (short: 432 iron ore)". The supervisor's per-minute perf
  line adds `idle-with-work N: <reason> <pawn-seconds>, …`, with the six biggest reasons and the rest summed. A pawn whose top work
  type is all leased, or waiting for a tool, takes its next work type.
- **A material that is out holds only its own cells.** When a build's open cells include a material the colony doesn't have (a mine
  design's torches, a wall's roof stairs), the build's other cells still go in. The build turns `blocked`, with the alert said once
  per reason, only when nothing of it can go in.
- **Wake checks stay cheap.** A wandering pawn searches for work again when new work is announced. Otherwise it searches only when
  something it reads has changed since its last empty search: designations, zones, builds, benches, storages, materials or leases.
  It searches at most every 3 s, and regardless every 10 s. The tick runs one such search at most. A search from the tick sets up
  the same per-pawn caches as a pick, such as tool plans.
- **Water and caves in pathing.** Every `goto` plan pays for flowing water in the body's cells (+4 a step: a stream is crossed, not
  followed) and much more for a waterfall column (+30). A walk that starts under the sky pays +3 a step for cells the sky doesn't reach,
  from the light the server sent, so a surface route wins over a cave one.
- **Ditches (added 2026-11-04).** Fox: "The pawns are very bad at navigating one-drop-level holes. I have channels for water to flow,
  one level down … They fall into it when they're walking, and they can't walk out because they have to jump once to get out."
  A step down into a ditch (a 1-deep trench or hole, dry or a water channel, with a bank on both sides within 2 cells) costs
  `DitchPenalty` (15 ticks, about 3 blocks), and a level gap jump over one only `DitchJumpPenalty` (6, instead of 30: a miss lands in
  it and climbs out), so a pawn walks round a short channel or jumps a 1-wide one rather than wading through. A long one is still
  crossed through it: down, then a jump out (out of water: jump held while pushing into the bank, vanilla's collided-in-liquid hop).
  A body knocked off a level step into a ditch beside it jumps straight back out onto the step (it used to push into the wall until
  "stuck" 4 s later and then avoid that move); one that lands farther away re-plans from there at once. The goto's own escape for
  "stuck twice here" hops toward the step holding jump in water (one tap only floated the body off the floor).
- **Caves in picking.** Idle cells are never cave cells. A home zone's own floors still count, such as a basement at the zone's height.
  Cut and harvest marks the colony placed itself (`seek_seeds`' grass) wait when the plant is in a cave. Marks the owner placed are
  ordinary work wherever they are, cave or not (live 11-04: five pawns idle all evening on "cut 91,55,463: in a cave"): the route,
  reach and stuck rules decide, and the idle audit says why when they can't. Mine designations go as before. Guard targets in a cave
  are ignored (see Defending).
- The pawn's row carries `stuck` (`{since, at, reason, step, attempts}`, or null); the web lists stuck pawns as a condition.

### Keeping loot while gathering (added 2026-11-04)

Fox 11-04: "they bring the block they mined back to the chest right away. They shouldn't. They can dump their inventory all at once
later while they keep mining until the job is done or their inventory is full."

- After a gathering job (a designation, a zone task, a crop, a catch, a furnace's output), the pawn keeps what it picked up and goes on
  to its next job. That is its next target of the same work when there is one.
- One trip later takes everything to storage, by the usual rules (every chest of the load in one trip). That trip comes when:
  - it has 2 free slots or fewer;
  - no target of that work is left for it;
  - its hours turn to sleep or free time;
  - nothing is left to do at all.
- Exceptions:
  - A hauler's pick-ups still go at once (they are batched already).
  - A drafted pawn keeps what it carries.
  - A build that needs what a pawn carries takes it from its pockets (no fetch).
- Benchmark (`StoryLootBatchingTests`, 24 dirt cells, the chest 9 blocks away, simulated time): about 6.5 → 18 blocks a minute.
- **Tool rules between blocks.** Every block is its own job, and its tool is planned when it is handed out. A material rule or gear
  policy changed while a pawn works takes effect at its next block: it fetches the newly wanted tool from storage on the way, or holds
  the one it carries. The block under way finishes as it started (never cut mid-swing). A rule that now waits (an enchantment that isn't
  there, fallback `wait`) leaves the rest of the field waiting with that reason and no failure.

### Reaching high blocks (added 2026-11-04)

Fox 11-04: "With the blocks-out-of-reach prompt for mining, cutting or harvesting, it should nerd-pole up to reach those blocks on its
own and destroy the nerd pole on the way back down. Not many blocks should truly be out of reach."

- **Digging (mine, cut, harvest, deconstruct, a build's dig cells): `story.dig(at, job)`.** A dig refused for reach ("can't
  reach", "… blocks from my eyes") goes through `storyReach` (zero tokens).
  - The scan picks a column under the target (a ceiling) or beside it, up to 2 away. The column needs ground under it (never a
    liquid) and must be free all the way up, with no lava beside it. A column under the target is not used when sand or gravel sits
    over the target.
  - It picks the fewest blocks to stack (at most 24, never above the build height) until the target's centre is within 4 blocks of
    the eyes.
  - The pawn walks there and nerd-poles up: each step is `place(block, its own feet)`, a pillar step (jump, place under itself, land
    on it). It digs the target, then digs its pillar back down from the top, standing on each block it placed and collecting them
    again. Nothing is left behind.
  - A walk that can't get within reach stops on ground below the target instead (`story.go`), and the dig climbs from there.
    This also applies when the pawn starts above the target, such as a quarry remnant below the rim but high above the floor.
  - When the walking planner finds a route that needs temporary blocks (`reachable by building`), `story.go` retries once with
    building enabled. This also reaches quarry remnants below the rim that a normal walk cannot approach. An explicit `build: false`
    is respected. Access blocks stay in the client's temporary ledger and are reclaimed by the pawn's post-job sweep or colony cleanup.
- **Blocks to climb with:**
  - the cheap blocks it carries: dirt, cobblestone, netherrack, stone, sandstone, planks last (see "Temporary blocks");
  - else the job's `pillar` stack from storage. The colony adds `{from, item, n}` to a job more than 3 blocks above the pawn, or a
    tall tree, when the pawn carries fewer than it needs. It is taken only if the climb happens;
  - else dirt dug around the column (never the ground under its feet).
- **The pillar stack:** a pawn keeps a kind of pillar block (dirt first, then cobblestone, netherrack) out of its store trips while it
  carries no more than option `pillar_stack` (default 16, 0 = none). A bigger pile is stored, and fetched back when a climb needs it.
- **Tall trees (`chop_tree`, client):** in a normal world the tree pillar may now be 12 blocks high instead of 2, using dirt as well
  as cobble (skyblock keeps dirt and the short pillar). Each step goes straight on from the top of its own pillar. The pillar is dug
  back down from the top afterwards. Build-mode `goto` and clutch blocks count dirt as a scaffold block in a normal world too.
- **Truly out of reach** (only liquid under it, no free column, more than 24 up): "out of reach even from a pillar: …". The target
  rests for that pawn; once 3 different pawns (fewer when fewer are online) couldn't reach it, the designation waits with the reason
  (`wait`). It is not counted as a failure and raises no alert.
- Not yet: several targets from one pillar top (each high block is its own climb; a tree is one job), a short staircase instead of a
  pillar, the night-outside rule while climbing.

### Temporary blocks (added 2026-11-05)

Fox 11-05: "I'm seeing a lot of stray planks have been placed down as well." and the rule: **"Bots should delete whatever they put
down unless it was a directive to build it. So, nerd poles, other blocks. If a bot can't navigate something without deleting its nerd
pole, then it should teleport."**

Where the planks came from (live, the hive's placed-block log and the ledger): shelter boxes (the night / low-hp box-in ranked planks
second, after cobblestone: maid_chan_67's box of 9 oak planks at 193,37,430 on 11-04 stayed when it died beside it a minute later),
2-high plank tree pillars from the 11-03 deforest runs, and walks' own pillars and bridges (a normal world's walk spent planks before
dirt, and a Story job never ran the client's clean-up of them at all). Seeds, wheat and bread booked as "placed while moving" were the
ledger's mislabel (a sowing's or deposit's slot update landing during the next walk), not placements.

Now:
- **Only a directive stays.** A build's cell, a quarry's stair steps and hazard seals, a sown crop: the owner asked for them. Everything
  else a pawn puts down for itself is temporary: reach and escape pillars, a build's scaffold, a walk's pillar or bridge, a shelter box,
  a fall clutch, a tree pillar.
- **Immediate ownership.** A pawn's local placement log overrides soft protection as soon as its block is confirmed, so cleanup
  need not wait for the shared store's next flush. Never-break marks and container protection still apply.
- **Cheap blocks.** Temporary blocks are dirt or cobblestone when the pawn has them (on skyblock: cobblestone and stone kinds, never
  dirt); planks and logs, a build's material, only when it carries nothing else. A build scaffold is cobblestone or dirt it carries,
  else one of the batch's blocks. The pillar stack never counts planks.
- **Taken back, whatever the outcome.** A reach pillar is dug back from the top even when the work up there failed or threw; a
  scaffold once its cell stands or the placing failed; an escape pillar from the rim once it is out (never the column it stands on).
  After every job, done or failed, the pawn runs the hidden `clear_temporary` (its client's own scaffolds, boxes and clutch blocks in
  reach).
- **Stranded: teleport.** A pawn still up on what is left of its pillar (a block under it it couldn't dig back) asks the colony
  (`stranded on its pillar …`): the rescue teleport takes it home. The block stays registered and becomes a cleanup job.
- **The registry** (`ColonyMeta.TempBlocks`, saved): every temporary cell with its pawn and kind, from the SDK (`H.storyTemp`) and the
  pawns' clients. A cell is forgotten once the world shows it without that block; a build's cell is never registered. One its pawn left
  behind (offline, or 45 s on and more than 8 blocks away: a failed or cut job, a death, a teleport) becomes a deconstruct designation
  ("removing a temporary oak wood planks (reach pillar) left by …", the colony's own) for any builder: self-placed blocks are always
  minable. At most one alert a minute says how many were queued.
- **Strays, once per load.** The colony looks at the hive's placed-block log within 200 of home: its own pawns' dirt, cobblestone,
  planks, stone kinds, logs, sand and gravel that still stand, in a small cluster (12 or fewer: a box, a stub, a lone block) or one
  column (a pillar), not a build's cell, not in or beside a quarry, not already designated, become cleanup jobs with one alert ("Found 23 stray
  block(s) the pawns left near home (14 oak wood planks, …)"). Never a player's block, never main's, never a wall or a floor.
- **Cleanups that fail** (live 11-06, the first sweep). A cleanup digs from a stand beside its block (never "won't dig the dirt I'm
  standing on"). One refused as protected is not the pawns' block any more (live: a stonebrick where a pawn's dirt had been): it is
  never dug, only listed ("Left for you to remove … not the pawns' block any more"). One out of reach (old boxes in a cave nobody can
  path to) rests 30 minutes; a second miss leaves it to the owner, listed the same way (one alert every 5 minutes at most). A pawn's
  own blocks are always minable in the same session too: an in-process body's placements join main's placed-block log (the one the
  protection reads) at the store's next flush, not only after a restart.
- **Never on a lid.** No temporary block goes on a chest, a furnace, a crafting table or another container or workstation (a chest
  under a full block won't open; live 11-06: oak planks on storage chests 216,64,400 and 223,64,400, 241 failed opens in 7 hours): the
  walk's scaffold veto, the reach pillar's column and the build scaffold's spot all skip them. On load, a block on a storage chest's
  lid that a pawn placed is queued with the strays; one the placed-block log can't put on a pawn is listed in the alert ("left for you
  to remove"), never removed.
- **No chests of its own.** A pawn's deposit that finds every chest full never puts an overflow chest down (client option
  `NoOverflowChests`, set for Story pawns; the logout deposit too): the colony says storage is full and lists chests near home that
  aren't storage yet (see "Storage full"). Live 11-05/06: overflow chests at 223,65,404 (on a storage chest), 218,64,395, 218,64,392,
  239,64,397, 226,64,429 the colony never knew of.
- **The ledger** books "placed while moving" only for a block a walk builds with; any other loss during a walk goes to the action
  before it ("placed as a block (its update came while moving)"), else "gone while moving".

Tests: `StoryTempBlocksTests` (a left pillar becomes a cleanup job said once and goes with its blocks; one taken back leaves nothing;
an offline pawn's box from its client is queued and a build cell never is; strays on load in one alert, never a wall, main's block or
a far one; a pawn's lid block is queued and an unknown one only listed; a stranded pawn is teleported and its pillar is cleaned up),
`StoryReachSdkTests` (stranded on its pillar: teleported, the block it couldn't dig is the colony's; the pillar comes down when the work
at the top fails; never on a chest), `ChestContentionTests` (a Story pawn places no overflow chest), `StorySdkTests` (a dirt scaffold left
after a failed cell stays registered; `clear_temporary` after every job), `PillarMaterialTests` (a normal world's walk pillars with dirt,
not planks, and the clean-up digs it back), `InventoryLedgerTests` (a sowing's late update isn't a movement placement).

### Tree farming and routes under ground (added 2026-11-04)

Fox 11-04: "There are still edge cases with the pathing, where it makes the pawn go way out of the way underground. I'm seeing this a
lot when I'm trying to make it farm trees." Live 11-04 (story-1, trees at x 110–150, z 330–365): "39 log(s) out of reach (up to y 76);
chopped 0/39", "no clutch blocks on me (no cobble/planks to be had) … 4 log(s) out of reach (up to y 67)", "no path to within 3 of
(144,65,334) (no route); it's 5 blocks up" for trees 5 to 8 blocks above the pawn, and gotos of 16–28 s. The pawns were under the trees
(a cave, a trench, a quarry) when they got them:
- "within 3 of" the tree's base was met in a cave under it, three blocks of ground between the pawn and the trunk;
- from there no walk reached a spot that sees a log, so not even the bottom log came down;
- the pillar spot was looked for "within 3 of the pawn's feet", five blocks under the trunk, so the climb never started;
- only cobblestone or planks were pillar blocks in practice (dirt came after them), and with none of either the tree failed whole.

Now:
- **A walk "within N of" a spot** (`goto` with a distance of 2 or more, so every `story.go`) is never met from under the ground below
  it: a cell lower than the spot counts only when nothing solid (leaves aside) stands between its eyes and the spot.
- **A chop from below** (normal world; the pawn's feet more than a block under the trunk's ground) first digs its own way up beside the
  trunk: the walk may dig natural ground (soil, sand, gravel, stone, leaves; never ores, never a placed or protected block, never the
  tree's own soil) and place the pillar blocks it carries. A step with natural ground over it costs about six walked blocks, so a short
  dug stair beats a long walk round through a cave, while an open quarry's own stair (no ground over it) stays the cheap way out. When
  no walk reaches a spot by the tree, the dig is the fallback too.
- **Pillar blocks (normal world):** any placeable junk, dirt first, then cobblestone, netherrack, stone, sandstone, gravel, sand,
  planks, stone bricks, and the felled logs of the tree itself (its own pillar is never taken for "the next log"). With nothing at all,
  it digs dirt or grass 2 to 5 out from the trunk (never its soil, never under anyone, never beside water or lava) for the climb, and
  fills those holes again once the pillar is dug back. A taller climb starts where one step doesn't reach a log yet, as long as the
  column is free all the way up. The pillar spot is by the trunk at the trunk's ground, wherever the pawn stands.
- **Partial trees stay designated.** A chop that took part of a tree (`partial: …`) keeps the designation, now on the lowest log left;
  the pawn that took part rests from it 20 s, so another pawn (or it, later) climbs for the rest. After two partial goes the designation
  leaves the map with the alert, as before.
- **Under ground, trees go to a pawn up top.** A pawn under ground (natural ground over it) leaves chop work to a pawn on the surface
  that does chop work (not drafted, not stuck) and will take it: one idle now or chopping now. A pawn up top busy at other work doesn't
  count (live 11-04: the trees waited while the pawn under ground stood idle). It keeps to the work down there, or heads up by the
  under-ground rule. With nobody up top to take it, it takes the tree itself.
- **Replanting (normal world).** With no sapling of the tree's species on it, the pawn breaks crown leaves it can reach (from its
  pillar before it comes down, then from the ground, at most 24, no long walks) until a sapling drops, and waits up to 12 s for the
  rest of the crown to decay. The spot is planted with the tree's own species, as on Skyblock.
- **Route diagnostics.** Every planned route's shape is counted: its walked length against the straight line from start to end, and
  its lowest y. A **detour** is longer than 3 times the straight line (and at least 10 blocks longer), or dips more than 4 below both
  its start and its end. A chop's own walks log `goto (chop): route 39 blocks for 8 straight, …, lowest y 57 (start 57, end 64)`;
  any detour logs `goto: detour: …`. The supervisor's perf line adds `routes N, detours M (K dug short; worst …)`.
- **Detours dug short (pawns only).** A pawn's walk planned as a detour (normal world, first plan of the walk) is planned once more
  digging natural ground with the under-ground step cost; the dug route is taken when it is at most 60% of the walk's length.

### Defending (added 2026-11-04)

Live 11-04: "attack failed: no skeleton within 16" ×15, creeper ×8, zombie ×7 for one pawn (82 failures). The merged view showed every
body's monsters, including cave mobs under home, and each one sent a pawn walking for nothing. A guard goes only after a monster
that meets all of these:
- **Fresh:** some body had news of it in the last 45 s (`seen_at`, the freshest copy across bodies).
- **At home:** in a home area.
- **Reachable:** within 8 blocks of home's ground level, or within 3 of the pawn, and not under ground.
- **Not just missed by this pawn.** One it couldn't find rests 60 s for that pawn only. That is not a failure: no alert, no rest.

Among guards with nothing better to do (idle or wandering), the nearest goes. Drafted pawns' attack orders are separate and unchanged.

### Speed: Fast and Faster (added 2026-11-04)

Fox 11-04: "next to the pause button on the bottom right, add a Fast and Faster mode. They should just change the movement speed
through the network itself." There is still no time control (the server owns the clock): Fast and Faster make the colony's pawns
**move** about 2× and 3× as fast. Main and every other body are never touched.

- **The option.** `POST …/options {speed: normal|fast|faster, speed_effect?: bool}`; state `options.speed` (default `normal`) and
  `options.speed_effect` (default on), saved with the colony. Each pawn carries `speed: {level, multiplier, requested,
  server_factor, rollbacks}` (null while offline): `level`/`multiplier` are what the body actually runs at.
- **How (client).** The director gives every online pawn body the multiplier each tick (`MinecraftClient.SpeedMultiplier`, 1..3;
  default 1 for every body). After the normal physics tick (input, friction, gravity, jumps, falls all vanilla), the body is moved
  again by `(factor − 1) ×` what the tick moved horizontally, with the version's own `Entity.move` (b1.8.1 `PlayerPhysics.MoveEntity`,
  ySize zeroed like the server's replay; 1.12.2 `Physics1122.Move`: the same swept collision, step-up and ground contact), in
  segments of at most 0.75 blocks, **each its own position packet** after the normal tick's own packet (`SpeedBoost`,
  MinecraftClient.Speed.cs). Velocity, fall distance and fall damage stay the normal tick's.
- **Only where it's safe.** On the ground (not sneaking, not in a web, never near lava), swimming (capped at ×1.5) and climbing a
  ladder up (×1.5, the feet must stay on the ladder); never in the air, so jumps, gap jumps, drops and falls are vanilla. Each extra
  move must end with ground within a step below and out of liquids (in water when swimming), else it is halved, then dropped. An edge,
  drop or liquid within about a block past where it would end lowers the factor to ×1.25. Only the path follower boosts, and only
  as far as its **room** allows (`PathFollower.BoostRoom`: a plain walk or diagonal to its cell centre plus the steps that go on in
  a straight line at the same floor, at most 1 block a tick, so no cell is skipped; 0 for precise steps, actions, sneaking, jumps,
  drops, bridges, pillars, doors and the last step). A boosted body keeps sprinting on straights. Fights, holds, planned drops,
  gap jumps, void steps and the 10 ticks after any teleport are never boosted. Stuck detection and escape work as before (they
  count ticks, not distance).
- **The server's checks.** Vanilla replays each packet's delta with that same `Entity.move`: "moved wrongly" past 0.25 blocks off
  (horizontally; b1.8.1 `NetServerHandler`, 1.12.2 `NetHandlerPlayServer`) means a teleport back, "moved too quickly" past 10
  blocks a packet a kick (b1.8.1) or a teleport back (1.12.2, against the start of the server tick, × the packets in it). One
  packet per collision pass keeps the replay identical; no packet carries more than about 1.1 blocks; b1.8.1's stance stays legal.
- **Rollback safety.** A correction (a teleport back within 8 blocks, not a login, respawn or `/tp`) within 2 s of a boosted tick
  counts against the boost; two within 30 s step the body down one level (3 → 2 → 1) and the colony raises one alert per level:
  "Server rejected faster movement — using Fast" (or "— using normal speed"). The HUD marks the level the slowest pawn runs at.
  Choosing a speed again clears the step-down.
- **Speed effect (1.12.2, `speed_effect`).** With an op account (main is op: the server's word, else `Supervisor:Viewer:OpAccounts`),
  Fast and Faster also ask the server for `/effect <pawn> minecraft:speed 1000000 1|2 true` (Speed II / III: +40 % / +60 % on the
  movement attribute, which the 1.12.2 physics applies) — once, as a toggle: sent again only when the level changes, the pawn comes
  back online, or its body shows no Speed effect any more (it died; checked at most once a minute) — through the command gate's scoped exception
  (`CommandGate.ArmManagedSpeed`: only that shape, only the hive's own managed accounts, never main or a player, never when the
  config blocks `/effect`; audited). The server then sees part of the speed as legit and the client adds only the rest
  (`multiplier / server_factor`). Back to normal, the option off, a pawn leaving or a released colony clears it
  (`/effect <pawn> minecraft:speed 0`). b1.8.1 has no effects: the client boost only.
- **Measured** (the fake server enforcing both checks, 30-block corridor, sprinting, timed from the first step; 5 ms test ticks):
  b1.8.1 131 ticks normal, 67 fast (1.96×), 49 faster (2.67×); 1.12.2 127 / 67 (1.90×) / 49 (2.59×); zero rollbacks, zero
  "moved wrongly", largest packet under 0.8 blocks. Faster stays under 3× because accelerating and braking for the goal run at
  vanilla speed.

### Idle behaviour (added 2026-11-03)

Live 11-03 (Fox): "When there's nothing designated to do, they don't do anything at all. They tend to gather in the same spot." The
cause: every idle hold, free-time hold and no-bed rest targeted the one home cell (`go(home, 2)`), and every teleport home went to
one spot, so the pawns stood in a pile there.

- **Wandering.** A pawn with nothing to do in its `work` or `anything` hours, and any pawn in its `free` hours, wanders. The director
  hands it one leg at a time (kind `idle`, `wander:true`, label "Idle (wandering)" or "Free time (wandering)", state `idle`): a safe
  cell of the home area to walk to (`go`, walking pace, `dist:0`) and linger on for 4–12 s. The home area is the home zones' columns
  when the colony has any, else 14 blocks around the primary home. A leg 3–12 blocks off is preferred; a pawn far from home walks
  back first. The script runs the leg as `story.idle(job)`.
- **Real work interrupts at once.** Between legs the pawn asks again. During a leg the tick looks every 2 s (at once after new work)
  for an urgent need or a job of its work types and hands it over in the same step, leased, so the next idle pawn sees it taken. The
  pawn script's `story.during` sees its leg is gone and cuts the walk or the linger; nothing is stopped or restarted. A leg whose
  hours are over (the schedule moved on to sleep) is taken back the same way. A cut, unreachable or failed leg is never a failure:
  no back-off, no alert, no rest. An unreachable cell rests 60 s for everyone.
- **Personal space.** Every idle, rest, flight and brought-home target is reserved per pawn. The reservation is light and short-lived
  (until the leg should be over, and only while that job is the pawn's) and apart from the work leases, so it never blocks digging,
  building or hauling. A new target keeps 2.5 blocks to every other pawn's reservation and standing spot (and any player's) when
  there is room, then 2, then 1: never the same cell. A pawn that arrives where someone already stands (they met on the way) moves
  on, and the director picks it another cell.
- **Safe cells only.** Solid ground under two free cells: never water, lava, leaves, fire, cactus, magma, farmland, a crop, a fence
  top, furniture or a bed. Nothing that hurts beside it, no door beside it (a doorway), no drop of three or more beside it. Loaded
  columns only, the colony's dimension only. Never on, over or under a designated or build cell, never right in front of a storage
  chest or a workbench, never in a forbidden area.
- **Night.** After dark (19:00–06:00) idle pawns wander only cells that are inside (roofed, in a home zone) or lit (light 8 or more
  after dark). With none, they potter about within 5 blocks of home (or stargaze there). Sleep hours are beds as before.
- **No home** (or another dimension): the pawn holds where it is ("idle: nothing to do"); new work cuts that hold as before.
- **Spread spots.** A no-bed rest, a flight home and a teleport home (founding, migration, a join far away) each get the pawn's own
  free cell nearest home, never one cell for all.

**Resting and recreation (added 2026-11-03, late).** Fox: "If a pawn is idle or resting, it should do recreation or something else
instead of sitting in the same place."

- **Resting wanders too.** The back-off after three failures ("resting") is idle legs about home (label "Resting (wandering)" or
  "Resting: …"), not a hold. The legs never shorten it: work isn't handed to a resting pawn, and the leg in progress is taken back the
  tick the back-off ends, so the pawn works at once. No other hold left a pawn standing still: blocked builds and missing materials
  just mean no job, which is idle.
- **Recreation** (the colony's `idle` option, default `recreation`) in free hours, when idle and while resting. Each leg is one
  pastime, never the same one twice in a row when another is possible, all with the safe-cell and personal-space rules, all cut the
  moment work comes up (the same step that hands the work over):

  | `act` | where | looks at | label |
  |---|---|---|---|
  | `wander` | a cell 3–12 blocks off | a glance about | "Idle (wandering)", "Free time (wandering)", "Resting (wandering)" |
  | `socialise` | 2–3 blocks from another idle pawn (never closer than 2 to anyone) | that pawn, which turns to it on its next leg | "Recreation: socialising with Dara" |
  | `stargaze` | at night, under open sky (nothing overhead), within 8 of home or in a home zone | up, slowly round | "Recreation: stargazing" |
  | `sit` | beside a chair (a stairs block), else two blocks from a colony workbench or chest (never in front of it) | the chair or table | "Recreation: resting by a chair" / "sitting by the crafting table" |
  | `garden` | 1–2 blocks outside a growing zone, or beside flowers | the zone's middle or the flower | "Recreation: visiting the garden" |
  | `sunset` | at dusk (18:00), the west edge of the home area | west | "Recreation: watching the sunset" |

  The job carries `act`, `look:["x,y,z"]` (where it looks while it lingers, 8–16 s; strolls 4–12 s) and `with` (socialise). The
  director only offers the pastimes; the SDK's `story.idle` walks, turns (`face`) and lingers.
- **The option.** `POST …/options {idle: recreation|wander|stay}` (state `options.idle`): pastimes, strolls only, or hold where they
  are (the old behaviour). The Colony tab has an "Idle pawns" picker beside the bench options.

### Adding and removing pawns (added 2026-11-03)

Fox 11-03: "+ and − buttons to add or remove pawns in Story Mode."

- **+** (`POST …/pawns {op:"add", count}`): the colony's wanted size grows (no upper limit: see "How many pawns"). As many as the free
  seats and the memory ceiling allow come now (never the seats kept for offline regulars) through the usual spawn path: managed
  `maid_` accounts, whitelisted through main. The rest are pending (`seats.pending`, `pending:[{reason, count}]`: one entry with a
  count, never one per pawn) and join with the seat retry as room frees up. One log line and one alert per change.
- **−** (`{op:"remove", pawn}`): the pawn's job, leases, idle reservation and bed go back at once and it is `leaving`. It takes what
  it carries to storage by the filters (what no chest accepts goes with its logout's deposit, with an alert), its script ends, then
  it logs out and its account is released (whitelist removal through main). It leaves the state (`removed.pawns`). An offline pawn
  leaves at once; one that doesn't finish within 2 minutes is released anyway. The wanted size drops by one. The last pawn can't be
  removed (release the colony instead).
- **Web.** The colonist bar ends with **+** (Shift+click asks how many) and **−** (the one selected pawn, after "Remove Nils J.?
  Their items go to storage."). Pending pawns show as greyed "joining…" tiles; more than 4 as one "+N waiting" tile, the reason on
  hover.

### How many pawns (added 2026-11-05)

Fox 11-05: "Don't set an upper limit at all for how many pawns I can spawn in. Allow me to do as many as I want." (and Story no
longer has to copy RimWorld). A colony takes any count, founding (`pawns ≥ 1`) and + (`count ≥ 1`) alike; the old cap of 12 is gone
from the director, the routes, the contracts, the mock and both web steppers (the founding stepper's Shift+click steps by 10). Only
two real limits decide how many are in game, and both are shown, never silent:

- **The server's seats**: its max players (the Join Game packet) less those online and logging in, and one seat kept per offline
  regular (`Hive:RegularPlayers`). The server's max-players and the regulars' reservation are the owner's: Story never changes them.
- **The memory ceiling** (`Hive:Resources`, `ResourceCeiling.BodiesThatFit`): how many more in-process bodies fit.

`Hive:MaxClones` and `Hive:Subhives:MaxPerSpawn` govern autonomous hive spawning; neither caps a colony's pawns.
As many as fit come now; the rest wait (`seats.pending`) and
the seat retry (every 15 s) brings them in as seats or memory free up. Accounts are allocated per pawn with no fixed roster
(`maid_chan_101`…`9999`, quantity-like ids skipped); their `/whitelist add` lines go out one at a time through the gate's scoped
exception, each armed only when its turn comes (40 at once used to outlive the gate's 15 s arm in main's chat queue), and a launch
waits that much longer for its add per line queued ahead of it.

The colony state's `seats` says how many and why: `{online, wanted, pending, waiting (wanted − online), waiting_reason, cap_free,
kept_for_regulars, kept_for:[regular], slots, memory_room}`. `waiting_reason` names the limit that keeps the waiting pawns out
(`SeatFacts.WhyWaiting`): `server full (20 slots, 2 kept for poofimafox and kiwi)`, `memory: room for 3 more` (or the ceiling's own
words when none fits), null when they are all on their way. Founding short alerts `18 of 40 pawns came now; the other 22 wait and
join as room frees up (…)`. The Colony tab's seats line reads `18 of 40 pawns in game — server full (20 slots, 2 kept for poofimafox
and kiwi) · memory: room for 9 more`.

Scale (11-05, `StoryLiveShapeBenchmarkTests`, `StoryPickerBenchmarkTests`, `StoryBuildBenchmarkTests`, this machine): a busy
colony's pick is 0.21 / 0.27 / 0.39 ms at 10 / 30 / 50 pawns, the tick 0.05 / 0.09 / 0.10 ms, the state delta 27 / 69 / 113 KB a
second (≈2.3 KB a pawn) and the full state 94 / 135 / 177 KB. An idle pick (a spread-out wander spot) checked every candidate cell
against every other pawn: 0.9 / 2.3 / 3.2 ms (pawns² a round); the taken spots are bucketed by 4×4 columns now, 0.7 / 0.9 / 0.7 ms.

### Pawn bodies and the pawn script

- **The group.** A colony is the hive group `story-<n>`, owned by main.
  - Its accounts are managed `maid_` accounts (kind `script` in the managed list). They are whitelisted under the scoped
    exception before login and removed after logout.
  - In the ownership tree they are main's in-process bodies tagged with the group (`crew: story-<n>`), like a hive script's
    bodies. So releases go through the usual deposit → logout → whitelist remove, and main's brain and crews never drive them.
- **The script.** `story_pawn` ships with the bot and is installed into the script library on start (author
  `mineclaw (default)`). A version someone else saved is kept.
  - Each pawn runs it as an unattended lane with `NoModel` set: no patch turn and no brain wake, ever.
  - A watchdog restarts a pawn's script when it ends. A run ends after 300 jobs or the run limit.
- **The SDK primitive** (module `story`):
  - `story.next(s=0)` returns the pawn's job, or null.
  - `story.report(job, 'done'|'failed'|'progress', note?)` reports on it.
  - `story.during(job, fn)` runs `fn` while the job is still the pawn's; the moment the director takes it back the running action
    is cut (`{ok:false, cancelled:true}`; the stale report that follows is ignored, never a failure).
  - `story.idle(job)` is an idle leg inside `story.during`: walk to its cell, then linger looking at each point of `look` in turn
    (with the hidden `face{at}` action: turn the head, nothing else).
  - `story.till(job)` holds the hoe and uses it on the block (walking over first when it is far).
  - `story.fetch(job)` takes the fetch list (a busy chest is waited for), and `story.place(job)` lays a build job's cells and
    reports each one.
- **A job** is `{id, type, kind, label, at?:"x,y,z", fetch?:[{from, item, n}], stash?:[{to, items}], ...}`; a build job adds
    `cells`, `want`, `clear` and `stand` (see "Building fast").
  - Kinds: `go|chop|dig|harvest|till|collect|place|pour|attack|equip|eat|craft|smelt|collect_furnace|give|blueprint|sleep|idle|hold|end`
    (`pour`: `story.pour(job)`, see "Water").
  - The script withdraws the `fetch` items, runs the kind with the existing primitives (`go`, `chop`, `dig`, `collect`, `place`,
    `attack`, `eat`, `craft`, `smelt`, `collect_furnace`, `handTo`, `build`, `wait`), deposits the `stash` items and reports.
  - Gear (added 2026-11-03), in the SDK so the pawn script stays under its 4000-character cap: `story.fetch(job)` takes the
    `fetch` items, an entry with `ench` (a tool, weapon or armour piece) exactly with the hidden `equip(item, 'take', from, ench)`,
    then holds the job's `tool`/`tool_ench` (`equip(item, 'hold', '', ench)`); `story.work(job)` runs an `equip` job: armour off
    (`unwear`), `wear`, then `hold`/`hold_ench`. An `attack` job names its `target` (the entity id) and the script attacks exactly
    that one.

### Designations

- Add: `POST …/designations` `{op:"add", kind: chop|cut|harvest|mine|haul|hunt|deconstruct|forbid|allow, area?:[x1,y1,z1,x2,y2,z2] | blocks?:[[x,y,z]], filter?: all|ores|stone|soil}`
  → `{count, targets:[[x,y,z]] | [entity id]}`.
- Remove: `{op:"remove"|"cancel", kind?, area? | blocks? | ids?}` → `{count, removed:[id]}`.
- **Marks on entities follow their entity (added 2026-11-04).** Fox: "When I have items selected for hauling and they get hauled to a
  chest or into an inventory, the hauling icons don't go away, but the items do."
  - A haul trip takes the designated drops within 6 blocks of the first one with it (each leased, "(+2 more)").
  - A collect job done completes the haul designation of every drop it leased, standing pick-ups included.
  - Every 3 s, a haul, hunt or forbid designation whose entity the colony's view hasn't shown for 8 s goes (picked up by a player or
    another bot, killed, despawned). That happens only while the column where the entity lay is in view and no pawn's job holds it.
  - A drop that merged into a stack of the same item right beside it passes its designation on to that stack (or simply goes when
    that stack is designated already).
  - Each removal is in the state delta's `removed.designations`, so the web clears the icon.

The area expands at designation time to the matching targets in the bot's world view. Each target is one designation, at most
4096 per request; targets already designated are skipped.

| kind | targets | work type |
|---|---|---|
| chop | Each tree, a log whose block below isn't a log. One job per tree, with `blocks` = its logs; leaves are left to decay | chop |
| cut | Tall grass, flowers, ferns, dead bushes, saplings, mushrooms, vines, lily pads and leaves | chop |
| harvest | Ripe crops only: wheat, carrots and potatoes at age 7, beetroot and nether wart at 3, cocoa at stage 2, melon and pumpkin blocks, sugar cane and cactus above their base. Replants the version's seed item | farm |
| mine | Blocks that match the filter (all = anything diggable; never bedrock, liquids or portals) | mine |
| haul | Item entities in the area, into storage chests | haul |
| hunt | Passive animals in the area | hunt |
| deconstruct | Blocks the world doesn't make by itself (planks, glass, doors…) | build |
| forbid / allow | Drops and doors pawns may not use, or may use again | — |

Designation: `{id, kind, work_type, target:[x,y,z]|{entity}, blocks, label, created, status: open|failed, reason, fails, wait}` (`wait`: why it
waits without failing, "needs a stone pickaxe": see "Tools and gear").

A chop designation, order or zone means whole trees. Any log or leaf of a tree stands for it: the canopy top a top-down click lands
on (or the air just above it), a branch, or a box drawn only over the crowns. Each resolves to the tree's trunk base (the lowest log
standing on ground), and the box's columns are searched 32 blocks below its bottom for the trunks. Branches and crowns are never trees
of their own.

A chop job also carries `wood` (the version's log item names), `wood_blocks`, `planks` and `logs` (the tree's logs, lowest first).
`story_pawn` uses them to work a tree from an empty inventory. It fells what it can reach. If some logs came down but not all, it
crafts planks from them in the 2x2 grid (clutch and pillar blocks for the work up high) and goes once more at the lowest log still
standing. The fall-safety rules of `chop_tree` are unchanged. Logs still out of reach after that make a partial job: reported done
with a note starting `partial:`. The designation stays on its lowest log left for another go (twice at most); then it leaves the map
and an alert says what was left (see "Tree farming and routes under ground").

### Home

The colony's home is set at founding (`home`) or moved with `POST …/home`. A pawn farther than 48 blocks from home (or in another
dimension) when it joins, when the colony is founded or resumed, or when the home moves, is brought there:
- **1.12.2.** Main sends `/tp <pawn> x y z`, at most once a minute per pawn, to a standable spot at home: solid ground (never
  leaves, liquid, fire or cactus) under two free cells. That is the home cell itself when it is one, else the open-sky surface of the
  nearest column within 4 blocks that main's view has loaded. The line is armed in the command gate's scoped exception, which takes
  only that exact shape, only for the hive's own managed `maid_` accounts, and never when the config blocks `/tp`. It is audited with
  the reason. The pawn's current job is cut, and it picks again at home. The colony owner's founding or home move is the request.
- **b1.8.1, or when the gate refuses.** b1.8.1's `/tp` takes no coordinates, so the pawn gets a move order home that runs before
  any work.
- **Another dimension.** A pawn can't be teleported across dimensions, so an alert asks for it to be walked through a portal.

Each pawn moved logs one line (an alert): "X brought home to x,y,z from N blocks away (why)".

## Zones (cell sets, added 2026-11-03)

Fox 11-03: "Removing zones doesn't seem to work either, and overlapping zones seem to create two zones over top of each other when
they should just mesh into one zone. Overlapping zones of a different type should overwrite what was overlapped with the type of zone
selected."

Why remove failed: the web's Remove zone tool always sent `kind: "home"` (it erased only houses), and the bot matched the dragged box
in 3D, so a zone painted while the map showed another level was never hit; it also removed whole zones, never cells.

**Model.** A zone is a set of XZ columns plus its level range, with its settings (kind, name; growing zones: planting bills, sow,
harvest). Zones are column-wise like the map they are painted on: overlap, merging and erasing ignore height. The level range
(`area` y: what it was painted at; the union when zones merge) is what the scans that need a height read, as before: a quarry mines
ores within it, a growing zone finds its soil near its bottom, a keep-out zone forbids blocks within it, beds are looked for 8 above
and below a house. `area` x/z is the cells' bounding box. Stored as normalised row runs `rows: [[z, x1, x2], …]` (sorted by z then x,
each run maximal; the state sends them with `cells`, the count). Older saves (one rectangle per zone) load as the same cells and are
saved as rows at once; a growing zone's single `plant` becomes a one-bill list.

**Painting (add, kind K).** RimWorld's rules:
- Cells already in a zone of the same type stay. Painted cells that touch (4 neighbours) or overlap zones of the same type join them;
  touching several merges them into the oldest (its id, name and settings; the others are reported in `merged` and leave the
  state's `removed.zones`). Cells touching none make a new zone (`created`). A zone of more than 65 536 cells is refused.
- Same type = the same kind, and for growing zones the same plant (the zone's first bill's). A growing zone of another plant is
  another type: it overwrites. `into: id` (the web's Extend, or Shift-drag from inside a zone) paints into that zone: the cells take
  its type and it keeps its id.
- Work zones (farm, chop, mine, fish) are mutually exclusive: the painted cells leave every other work zone (`shrunk`; a zone left empty
  is `deleted`, a zone cut in two `split`).
- Home areas, keep-out areas, drop spots and Preserve zones are **overlay layers** (RimWorld's home area and allowed areas are layers too): painting one never
  overwrites anything and work zones never overwrite them. A house can cover a field; a keep-out area can cover a quarry. Each
  layer merges only with itself.

**Erasing (remove with an area).** The cells leave the zones of the kind given (no kind, or `any`: every kind, overlays included).
A zone left empty is deleted; a zone cut into parts keeps the largest part and its id, and each other part becomes a zone with the
same settings, its name numbered " 2", " 3" (`split: [{from, into}]`). `{op: remove, id}` still deletes one whole zone.

**Consumers read cells.** "At home", the bed's house and the idle wander area use a column index of the home zones (rebuilt when a
zone changes); the picker's index holds the keep-out columns; quarries, woodlots, farm zones, growing zones, bed and bench scans
walk the cells (a tree counts when its trunk stands in a cell).

**Web.** The Architect's Zones tab: the work zones (Growing zone with its plant, Woodlot, Quarry, Fishing zone), the overlay layers (Home area,
Keep out), and Erase zone with the kind it erases (Any by default). Zone tools stay in hand until Esc or a right-click (the genre's
way); Shift-drag from inside a zone, or Extend in the Growing zones window, extends that zone. While dragging, the map previews
the result with the same rules (`paintZones` / `eraseZones` in contracts): the zone the cells end up in (the merged shape) in the
tool's colour, cells taken from other zones in red, and a line ("18 cells · merged 2 zones"). Zones are drawn by their cells with
one outline around each zone: work zones tinted with a dashed edge (growing zones labelled with what they sow), houses with the
home-area tint, a solid edge and the name, keep-out with red stripes. The Houses and Growing zones lists show each zone's cells.

## Farming (added 2026-11-03)

Fox 11-03: "I don't see the ability to plant a specific type of plant, or how acquiring the seeds will work."

A **growing zone** is a zone of kind `farm` with a `plant` (Zones tool → Growing zone, with a plant picker). Without a plant a farm
zone works as before (ripe crops, bare farmland). `sow` and `harvest` (both on by default) switch tilling and sowing, and harvesting.

**Plants** (`plants` in the full state: `{id, label, block, seed, seed_item:[id,meta], soil, harvest, source}`):

| plant | versions | soil | harvest |
|---|---|---|---|
| wheat, pumpkin, melon | both | farmland (dirt or grass is tilled first) | wheat: ripe at age 7, sown again; pumpkin, melon: the fruit beside the stem (every other row is left for it) |
| carrots, potatoes, beetroot | 1.12.2 | farmland | ripe (7, 7, 3), sown again |
| sugar cane | both | sand, dirt or grass right beside water | cut above the bottom block |
| cactus | both | sand with nothing solid beside it (a checkerboard) | cut above the bottom block |
| nether wart | 1.12.2 | soul sand | ripe at 3, sown again |
| oak, spruce, birch saplings (jungle, acacia, dark oak on 1.12.2) | both | dirt or grass, on a 3-block grid | the grown tree is chopped |

**Jobs** (Farm work, by priority and schedule like any other): harvest what is ripe first, then sow the empty cells that suit the
plant (the seed item is fetched from storage when the pawn has none; pending sow jobs reserve seeds), then till dirt and grass within
4 of water (kind `till`: the pawn holds a hoe and uses it on the block). Tilling needs a hoe: carried, fetched from storage, else a
count bill for a wooden hoe at the colony's crafting table (an alert when there is none). Produce goes to storage by the filters,
like any gathering job.

**Validation.** Adding or updating a growing zone is refused when the plant doesn't exist in this version or nothing in the zone can
grow it ("Cactus grows on sand: there is none free in the zone"). It answers `warnings` for what only slows it ("no water within 4
blocks of the zone: dirt there isn't tilled").

**Seeds.** The zone's state says what it has and where seeds come from:
`{plant, sow, harvest, status: "12/40 sown, 6 ripe[, N too far from water]", seeds: {item, have, needed_for_empty, source_hint, missing, action}, needs_hoe}`.
`have` counts every storage and every pawn's inventory. With none and cells to sow, `missing` says what to do about it, and the
colony gets an alert (at most every 10 minutes per zone):
- wheat: "No wheat seeds — cut tall grass to find some". `action: "seek_seeds"`: `POST …/zones {op:"seek_seeds", id}` designates
  `cut` on the tall grass and ferns within 24 blocks of the zone (up to 64, never flowers). Explicit, never automatic.
- carrots, potatoes: "Needs carrots — none in storage". The crop itself is the seed: harvests, zombie drops, village farms.
- beetroot: harvest beetroot, village chests. Sugar cane and cactus: the item itself. Saplings: leaves of chopped trees.
- pumpkin, melon: `action: "craft_seeds"`. With a pumpkin (or a melon slice) stored, the colony adds a count bill for 4 seeds at its
  crafting table: by itself when a pawn would sow, or on `seek_seeds`.

**Planting bills (added 2026-11-03).** Fox: "I should be able to add harvest bills, or maybe planting bills, to zones — as a way to
choose which types of seeds to plant there." A growing zone has an ordered list of bills, like a workbench's:
`{id, plant, mode: forever | until | count, target, done, paused}`. `until` is met while the colony has `target` of the produce
(wheat, the carrot, the potato, beetroot, the pumpkin, the melon slice, the cane, the cactus, the wart, a tree's logs) in storage and
on pawns; `count` when `done` harvests reach `target` (each finished harvest in the zone counts toward the first unmet count bill of
that plant). The **active bill** is the first unpaused bill whose target isn't met: it decides what is sown and tilled. When it is
met sowing moves to the next bill; crops already in the ground are harvested as their own plant (sown again only if that is still
what the zone sows), never ripped out. No active bill: the zone keeps harvesting and sows nothing (status "all bills met: …").
A zone's `plant` is its first bill's (what painting calls its type; `update {plant}` changes the first bill). Ops on the zones
route: `bill_add {id, plant, mode?, target?, paused?}`, `bill_update {id, bill, …}` (a new plant or mode restarts `done`),
`bill_remove | bill_pause | bill_resume {id, bill}`, `bill_reorder {id, ids}` (16 bills at most). A bill's plant is validated like
a zone's (this version, the zone's soil). State: `bills: [{id, plant, label, mode, target, done, stored?, paused, met, active,
seeds: {item, have, needed_for_empty, source_hint, missing, action}}]`, `active_bill`, `active_plant`; `seeds` and `status` are the
active plant's. The chain stays automatic: till (a hoe, else a hoe craft task) → sow → harvest → replant or the next bill.

**Web.** The Zones tool's Growing zone shows a plant picker (icons from the item atlas): the new zone's first bill. The Colony tab
opens a Growing zones window beside it: each zone's cells, Extend, what it sows now ("Sowing: Carrots"), its planting bills (Add bill
with the plant picker; mode and target; drag or the arrows to reorder; pause; remove; the active one marked "sowing", met ones
"met"), status, seeds (have, cells to sow, where from, what is missing), Sow and Harvest switches, "needs a hoe", and the action
button ("Designate grass cutting nearby" or "Craft seeds (a bill)"). The map labels a growing zone with what it sows.

**Keeping the field (added 2026-11-06).** Fox: "The farm looks really bad. I don't know why it's not being maintained." Live (story-1,
a 1,565-cell wheat field): 142 cells grass or dirt for 9 hours, "harvest failed: … not ripe (0/7)" ×175, "seeds … had no effect",
"chest at 216,64,400 didn't open (blocked above)" ×146, and every chest full.
- *The hoe.* Soil to till and no hoe anywhere makes the hoe's craft task (immediate: a crafter takes it before other work) whatever
  else the zone offers first, looked at every 30 s. Only an open task counts as "already asked for": an owner's bill for a hoe waits
  its turn in the owner's list (live: an old "wooden hoe 1/1" bill there kept the task from ever being made). An unfinished one-off
  bill the colony made for a need (a hoe, a rod, seeds) moves to the tasks on load.
- *Tall grass on soil to till* is cut first (`till` jobs carry `clear_at`; a hoe does nothing under a plant).
- *The pawn's own view.* Each ripe, empty or tillable cell offered is checked in the picking pawn's own view first (its connection's
  world, when it holds the column): main's copy of a harvest-and-replant or a sowing can come after the pawns'. A cell the pawn sees
  differently rests 10 s and the next is offered. Ripeness is each crop's own last stage (wheat, carrots, potatoes 7; beetroot,
  nether wart 3).
- *Full storage.* A ripe crop whose produce no storage chest has room for is left standing (a ripe crop keeps in the field) while
  tilling and sowing go on; the zone's status adds "storage full: ripe crops left standing", and one alert (every 10 minutes at
  most) says storage is full, how many chests are full, and which chests within 24 of home aren't storage yet (or to place more).
  With no storage at all nothing changes (the drop spot).

**Walking fields (added 2026-11-04).** Fox: "The pawns need to crouch-walk through growing zones, so they don't crush plants or
tilled ground", "If the ground in a grow zone isn't planted or tilled, they don't have to crouch-walk — better for speed", and "In
the version we're using now (1.12.2) we can walk on wheat and not crush it. We can't sprint on it, but we don't have to crouch-walk
either, like the beta version." Each director tick hands every online pawn's body the colony's growing zones
(`IStoryHost.SetGrowZone`: the zones' cells at their painted level ± 2, an immutable filter rebuilt only when a zone changes). A move
takes care when it is onto or off farmland (anywhere, zone or not), or onto, off or past a planted cell inside a zone (a crop,
sapling, stem, cane, wart, cocoa in or beside the body's cells), read from the live blocks, so a harvested or untilled patch is
walked at full speed. The care is the version's (`IPhysicsRules.WalkingTramplesFarmland`): b1.8.1 tramples under a walker that
isn't sneaking (mc-dev `BlockSoil.b`, 1 in 4 per step), so those moves are sneaked (the server sees the entity action; a sneaking
body is never sped up); 1.12.2 tramples only on a landing from more than half a block (`BlockFarmland.onFallenUpon`), so they are
walked at normal speed, never sprinted (`PathStep.Gentle`). On both, a path prefers an aisle to the soil, no drop and no gap jump
ever lands on farmland, and a hop onto it (only out of an irrigation trench, a fall of about 0.3) costs as much as a 20-block walk
round.

## Quarries (added 2026-11-04)

Fox 11-04: "The quarry zone didn't make sense — nothing happened." (A mine zone only offered its ores: a square of stone and dirt
had no work at all.) A mine zone now has a mode: `quarry` (the default for new ones) or `ores` (the old behaviour; zones from before
keep it). `{op: add, kind: mine, area, mode?}`, `{op: update, id, mode}`.

**Digging.** A quarry digs everything in its columns, layer by layer from its top (its highest ground when first seen, not trees;
kept as `quarry_top`) down to bedrock: never bedrock, never y 0; each column stops one over its highest bedrock (b1.8.1 and 1.12.2
put it at y 0..4). A layer is offered once the one above has nothing left to dig, resting cells included: a layer is never left half
dug (live 11-04: "no pawn wants to mine in it any more after about the 7th depth layer": cells that rested after "no path" failures
were left behind, the floor went uneven, paths dropped 2–3 blocks onto cells being dug and pawns got stranded on the leftovers).
The scan starts from the top every 30 s, so a cell left or refilled above is found again. The whole stair from the top down to one
step under the working layer is checked against the world each scan; a missing step is placed before anything else, and while a
step at or above the working layer is missing nothing deeper is dug. Why a quarry offers nothing ("stair broken at y 59: placing
a step", "3 cell(s) on y 57 resting after failures", "waiting for the stair step over y 57") is in its status and the idle audit. Mine work; only exposed cells (a face open), never one someone stands on, the pickaxe its material needs (tool
floors and rules, StoryColony.Gear.cs), loot batched like any gathering. A cell beside lava or water is
left standing as a seal; lava or water in the zone is covered first; caves, falling blocks and spawners too (see "Quarry hazards").

**The stair.** Along the inside of the zone's edge: the edge cells in a ring, clockwise from the north-west-most one; layer k
(y = top − k) leaves ring cell k mod n standing as a step. So the steps go down one block per cell around the ring, a helix from
the surface to the bottom: each step is beside the last and one lower, and the next step in the same column is n ≥ 4 layers up, so
the 2 cells over every step (and the one over those, for the jump) stay open. A 2×2 zone's ring is its 4 cells: a spiral shaft,
1 step and 3 dug cells per layer. 3×3 and larger use the edge ring (the middle is all dug). A 1-wide zone has no ring: painting one
as a quarry is refused ("a quarry needs at least 2×2 (a 1-wide strip is a trench…)"); Ores only still works there. Steps are the
existing blocks; a step that is air, liquid or would fall (sand, gravel, concrete powder) gets a cheap block placed first (cobblestone,
dirt, netherrack, planks, stone, sandstone: carried, else fetched from storage; none: an alert), before the layer under it is dug
(the cell under a step waiting for a block is held back). The quarry never digs its own steps. Repainting a quarry recomputes the
ring (its top stays).

**Walking the stair.** A pit with a floor is no "edge" to sneak at: a path step sneaked only because of such an edge is walked (the
pathfinder keeps its edge costs, so routes don't change; fields and growing zones stay sneaked), and the edge guard forces sneak and
stops sprint only beside a fatal drop (the void, lava, water over the void: `Safety.IsFatalColumn`). Beside a quarry's pit the stair is walked normally (live 11-04: "they crouch-walk when the stairs get high on the
outside wall"); the guard's per-tick simulation still never lets the body step off, and drops into the pit stay limited to safe ones.

**Digging from where a face is in view.** Live 11-04 (Fox: "the pawns refuse to go into it. They get the idea to do it, they go to do
it, they get almost down the stairs and then cancel that idea"; "dig 181,57,366 failed: can't reach stone" ×14 from as many pawns, the
layer held for it): the last cell of a layer was the one under a stair step: the step on top of it, the next step beside it, its open
faces toward the pit floor. The walk "within 3" ended on the step over it, and a dig's reach walk (distance only: eyes within reach of
the block's centre) was met right there, with the step in between; the dig gave up without a face in view. Now a dig that arrives
within reach without a face in view walks on to a spot that sees one (the same goal as using a chest: eyes in reach and a ray to a face,
never in the block's own column), here down the last step onto the layer's floor, and aims again; only when no spot it can walk to sees
a face does it fail ("can't reach stone at …: none of its faces is in view from a spot I can walk to"). Every dig gets this (client,
`MinecraftClient.DigAimFromViewAsync`); a dig that worked before walks exactly as before. A quarry cell resting after failures is named
with its last failure in the status and in the idle audit of a pawn that found nothing there ("waiting: quarry z1: 181,57,366 resting
after failures (can't reach stone)"; several: "3 cell(s) on y 57 resting after failures (e.g. 181,57,366: can't reach stone)").

**The pit's rim is no void walkway.** Live 11-05 ("9 cell(s) on y 63 resting after failures (e.g. 195,63,366: won't dig dirt at
195,63,366: it's the floor of a walkway with the void 1 away (dig only the taught cell))", ×3 each): the client's skyblock rule that a
free-standing dig never takes the floor of a walkway near the void (`MinecraftClient.WalkwayFloorVeto`, live 10-27 B21: a script dug
the island floor beside the cobble generator) counted any drop over 3 as "the void", so the quarry's own pit, dug to bedrock, made
every top-layer cell on its rim a void walkway and the quarry never got past that layer. The veto now counts only fatal drops (the
void, lava, water over the void: `Safety.FatalEdgeDistance`, built on `Safety.IsFatalColumn`, like the edge guard's sneak); a pit,
a ravine or a cliff with a floor is not one. On a skyblock island the floor beside the real void is refused exactly as before.

**Quarry hazards** (added 2026-11-05). Fox: "the quarry will also need to take into consideration natural caves that it can come into,
and lava and other dangerous things." Each cell of the working layer is judged against the world on every look
(`StoryColony.QuarryHazards.cs`):
- **Caves.** Cave air in the zone is already dug (no work; its floor and walls are quarry cells as usual; a missing step over it is
  placed by the stair repair). A cave beside the zone (open cells outside it, under ground or over a drop of 20 or more; another
  quarry's pit is no cave) is walled off: the zone's edge cell beside it is left standing; an edge cell already open (the cave runs
  through it) gets a block placed. Where the edge cell is the stair's head room (up to 3 over a step) it is dug and the cave's cell
  beside it gets the block instead, so the stair stays walkable. Sealing walls mobs out too.
- **Lava and water.** A cell with lava or water on any of its 6 sides (flowing down from over it too) is never dug: it stands as a
  seal. Lava or water in the zone itself gets a block placed in it (cobblestone, stone or dirt: never anything that burns) before the
  cells round it are dug; with no liquid left beside them they are dug later like any other.
- **Falling blocks.** A cell under sand or gravel left standing (beside water) is left standing too: nothing falls into the pit.
- **Monster spawners** in or beside the working layer (the zone's box and 4 round it, 4 under to 1 over) stop the quarry ("stopped:
  a monster spawner at …"), with one alert, until the spawner is gone.
- **A pawn in danger** at a quarry cell (in lava, suffocating) leaves that cell standing from then on.
- **Walled-in cells.** A cell no dig can reach (closed in by steps, seals and the outside, no cell to dig on the way: a corner under a
  step behind a cave's wall) is left standing, or the layer would wait for it for ever.

A seal to place comes before any more digging (like a missing step; one resting after failures doesn't hold the quarry). Each hazard
is alerted once, not once a look: cells near a hazard already said (a cave within 8, a pool of lava or water within 4) are the same
one ("Quarry z3: breached a cave at y 52: 4 opening cell(s) walled off"). The status ends with what was sealed ("…; sealed: 4 cave,
2 lava (breached a cave at y 52)"), and the state's `quarry.hazards` has the counts by kind, `spawner_at` and `to_seal`.

Tests: `StoryQuarryHazardTests` (a breached cave walled off and never walked into, the quarry to bedrock; a cave by the stair's head
room sealed outside; a cave inside the zone already dug with its steps placed; lava and water in the zone covered with cobblestone,
never planks, never a cell dug beside them, said once a hazard; nothing dug from under standing sand; a spawner stops the quarry
until it is gone; a cell where a pawn met lava left standing) and `StoryFakeServerTests.A_quarry_walls_off_a_breached_cave_and_leaves_a_seal_by_lava_and_moves_on`
(a pawn, both versions).

**Quarries win over builds.** Live 11-05 (Fox: "The pawns are broken at the quarry. They keep placing and deleting blocks on the top
level"): a dirt floor build (b11401, 432 blocks at y 63) covered the top layer of quarry z17860. The quarry dug its cells, the 5-second
reconcile saw them missing and reopened the build ("Build b11401 (floor (432 blocks, dirt)): 3 blocks no longer match the design (3
missing): reopened", every few seconds), builders placed the dirt back, and the quarry's full look from the top found its first layer
refilled and called every pawn up out of the pit to dig it again (a pawn at y 46 was handed rim placements: "its nearest support face is
18.7 blocks from my eyes"). Now a cell is never both to dig and to place (`StoryColony.QuarryConflicts.cs`):
- A build's placed cell in a quarry's columns, at or under its top, is **blocked** with the reason "conflicts with quarry z…: the quarry
  digs this cell" (the map's failed-cell marker): never offered, never reopened by the reconcile, never support another build waits for.
  One alert per build and quarry ("Build b7 (floor …): 38 blocks lie in quarry z3, which digs them out: left unbuilt. Move or remove the
  build or the quarry"). A build with nothing else left is `blocked`. Queuing such a build answers it in `warnings`; painting a quarry
  (or switching a mine zone to quarry) over a build's cells answers "overlaps build b7 (…): the quarry digs N of its blocks". Erasing
  the quarry (or moving the build) frees the cells on the next pass. The quarry's stair steps are its own: placed where the stair needs
  them, never dug.
- A pawn down in a quarry pit (feet in its columns, more than 2 under its top) is offered no build cell more than 3 above it while a
  builder up top (not in a pit, not under ground, idle or building) can take it; a lone builder still gets it and walks up the stair.

**State.** A mine zone has `mode`; a quarry adds `quarry: {top, bottom, depth, layer, y, cells_left, stair_fixes, sealed, done,
stair: [[x,y,z], …] (top first), status}` ("layer 3 of 61 (y 61), 34 cell(s) left", "done: down to bedrock").

**Web.** The Zones tool's Quarry shows a mode picker (Quarry (everything) / Ores only). While dragging a quarry, the map draws the
stair ring as numbered steps (step 1 at the top) and the label says how many steps a turn and about how deep it goes. Quarry zones
draw their ring with the step being worked highlighted. The Colony tab's Quarries window: progress (a bar, the status), from y to y,
the stair's length, steps waiting for a block, sealed cells, and the mode (switchable).

## Preserve zones (added 2026-11-06)

Fox 11-06: "A zone that protects the current state of the blocks, and it can be a three-dimensional zone. You select it 2D, and then in
the settings you can change how deep it goes. This will keep the state the same: if a creeper blows up or something changes, one of the
pawns gets queued to fix it to the original state. I want to be able to toggle it on for any other zone as well in that zone's settings.
For example my wheat field has taken some hits — craters from creepers. If I could protect its state with the same mechanic, the pawns
would keep the flat land it farms on."

**Model.** Preserve is a setting any zone can have (`ZoneState.Preserve`: on, `below`, `above`, the snapshot), and a zone kind of its
own, `preserve`: an overlay layer like the home area and keep-out (painted 2D with the zone tools, never overwrites or is overwritten,
merges only with itself, always on). A mine zone refuses it ("a mine zone digs its blocks: it can't preserve them (paint a Preserve zone
beside it instead)"). The kept region is the zone's columns from `below` blocks under its painted level (its lowest) to `above` blocks
over it (its highest): defaults 1 and 3, each 0..32, at most 262 144 blocks per zone (`StoryColony.Preserve.cs`).

**Snapshot.** Switching Preserve on (or "Re-take snapshot", `retake`) records the block id and meta of every kept cell from the colony's
view. A column not loaded yet is recorded as unknown and filled the first time a pass sees it loaded; a pawn's registered temporary
block is recorded as air (it is cleaned by the temporary-block rule, never kept). Changing the range or the zone's cells (painting more,
erasing, a split, a merge) keeps what the snapshot had for the cells still in and takes the new cells from the world at that moment; a
preserved zone merged into another gives its values over. Saved with the zone (`preserve.snap`): the snapshot's own columns as row runs,
a palette and run lengths, base64 (`PreserveCodec`): a 36-column, 6-layer field is a few hundred characters.

**What counts as a change** (`PreserveRules`). Each snapshot cell is one of:
- *ignored*: leaves (they decay), and in a growing zone or a woodlot logs too (trees grow and are felled there);
- *open*: air, or what comes and goes in an open cell by itself: tall grass, flowers, ferns, saplings, mushrooms, vines, lily pads,
  crops at any stage, stems, sugar cane, cactus, pumpkins and melons, cocoa, snow layers, fire, water and lava. Only a *foreign* block in
  it (anything not on that list: a block dropped in a field, a pillar, cobblestone) is a change; it is dug out;
- *kept*: any other block. It must be there again by the build rule (`Matches`, with the drift table `StoryDrift`): the ground family is
  one block (dirt, grass, farmland, and on 1.12.2 coarse dirt, podzol, mycelium, grass path), a lit furnace is a furnace, turned blocks
  keep their facing, a door's or bed's second half comes with its first. Runtime metadata bits are ignored while facing and half are kept: redstone
  power, pressed plates and buttons, levers, a cake's bites, a cauldron's water, a record, a brewing stand's bottles, doors, trapdoors and
  gates open or shut (a b1.8.1 door's open bit reads as a turn), a bed's occupied bit, a repeater's delay, a piston's extension (its head
  in an open cell comes and goes too), a dispenser's or hopper's trigger, rails' shapes.

So in a preserved growing zone, harvesting, replanting, growth stages, tilling (dirt ↔ farmland ↔ grass), trampling and the air above
the crops are never changes; only the soil's shape (craters, missing or foreign blocks) and blocks outside the crop layer are restored.
Containers' contents are not tracked (only the block); mobs, items and other entities never are.

**Repair = reconcile.** Every reconcile pass (5 s, before the builds' own: `StoryColony.PreserveRepair.cs`) compares each kept cell with
the world. The cells that differ are the ghosts of the zone's **repair build** (kind `preserve`, label "repairs: <zone>", one per zone,
first in the queue, Build work like any build):
- a kept block missing → placed. What goes back is `PreserveRules.Restore`: the ground family as dirt (grass can't be placed: dirt grows
  it, and farmland is tilled again by the growing zone, never placed), ores as stone (only silk touch gives them back), a runtime twin as
  its block; any of them standing counts as kept;
- another block in a kept cell → cleared first (the build's `clear`), then placed; its block turned wrong → taken back and placed again;
- a foreign block in an open cell → a dig ghost, done with the owner's ordered clear (`ordered: true` on the dig job, `clear_dig`);
- a block pawns can't place (a sign, a skull) → an unsupported ghost, shown and never a job.

From there the build machinery does it all: batches, stands, the support rule and scaffolds, the material from storage, craft tasks and
the materials pass ("waiting for 6 dirt"), blocked-cell alerts once per reason, misrotation. A cell that matches again leaves the build
(the delta's `removed.ghosts: ["<build>|x,y,z"]`); a done cell damaged again reopens. The repair build's "is done" alert is never said
(repairs come and go); the zone says it instead: one alert per zone every 10 minutes at most with the counts ("Preserved zone Wheat
field: 12 blocks changed (9 missing, 3 foreign): queued for repair (b31)"), the cells found between are counted into the next one. It
can be paused like any build; it can't be removed (400 "… switch Preserve off in that zone's settings instead"); switching Preserve off
drops it with the snapshot. A cell two preserved zones share is repaired by the oldest.

**Owner directives win.** A build's cells (any status: a finished build keeps its own through its reconcile), the owner's mine, chop and
deconstruct designations (not the colony's own cleanups) and a quarry's columns from its top down are *held*: never repaired while the
directive stands, and taken again from the world as the directive leaves them once it ends (a dug block's hole, a built wall become what
the zone keeps). Each meeting is said where it is made: a build queued over preserved cells ("build b9: 12 blocks lie in preserved zone
Wheat field: the build's while it stands, then kept as it leaves them"), designations (`warnings` on the answer), a quarry painted or
switched on over a preserved zone ("overlaps preserved zone …: the quarry digs its columns there (not repaired)"), and Preserve switched
on over builds, quarries or designations. A pawn's own temporary blocks in a preserved zone are cleaned by the temporary-block rule and
never restored.

**Never dug for other work.** Each pawn body gets the colony's preserved blocks as a filter (`IStoryHost.SetPreserved`, rebuilt when a
snapshot is replaced, handed over with the speed each tick): a kept cell that holds its block. The client refuses it to every dig
(`DigRefusal`: a pick, a script's dig, a pillar's, a clutch's or a shelter's dig: "won't dig dirt at …: a preserved block (the colony
keeps it; only its repairs dig there)"), the pathfinder's dig moves (`CanBreak`) and autonomous `mine` (`IsMineSource`). Only an ordered
clear (`DigForBuildAsync`, the repairs') passes. Open cells, held cells and changed blocks aren't kept, so a foreign block or a pawn's
pillar in a preserved zone is still anyone's to dig.

**API.** `POST …/zones {op:"add", kind:"preserve", area, name?, below?, above?}` paints one; `{op:"preserve", id, on?, below?, above?,
retake?}` → `{id, preserve, warnings?}` on any zone. The zone's state carries `preserve: {on, below, above, y1, y2, cells, taken_at,
build, unknown, held, damage:{missing, foreign, turned}, to_repair, status}` ("intact", "12 blocks to repair: 9 missing, 3 foreign;
waiting for 6 dirt", "…; repairs paused").

**Web.** The Zones toolbar has a Preserve kind; every zone's settings show "Preserve blocks" (on/off, Depth below, Height above, Re-take
snapshot, the y range and the status); the map draws the preserved region's outline, and the cells that differ are the repair build's
holograms (missing, clear, misrotated).

Tests: `StoryPreserveTests` (a crater in a preserved wheat field, both versions: dirt placed from storage, then tilled by farming;
harvest, replanting, tilling, trampling, grass, tall grass and snow over three growth cycles are never changes; a foreign block cleared
and the plank put back, one in an open cell dug by an ordered clear; the range taking cells in and dropping them; a build said, held and
kept as it leaves the cells; a quarry said and its columns unrepaired; the bodies' dig filter; the repair build never removed by hand,
paused, gone with Preserve; temporary blocks never kept and unloaded columns taken once loaded; the snapshot saved compactly and
reloaded; the codec), `PreservedBlockBuildDigTests` (the client: a preserved block refused to a plain dig, dug by an ordered clear) and
`StoryFakeServerTests.A_pawn_fills_a_crater_in_a_preserved_flat_field` (a pawn, 1.12.2).

## Fishing (added 2026-11-03)

Fox 11-03: "Make me a fishing zone where pawns can fish using fishing poles; it must be over water blocks."

**Zone.** Kind `fish`, a work zone (painting it overwrites other work zones' cells, and theirs overwrite it). It keeps only still
water: a column's top block near the painted level that is water with meta 0 (a source; flowing water and waterfalls are not
fished) under open air. Painting over land ignores the land (a field on the bank stays). Refused with no still water ("no still
water here"), fewer than 4 water cells (counting the fishing zones it joins), or no shore: a standable cell (solid floor, room for
the head, no liquid at the feet or under them, nothing that hurts) within 3 blocks of the water. The web's preview scans the
dragged rectangle for water (ids 8/9) and shows only the still-water cells; the mock director filters the same way.

**Work.** A work type `fish` (the Work grid's Fish column, after Hunt; new pawns draw its skill from its own seed so every other
draw stays as it was; older pawns start with it off). A pawn with Fish work picks the nearest fishing zone that wants fish, a shore
cell at least 2 blocks from anyone already fishing (lease `fish:x,y,z`), and the nearest water cell to it. It needs a fishing rod:
carried, else fetched from storage (the job's `fetch`), else a craft task for one at the colony's crafting table (3 sticks, 2
string; with no string in storage the colony hears "spiders drop it" instead, at most every 10 minutes). The job is
`{kind: "fish", at: shore, water, s: 150, max}`: the pawn script walks to the shore cell and calls `fish(water, s, max)`. The
catch is delivered to storage like any gathering (raw fish counts as raw food: it is stored, and cooking bills turn it into cooked
fish). A session's "N fish caught" counts toward the zone's `catches` and its first unmet count bill.

**Bills.** Optional, on the zones route like planting bills (`bill_add {id, plant: "fish", mode, target}`, update, pause, resume,
remove, reorder): `forever`, `until` N raw fish in storage, `count` N catches. No bills = fish forever; every bill met or paused =
the zone rests ("all bills met: not fishing"). A session stops early once the active bill's remaining fish are caught.

**State.** A fishing zone adds `{water_cells, fishers: [display name], catches, status, bills?}`. The Colony tab's Fishing zones
window (beside the Growing zones) shows them, with "Fish until 20 stored" / "Catch 10" bills, pause and remove.

**The primitive** (`MinecraftClient.FishAsync`, the hidden `fish {at, seconds, max}` tool, scripts' `fish(at, seconds, max)`):
hold a rod (the least worn; one under 5% is swapped for a spare when there is one), face the water, cast (use item), find our
bobber: an object of type 90 whose spawn data is our entity id (b1.8.1's 0x17 thrower, 1.12.2's Spawn Object owner).
On 1.12.2 a bite is the server's `entity.bobber.splash` sound at our hook. A delayed flight update must not be mistaken for
a bite, and a splash at another angler's hook is ignored. Beta uses `BiteDetector`: after floating for 10 ticks, a fresh
downward velocity of at least 0.12 blocks/tick or a drop 0.2 under the floating height. On a bite it reels in once and waits
for the fish; no bite in 45 s reels in and casts again. Cancellation takes the line back, and a vanished hook never causes
an extra use (which would cast again). One diagnostic line per cast records its outcome and duration.
The fake server (`FakeServer.Fishing`) toggles cast/reel on each use, defaults to a random 5–45 second bite delay (tests may
override it), and awards fish only inside the bite window. Modern tests receive bite sounds without synthetic bite motion.

## Construction

`POST …/builds`:
- `{op:"add", items:[Item]}` → `{ids}`. At most 64 items, each at most 4096 blocks.
- `{op:"remove", ids}`, `{op:"pause", ids}` or `{op:"resume", ids}` → `{ids}`.
- `{op:"reorder", ids}` → `{ids}`: the whole queue order, with the named ones first and the rest after them in their order.

Item: `{kind: wall|floor|roof|door|window|stairs|fence|block|remove|water|crafting_table|furnace|chest|bed|torch, area?:[x1,y1,z1,x2,y2,z2],
blocks?:[[x,y,z]], material?: <block name> | [id,meta], height?:1..12, roof?:{style: flat|slab|gable|none, material?, overhang?:0|1},
facing?: n|e|s|w, level?:y, name?, group?, infinite?, indoor?}` (`water`, `infinite`, `indoor`: see "Water").

`{op:"preview", items}` → `{cells, done, error, warnings, flow:{spread, cells}}` checks water items and says where the water would
run; nothing is queued (see "Water").

- **wall / fence:** the rectangle's perimeter (corners included) × height, from the area's y1 up. Walls default to 3 high,
  fences to 1. A wall with `roof` also gets its roof, one above its top.
- **floor:** the filled area at `level` (default: each block's own y). For multi-floor homes, a floor at a higher level over
  walls makes the next storey; the next storey's walls are items with their own y1.
- **roof:** over the footprint at `level` (default: the area's top y).
  - flat: full blocks.
  - slab: half slabs (b1.8.1 stone-slab metas; on 1.12.2, wooden slabs for planks).
  - gable: stair rows rising from both eaves to a ridge along the longer axis, a slab ridge when the width is odd, and the
    gable ends filled with the wall material.
  - `overhang:1` widens it by one on each side.
- **door:** 2 tall. The lower half is the job and the upper half comes with it. Facing meta: e 0, s 1, w 2, n 3.
- **window:** glass panes (or the material) in place of the wall blocks, which are dug first.
- **stairs:** a staircase rising toward `facing`, from y1 to y2.
- **Furniture** (crafting_table, furnace, chest, bed, torch): each block. A bed is two.
- **remove:** every diggable block in the area becomes a dig job.
- **mine (added 2026-11-03):** a placed mine design (docs/web/api.md "Mine designs"): `blocks` are the cells to dig at their
  absolute y (the design's y is kept: ore heights matter), `place` is `[[x,y,z,id,meta]]`, the blocks that go into dug cells
  (torches, ladders, rails). At most 4096 cells per item; a big mine is several items with one `group`. Each dig ghost carries
  the block the world view shows there when it is queued (its expected material, for the tools a pawn needs; 0 when the column
  isn't in view yet, and such a cell is only done once its column is). Refused when a cell is below y 1 or would cut bedrock;
  open cells (a cave) need nothing, liquids, fire and portals are left alone. Order: a cell opens once a neighbour is open (from
  the entry at the surface outward and down), top-down first, then nearest; a block goes in as its cell opens. The web places a
  mine at its saved y (only x, z and the turn follow the click), refuses one that would reach below y 1 and says its y range.
- **group:** the items of one placed design. A design with a basement (Design Studio dug cells) is queued as a `remove`
  item of its dug cells first, then its block items, all with the same `group`: its blocks wait until that dig is done.
  The design's ground floor goes on the clicked level and the basement under it.
- **A design's blocks (added 2026-11-03).** The web opens the design converted to the server's version (`designer.create`
  with `convertTo`): a design drawn for the other version keeps what maps (the substitutions are said with the placement
  hint) and is refused, naming the blocks, when one of them doesn't exist here. Each `block` item's cells get the item that
  places exactly that state (red wool, not wool; a spruce door's item for a spruce door); a second half (a door's upper, a
  bed's head, a double plant's top) comes with its first, never a job of its own. A water or lava source cell (meta 0) is
  poured like a `water` item (see "Water"). Blocks pawns can't place yet (crops, flowing liquids, signs, banners, skulls) stay
  `unsupported` ghosts: shown, said once in an alert, never a job, and the build finishes around them.

Build: `{id, kind, label, status: queued|active|paused|blocked|done, reason, blocks_total, blocks_done, material, order, created}`.
Ghosts: `[x,y,z,id,meta,build_id,state,kind,reason?]`, with state `todo|leased|done|blocked|unsupported|misrotated|clear` (the last
two: see "Builds reconcile with the world") and kind `dig|place|pour` (a dig
ghost's id/meta is the block expected there, read from the world view when it was queued; a pour ghost is a liquid source, water
9 or lava 11). A water build adds `liquid: water|lava` and `renewable` (its poured sources refill).

Execution:
- A pawn with Build on gets a batch of ghosts as one job (see "Building fast" below), by the picker and lease rules.
- Order: the first unpaused build in queue order gives out its jobs, only from its lowest unfinished level (support first).
  Blocks touching something solid go first, then the nearest. When all of those are leased, the next build's jobs go out.
  A build that only digs goes top-down instead (a buried cell opens once the one above it is gone). A build of a `group`
  waits while an earlier build of that group still has cells to dig (a dig that keeps failing doesn't hold it).
- Never a block where a player stands.
- Material: the pawn uses its own, or fetches it from storage (see "Building fast"). With none anywhere, the build is
  `blocked` with `reason:"need 34 wooden planks"` and an alert. It goes on by itself once some is stored (a Chop, Mine or Haul
  pawn, or a bill, can supply it).
- A ghost that failed 3 times is `blocked`.
- Facing, axis and half are the design's: each turned cell is placed the way its meta says (see "Rotation" below). Pillaring is
  the goto tool's own; a cell with nothing at all to place against gets a scaffold block (see "Builds reconcile with the world").

### Building fast (added 2026-11-03, night)

Live 11-03 (`story-1`, 10 pawns): "Building floors and placing blocks seems to go very slowly." The pawns' shifts were
`wait 256x 1023.8s, dig 40x 44.7s, goto 266x 23.5s`: about 90 % waiting while build cells were queued. The journal had
`place … failed: busy: maid_chan_102 is using chest 218,64,400` over and over, `can't place at …: its nearest support face is 8.7
blocks from my eyes`, and `194,63,395 is occupied by planks` raised as a warning for a cell that was already built. Causes:

- **One block per job, a stack per block.** Each ghost was its own job (fetch, walk, place one block, report), and its fetch
  asked for up to 64 of the item, enough for the whole build. The next builder was sent to the same chest for the same stack,
  because the director's ledger still listed it until the first job ended.
- **A busy chest was a failure.** The withdraw refused at once while another body held the chest, so the job failed. Each
  failure rested the cell (30 s, then 1 min, then 10 min), a pawn whose last three jobs failed rested 15 s, and a cell that failed
  three times was `blocked` for good. The pawns spent their shift in these holds.
- **No stand within reach.** A builder first went to a spot just outside the build's footprint at its lowest level. For a floor
  at ground level that spot is inside the ground, and for a wide floor it is far from the middle. The place tool then had to path
  in by itself, and when that didn't work the cell failed as if it were impossible.
- **Built is not a failure.** A cell that already held its block, because the colony's view lagged or a retry came after a
  placement that had worked, failed with "occupied by planks" and counted toward `blocked`.
- **A probe leaked cells.** The idle-wake check asked the picker for work, and a build answer marked its cell `leased` with no
  lease. Nobody was ever handed that cell again, so a build's last cells could hang.

Now:

- **A build job is a batch.** It holds up to 24 cells of one item (and facing) within 6 blocks of its first open cell, and comes
  with one `stand`.
  - The stand has feet and head open and solid ground under it, up to three blocks below the cell (a roof is laid from the
    ground). It is not the first cell and not on a batch cell.
  - The stand must have the first cell in reach, then as many as possible of the cells within 3 blocks of it. In reach means
    3.8 blocks from the eyes to the cell's centre, under the client's 4.3 to a support face. That way the place tool never walks
    on its own: on FakeServer, one that did walked into a hut and was walled in.
  - The stand may be another open cell of the patch, so the pawn stands in the middle of its patch. That cell is then left out.
  - Another builder's stand or a still-open cell of the build costs extra.
  - A wall or fence ring is built from outside only. Its stand is never on or inside the footprint, and its batch has only cells
    in reach of the stand, so builders never wall themselves in. (FakeServer: with stands inside the ring allowed, a hut's builder
    was shut in and couldn't get to a chest.)
  - The cells in reach of the stand come first, then the ones a few steps on. Cells left over between earlier batches are taken
    along this way instead of becoming one-block jobs.
  - A batch is one level: the next goes up once this one stands. (b1.8.1 FakeServer: a roof row laid early, over a wall still
    going up, had no visible support face, and its builder climbed onto the wall and was shut in.) Within each group, each cell
    has support when its turn comes: touching something solid or an earlier cell, then nearest the last one, along the row.
- **Fair share.** The batch is the pawn's share of the build's cells still to place (`ceil(left / builders)`, 1 to 24), no more
  than it can get items for. Ten builders on a 16×16 floor each get a part, and a seed next to another pawn's batch comes after the rest, so
  they spread out. Each cell is leased (`g:<build>|x,y,z`) to the pawn until it reports that cell. The cells stay its own while it
  works: they are renewed with the job's lease by the tick and by progress. A probe claims nothing: cells are leased only when the
  job is handed out (`ActiveJob.Assign`), by `Next` or when a wandering pawn is handed work. (The wander path once skipped this,
  and two pawns got the same cells: "busy: … is using block".)
- **One fetch per batch.** The job's `fetch` covers the batch less what the pawn carries, taken from as few chests as possible.
  Chests no other job is still fetching from come first, then the nearest. The amount a chest can give is what the ledger lists
  minus the fetches other jobs haven't made yet. When the items are out of the chests the script reports `fetched`, and the ledger
  is updated then, not when the batch ends.
- **A busy chest waits.** A withdraw at a chest another body holds waits its turn, up to 12 s (`ClaimAwareGameClient.WithdrawWait`),
  then says busy with the holder and the wait. The script waits 2 s, then 4 s, and tries again. A busy chest, a placement out of
  reach ("couldn't get closer") or someone standing in the cell is a transient refusal, as for designations. The cell rests 15 s
  with no fail count and no alert, and only the 5th in a row counts.
- **Per-cell reports.** `story.place(job)` (SDK) goes to the stand, then for each cell:
  - A cell that already shows the block (`want`) is reported done.
  - A cell listed in `clear` is dug first.
  - A cell out of reach makes the pawn step closer first.
  - A "too far" or "standing in it" refusal makes the pawn step closer, or back to the stand, and try once more.
  - Each cell is reported as progress `cell x,y,z done` or `cell x,y,z failed: why`.
  - When the pawn runs out of items, the rest of the batch goes back to the queue.

  The job ends `done` with `batch: n/m placed`. A cell left unreported at the end goes back to the queue with no failure, unless
  the world shows it built. (A job's own message now rides with every `done`. Before, the stash step's empty result replaced
  it, so a chop's `partial: …` note never reached the director.)
- **Already built is done.** A cell that holds the same block and variant (`DesignFacing.VariantMask`), turned the designed way
  where the placement decides it (see "Rotation"), is done at pick time, when the pawn reports it, and when a job ends or fails. "Occupied by" anything else is
  transient: the next pick clears the cell first (`clear`), and only repeated refusals count. A "done" for a cell that shows another
  block (another variant with the same name) is not taken.
- A job: `{kind:"place", at, block, want, cells:["x,y,z"…], metas?:[meta…], clear?:["x,y,z"…], stand?, facing?, fetch?:[{from, item, n}…]}`
  (`metas`: each cell's meta, when the batch has a turned block; see "Rotation").
  An older saved pawn script places only `at` and reports the whole job. Its first cell counts as built, and the rest go back.

Measured on the director with a simulated colony (`StoryBuildBenchmarkTests`): 10 pawns with only Build on, a 16×16 planks
floor and a 4×4×3 cobblestone wall ring, the planks and cobblestone split over two chests. The model: walking at 3.3 blocks/s,
a 1.5 s chest session with the chest held, 0.6 s per placement, 0.5 s per job to ask and report. "Exact" stock is what the builds
need and no more (what their auto bills make). "Plenty" is 320 planks and 42 cobblestone. Before is the old director with the old
client, whose withdraw failed at once on a busy chest. After is the new director with a withdraw that waits its turn.

| stock | before: blocks/min | before: idle | before: jobs, failed | after: blocks/min | after: idle | after: jobs, failed |
|---|---|---|---|---|---|---|
| exact | 108.6 (161 s) | 63 % | 328, 36 busy | 188.0 (93 s) | 10 % | 58, 0 |
| plenty | 150.9 (116 s) | 47 % | 347, 54 busy | 188.0 (93 s) | 10 % | 58, 0 |

The old run also depends on timing. In one variant of the model, ten cells hit their third busy failure, were `blocked` for good,
and the build never finished: 282 of 292 cells after 30 simulated minutes, 99 % idle. That is the live picture. The simulation
leaves out pathfinding and the reach failures, so live gains should be larger. Its pawns walk in straight lines, and every place
within reach works.

FakeServer 1.12.2 (`StoryFakeServerTests.Three_pawns_lay_a_6x6_floor_from_one_chest_fast_and_without_failures`): three pawns lay
a 6×6 floor from one chest with no failed cell (no busy chest, no "too far", no "occupied") in about 2 s of wall time (1.8–2.5 s
over the runs). The fake clients tick every 5 ms, so that is about 20 s of game time. Each pawn placed 12. The test allows 60 s.

### Rotation (added 2026-11-04)

Fox, 11-04: "When pawns place special blocks like furnaces that have rotation, they don't take into consideration the direction
they're facing when they place it to replicate the same rotation that was specified in the hologram." The ghosts already carried
the design's meta (the hologram showed it), but the pawn placed with the plain `place` tool: the client clicked the nearest
visible support face while looking at it, so the furnace faced wherever the pawn happened to stand, a log lay along whichever face
was nearest, and a cell holding the block turned any way counted as built.

Now:

- **The meta goes to the pawn.** The Architect's turn (Q/E, or the turn buttons) becomes the ghost's meta for furniture and single
  blocks too (`StoryConstruction.Faced`: a furnace's or chest's front, stairs' rise, a pumpkin's face; a piston from the palette
  turns from "down" to the facing). The Single block tool shows the turn control when its material faces somewhere and sends the
  turned meta, so the hologram is what gets built. A batch with a turned block carries `metas`, and `story.place` places those cells
  with the hidden `place_turned(block, at, meta)` tool (`MinecraftClient.PlaceTurnedAsync`).
- **The click that gives the meta** (`MineClaw.World.BlockOrientation`, per version, from the 1.12.2 decompile and b1.8.1 mc-dev):
  - From the yaw (said in a look packet right before the click; the server reads the player's rotation then): furnaces, chests,
    trapped chests, b1.8.1 dispensers, pumpkins, jack-o'-lanterns, glazed terracotta, repeaters and comparators face the player;
    stairs, fence gates, doors and beds take the look; an anvil the look turned clockwise.
  - From the stand: pistons, 1.12.2 dispensers and droppers point at the player in 3D, observers away. Up when the player is within
    2 blocks horizontally and the eyes are more than 2 above the block's base (a stand one block higher, beside it); down when the
    eyes are below its base (two lower); else horizontal from the yaw. The pitch is never read.
  - From the clicked face: torches, ladders, buttons, levers, end rods, shulker boxes, tripwire hooks, side-clicked trapdoors; a
    hopper outputs into the block clicked; 1.12.2 logs, hay, bone blocks, purpur and quartz pillars lie along the face's axis.
  - From the face and cursor height (1.12.2): slabs, stairs and trapdoors are the top half from the underside or high on a side,
    else the bottom half.
  - Doors and beds go on the top face of the block below. A 1.12.2 door is asked for the left hinge (designs don't keep the hinge;
    neighbours can still turn it).
  The client takes a click from where it stands when one reaches the face (a face in view first), else walks to the nearest stand
  that has one, says the yaw, sends the face and the cursor.
- **Checked after placing.** The world's meta must agree on the bits the click decides (`BlockOrientation.Same`: not the open,
  powered or lit bits; a b1.8.1 door's flipped form counts as its look, a b1.8.1 floor lever is 5 or 6 at random). A wrong one is
  dug and placed once more from another stand when the pawn can dig it for its drop (a furnace needs a pickaxe); else the cell
  reports `failed: placed with wrong rotation …`.
- **Shown, not hidden.** The director counts a cell built only when it is turned the designed way. A wrong one already there is
  cleared and placed again once; after that, or at once for a block that holds items (chests, furnaces, dispensers, hoppers,
  brewing stands, shulker boxes: never broken for their rotation), the ghost is `misrotated` with the reason ("placed facing north,
  wants facing east"), an alert says so, and it counts as finished for the build. The map keeps drawing it as a blocked hologram over
  the block, even once the build is done. A retry (`builds {op:retry}` / `retry_all`, the Retry tool) counts it as a failed cell:
  it starts over with its one take-back again (a container is only looked at again: turned by hand, it is done).
- **Not decided by the click:** rails (their shape follows the neighbours), a chest beside a chest of its kind (both halves take
  the newer one's facing, so a designed double chest faces one way; three in a row can't be placed in vanilla), an end rod on an
  end rod. Signs, banners and skulls stay unsupported ghosts.

Tests: `BlockOrientationTests` (metas → face, yaw, cursor, stand, both versions; the model against the fake server's own
transcription of the vanilla rules over every face, quarter, half and a ring of stands), `TurnedPlacementTests` (the client on
the fake server: a furnace, stairs each way, a wall torch, a log on its side, an upside-down stair, a top slab, a piston pointing
up from a stand over it), `StoryRotationTests` (the director), and `StoryFakeServerTests.A_pawn_builds_a_design_with_each_block_turned_as_drawn`
(a pawn, both versions).

### Builds reconcile with the world (added 2026-11-05)

Fox, 11-05: "The hologram of a house being built is failing a lot. We're not even detecting the stairs or other blocks that are
rotated the wrong way … It's also saying there's no way to place blocks, but there definitely is. I think it's put some blocks down
by accident." The hour before: `can't place at …: … is occupied by stone` ×116 for one build (9 000 such cell failures in five hours,
stone and dirt), `… has nothing solid on any side to place against (all air)` ×97 for another, and no rotation warning at all. Causes:

- **A cell was looked at only when it was picked.** Once `done` (by a pawn's report, or at queue time) it was never compared with the
  world again. Stairs placed before the rotation fix, or turned since, stayed `done`; so did a block broken or swapped later.
- **The clear's dig was refused, silently.** A wall cell holding natural stone or dirt was sent with `clear`, but the pawn's dig was
  refused by protection (`protection refusal (floor): stone at …: a placed stone (someone's floor, wall or path)`, 581 in that hour)
  or because the pawn stood on it. The script ignored the dig's result, placed anyway and reported "occupied by stone", which only
  rested the cell; it came back fifteen times.
- **Cells with nothing to click against were handed out.** A build's own lowest level went first, but a placed design is one build per
  block kind: the planks over the house's unbuilt stone-brick footing (short of cobblestone) and the roof over the unbuilt walls were
  each "their build's lowest level" and were offered at once, into the air. The seed order only put supported cells first; when they
  were taken, the unsupported ones went out too.

Now:

- **Every cell is compared with the world every 5 s** (`StoryColony.Reconcile.cs`), done builds included (paused ones not), by the
  same rule as at pick time (`Matches`: id, variant and the meta bits the placement decides; open, powered and lit bits never count):
  - the design's block → done (built by hand, or the view caught up), and a build whose cells all show the design is done;
  - air → to place;
  - its block turned another way → **misrotated**: taken back and placed again with `place_turned` (one take-back; a block that holds
    items is never broken for its rotation: it is shown and alerted, as before);
  - another block (natural stone or dirt, a block put there by mistake) → **to clear**: dug out first, then the design's block.
  A `done` cell that no longer matches on two passes in a row (one could be the view lagging a fresh placement) reopens with its
  failures forgotten, its build is queued again, and one alert says what changed ("Build b7 (stairs): 3 blocks no longer match the
  design (2 turned the wrong way, 1 hold another block): reopened"). So wrongly turned stairs in a house built before the fix are
  found on the first passes after the update.
- **The map shows it.** An open cell holding another block is sent as state `clear` with the reason "holds stone: dug out first", one
  holding its block turned wrong as `misrotated` ("placed facing north, wants facing east"); both are drawn as blocked holograms over
  the block in the way.
- **The clear is the owner's order.** `story.place` digs a cell in `clear` with the hidden `clear_dig{at}` tool
  (`MinecraftClient.DigForBuildAsync`): soft protection on natural ground (stone, dirt, grass, sand, gravel, ores, plants; on skyblock
  only what is a resource there) yields to it, because the colony owner ordered that cell built. A block someone placed, a chest or
  anything else that opens, and a never-break mark still refuse; every safety rule and the strict-harvest rule hold (stone takes a
  pickaxe: the job carries one). The bot's own digs keep every protection.
- **A refused clear is said once, not retried.** The script reports `cell … failed: can't clear the cell …: <the dig's reason>` and
  never places into an occupied cell. The director blocks that cell at once with that reason. Alerts for blocked cells are said once
  per build and reason (coordinates left out), so a hundred cells failing alike are one alert. A clear that is only "not now" (out of
  reach, the pawn stood on it) rests the cell as before. A pawn standing on the cell steps to the stand first, and a stand is never
  chosen on top of a cell still to clear.
- **Support is the client's own rule.** A cell is offered only when a neighbour gives a face to click by the pawn client's rule
  (`PlaceRules.IsSupport`: anything with a collision box that doesn't open when clicked: stairs, slabs, fences, panes, glass and leaves
  count; torches, flowers, chests and doors don't). A batch's later cells may lean on its earlier ones. Cells that touch a block still
  to be placed by another build (or another pawn's batch) wait for it ("build b7: 12 blocks have nothing to place against yet (the
  blocks beside or under them go in first)"). A level that truly floats (no support and nothing pending beside it) starts with a
  **scaffold**: the job carries `scaffold: {"x,y,z": "x,y,z"}`, the pawn places one of the batch's blocks at the spot (under the cell
  first, else beside it; never a build cell), places the cell against it, and digs the scaffold again. The fetch takes one block more.
  "Nothing solid on any side" from a pawn whose view lagged a neighbour's placement is a "not now" refusal, not a failure.

- **Natural drift is not a mismatch** (live 11-05, the first hour: dirt floors reopened ×22 in 7 minutes as grass spread over them
  and pawns tilled them, and the clears of the farmland were refused as a farm). A small table per version (`StoryDrift`) counts as
  the design's block: the ground family (dirt, grass, farmland; on 1.12.2 also coarse dirt, podzol, mycelium and grass path), and a
  block's runtime twin (a lit furnace, a lit redstone lamp, glowing redstone ore, an unlit redstone torch, a powered repeater or
  comparator: their facing still compared). Such a cell is done and never cleared. A decayed leaf, a broken or swapped block stays a
  real mismatch.
- **Reopening is said at most once per build every 10 minutes**; the cells reopened between are counted and said with the next alert
  ("… (14 more reopened since the last alert)"), and each pass is logged.

Tests: `StoryReconcileTests` (the director, both versions: a stair turned wrong in a finished build is found, shown, cleared and placed
again; stone in a wall's cells is cleared and a refused clear is blocked and said once; a cell over a pane, a slab or stairs is offered
and one over a torch isn't; a cell over another build's unplaced footing waits and a floating one gets a scaffold; a finished build whose
blocks were broken or swapped reopens and closes again when they are put back; a dirt floor that grew grass or was tilled stays done
while a stone in it still reopens and is cleared; a lit furnace is the furnace; reopening alerts once per window), `StorySdkTests` (the script: a refused clear is
reported as such and never placed into, a scaffold goes in before its cell and is dug after), `ProtectionReworkClientTests` (the
client: the ordered clear digs natural ground through soft protection, never planks, a chest or a never-break mark; a plain dig still
refuses), and `StoryFakeServerTests.A_pawn_fixes_a_wrong_stair_clears_a_stone_and_places_against_a_slab` (a pawn, both versions).

## Water (added 2026-11-04)

Fox 11-04: "I need a way to make them pour water down in different blocks so that I can make aquifers and hydroponics for
different farming." Pawns place water (and lava, off by default) with buckets, cell by cell, through the build queue.

**Build items.** `{kind: "water", blocks | area, material?: "water" (default) | "lava", infinite?, indoor?, name?, group?}`: each
cell becomes a still source of the liquid (ghost id 9 or 11, meta 0, kind `pour`). A design's water or lava source cells are
poured the same way. Checked when queued (`StoryColony.Water.cs`, 400 with why):
- Lava only with the colony option `allow_lava` (`POST …/options {allow_lava}`, default off); never in a home zone, never with
  something that burns (planks, logs, leaves, wool, fences, chests, benches…) within 2 blocks.
- Each cell is open (air, tall grass, flowers, snow, vines, flowing liquid) or dug first by its `group` (a `remove` item of the
  same group, earlier in the request or queued: the dig goes first, as for a basement). Anything else: "4,63,12 holds stone: dig it
  first". Water never goes onto lava, nor lava onto water (obsidian or cobblestone).
- Something must hold each cell: under it a block that stays (not dug first), another cell of the item, or a source of the same
  liquid. Else "nothing under 4,66,12 to hold the water: it would pour down" (once, at queue time: live 11-04 it was "no block
  face to pour against" cell after cell).
- Indoors (a home zone with a roof within 8 above) only with `indoor: true`: the Water source tool's explicit click sends it, the
  channel tool and the irrigation planner never do.
- A cell that already holds the source is done at once.

**Flow preview.** The answer (and `op: "preview"`, which queues nothing) carries `flow: {spread, cells}` and `warnings`: where the
water would run beyond the drawn cells, after the game's rules (BlockFlowing: straight down while it can fall, else 7 sideways,
lava 3; a fall lands and spreads again), and whether it would meet the other liquid. Fox wants deliberate placement: a trench dug
into the ground stays put; a channel laid on open ground runs off ("the water will spread to 40 cells beyond what was drawn (e.g.
4,64,11): place dam blocks first, or dig the channel into the ground").

**The pour job** (Build work, batched like other builds; `PourJob`). A batch is up to 12 cells of one water build (the pawn's
fair share), with one stand in reach of them (`StoryColony.WaterPour.cs`):
- **Queue order.** Water builds are poured in queue order: a later one waits ("waits for … : water builds go in queue order") while
  an earlier one still has cells to pour (a blocked one holds nobody up). Live 11-04 and the FakeServer canal: a pawn holding two full
  buckets poured the canal before the pool queued ahead of it (the pool's dug cells still looked solid to the colony), then filled
  its buckets from the canal itself ("scooped at 17,63,16"). A water build still being poured is never a fill source either: only a
  finished build's cells that refill.
- **Bucket.** The pawn's own (empty or full), else up to two from storage (full ones first: a full one pours at once, with no fill
  trip), else the build waits: the materials pass counts a liquid build's need as one bucket, not one per cell, and liquid builds
  share it. Any bucket the colony has, empty or full, in a chest or a pawn's pocket, covers it (live 11-04: a water bucket put in a
  chest still had "Make Bucket" asking for iron); with none, an auto bill "for build #N" (3 iron ingots at the crafting table),
  "waiting for a bucket (being crafted)", or "needs a bucket" blocked with one alert. Buckets are tools: the pawn keeps them
  (deliveries never take tools) and a pour job never stashes them, so the colony's working bucket stays out of the chests.
- **Fill source** (`FillSource`): a still source within 24 blocks of the cells that won't be used up: one the colony poured that
  refills (an infinite pool it built first, then a channel of its own that refills), then any that refills (two source neighbours over ground: a lake, a pool), then a
  source with another source beside it; never a lone puddle, never water the colony poured that doesn't refill (Fox's
  channels), never a cell of the batch. None and no full bucket: blocked, "no still water within 24 blocks to fill a bucket
  from". Lava: any lava source the colony didn't pour.
- **Order** (`PourOrder`): every other cell first, then the ones between them. A cell between two sources over ground becomes a
  source by itself (the game's rule), so a canal or a 2×2 pool takes about half the pours; the pawn finds those cells done.
- Job: `{kind: "pour", liquid, block: "bucket", want, at, cells, source?, buckets, stand?, fetch?, stash?}`.

**`story.pour(job)`** (SDK, `75-story.js`; the pawn script hands `pour` to `story.work`): for each cell, a source already there is
reported done; with no full bucket it fills every empty one at `source` (`fill_bucket`), goes back to the stand and pours aimed at
the cell (`empty_bucket`, lava `move_lava`: the pour aim lands it in that exact cell), then checks the cell shows a source of its
liquid (the hidden host call `storyLiquid(pos)` → `water | lava | flowing_water | flowing_lava | ""`). Each cell is reported
`cell x,y,z done | failed: why`; a cell that shows flowing water instead is a failure ("shows flowing_water, not a water source").
No way to fill a bucket ends the batch (the rest go back), or fails the job when nothing was poured. The director takes a cell as
done only when its view shows the source (water 8 or 9 with meta 0: a just-poured source is 8 for a tick) or still air (its copy
lags); flowing water there is not done.

**Infinite sources.** The Architect's "Infinite water source" queues a 2×2 pool (dug into the ground by default) with `infinite:
true`; poured as two opposite corners. Once its sources refill, the build is `renewable` and an alert says "… refills: pawns fill
their buckets there from now on"; fills prefer it over any other water.

**Irrigation** (`POST …/zones {op: "irrigation", id}`, nothing queued): the channels a growing zone wants. Farmland needs water
within 4 blocks, so for a crop a straight channel at soil level along the zone's longer side, one every 9 rows (a centre line in a
zone 9 or narrower; one more by the far edge when that would stay dry); sugar cane needs water right beside it, so one every 3
rows. A line whose band is watered already is left out, and so are indoor cells. → `{id, cells, dig, axis, lines, note}`: `dig` are
the cells holding soil (queued as a `remove` item in the water item's group).

**Web.** The Architect's Water tab: Water source (click: one cell, indoors allowed), Water channel (drag a line or an area: a
canal, a hydroponic tray), Infinite source (click a corner: a 2×2), Lava source (only while `allow_lava` is on), and the palette's
entries at the bucket's tier ("workbench"; a bucket anywhere in the colony unlocks them). A water tool docks its own bar
(`WaterTools.tsx`) where the material selector sits: "Dig the trench" (on for the channel and the pool: the cells go one block
into the ground, a `remove` item of the same group first). The placement shows as water holograms; on release the director
previews it: water that would run beyond the drawn cells (or an irrigation suggestion) waits in a confirm bar ("Water channel: 4
sources · spreads to 23 more cells", the director's warnings, Queue / Cancel) with the flow drawn as flowing-water holograms;
otherwise it is queued at once. The trench's cells (the `remove` item) show as red dig cells in the drag preview and the confirm
bar alike, apart from the water and its flow; queued, a trench cell shows its dig outline while the ground is there and its
water hologram once it is dug (a pour in a still-solid cell waits underground: fixed 2026-11-04, Fox: "The holograms for
digging the water trenches don't show up"; the dig outlines sat just inside the cell, so the ground around them hid them, and
the previews left the dig out). The Growing zones window says when cells are dry ("40 cells have no water within 4 blocks") with
Add irrigation: the planner's channels, pre-filled as a pending placement to confirm. The mock director (`storySim`) and
`@mineclaw/contracts` (`storyWater.ts`: `waterToolCells`, `waterItems`, `liquidFlow`, `pourOrder`, `irrigationPlan`) carry
reference copies of these rules.

**Canals that work (fixed 2026-11-04).** Fox, live 11-04: "The pawns have a really difficult time pouring water in the canals." The
log: 79 "no block face to pour against", 26 fills refused as "a protected water source" (the colony's own infinite pool), ponds at
the bottom of a pit ("no path to any reach (227,54,406)"), pawns nearly drowning in the channel, a `story_pawn` run at its 30-minute
limit with 429 actions, and "server correction" ledger lines for every bucket. Causes and fixes:
- **Pour stand.** `empty_bucket` stepped to within 2 blocks of the cell's centre when its face wasn't in view: from the bank of a
  1-deep trench the eye is 2.1 away, so only the channel itself qualified and the pawn walked into the water, where no face was in
  view either. Now it walks to a dry stand (feet, head and floor out of any liquid) from which a pour aim exists (`BucketOps.DryStand`,
  `Aiming.FindPourAim` from that cell's eye): the bank, looking down at the trench floor. A cell already holding flowing water takes
  the pour (the full bucket's ray passes liquids) and becomes a source. A cell with nothing solid beside or under it says so.
- **Protected sources.** Protection (farm irrigation, a generator, the colony's pool) refused every scoop. A source that refills
  (`FluidRules.Refills`) is never used up, so `BucketOps.Scoopable` lets a bucket take it; a lone or non-refilling protected source
  stays refused.
- **Fill sources.** The director takes only a source with dry ground beside it (a bank at water level, or a shallow step), prefers
  one near the cells' height (3 per block of height difference), and a pond a pawn couldn't fill at (`fill failed at x,y,z: why`, the
  SDK's progress note) rests 10 minutes for that pawn, with the cells of its batch back in the queue and no failure counted.
- **No retry loops.** `story.pour` tries a cell twice at most, reports it, and moves on; a fill that fails ends the batch at once.
  In water, the pawn gets out (`story.escape`) before it fills or pours.
- **A face to pour against (fixed 2026-11-04, later).** Live: 371 "nothing beside or under X to pour against" in 30 minutes, each cell
  tried six times. A pour needs a solid block beside or under the cell that stays (the bucket's ray passes water): the queue-time check
  now asks for one, and says "X sits on water already (the channel under it is full): draw it one block lower, in the trench" for a
  channel drawn one above a filled trench; a cell that fails that way is blocked at once. The skill, finding no face in its own view,
  walks to dry ground near the cell and looks again (a stale or unloaded column reads as air), and its refusal names what is under
  and beside the cell. Once on load (`ColonyMeta.WaterCellsFixed`), pour cells sitting right above their own group's dug cells move
  down into them (the channel's water goes in the dug cells: the web's dig and pour cells are the same cells) and those failures
  start over.
- **One bucket.** Water builds ask for the colony's bucket once: the first is crafting it (or short of iron, with the one alert);
  the others wait on it without a bill or an alert of their own.
- **Ledger.** 1.12.2 answers every item use with the whole inventory (`sendContainerToPlayer`): a bucket filling or emptying in that
  resync is the use ("a bucket filled or emptied"), not a server correction.
- FakeServer: `LiquidFlow` settles water after a pour or a scoop (a cell between two sources over ground becomes one; a source runs
  one step into an open cell beside it), and `PouredInto` records what each pour replaced. `StoryFakeServerTests.A_pawn_pours_an_infinite_pool_then_a_canal_every_other_cell_filling_at_its_own_pool`
  (b1.8.1 and 1.12.2) pours a 2×2 pool from two full buckets, then a 4-long canal every other cell, filling at that pool; the
  canal's end holds flowing water before its pour. `BucketPourTests` (skills) cover the bank stand, flowing cells and protected pools.

**Retrying failed build cells (added 2026-11-04).** Fox: "There are some water channels I can't unblock to retry to make them
fill." The Retry tool only covered designations; a build cell that failed three times stayed `blocked` for good.
- `POST …/builds {op:"retry", id | ids, cells?}` reopens a build's failed cells (all of them, or the given ones): blocked → todo,
  their failure counts, rests, soft failures and per-pawn skips forgotten, a blocked build queued again. `{op:"retry_all"}` does
  it for every build. → `{ids, reopened, reasons:[{id, reason}]}`: the build's checks run at once (a water build's cells, its
  bucket, water to fill from; ponds a pawn missed are tried again), and a reason that still holds blocks it again now, said in
  `reasons`, instead of after three more failures.
- A failed cell keeps its last reason (`GhostState.Reason`), sent as the ghost's 9th element: the map draws a dashed red ring with a
  cross on each failed cell (holograms on or off), and hovering one names it in the map's tooltip ("Build b3: 4,63,12 failed: …
  (Retry tool: O)").
- The Retry tool's drag (O) takes the failed designations and the failed build cells in the box (one `retry {id, cells}` per build),
  and its live count counts both (before, its count was always 0 and a drag sent nothing); "Retry all failed (N)" counts both and
  sends `retry_all`. The build queue shows "Retry failed cells (N)" on a build with failed cells.
- On the first load after this fix (`ColonyMeta.WaterRetried`), blocked water cells reopen (they were blocked before cells kept a
  reason, or by "no block face", "protected", an unreachable pond), and a water build blocked by those reasons is queued again.
- The mock director fails a pour cell with nothing under it at queue time (blocked, with the reason) so the retry UI has something
  to show; the bot refuses such an item outright.

**Tests.** `StoryWaterTests` (director: checks, flow, dig before pour, fill-source choice, bucket fetch and auto bill, infinite
sources, irrigation), `StoryPourSdkTests` (the SDK), `StoryFakeServerTests.A_pawn_digs_a_canal_fills_its_bucket_at_a_pool_and_pours_each_cell_a_source`
(b1.8.1 and 1.12.2: the fake server's vanilla buckets now scoop as well as pour), `storyWater.test.tsx` (web) and
`e2e/storyWater.spec.ts`.

## Storage

Storage is chests; there are no ground stockpiles (a `dump` zone is only where loot goes when the colony has no storage at all, see
"Hauling"). `POST …/storage`:
- `{op:"set", chests:[[x,y,z]], filter?:{allow:[…], deny:[…]}, priority?, label?}`. A double chest is one storage: its other half
  is found from the world.
- `{op:"remove", chests}`, `{op:"copy", from:[x,y,z], to:[[x,y,z]]}` or `{op:"assign_area", area, filter?, priority?, label?}`
  (every chest in the area) → `{chests:[[x,y,z]]}`.

Filters:
- Entries are `"*"`, a category id (any level), `"id"` or `"id:meta"`.
- An item is accepted when an allow entry covers it and no deny entry does. A new chest allows `"*"`.
- Priority is one of `low|normal|preferred|important|critical`.

Storage: `{chest, double_with, label, priority, filter:{allow, deny}, contents:[[id,meta,count]], free_slots, known_at}`. The
contents start from what the bot remembers of the chest. The director keeps them up to date after each deposit and withdrawal.

Chests that can't open and chests the colony builds (added 2026-11-06; live: oak planks lay on two double chests and every fetch
and delivery that chose them failed, ×241 in 7 hours; Fox placed 14 chests when storage filled up):
- A chest under a full opaque block (planks, stone, dirt, a crafting table…; either half of a double chest) doesn't open in either
  version. The colony reads that from the world (every 5 s per chest at most), leaves the chest out of every fetch and delivery, and
  says once (every 30 minutes) "Chest at X can't open: oak planks on top of it at X". A pawn's "chest at X didn't open" (a cat on
  it, anything the colony can't see) rests that chest for the pawn like "can't reach" (2, 5, 10 minutes), never the job's target;
  when every pawn has failed it, "Chest at X won't open for the pawns".
- A chest a colony build places (the Architect's chest, a design's chests) is storage once built, taking anything; one built
  beside a single storage chest makes it a double chest. The chest of an Architect chest build is crafted as an immediate task.

Web (added 2026-11-03): clicking a chest opens its inspector with the storage settings and the chest's contents as the game's grid
(27 or 54 slots) with how fresh they are ("Seen 3 min ago by maid_chan_102", "Open right now by …", "Story storage ledger", "Not
opened yet by any bot"), from `GET /api/world/container` (api.md §6: a bot's live window, its last look, the world memory or this
ledger, whichever is freshest); a furnace's bills inspector shows its input, fuel and output the same way. Zoomed in, every chest
on the map carries a badge: its category (the label, else the filter's summary) and a fill bar from the hive's container
knowledge (`GET /api/world/containers`, polled every 10 s over the chests in view) or, when that is older, from `free_slots`.

Categories come in the full state as `categories`: `{id, label, children:[Category], items:[[id,meta]]}`, where meta −1 = any.
- They are built per version from the item data plus a curated name list. Every item, and each dye colour, is in exactly one
  leaf; the rest are Misc.
- Top level:
  - food: raw meat, cooked meals, crops & produce, fish
  - plants: seeds, saplings, flowers & plants
  - wood: logs, planks, sticks
  - stone_building: stone, dirt/sand/gravel, glass, bricks & clay, wool & carpet, slabs/stairs/fences
  - ores_metals: ores, ingots, nuggets, metal & gem blocks
  - gems_minerals, tools, weapons, armour, mob_drops, redstone_mech, misc

Hauling (reworked 2026-11-04; Fox: "Dropped items should be auto-hauled by pawns that have hauling with high priority" and "if I
limit the types of items allowed in a chest, pawns … should rearrange … Otherwise they end up dropped on the ground"):
- **Where a stack goes:** the accepting chest with the highest priority, then the nearest, that has room (a full chest is skipped).
  Else, with the option `unfiltered_storage_takes_anything` (default on), any chest with room whose filter doesn't *deny* it: open
  (`"*"`) chests first, then the lowest priority, then the nearest. Else, when the colony has no storage at all, the **drop spot**: the
  nearest `dump` zone (a zone kind), else a standable spot by home. Else nowhere.
- **Nowhere** is a condition, not a stream of alerts: one alert "No storage for: Oak Wood, Dirt…" when the first kind has nowhere to go,
  and the state's `unstored_kinds` lists them (a kind drops off the list as soon as a chest takes it). A pawn keeps loot that has
  nowhere to go.
- **Deliveries:** a pawn that carries loot (after chopping, mining, harvesting, hunting or picking up drops) takes every stack to
  its chest in one trip (`stash` lists each chest, filter-accepted ones first). With no storage at all it drops them at the drop spot
  (job kind `dump`, `drop:[{item, n}]`). Tools, weapons, armour and one food stack stay with the pawn.
- **Drops on the ground** are a standing haul task when they meet all of these:
  - Not forbidden.
  - At home, or within 48 blocks of home or of colony work (a designation, a zone, a build).
  - Within 12 blocks of the pawn's or home's height, and not under ground.
  - Not within 4 blocks of a monster.
  - Not dropped by a player (not a pawn) at their feet in the last 10 s.
  - Not lying at the drop spot with nowhere better to go.
  - Somewhere to go.
  Designated drops come first. Then come drops at despawn risk: older than 3 of their 5 minutes (age from the first sighting by any
  body), labelled "hauling Oak Wood (despawns in 95s)". Then the nearest.
- **Priority:** hauling follows the pawn's Haul priority like any work type. Within one priority, a drop at despawn risk is hauled
  before the other work types of that priority. A working pawn is cut for a haul only when a drop despawns within 30 s, nobody hauls it,
  and the pawn's Haul priority is at least as important as its current work's. This never cuts an order, a need, an escape or a haul.
  It is the nearest such pawn, once per drop, at most one every 5 s.
- **Batching:** a trip picks up the drops within 6 blocks of the first one that go to the same place, up to the pawn's free slots
  (`collect` with a radius; each drop is leased, so no other pawn is sent to them; label "(+2 more)").
- **A collect that found nothing** ("no reachable drops nearby", live 11-04: ×36 in 4 minutes on the farm): when none of its drops is in
  the colony's view any more (another pawn or a player picked them up on the way, they merged, they despawned) nothing failed. A drop
  still in view rests for that pawn only (30 s, 1 min, then 5 min) and the others may go; only when 3 pawns missed it does it rest for
  everyone. A drop the pawn's own safety checks wrote off ("; 1 drop(s) left near the void") rests for everyone at once, as before.
- **Unreachable drops are kept by place (added 2026-11-04).** Live 11-04 (release 20261104-181056): "no reachable drops nearby" ×63 in
  6 minutes all over the farm, each collect about 0.2 s. The rests above were keyed by the drop's entity id, so a drop that came back with
  a new id at the same spot (another seed or egg, a merged stack, a re-drop) was new work every time, and a batch's other drops never
  rested. Now (`StoryColony.DropSpots.cs`) each collect that found nothing counts a miss at the spot of every drop of it still in view
  (any drop within a block counts there). 3 misses there (1 when the pawn wrote them off) and the spot rests for everyone, whatever lies
  there: 5 min, then 15, then 30; after a rest one more miss rests it again. Before a drop is offered at all there must be somewhere to
  stand by it, by the pawn client's own rule (`ItemEntityRules.HasPickupSpot`, World package): a cell within 2 of it (its level, one
  under, one over) where the body stands on anything that holds a player up in the version's collision boxes (`Safety.StandFeet`: full
  blocks, and farmland, path, soul sand, slabs, snow, beds, chests… at their real top), room for the body, the head out of water, and a
  pickup box that touches it. A drop on a deep pond, in a wall or over the void is never offered. Live 11-04: seeds inside the farm were
  offered and the client wrote them off "; 1 drop(s) left near the void": 1.12.2 farmland is 15/16 high and the client only stood on
  full blocks; both now ask the same rule.
- **Fresh drops beside a working pawn are its own (added 2026-11-04, late).** Live 11-04 (releases 20261104-2005 and -201512): "collect …
  failed: no reachable drops nearby (none in sight within 3 with room for it)" ×1050 in 75 minutes over the farm, every target a different
  spot, 83% a cell over the farmland (a drop still in the air, a second after its harvest): a hauler was sent to each fresh harvest drop
  and the farmer beside it picked it up first. Now a drop younger than 12 s with another pawn within 3 blocks of it is left to that pawn;
  later it is offered as usual. A collect whose drops nobody sees any more is logged as "its drops were gone when it got there (picked up
  by someone else): no failure", not "failed", and nothing rests. A pawn whose bag has no room for a drop (no free slot, no part stack of
  it) isn't sent for it; the collect says a full bag apart (": 2 in sight with no room in my bag"), and that never counts as a miss at
  the drops' spot. The colony reads collect failures by their stable parts only: the "no reachable drops" prefix, a ";" for drops the
  pawn wrote off, "no room in my bag". The idle audit says it: `waiting: drops at
  6,64,6 unreachable`, or for a haul mark `no reachable target: haul …: drops there unreachable (…)`. The collect's failure now says what
  it saw: "no reachable drops nearby (none in sight within 4 with room for it)", "(2 seen: 1 with no way to a pickup spot, 1 not picked up)".
- **Re-sorting** (background, after drops): a stack a chest's filter no longer allows goes to a chest that takes it. A stack a
  better-priority chest accepts moves up. One trip takes as many stacks out of one chest as the pawn has room for, each to its own
  chest. With no chest for it, the fallback chest takes it (option on). What the fallback put somewhere stays there. Else the stack is
  dropped beside its chest (job kind `dump`) and the condition alert names it. Like the genre, it then despawns unless a chest takes it
  and a hauler picks it up. Option `keep_disallowed` (default off) leaves it in the chest instead.
- Options (`POST …/options`): `rescue_teleport`, `unfiltered_storage_takes_anything`, `keep_disallowed` (booleans, in `options`).

## Workbenches & bills

Workbenches (designated, added 2026-11-03):
- The colony uses only the benches it was given. A bench becomes the colony's when the owner designates it (the bench
  inspector's "Use as colony workbench" → `POST …/workbenches {op:"designate", pos:[x,y,z]}`), when the owner adds its first
  bill, or when the colony places one for a build (below). `{op:"release", pos}` gives it back: its bills go with it, the
  colony's craft tasks move to another bench or wait. Answer `{pos, kind, designated}`; 400 for no bench at `pos`, or releasing one
  that isn't the colony's. Another player's table near home is never used.
- Detection is a colony option: with `detect_benches` on, crafting tables and furnaces in the home zones (or 12 blocks around
  home) join by themselves every 30 s, as before. Off by default (`POST …/options {detect_benches}`); colonies saved before keep
  the benches they had.
- Only designated benches unlock the Architect's tiers (below) and receive bills.
- Every interactive station takes bills (added 2026-11-03): crafting tables and furnaces on both versions; brewing stands, anvils
  and enchanting tables on 1.12.2 (b1.8.1 has none of the three: listed with `supported:false`, an `add` answers 400). See
  "Stations" below.
- A station the colony's own build placed (the Architect's production tab) is the colony's at once.
- Workbench: `{pos, kind, label, supported, loaded, ready_at, station, bills:[Bill]}`. `station` (the three 1.12.2 stations, else
  null): `{present, bookshelves, max_bookshelves, top_offer:[min,max]}` for an enchanting table, `{present, condition:
  intact|chipped|damaged|gone}` for an anvil, `{present, water:[x,y,z]|null, fuel}` for a brewing stand.

Recipes: `GET /api/story/recipes?workbench=crafting_table|furnace|brewing_stand|anvil|enchanting_table` →
`[{id, label, workbench, work_type: craft|cook, output:[id,meta,count], inputs:[{options:[[id,meta]], count}], fuel}]`; a station
recipe adds `requires:{xp_levels, lapis, fuel_item, water, note}`, `modes` (the bill modes it takes), `chain` (a brew's steps) and
`enchantments` (what an enchant can prefer). Ids are `craft:<output name>`, `smelt:<input name>`, `brew:[splash_|lingering_]<potion
type>`, `enchant:<item>`, `repair:<item>`, `combine:<item>` and `book:<item>`. Furnace food and brewing are Cook work; the rest
(crafting, smelting, enchanting, the anvil) is Craft.

`POST …/bills`:
- `{op:"add", bench:[x,y,z], bill}` → `{id}`
- `{op:"update", id, …bill fields}`, `{op:"remove"|"pause"|"resume", id}`
- `{op:"reorder", bench, ids}` → `{ids}`. Bills run top to bottom per bench.
- `{op:"enchant_tools", prefer?, material?, items?, count?, prefer_only?}` → `{ids, note}`: the tools system's hook (below).

Bill: `{id, recipe, mode: count|until|forever, target, done, paused, suspended_reason, ingredients:{allow, deny}, radius, pawn: "any"|name,
min_skill, store: "best"|{chest:[x,y,z]}, option, prefer, prefer_only, xp_needed, last}`. `until` counts the product in every storage
plus the pawns' inventories. `option`/`prefer`/`prefer_only` are an enchant bill's (below); `xp_needed` is the XP level the last try
needed (an offer's level, an anvil's price); `last` is what the last job made ("enchanted diamond pickaxe: Fortune II (offer #3,
level 18)").

Execution:
- A pawn with the bill's work type on, and allowed by `pawn` and `min_skill`, takes the first runnable bill of the nearest free
  bench. One pawn works a bench at a time.
- It fetches the ingredients from storages within `radius` of the bench (the ingredient filter applies), crafts, and stores the
  product in the bill's chest, else by the storage rules.
- A furnace is loaded and left to smelt (10 s an item) with the fuel its choice allows (see "Fuel" below). A later job of the same
  work type collects it, and the output is stored.
- Missing ingredients or fuel set `suspended_reason` and raise an alert. Resume clears it.
- An owner's bill leaves what the builds reserved: when the stored ingredient is only there for a build, the bill is suspended
  with "the … in storage is reserved for builds".

### Craft tasks (added 2026-11-05)

Fox: "Separate having bills and having personal crafting tasks. Bills are something I configure for crafting tables, but it's
over-inundating having each personal crafting task as a bill", and "Immediate requirements like that should always be at the top
of the list for crafting." (Live 11-05: one table listed 20 finished "wooden hoe 1/1" bills the colony had added for its farms.)
- **Bills are the owner's.** A bench's `bills` are only what the owner configured, in the owner's order. The colony's own crafts are
  **craft tasks** (`WorkbenchState.Tasks`, `StoryColony.CraftTasks.cs`): a build's chain ("Builds craft what they need"), the
  tools pawns need and the spares ("Tools and gear"), a water build's bucket, a growing zone's hoe or seeds, a fishing zone's rod.
  One task per need and recipe with a count, never one per unit or pawn; it carries its reason ("for mining: Cleo Z., Dov U. (+1
  spare)", "for the fence of build b4", "growing zone 'East' needs a hoe to till"), is taken by pawns with the recipe's work type
  (Craft, or Cook for a furnace's food), and goes when it is made or no longer needed. A one-off need (hoe, rod, seeds) stays until
  its count is made, then goes.
- **Order on a bench:** immediate tasks, then the owner's bills top to bottom, then the other tasks (a build's ingredients, by build
  order then step). **Immediate** = work waits on it right now: a tool a pawn is waiting for (or works on with a worn one), a
  need's one-off (hoe, rod, seeds), a water build's bucket chain, the chest of an Architect chest build (storage the owner is
  waiting for). A pawn with Craft (or Cook) work takes an immediate task before any other work, whatever its priorities (live
  11-05: the pickaxe bill sat "crafting one" for 30 minutes while every crafter hauled at priority 2).
- **State:** `craft_tasks: [{id, item, label, count, reason, for, for_item, priority: immediate|normal, bench:[x,y,z]|null, status:
  queued|crafting|waiting|paused, blocked}]` (every state, full or delta), `count` what is still to make, `blocked` what held it up
  at its last try ("no sticks: checked 2 chests …"). Hand crafts (a 2×2 recipe while there is no table) are listed with `bench:
  null`. A task is cancelled with `bills {op:"remove", id}` (as an auto bill was: the build waits for that item, a tool isn't made
  by itself again); the other bill ops answer 400 for it.
- **Web:** the Colony panel's Work tab shows "Crafting needs" (each task: count, item, reason, immediate first, its status, cancel);
  the bench inspector's bill list is the owner's bills only, with one line for the tasks there ("Also crafting: 7 wooden pickaxes
  for mining").
- **Older saves:** on load the auto bills move from the bills to the tasks, and finished one-off bills the colony had added itself
  (a count bill for a hoe, a rod or seeds, done, with the defaults the colony gave it) are dropped.

### Reservations (added 2026-11-04)

The materials pass records who holds what (`StoryColony.Claims.cs`): a build's own item in storage (planks it will place), or an input
of the chain that crafts it (the 3 iron ore a water build's bucket is smelted from, with the chain's step outputs). The state carries
`reservations: [{item, count, build, for_item, chain}]`.
- **A step of the chain is the chain's.** An owner's bill that makes one of a chain's step outputs may use that chain's reserved input
  (smelting the bucket's iron ore into ingots is what the build needs): its product lands in storage or the furnace and the build's
  next pass counts it (the furnace's load too), reserving the ingots instead. While an owner's live bill makes a step, the build adds
  no automatic bill for it (no double smelting).
- **Partial batches.** Only what is free (plus what the pawn holds) is used: a bill runs a smaller batch when some is free and waits
  only when none is, naming the builds and the amounts: "3 iron ore reserved for build b12 (iron ore) — 0 free".
- **Release.** `POST …/bills {op:"release_claim", build, item}` → `{build, item, done}`: a chain claim cancels the build's crafting of
  that item (it waits for it, as cancelling its auto bill does); the build's own item pauses the build. The storage and bench inspectors
  list what builds hold of their items, each with a Release.

### Bills see the chests (added 2026-11-04)

Live 11-04: "Make Stick (from any planks) suspended: no planks within 32 blocks" with planks in the chest beside the table. The colony's
record of a chest only changed with its own pawns' takes and puts, so a player's deposit was invisible. Now (`StoryColony.StockCheck.cs`):
- A body's look inside a chest (the bot's memory: any body, any reason) newer than the record's last change replaces it
  (`StorageState.SeenAt`). Checked before a bill waits and every 30 s.
- Before an owner's bill waits for a missing ingredient, a pawn looks into the nearest chest within the bill's radius of the bench that
  nobody has looked into for 10 minutes (a `look` job: `peek_container`, each chest at most every 2 minutes).
- Every change of the stock (a deposit, a take, a corrected record) retries the waiting bills on the next pick, not on a timer.
- The reason says what was looked at and who carries some: "no planks: checked 6 chests within 32 of the bench (2 not looked into
  lately); Ann carries 4 (stored soon) — needs logs (chop trees)". The radius is the bill's, from the bench.
- "From any planks" ingredients match every plank (and the ingredient filter's `id:-1` entries every meta) in the stock check, the
  reservation and the fetch.

### Furnaces are collected (added 2026-11-04)

- A furnace load lands on the furnace whatever became of its bill (an auto bill the build stopped wanting while the pawn walked: live
  11-04, three ingots smelted and never collected). An auto bill with a job in flight stays until that job ends.
- `done` counts on collection, never on loading: the collect job's "collected N" is what the bill gets; "still smelting K" keeps the
  rest loaded and collects it later. The pawn stores what it took out by the storage rules (a build fetches it from there).
- The furnace's expected progress (an item every 10 s since it was loaded) is in the workbench state, `smelting: {recipe, input,
  output, loaded_at, ready_at, estimated}`; the materials pass counts the load's output for the builds.
- The container panel estimates a furnace's slots between looks the same way (`estimated: true`, "estimated (smelting since)"): the
  input goes down and the output up while it was fuelled (its fuel slot, plus a coal's worth that may have been burning; a lit furnace
  burns on). A fresh look by any body replaces the estimate.

### Fuel (added 2026-11-04)

A furnace's fuel is a bench setting with a per-bill override (`StoryFuels`, `StoryColony.Fuel.cs`):
- Choices: `auto` (the default: coal or charcoal, then planks, then logs), `coal`, `charcoal`, `coal_or_charcoal`, `planks`, `logs`,
  `lava_bucket`, `blaze_rod` (1.12.2 only), `any` (auto plus blaze rods and lava buckets). The workbench state lists the version's
  `fuel_choices`.
- `POST …/workbenches {op:"fuel", pos, fuel}` → `{pos, kind, designated, fuel}` sets a furnace's choice (400 for another bench kind, or a
  fuel the version doesn't burn); `POST …/bills {op:"update", id, fuel}` overrides it for one bill (`inherit` or null clears it; furnace
  bills only).
- A named fuel is the only one burnt: the pawn brings only it, and the smelt primitive takes another fuel out of the fuel slot
  first and never stands another fuel in when it runs short (`MinecraftClient.SmeltAsync` with a fuel). Auto and any leave stock the
  builds or bills reserved (planks a build needs) unless nothing else is there.
- What the furnace still holds counts: the colony keeps `fuel_credit` (items it can still smelt on the fuel it loaded, burn time
  and slot leftovers); a job tops up only what its batch needs beyond it (`fuel_units` 0 = none brought). A job that finds the
  furnace short of fuel resets the credit. The primitive itself counts the real fuel slot and burn time.
- **A lit fuel burns out (changed 2026-11-04).** Only whole fuel items left in the slot keep; what is left of a lit one counts only
  until it burns out (`BurnUntil`: a lit furnace burns on with nothing to smelt). Live 11-04: one-log charcoal batches on logs left
  half a log's credit each time, the halves added up, and a batch came with no fuel.
- **Fuel first (changed 2026-11-04).** The smelt primitive loads the fuel before the input and loads only the input that fuel covers; with
  none it loads nothing ("not enough fuel"). The input is never counted as its own fuel: charcoal from logs on logs splits the carried
  logs between the two, and the colony's fuel pick doesn't count the log a pawn carries for the input (live 11-04: "in=oak wood x1
  fuel=-", the one log it had was both).
- **A load left without fuel is refuelled (added 2026-11-04).** A collect that finds nothing to collect is no failure (no rest for the
  pawn or the bench); what the furnace showed decides: its input with no fuel and not burning → the bench waits for a refuel
  (`RefuelCount`), and the next pawn of the bill's work type brings the furnace's fuel for that input (kind `smelt` with `n` 0 and
  `refuel` N: the primitive with count 0 tops up the fuel for what the furnace holds, "fuelled the furnace … ready in ~10s"); no fuel
  anywhere: the idle audit says "waiting: the furnace at … holds 1 oak wood and no fuel: needs fuel: …". Empty (`in=-`): the load is
  forgotten. Burning or fuelled: looked at again when its input should be done. Live 11-04: "collect_furnace 218,64,399 failed: nothing
  to collect; … in=oak wood x1 fuel=- out=-" ×108, a collect every 20 s that rested pawns and the bench.
- Rates come from the version's burn times: coal and charcoal 8 items each, planks and logs 1.5, a blaze rod 12, a lava bucket 100.
- No allowed fuel: the bill waits with "needs fuel: no coal, charcoal, planks or logs within 32 blocks" (or the choice's own list).
- The materials pass plans the builds' smelting with the furnace's choice too.

State: a furnace bench carries `fuel` (its choice), `fuel_choices` and `fuel_credit`; every bill `fuel` (its override) and `fuel_need:
{choice, item, name, need, have, short, per_item}` for its next batch (a brew: blaze powder, one per 20 brews; `need` 0 when the stand's
fuel is known to last). Furnace recipes carry `fuels:[{item, name, items_per_unit}]` (the version's table), a brew its blaze powder.

The web: the furnace inspector has a fuel picker (an icon per choice) at the top; each furnace bill row a fuel chip (the furnace's,
or an override) and its fuel need ("fuel: 1 coal (have 12)", red when short); the bill dialog lists the fuel table and takes an
override; the recipe picker shows "+ fuel" in a smelting recipe's line.

### Stations: brewing stand, enchanting table, anvil (added 2026-11-03)

1.12.2 only (b1.8.1 has none of these blocks). Each runs as one job at the station, through the pawn's skills (`brew`,
`fill_bottles`, `enchant_item`, `use_anvil`; client primitives in `MinecraftClient.Stations.cs`) via the story SDK's `story.work(job)`
(`Sdk/js/75-story.js`; `story_pawn.js` hands every kind it doesn't know to it). Items go into these windows by hand (pick up,
place): the server routes a shift-click into them by item, which the chest-like window model can't predict.

**Brewing stand** (Cook work). One recipe per potion, splash and lingering potion a chain from water bottles makes ("Brew Potion of
Swiftness": water + nether wart → awkward, + sugar → swiftness; + redstone → extended, + glowstone → II, + fermented spider eye →
corrupted, + gunpowder → splash, + dragon's breath → lingering). Output: 3 potions per job (a stand holds three bottles); count, until
and forever all work (a 1.12.2 potion's type is NBT: the colony gives a potion of type T the meta T's registry id, water 1, awkward
4, swiftness 14…, in pawn inventories and the storage ledger).
- The job starts from the furthest stage of the chain the pawn holds (awkward potions skip the wart), else from water bottles it
  holds, else from glass bottles (its own or from storage) it fills first at the nearest filled cauldron or water source block
  (open above) within 16 blocks (4 up/down) of the stand: `fill:{at, n}`.
- It brings each step's ingredient and, when the stand's fuel is short or unknown, a blaze powder (one fuels 20 brews; the colony
  keeps the stand's `fuel` as it last left it). It stays at the stand through every brew (20 s each), checks the bottles after each
  step, then stores the potions (the bill's chest, else the best).
- Reasons: "no water (a source block or a filled cauldron) within 16 blocks of the brewing stand", "no glass bottles (or water
  bottles) within N blocks", "no sugar within N blocks", "no blaze powder (the brewing stand's fuel)" (only when the stand is known
  to be short), and from the stand "the stand won't brew …", "the brewing stand is busy brewing".

**Enchanting table** (Craft work). "Enchant Diamond Pickaxe" for every item the table takes: pickaxes, axes, shovels, swords,
armour, bow, fishing rod, book (hoes, shears and the rest only through an anvil). Modes count and forever (an enchanted item can't
be told from a plain one in storage: no until).
- Bill fields: `option` 1–3 (the highest offer it takes; offer k costs k lapis and k levels, default 3), `prefer` (an enchantment
  the table can put on the item, validated), `prefer_only`.
- The pawn brings the item (from storage first: its own may be its working tool, or enchanted already; the primitive only takes a
  plain one) and `option` lapis. The table shows its three offers once the item is in (each needs its own level, up to 30 with 15
  bookshelves); the pawn takes the highest offer hinting `prefer` it can pay, else (unless `prefer_only`) the highest it can pay.
  The offers are fixed for that pawn and item until it enchants something: no reroll. A pawn whose table showed no offer for a
  prefer-only bill leaves it to the others; when every pawn has tried, the bill waits: "no pawn's table shows Silk Touch for it".
- The enchanted tool stays with the pawn unless the bill names a chest (pawns keep their gear); a book goes to storage.
- The inspector shows the bookshelves (`Enchanting.Bookshelves`: the game's walk around the table, at most 15) and the top offer's
  level range.

**Anvil** (Craft work). Per damageable item: "Repair Iron Pickaxe (iron ingot)" (the most damaged one, the pawn's own first, with
the material units that mend it, a quarter of its durability each), "Combine two Iron Pickaxes", "Apply an enchanted book to Iron
Pickaxe". Modes count and forever. Prices (`Anvil`, the game's rules): prior work (doubles plus one each use) + 1 per material unit;
2 when a combine mends + the right item's enchantments; a book's enchantments at half the rarity multiplier (common 1, uncommon 2,
rare 4, very rare 8) × level; 1 per conflicting enchantment. 40 or more is refused ("too expensive", the bill is suspended).
- Every use may wear the anvil (12%): the inspector shows `condition`; a job that reports "the anvil broke", or a bench whose block
  is gone, suspends its bills: "the anvil at x,y,z is gone (worn through): place a new one and designate it".
- Renaming isn't offered: it needs a typed name and a pawn has no use for one.

**XP levels.** Pawns earn XP the game's way (mining ore, smelting, fighting); `xp_level` is in the pawn state (the pawn inspector's
Skills tab shows it). An enchant needs at least `option` levels, a repair the units, a combine 2, or the bill's `xp_needed` when the
last try needed more ("needs 18 XP levels (has 10)" from the station teaches the bill; it's not a failure). A pawn short of them
leaves the bill to one that has them; when no pawn that may take it has them the bill waits: "needs 18 XP levels: no pawn has them
(best: Ann 10)" (retried every 30 s).

**Work types.** Brewing is Cook (RimWorld brews at the stove's skill); enchanting and the anvil are Craft. No new work type.

**Tools hook** (for the tools/equipment system): `StoryColony.EnchantTools(prefer, items?, count = 1, preferOnly = false)` → `(ids,
error)` adds count bills at the colony's enchanting table nearest home for `items` (default: every pickaxe kind the colony has)
preferring `prefer`; an unfinished bill with the same recipe and preference isn't added twice. `StoryEnchanting.PreferFor(material)`
picks the enchantment per mined material: fortune for coal, diamond, emerald, lapis, redstone and quartz ores; silk touch for
glass, ice, glowstone, grass, mycelium, ender chests and bookshelves; efficiency otherwise. Over HTTP: `POST …/bills
{op:"enchant_tools", prefer | material, items?, count?, prefer_only?}`.

**Interactive blocks that take no bills** (`StoryRecipes.NotBenches`): chests (storage settings), hoppers, droppers and dispensers
(containers; a "fill with X" order may come later), the cauldron (water for brewing: a level per bottle), the beacon (a one-time
setup), the jukebox and the note block. The bench list is `StoryRecipes.Benches`: crafting table, furnace, brewing stand, anvil,
enchanting table.

### Builds craft what they need (added 2026-11-03)

A queued build whose items aren't in storage or in the pawns' pockets has them crafted, colony-sim style. The director's
materials pass runs whenever storage, the queue, benches or bills change (and every 3 s otherwise, for the pawns' pockets):
- **Reservations, queue order.** Each live build (not paused or done) reserves what the colony has of its items: storage plus
  the pawns' pockets (tools, weapons and armour excluded), minus what jobs in flight already took from a chest, plus what a
  furnace is smelting. A later build never counts on what an earlier one reserved; a builder fetches only its own build's share
  ("waiting: the … in storage is reserved for an earlier build").
- **The shortfall goes through the recipes** of the colony's version (`RecipePlanner`, smelting allowed), against what is still
  unreserved: logs → planks → sticks → fence, cobblestone → stairs, sand → glass (fuel: coal or charcoal 1 per 8, planks or logs
  1 per 1.5) → panes. What the stock covers (the most, by a binary search; fuel included) is crafted; its ingredients are
  reserved too, so two builds or an owner's bill never take the same planks.
- **Craft tasks, not bills.** Each step is a craft task "for build #N" (`for_build`, `for_item`, `step`, a `reason` such as "for the
  fence of build b4") on the colony's crafting table or furnace nearest home, radius 256: one task per build and recipe with a
  count, in the bench's task list, never in its bill list (see "Craft tasks" below). Its target is `done` + what is still to make,
  so it follows the build and goes when nothing is left to make (a loaded furnace's task stays until collected). A step whose
  ingredient isn't there yet waits quietly (an earlier step makes it; the task's `blocked` says what). The owner can only cancel a
  task (`bills` `remove` with its id; `update`, `pause`, `resume`, `reorder` answer 400): the build then waits for that item
  (`no_craft`).
- **2×2 recipes** run in a pawn's own grid while the colony has no crafting table (a `craft` job with no bench, Craft work).
- **No bench.** A chain that needs a crafting table (or a furnace, which is made at a table) the colony hasn't got: with
  `auto_place_benches` (a colony option, default on) a pawn with Craft on fetches the materials, crafts the bench and places it
  on a free cell near home (solid ground, headroom, not a build's cell); it becomes the colony's. One at a time; the build shows
  it as `station`. Off, or nothing to make one from: an alert "Build bN needs a crafting table: place one or designate an
  existing one".
- **Short raw materials** are said once per change, per build: "Build b3 (fence) is short of materials: need 3 oak fence (short
  of raw materials: 2 Wood)". Nothing is designated by itself (no surprise chopping).
- A build waiting for crafted items is `waiting` with "waiting for N … (being crafted)" and no alert; one with nothing to craft
  from is `blocked` ("need N …").

Build state gains `materials: [{item, label, need, have, crafting, short, via: craft|smelt|none, chain:[step], raw:[missing]}]`,
`no_craft: [item]` and `station: {kind, pos}|null`. The web's build queue shows have/need, crafting and short per item, the
recipe chain and the raw shortfall on hover; the bench inspector's bill list is the owner's bills only, with one line for the
colony's tasks there ("Also crafting: 16 sticks for the fence of build b4") and the Colony panel's "Crafting needs" lists them all.

`POST …/options` `{auto_place_benches?, detect_benches?, idle?: recreation|wander|stay, speed?: normal|fast|faster, speed_effect?, allow_lava?}` → the options (see "Idle behaviour" for `idle`, "Speed" for `speed`, "Water" for `allow_lava`); the colony state carries `options`.

### Architect palette and tiers (added 2026-11-03)

`GET /api/story/colonies/{id}/architect` → `{entries, benches:{crafting_table, furnace, brewing_stand?, anvil?, enchanting_table?}, tabs}` (the designated benches per kind the version has). Entries are every placeable the
version has (`StoryArchitect`: blocks, stairs, slabs, fences, doors, glass and panes, wool, beds, bookshelves, carpets, benches,
chests, torches, glowstone, ladders…; variants the version lacks are left out), by tab: structure, furniture, production,
storage, lighting, water (see "Water"), misc. Entry: `{id, label, tab, tier, unlocked, reason, item:[id,meta], material:[id,meta], kind, kinds, chain,
have, craftable, from, short}`.
- `tier` from the recipes: `gathered` (no recipe), `hand` (2×2 only), `workbench` (a 3×3 step), `furnace` (a smelting step).
- `unlocked`: gathered and hand always; workbench with a designated crafting table; furnace with a designated furnace (and a
  table when its chain has a 3×3 step). Else `reason`: "Needs a crafting table — place one or designate an existing one".
- `have` = unreserved stock, `craftable` = how many (up to 64) the unreserved stock makes now, `from` = what one takes, `short`
  = what one is short of when neither.
- **Fetched when it changes (2026-11-04).** The colony state carries `architect_key`: everything the palette reads (the free stock
  after reservations, the designated benches by kind, the version) folded to one number. The bot keeps the palette it built for a
  key and answers from it until the key moves (the recipe chain per placeable is planned once per version); the web keys its
  query on it and fetches only while the Architect (or a material selector) is open. Live 11-04 the web refetched every ~4 s
  (the colony revision / 20), ~200 ms each under the colony lock.

The web's Architect (B) has tabs Orders, Zones, Structure, Furniture, Production, Storage, Lighting, Water, Misc. The build tools stay
where they were; beside them, the tab's placeables: icon, name, tier and "have N" / "craftable N from …" / "short: …". Locked ones
are greyed with a lock and the reason; picking an unlocked one arms its build tool with it as the material (a furniture entry its
furniture tool), and the queued build's auto bills craft what's missing.

**The material selector** (added 2026-11-03, `MaterialBar.tsx`, `materials.ts`). One bar for every structure and furniture tool,
docked bottom-left just above the tab bar (never a popup over the map; it takes pointer events only over itself, and works in
full screen). It holds the tool's name, its materials, a search box, the recent row, and the tool's options as small segmented
controls to the right: wall height, roof style and overhang, facing. The top options bar is for the other tools only.
- **Materials per tool:** the palette entries whose `kinds` list the tool (wall, floor, roof, door, stairs, fence, single
  block), each with its icon, name, tier, lock state (greyed with the reason) and stock; unlocked ones first. Until the palette
  loads (it is fetched whenever such a tool is in hand), a static list with the stockpile counts.
- **Memory:** each tool remembers its last material, and the recent row its last ones across tools (per device,
  `localStorage` `mcx-story-materials`). Picking a placeable in the palette counts as choosing it.
- **Keys:** Tab / Shift+Tab or ] / [ cycle the tool's unlocked materials (in the search's results), 1–5 pick a recent one.
- **Preview:** hovering a material shows the placement in it at once, as a build hologram at the last map cell (and the
  placement under the cursor or the drag is drawn in its material).
- **Narrow maps** (under 760 px) fold the bar into one material chip that opens it.

## Tools and gear (added 2026-11-03)

Fox: "Pawns must always use the correct tools to mine mat types so we don't lose the mat." Every dig a pawn does takes at least the
tool its material needs to drop anything, from the version's own data (`StoryTools`, `IGameData.CanHarvest`; both versions):

| Material | Minimum (the floor) | Faster with | Kept with |
|---|---|---|---|
| stone, cobblestone, sandstone, netherrack, coal ore, quartz ore | wooden pickaxe | | |
| iron ore, lapis ore | stone pickaxe | | |
| gold ore, diamond ore, redstone ore, emerald ore | iron pickaxe | | |
| obsidian | diamond pickaxe | | |
| snow (b1.8.1) | wooden shovel | | |
| cobweb | sword or shears | | |
| dirt, grass, sand, gravel, clay, farmland | the hand | shovel | |
| logs, planks | the hand | axe | |
| leaves, vines, tall grass | the hand | | shears (the block itself) |
| wool | the hand | shears | |

- **The gate.** A mine, deconstruct or cut designation, a mine zone's ore, a mine order and a build's dug cell (a design's basement,
  a mine design's dig cells: the block found at the cell, else the cell's expected material) go only to a pawn that carries the tool
  or can fetch one: the job then carries `tool` (held before the dig) and a `fetch` entry for it. A pawn with neither leaves it, and
  the target **waits** with why: a designation gets `wait: "needs a stone pickaxe"` (on the map as a neutral label, no failure count,
  no alert), a build is `waiting` with that reason, a mine order's block is dropped with one alert. Another pawn with the tool still
  takes it at once.
- **Never touched without the tool (added 2026-11-04).** Fox: "it must stop acquiring a lock on the block and then airing it out for 10
  minutes because it couldn't mine it. Obviously it shouldn't be touching it." A target is offered, and leased, only to a pawn that
  holds the tool or can fetch a verified one (fresh stock, the record corrected by what pawns see, not claimed by another pawn). With
  none, the designation's `status` is `waiting` and its `wait` says what the colony does about it: "needs a stone pickaxe — crafting
  one" / "— no cobblestone for it" / "— no crafting table for it" / "— auto-crafting tools is off". No lease, no fail count, no
  back-off: it is offered again at the next pick once a tool exists. A tool's trouble (the strict dig's refusal, a tool that couldn't
  be held or broke, a fetch that found no tool) is never the target's failure, never a back-off and never a pawn's rest; the next
  pick plans again. After a load, designations whose recorded failures were the tool's or the record's start clean.
- **The primitive refuses too.** A Story pawn's body has `StrictHarvest` on: an asked-for dig that the tool in hand can't harvest is
  refused ("needs a stone pickaxe: digging iron ore at … without it would lose the drop") rather than broken for nothing. Its
  `PreferHeldTool` keeps the tool the director chose in hand (the plain pickaxe for stone, the Fortune one for ores) instead of
  swapping to the fastest one carried.
- **Choosing.** The lowest tier that does (good tools stay good), fresh before worn: a tool under 10 % durability is swapped for a
  fresh one carried or stored, and a worn one is used only when there is nothing else (never a lost drop). Never another pawn's
  pocket: storage only.
- **Craft tasks "for tools" (reworked 2026-11-05).** Live 11-05 (Fox: "I'm seeing pawns idle during work hours, and they have active
  tasks, like the quarry"): twelve stone pickaxes crafted together wore out within two minutes mid-quarry, nothing was in storage,
  the wooden pickaxe asked for was one bill for one pickaxe, "no materials for it" with planks and sticks stored (a worn wooden
  pickaxe in storage read as the tool itself), then "crafting one" for half an hour while every crafter hauled at priority 2. Now:
  - **Per pawn.** Every pawn that needs a tool and has no fresh one (waiting, or working on with a worn one: its replacement is made
    before it breaks) counts. A tool's craft task (`for_build: "tools"`, `for_item` the tool, a `reason` "for mining: Cleo Z., Dov U.
    (+1 spare)") is for all of them plus `ToolWantSpare` (1; the owner's spare count when larger), less the stock: storage plus what
    pockets hold beyond the one their pawn's work uses (a pickaxe a miner works with is no spare; a hoard of 13 is). A pawn that gets
    a fresh one stops counting; a want nobody repeats goes after 3 minutes (its spare with it).
  - **As many as the stock makes:** a wooden pickaxe asked for is made of stone while there is cobblestone, the rest of wood; planned
    from the materials alone (a worn tool in storage never counts as the tool). It runs before the builds, so a build's reservation
    never starves it. The wait says what the colony does: "crafting one", "no sticks for it", "one is in stock" (never "no materials"
    for a tool that is there).
  - **Immediate.** While a pawn waits on it, the task is immediate: any pawn with Craft work takes it before its other work, at any
    priority (see "Craft tasks"). Meanwhile the waiting pawns take their next work type; the idle audit names the tool only for a pawn
    with nothing else to do.
  - Cancelling one stops that tool being made by itself (`no_craft`). `options.auto_craft_tools` (default on) turns it off.
  **Spare tools:** `options.spare_tools: {count 0..8, kinds}` keeps that many of each kind in stock (storage, and pocket extras):
  iron from stored ingots (never smelted for), else stone, else wood.

### Material rules

`POST …/material-rules` `{op:"set", rule:{material, tool?, tier?, enchant?, fallback?}}` → `{rule}` · `{op:"remove", material}` →
`{removed}`. `material` is a block name or a group: `group:ores`, `group:stone`, `group:soil`, `group:logs`, `group:leaves`,
`group:wool`, `group:web`; a block's own rule wins over its group's.
- **The floor always holds.** A tier below the material's floor is refused (400 "iron_ore needs at least a stone pickaxe"); for a
  group, below its lowest member's floor, and each member keeps its own floor when dug (an ores rule at "wood" still takes a stone
  pickaxe for iron). A tool that can't harvest the material is refused ("a stone shovel can't harvest iron_ore").
- **Marked materials.** `enchant: fortune | silk_touch | any` (1.12.2 only; b1.8.1 answers 400 "b1.8.1 has no enchantments"): a
  matching enchanted tool carried, else one stored, is used; with none, `fallback: "plain"` (default) uses a plain tool ≥ the tier,
  `"wait"` waits ("waiting for an iron pickaxe with fortune"). Unmarked materials take plain tools first, so the Fortune pickaxe isn't
  worn down on stone. The colony knows which stored tools are enchanted when its own pawns stored them (`storages[].enchanted`).
- The full state carries `materials`: the editor's catalog `[{id, label, group, tool, floor, keep_tool, members}]`, groups first.

### Gear policies

`POST …/policies` `{op:"set", policy:{id?, name, tools_by_work?, tools?, weapon?, armor?, food?}}` → `{id}` · `{op:"remove", id}` ·
`{op:"assign", id, pawns}`. Policy: `{id, name, default, tools_by_work, tools:[kind], weapon: none|best|sword|axe|bow,
armor: none|drafted|always|night, food: 0..16, spare}`. The default policy (`default`, never removed) carries the tools of the pawn's work
(mine: pickaxe + shovel, chop: axe, farm: hoe, build: pickaxe + axe), no weapon, armour when drafted.
- **Kept up when idle.** About every 20 s, when nothing more urgent is on, a pawn's gear is checked against its policy: a missing or
  worn tool kind is fetched (the worn one goes back to storage), a weapon, armour to wear now (always, or at night) or to take off
  (none, night by day), food below half the policy's. Storage only, unclaimed items only; a failed fetch rests 2 min.
- Pawn state: `policy` (id, null = default), `equipment: {held, armor, tools, weapons}` (each `{item, name, label, kind, tier,
  durability, ench, slot}`), `gear_tasks: [{id, op, label}]`.

### Tools are colony stock (added 2026-11-04)

Live 11-04 (Fox): "one pawn holding 13 stone pickaxes". Spare-tool bills counted only storage, so while the crafted pickaxes sat in a
pocket (a pawn's tools were always "its own" and never stored) the colony kept crafting. Now:
- **A pawn keeps what its policy needs:** what it wears; per tool kind its work (or a material rule) uses, the best fresh one, plus a
  plain one beside an enchanted one when a rule marks a material, plus one spare when its policy says `spare: true`; its best weapon;
  per armour slot it doesn't wear, its best piece unless its armour mode is none. One stray tool of a kind its work doesn't use may stay,
  unless another pawn whose policy wants that kind has none and storage has none: then it goes at once. Everything else is **extra**
  (a hoard always goes back): its next gear check
  (when idle, about every 20 s, and right after the colony loads) stores the extras in the chests that take them, keeping the
  freshest of each item it keeps (the job's `store: [{to, item, keep}]`, the hidden `equip(item, 'store', chest, '', keep)`), so the
  other pawns' policies fetch them from storage. A pawn whose job fetches a better tool returns the plain one it replaces into the
  same chest (a wooden pickaxe for iron, a worn one).
- **Crafted tools are stock:** a tool bill's product goes into storage like any bill's; a crafter that needs one fetches it back by
  its policy.
- **Targets count stock:** spare-tool and tool tasks count storage plus what pockets hold beyond the one each pawn's work uses, so a
  hoard stops them, and a pickaxe in a miner's hand is never taken for a spare (changed 2026-11-05: counting every pocket left the
  colony with no spare when all its pickaxes wore out together).
- **A record that lies is corrected (StoryColony.Ledger.cs).** Live 11-04: "no wooden pickaxe in the chest at 218,64,400" and "no
  stone hoe in the chest at 223,64,400 (it had: dirt x333, …)" over and over: the colony's record said the item was there, someone had
  taken it, and every pick planned the same fetch. A fetch that finds its item missing now corrects that chest's record from what the
  pawn saw (the item gone; "it is empty" empties it; a full "it had:" listing replaces it), the job is not counted as failed, one
  alert says so, and the next pick plans from real stock: another chest, a pawn with one to spare, else the target waits ("needs a
  wooden pickaxe") and a craft task makes one (a farm's hoe: farming's own wooden-hoe task). The same fetch is never planned again
  until a deposit puts the item back on the record.

### Any-variant recipes and bill planning (added 2026-11-04)

Fox: "we really shouldn't care which type of wood we turn into planks … anything that follows this pattern should repeat it." Live
11-04: "craft failed: no oak wood planks in the chest" with birch planks there, "stone_pickaxe until 10 done 11", pawns resting from
stock errors while bills waited.
- **Any-variant bills (StoryRecipeFamilies).** Vanilla takes any planks for sticks, crafting tables, chests and wooden tools, any coal
  (coal or charcoal) for torches and any sand for glass: those recipes say "(from any planks)" and carry `any: true`. Where vanilla
  has one recipe per variant of a material family (logs, sand, coal), one bill takes the union, derived from the version's recipes:
  `craft:planks_any` "Planks (from any logs)" (each craft takes whatever log is stocked and makes that wood's planks; `any_output`:
  its stock counts every plank variant) and `smelt:charcoal_any` "Charcoal (from any logs)". Mixed families (slabs of stone,
  sandstone, bricks) never merge; b1.8.1 has one plank type, so nothing merges there. The bill picker lists the any recipes first in
  their group with an "any" badge, the exact variants below. Variant-specific recipes (oak stairs, birch fence…) keep their wood.
- **Fetching takes what's there:** an ingredient is gathered across stacks and variants, the most plentiful first (two spruce and
  one oak planks make a pickaxe); a furnace load stays one item.
- **Auto bills never stall on a wood:** a build's or a tool's chain uses the any bill for an intermediate step (planks for sticks);
  the item a build itself asks for (oak planks for an oak floor) stays exact.
- **Stock counts:** "until" bills count every chest and pocket, every variant of a family product, every wear state of a tool (a
  used pickaxe still counts).
- **Eating from real stock.** Live 11-04: "eat failed: no rotten flesh in the chest at 224,64,398: it is empty" ×784 in 10 minutes. A
  hungry pawn eats food it carries; else it fetches the most nourishing stored food (never rotten flesh, spider eyes or poisonous
  potatoes while anything else exists; those only when starving, hunger ≤ 6, fetched and left to the body's own starving reflex, never
  an "eat" that refuses them). A food fetch that fails corrects that chest's record, makes that pawn wait 15 s (then 60 s) and skips
  that chest for it for 2 minutes (an unreachable chest too: another chest, or it waits). No food anywhere: one alert, and it waits.
- **Unreachable chests (added 2026-11-04).** Live: 334 of 462 failed searches in 5 minutes were "can't reach chest at 218,64,400 …
  no path to use", each an A* run to its 30k-node / 2 s budget, because the rest went to the designation and not the chest. Now a
  pawn that can't reach a chest (or a bench) leaves it out of every plan of its own (fetches, deposits, food, ingredients) for 2, then
  5, then 10 minutes as it keeps failing; the job is never the target's failure and nobody rests. When every pawn in game has failed
  it, one alert says "Chest at x,y,z is unreachable — pawns can't get to it" and its contents aren't stock (builds and bills don't count
  on them) for 10 minutes or until a pawn gets there. A body's goto also remembers its failed searches (`NoRouteCache`): the same search
  from the same cell to the same goal with the same freedoms isn't run again for 15 s unless a block changes within 24 blocks of
  either end or a chunk loads. `Bodies:MaxPathfinds` defaults to half the cores, 2..4.
- **Missing stock is no failure:** a bill whose ingredient isn't there is suspended with why, with where the raw material comes from
  ("no planks within 32 blocks — needs logs (chop trees)"; with logs stored: "make them from the stored logs"), never attempted, and
  retried by itself every 30 s. A failure that was the plan's (the record said the item was there; "no … within", "reserved for",
  "not enough") never makes a pawn rest.
- **One pawn per bench** (the genre's rule). With 4+ runnable bills on the only crafting table (or furnace) and a crafter idle, the
  colony hears it at most every 10 minutes: "12 bills waiting on 1 crafting table — place another to craft in parallel".

### Bulk gear

`POST …/gear` `{op: arm|armor|pickup|return|tools, pawns, item?:[id,meta], count?:1..64}` → `{results:[{pawn, display, got, missing}],
summary}`: one op for a whole selection (the web shows the summary as one toast). Each pawn gets a gear task, run before its orders
and work; the same op again replaces it.
- **arm**: one weapon each, best fighters first (guard/hunt skill, then guard priority, then name): a stored weapon better than the
  pawn's own, else its own in hand.
- **armor**: per slot the best stored pieces go round the selection, starting with a different pawn each slot, so the best helmet
  and the best chestplate land on different pawns.
- **pickup**: `count` of `item` each, from storage, in order; what ran out is said.
- **return**: armour off, tools, weapons and armour into the chests that take them.
- **tools**: per kind, the tools each pawn's policy wants, to the pawns with that work highest in their priorities first.
- **Reservations.** Every fetched item is claimed for its pawn (pending gear tasks and jobs in flight), so two pawns never go for
  the same sword.

## Drafted combat (added 2026-11-03)

Fox: "When drafted I couldn't make them attack players, bots or mobs." The attack order took a mob type name and the client refused
players outright.
- **Any entity.** `POST …/orders` `{order:{kind:"attack", target:{entity}, confirm?}}` on a monster, an animal, a player or another
  bot (hive bodies included) sends every ordered pawn at exactly that entity (no lease: they gang up), with the best weapon carried;
  the client's fight engine follows it if it moves and ends when it dies or leaves. Undrafting (or stop) ends it. One of the colony's
  own pawns is refused (400); a drop or an unseen entity too.
- **Players and bots need the owner's word.** Without `confirm: true` the route answers **409** `{error, confirm:{entity, name, kind:
  "player"|"bot"}}`; the web asks once per target per session ("Attack PoofimaFox?") and resends. Only then does the director give that
  pawn's body a permit for exactly that entity (`CombatEngine.Permit`, 3 min, renewed with each attack job, withdrawn on undraft or
  stop): the client's hard gate still refuses every other player, and nothing ever starts a fight with a player by itself. No
  commands are involved.
- **Draft and undraft.** Drafting puts the best carried weapon in hand (by the policy's weapon preference) and, when the policy says
  "when drafted" (or always, or at night by night), the carried armour on; what was held is remembered. Undrafting holds it again and
  takes the draft's armour off.
- **Holding a post.** A drafted pawn's move order sets its post (the formation slot). With no order (or a hold), it engages a monster
  within 6 blocks of itself or its post, then walks back. Monsters that hit it are fought by the body's own defence reflex. Players
  and animals are never engaged without an order.

## AI hives

AI hives and their bodies take the same selections as directives. They're best effort: the brain decides how.

`POST /api/story/directives` `{bots:[name], directive:{kind, area?, targets?, filter?, build?: <build item>, bill?: <bill>, text?},
player, allow_commands?, request_id?}` → `{id, delivered:[login], line, design}`.

- **Kind:** `chop|cut|harvest|mine|haul|hunt|deconstruct|build|move|attack|guard|craft|text`.
- **Player:** must be a trusted player (the bridge's authenticated principal). Commands are allowed only when `allow_commands`
  is true and `Supervisor:Viewer:AllowCommands` is on.
- **Bots:** main, a hive or any body. A subhive body's line goes to its owning hive's brain, addressed to that body, as viewer
  chat does.
- **Delivery:** through the viewer chat path (`ChatRouter.OnRemoteChat`, origin `viewer`), as ONE line of at most 240 characters,
  for example:
  `poofimafox (story map): chop the 14 trees in the area 120,64,250 → 140,70,270 — stash logs in the Wood chest [dir3]`
  - Lists are never inlined: an area carries its count and bounds.
  - The exact targets stay on the bot, in `GET /api/story/directives/{id}` → `detail`.
  - No brain tool was added, so the brain's tool budget is unchanged.
- **Builds:** a `build` is saved as an accepted design (blueprint DSL, requester = the player, at most 32 a side) and the line
  names it: `build design #57 'hut' in the area …`.

Directive: `{id, at, bots, kind, summary, status: sent|acknowledged|done|failed|unknown, reply, design}`. The status comes from
the bot's own next lines in the viewer feed:
- any line: acknowledged
- a line saying done or finished: done
- a line saying can't or failed: failed
- nothing after 30 minutes: unknown

AI bodies for the pawn bar come from the supervisor's every-body list (a colony's pawns aren't listed):
`ai:[{name, kind: main|hive|subhive, owner, online, hp, food, pos, dim, task, brain, brain_state: offline|script|working|idle, script}]`.

## Additions (2026-11-03)

The design grew while it was built. The wire forms are in `docs/web/api.md` §6 ("Story Mode") and `story.ts` in the contracts.

- **Builds.** The Architect queues structures (wall, floor, roof, door, window, stairs, fence, single block, deconstruct) and
  furniture (crafting table, furnace, chest, bed, torch); pawns with build work fill them in queue order. Walls take a height
  (1–12), roofs a style (flat, slab, gable, none) and an overhang. State: `builds` and `ghosts`.
- **Levels.** Top-down shows the working level: everything from the roof line up (level + wall height + 1) is x-rayed, drawn
  as the hatched cut. PgUp / PgDn move a storey; an angled view (V) builds in 3D with the same tools, placing against the
  hovered face.
- **Designate tools** work by area the genre's way: pick a tool, drag a grid rectangle, every matching target lights up with a
  live count, release designates (Shift keeps the tool). Kinds gain `cut`, `hunt`, `forbid`, `allow` and a mine `filter`; the
  director expands an area to its targets and answers the count. Mine and Deconstruct send the exact cells instead (`blocks`):
  see "Mining height" under the Story Mode tab.
- **Storage is chests.** There is no ground stockpile (loose loot despawns). Chests get the genre's storage settings: a
  priority and a filter over a loot category tree. State: `categories` and `storages`.
- **Bills.** Workbenches (crafting table, furnace, brewing stand, anvil, enchanting table) take bills: a recipe, repeat
  mode (count, until, forever), ingredient filter and radius, worker restriction, where the product goes. State:
  `workbenches`; recipes come from `GET /api/story/recipes`. The bills inspector shows a station's facts (bookshelves and the top
  offer, the anvil's wear, the stand's water and fuel); the bill dialog shows what a station recipe needs (XP level, lapis, fuel,
  water, a brew's steps), offers only the modes the recipe takes, and an enchant's option, preferred enchantment and prefer-only.
- **AI bodies.** Main, the hives and their bodies sit in the top bar after the pawns. With them in the selection, a tool,
  order, build or bill also goes to them as a directive (`POST /api/story/directives`) with the same payload; the chat bar
  talks to them as the signed-in player. State: `ai` and `directives`.

## Web: the Story Mode tab

- **Map.** A top-down orthographic view (the world viewer's `top` camera) over the colony.
  - The mouse wheel zooms around the cursor, out to 112 blocks of view height. Pan with WASD/arrows, a middle-drag or (in full screen only) the
    screen edge.
  - In the top-down and angled views the viewer draws the whole ground the picture shows (its footprint's half diagonal,
    capped at 192 blocks) whatever the draw distance or the software quality lever says, with the fog beyond it; mobs keep
    the plain draw distance, so a zoomed-out map does not pay for far entities. Zoomed out past the draw distance, sections
    more than 32 blocks below the look-at point are skipped. The cave-culling walk of a camera far above the world starts
    just above it along the view ray (started at the camera it culled every section: the ground vanished past about 47
    blocks of zoom).
  - With no colony yet, the map opens over the main bot (else the signed-in user's player), not the world origin.
  - Pawns draw with name labels and job icons, and zones and designations are coloured overlays.
  - **Bodies as models (added 2026-11-03).** Every pawn and AI body is drawn as its Minecraft player model (skin, held
    item, armour, walking), like the Live view. They are bots of the world store; the server leaves bot players out of the
    entity list, and the map had mounted the viewer with bots off, so only mobs showed. Bots keep the full model out to at
    least 96 blocks of apparent distance, so the top-down camera hanging far above still gets models; the detailed-model
    setting at 0 still means boxes. The overlay draws only its marks on a model: selection brackets, a colour ring at the
    feet, the name (name tags setting; off keeps selected and endangered names), drafted and job marks. The hit area covers
    the model's height. Zoomed out below 9 px per block, or with no model for the body (not loaded, beyond the entity
    distance, a far box), the flat token is drawn as before. A body on a storey above the level cut is hidden like the
    blocks there (a faint token stays clickable), and bodies below a dimmed storey are dimmed.
  - **Build holograms (added 2026-11-03).** Queued cells show in the world as translucent holograms of the block they
    will become: the block's own model and texture (variant and facing) in a calm tint, about 40 % opaque, with static
    scanlines and a thin edge line. Cyan to do, brighter while a pawn builds it, grey-blue while its build waits (paused,
    missing materials), striped red when blocked or unsupported. Cells to dig are red outlines just outside the cell with a faint red fill over
    the exposed top, lifted a little above the ground around them (an outline on or inside the cell's own edges is hidden by the
    ground the cell is in). A water or lava cell still filled by a solid block is not drawn (its dig shows there). A built cell's
    hologram goes as soon as the block appears (matched by cell in the mesh worker). The cells are meshed in the worker,
    one merged mesh per state, rebuilt only when the ghosts or builds change or a block change lands on a queued cell. At
    most 20,000 cells draw; past that, and below 3 px per block, each build is one outline box. They are drawn after the
    opaque world with depth test on and depth writes off, and follow the level cut. A design being placed shows the same
    way, green where it fits and red where a block is in the way. The viewer setting "Build holograms" (default on) turns
    them off; the map then draws the flat ghosts as before. Measured (SwiftShader, 1440 × 900, top view at the default
    zoom, the mock colony): 58 fps before, 48 fps with a 2,000-cell build queued, 41 fps in the angled view.
    Fixed 2026-11-04 (Fox: "I still can't see holograms for queued builds like chests, or other blocks"): the live bot sends
    only the changed ghosts in a delta, and the web replaced its whole list with them, so every hologram went with the first
    catch-up a few seconds after the page loaded (the mock sends all of them, so mock mode never showed it). The client now
    merges ghost deltas (see "The colony state"). Ghosts are read by their 8th element: a `dig` ghost carries the block
    expected there, and is drawn as a red outline until the cell is air (by id it looked built and was hidden). Every block
    kind is the section mesher's own model, block entities included: chests (a double chest as one), beds, signs, banners,
    skulls, shulker boxes, doors, torches, slabs, stairs, fences, panes, ladders. A b1.8.1 door's upper half takes the
    lower half's facing (the queue sends it as plain 8, the 1.12.2 form, so the halves stood on different sides of the
    cell). A furniture tool's placement (chest, bed,
    furnace…) previews as a hologram too. The test hook `hologramKinds()` counts each build kind's queued and drawn cells.
- **Selection.**
  - A left-drag box selects many pawns, or objects (trees, blocks, drops) in designation mode.
  - A click selects one, shift adds, a double-click selects every visible pawn of the same kind, and Esc clears.
- **Orders.** A right-click with pawns selected opens a context menu for the target:
  - a block gives move, mine or chop
  - a mob gives attack
  - a drop gives haul
  - a design gives build
- **Speed (added 2026-11-04).** Beside Pause in the bottom-right controls, a RimWorld-style segmented control ▶ / ▶▶ / ▶▶▶
  (normal, fast, faster) sets `options.speed`; its tooltips say it is a movement-speed boost for the colony's pawns, not time.
  Keys **1 / 2 / 3** set it while no tool is in hand (the select tool); with a tool they stay the tool's (Mine / Deconstruct
  height modes, a structure tool's recent materials). A level some pawn was stepped down to by the server's rejections has a
  warning ring. See "Speed" for what it does.
- **Mining height (added 2026-11-03).** Live 11-03: Fox flattening the ground mined too deep, because a Mine drag took a whole
  storey (level − 1 to the wall height) and nothing showed how deep. A Mine or Deconstruct drag now has an explicit height mode,
  picked on the tool's options bar or with 1–5 while the tool is active:
  - **Surface** (Mine's default): the top block of each column (trees, leaves, plants and snow looked through) and the plants
    standing on it. Exactly one layer.
  - **Level**: only the blocks at the working level (PgUp / PgDn).
  - **Down to level**: from the surface down to and including the working level (terrace down to a height).
  - **Flatten here**: everything above the working level, the plants on a column already at the level included, so the ground
    comes out flat at the level. This is "make a surface level".
  - **Layers: N**: N blocks deep from each column's top, with a 1–8 stepper (+ / − while the tool is active).

  Deconstruct takes the same modes over placed blocks only (default Down to level: a building and its floor); Cut, Harvest
  and Chop target plants and trees as before. Air and liquids are never designated, nothing goes below the mode's bottom, and
  Surface and Layers see what the level's x-ray cut leaves (Down to level and Flatten here take the whole column above the
  level either way). While dragging, each affected column's top face is outlined (fuller the deeper it goes; the bottom dashed
  in the angled view) and the label says `Mine (Surface): y 64 → 62 · 3 layers · 48 blocks`. The release sends exactly those
  cells as `blocks`, never an area the director could expand deeper. One drag takes at most 4096 cells: over that the label turns
  red ("too many") and the release only says so. The columns come from the viewer engine's `stacks` query (each column's top and
  the cells under it, in the mesh worker), so the preview reads the same world the map shows.
- **Cancelling a drag (added 2026-11-03).** Esc, or a press of the other mouse button (right during a left drag, left during a
  right-drag formation line), cancels the drag in progress the genre's way: the preview goes, a small "Cancelled" toast shows,
  and the release does nothing, not even the right-click's usual "put the tool down". A middle-drag pan is never cancelled.
  A click under the drag threshold (5 px) paints no zone, and a designate box with nothing in it posts nothing (the tool
  stays). In full screen Esc is normally the browser's way out: while a drag is held, the map locks Esc (Keyboard Lock, Chromium:
  holding Esc still leaves full screen) so it cancels the drag instead. Where Esc can't be locked (other browsers), leaving full
  screen also cancels the drag. Right-click cancel works the same in full screen.
- **Draft.** A draft toggle, with R as the hotkey. Shift queues orders.
- **Formations.** With drafted pawns selected, a right-drag draws a line; on release every drafted pawn gets its own move
  order (`POST .../orders`, one pawn each) to a distinct standable cell spread evenly along it, at least a cell apart. Pawns
  and slots are paired in order along the line, so their paths do not cross. While dragging, the line and a ghost token
  per slot (the pawn's colour and initials) show where they go; a slot on an unstandable cell (water, a wall, no headroom)
  moves to the nearest standable one within 3 cells, never two pawns on one cell. Undrafted pawns in the selection are left
  out (the genre's rule; the toast says so). A right-click without a drag (under 5 px) is the order menu as before.
- **Time of day.** A slider under the clock over one day (06:00 at tick 0; dawn, noon, dusk and midnight marked) follows
  the page's one world clock (see "Time of day") and sets the world time on release, through the bot's op account. Without one it is disabled with "No op account — op the main bot
  to change time". Needs the `story` role.
- **Bottom bar:**
  - pawn portraits with hp, food and job, where clicking selects (and centres the map when the pawn is off it) and a
    double-click always centres the map
  - a designate palette: chop, mine, harvest, haul, build, deconstruct, cancel
  - a zones palette (see "Zones": work zones, overlay layers, Erase with its kind)
  - Work, Schedule and Colony panels
- **Work panel.** A grid of pawns by work type. Click to cycle 1–4 and off, with skill shown as the cell tint and a
  passion mark. Roles (see "Roles"):
  - Each row starts with a role dropdown (Custom or a role, with the role's icon). A role row shows the role's priorities in
    dashed, lighter "from role" cells; clicking one turns the pawn Custom with the role's values plus the change, and a line under
    the grid says "… is now Custom (was Farmer) · Undo" for 8 s.
  - A checkbox per row (the map's selected pawns come ticked; the header box ticks all) and "N selected · Set role: …" set a role
    for several pawns at once.
  - The **Roles** button opens the role editor below the grid: one row per role in the same 1–4 cell style (click cycles, every
    pawn with the role follows at once), the icon (click for the next), rename, how many pawns have it, delete (two clicks; its
    pawns turn Custom), "New role…" + Add, and "Reset built-in roles".
- **Schedule panel.** A grid of pawns by the 24 clock hours (00–23, as the bot indexes them); the current hour's column
  follows the page's world clock. Paint with the brush (work, sleep, anything, free) by dragging
  across cells. Copy and paste between pawns.
- **Inspector.** The selected pawn's characteristics, skills, current job and queue, and orders.
- **Houses and beds.** Any number of home zones (the Zones tool's Home area, or "Draw a house" in the Colony tab), each
  named, renamed and deleted from the Colony tab's Houses list and drawn with the home-area tint and its name. Beds show on
  the map with the owner's colour and initials; clicking one opens its inspector (owner, a "Set owner" pawn picker, Clear).
  A pawn's inspector shows its bed with "Show".
- **Colony panel.** Create a colony (name, number of pawns, home = the current map centre, shown as "Settles at x, z"),
  pause it or release it, and migrate: "Move home here (migrate)" (the map centre) or "Set home on the map" (a house tool:
  click the spot) asks for a confirm, then home moves to that column (its surface) and far pawns are brought there. The
  map marks home with a house.
- **Alerts.** A feed of alerts, such as a pawn in danger or idle with nothing to do.
- **Notifications (added 2026-11-03).** On the right edge, the genre's split:
  - **Alerts** are ongoing conditions derived from the state: pawns offline, pawns without a bed, no food in storage, no
    seeds for a growing zone (both once storage contents are known), main not op. They clear themselves and are never
    clicked away. A click selects the pawns or opens the storage.
  - **Messages** are the colony's alerts as they arrive. Info expires after 6 s and warn after 12 s; danger stays until
    dismissed. Hovering holds the timers. Repeats of a kind (the text with its positions and numbers taken out) collapse
    into one message with a ×count, and each repeat refreshes its timer. At most four show; "+N more" opens the log.
    "Dismiss all" and Esc on the stack clear them. An alert is taken in once (by id), so a state poll never shows it again;
    alerts older than a minute when the page first sees them go to the log only.
  - **Log**: the last 200 messages and the page's own toasts, coalesced the same way, filterable (all, problems, events)
    and searchable. A click jumps to the pawn, the alert's fix or the place named in the text. An unread badge counts new
    entries while it is closed.
- **Full screen (added 2026-11-03).** The button at the top right or Shift+F (F alone is the Forbid tool) puts the whole Story
  Mode element in full screen, the Live world pane's way: the map and the whole HUD (colonist bar, tabs, inspector, alerts,
  clock, toasts, menus). Esc (the browser's) or Shift+F leaves it. The map follows the new size every frame; drags, box
  selection and formations work the same. Portalled overlays (dialogs such as the bill dialog, tooltips) mount inside the
  full-screen element through `PortalContainer` (`components/ui/overlays`), or they would be invisible.
- **Offline pawns and failed designations** see "Offline pawns" and "A pawn's own claim" above.
- **Permissions.** Needs the `admin` role, or a new `story` role, for writes. Viewers can watch.
- **Performance.** It must stay smooth with software WebGL at 40 fps or more. It uses the shared renderer, and the
  overlays are cheap. Live 11-03 it lagged with 10 pawns and a few hundred designations. Since then the designations' marks and
  the failed ones' clusters are worked out once per state (not per frame), each mark is stamped from a cached sprite and skipped
  off screen, the pawn portraits and the viewer's element are memoised (the page re-renders on every state poll), and the map
  centre reaches the page only while the Colony tab or the new-colony card needs it. Measured with `STORY_PERF=1` in
  `e2e/story.spec.ts` (the mock's `busy` seed: 10 pawns, 600 surface mine designations plus the cuts and chops of a 41×41 box, 120 failed; Chromium with SwiftShader, dev
  build, 1440×900), before → after: panning 35 → 40 fps (p95 frame 50 → 33 ms) and main-thread script 263–299 → 178 ms/s;
  idle 43 → 40 fps (noise; p95 33 ms both), script 199–245 → 211 ms/s; zoomed in on the failed labels 51 → 57 fps, script
  213–219 → 175 ms/s. In the CPU profile React's render work fell 981 → 371 ms and the overlay's draw 779 → 491 ms per 12 s.
  `STORY_PROFILE=<file>` also saves the CPU profile.

## Construction (added 2026-11-03)

Building is queued, colony-sim style. Pawns with Build enabled fill queued builds in queue order, bottom-up.
- **Architect palette:** wall (height 1–12), floor, door, window, stairs, fence, roof (flat, slab, gable or none, with an
  optional overhang), single block, and deconstruct. Each takes a material.
- **Placement:** drag on the map to place, and Q/E rotates. Queued blueprints draw as block holograms in the world (see
  "Build holograms" above), or as flat ghost blocks with the setting off.
- **Build queue panel:** progress, the reason a build is blocked (such as missing materials), reorder, pause and cancel.
- **Multi-floor levels:** PgUp/PgDn change the working level. Everything above it, roofs included, is x-rayed in
  top-down, and the level below is dimmed.
- **Angled view:** a button (V) switches to an angled 3D view with the same tools and queue, for precise 3D building.
  Placement snaps to block faces. Top-down stays the default.
- **Bot side:** each build item expands into per-block placement tasks, leased across builder pawns. Pawns fetch
  materials from stockpiles. A material shortage marks the build blocked and raises an alert. Routes:
  `POST /api/story/colonies/{id}/builds` (add, remove, pause, resume, reorder), with `builds` and `ghosts` in the colony
  state.
