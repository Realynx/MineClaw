# Story Mode: colony management over the hive

Story Mode is a browser tab where Fox runs a colony of pawns, in the style of a colony-management game. Its mechanics are
the genre's usual ones:
- work priorities per pawn and work type
- a daily schedule
- characteristics that shape what a pawn is good at
- direct orders to one pawn or a group (drafting)
- zones and designations painted on a top-down map

It's an original design on top of MineClaw. It uses no other game's art, names or text.

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

  The hour comes from the world time, at 1000 ticks per hour, with 0 = 06:00.
- **Zones,** painted as areas: chop, mine, farm, home, forbidden. Storage is chests with their own settings (see "Storage").
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
- **Pause.** "Pause colony" makes every pawn hold. There is no time control: the Minecraft server owns the clock.

## Supervisor routes (bot)

They sit behind `Supervisor:Viewer:Enabled` (else 404) and the token in the `X-Supervisor-Token` header (else 403). Writes
(every POST) also need `Supervisor:Viewer:AllowStory`, which defaults to true when the viewer is on (false → 403; GETs still
work, so viewers can watch). JSON is snake_case with nulls written. Errors are `{error}` with 400 (validation), 403, 404
(no such colony or route), 405, 413 (body over 64 KB) or 503 (no world view yet).

| Method | Path | Body / result |
|---|---|---|
| GET | `/api/story` | `{colonies:[{id, name, created, paused, active, pawns:[name], version}], ai:[Ai], directives:[Directive], time_control:{available, by}}` |
| POST | `/api/story/time` | `{time: 0..23999}` (whole number) → `{time, by}`; 400 on a bad body, 409 `{error:"no op account"}` when no account the bot controls is op, 429 for more than one change a second. See "Time of day" |
| POST | `/api/story/colonies` | `{name (1..40 chars), pawns (1..12), home?:{x,y,z}}` → `{id}` (`story-<n>`). Spawns script-only bodies running `story_pawn` |
| POST | `/api/story/colonies/{id}/release` | `{}` → `{id, released}`. The pawns deposit and log out; the saved state is kept (`active:false`) |
| POST | `/api/story/colonies/{id}/resume` | `{}` → `{id, pawns, note}`. A released colony spawns its pawns again, their old accounts first |
| POST | `/api/story/colonies/{id}/pause` | `{paused}` → `{paused}` |
| POST | `/api/story/colonies/{id}/home` | `{home:{x,y,z}}` (whole numbers) → `{home:[x,y,z]}`; 400 on a bad body, 404 for an unknown colony. Moves and saves the colony's home; pawns far from it are brought there (see "Home") |
| GET | `/api/story/colonies/{id}?since=rev` | The state (below): full, or a delta when `since` is given |
| POST | `/api/story/colonies/{id}/priorities` | `{pawns:[name], work:{type: 0..4}}` (0 = off) → `{ok}` |
| POST | `/api/story/colonies/{id}/schedule` | `{pawns:[name], hours:{"0".."23": "work"|"sleep"|"anything"|"free"}}` → `{ok}` |
| POST | `/api/story/colonies/{id}/draft` | `{pawns:[name], drafted}` → `{ok}` |
| POST | `/api/story/colonies/{id}/orders` | `{pawns:[name], order:{kind: move|attack|chop|mine|haul|build|hold|stop, target?:{x,y,z}|{entity:id}|{area:[x1,y1,z1,x2,y2,z2]}, design?:id}, queue?:bool}` → `{ids:[order id]}` |
| POST | `/api/story/colonies/{id}/zones` | `{op: add, kind: chop|mine|farm|home|forbidden, area, name?}` → `{id}`; `{op: remove, kind?, area}` → `{removed:[id]}` (every zone of that kind, or of any kind, that overlaps the area); `{op: remove, id}` → `{removed:[id]}`; `{op: rename, id, name}` → `{id, name}` (name ≤ 40 chars, empty clears it) |
| POST | `/api/story/colonies/{id}/beds` | `{op:"assign", bed:{x,y,z}, pawn}` or `{op:"unassign", bed:{x,y,z}}` → `{beds:[Bed]}`; 400 on a bad body or no bed there, 404 for an unknown colony or pawn. See "Homes and beds" |
| POST | `/api/story/colonies/{id}/designations` | See "Designations" → `{count, targets}` or `{count, removed}` |
| POST | `/api/story/colonies/{id}/builds` | See "Construction" |
| POST | `/api/story/colonies/{id}/storage` | See "Storage" |
| POST | `/api/story/colonies/{id}/bills` | See "Workbenches & bills" |
| GET | `/api/story/recipes?workbench=<kind>` | `[Recipe]` (every bench when omitted) |
| POST | `/api/story/directives` | See "AI hives" |
| GET | `/api/story/directives` · `/api/story/directives/{id}` | `{directives:[Directive]}` · one directive with its `line`, `delivered` and `detail` |

`pawns:[name]` takes the account (`maid_chan_101`), its number (`101`) or the display name. A zone of kind `stockpile` is refused:
storage is chests (see "Storage").

### The colony state

```
{id, rev, since, full, at, time_of_day, hour, paused, active, home:[x,y,z]|null, tie_break: "column"|"skill", version,
 work_types:[guard, doctor, cook, hunt, build, farm, mine, chop, craft, haul],
 pawns:[Pawn], zones:[{id, kind, area, name}], beds:[{pos:[x,y,z], dim, pawn, zone}], designations:[Designation], orders:[{id, pawn, kind, target, design, given, left}],
 alerts:[{at, level: info|warn, text, pawn}], builds:[Build], ghosts:{rev, blocks:[[x,y,z,id,meta,build_id,state]]},
 storages:[Storage], workbenches:[Workbench], categories:[Category] (full only), ai:[Ai], directives:[Directive],
 time_control:{available: bool, by: account|null},
 removed:{zones:[id], designations:[id], builds:[id], storages:["x,y,z"]} (delta only)}
```

A delta (`since=rev`) carries only the objects with a newer revision, plus every pawn (they move all the time) and `removed`.
Pass the answer's `rev` back as the next `since`. This is the `story` event source the bridge polls, at 2 Hz while a client
watches.

The **Pawn** object:

```
{name, display, kind:"pawn", online, hp, food, pos:[x,y,z], dim, drafted, state: idle|working|moving|eating|sleeping|fleeing|fighting|drafted|offline,
 job:{type, label, target?, progress?, since}|null, queue:[Order],
 skills:{type:{level, xp, passion}}, traits:[{id, label, effect}], priorities:{type:0..4}, schedule:[24 × block], held, inventory:[[id,meta,count]] }
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
4. After a gathering job, or with ≤ 4 free slots, the pawn takes what it carries to storage.
5. The schedule hour (1000 ticks an hour, tick 0 = 06:00):
   - sleep: go home.
   - free: idle at home.
   - anything: eat when food ≤ 14, else work.
   - work: work.
6. Work. The work types with a priority of 1–4 go lowest number first. Equal priorities go in column order
   (`tie_break:"column"`, the default) or by the pawn's skill first (`"skill"`). Within a type, the nearest open target wins.
   With nothing to do, the pawn idles and the colony gets an alert, at most every 5 minutes.

Every target is leased: `b:x,y,z`, `e:<entity>`, `g:<build>|x,y,z` or `bench:x,y,z`.
- A lease lasts 90 s and progress reports renew it. It goes back when the job ends, fails or times out.
- A pawn keeps its job until it is done, fails, or an order (or draft, pause, stop) replaces it. New work never cuts a working pawn.
- A failed target rests before anyone takes it again: 30 s, then 1 min, then 10 min after each further failure. A failed delivery
  rests the same way for that pawn.
- A pawn whose last 3 jobs failed within 20 s rests 15 s before it picks again (orders and urgent needs still come first).
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
  home, and the colony hears "X has no bed" once.

### Time of day

The minecraft server owns the clock, but an op account can set it. `time_control` (in `GET /api/story` and in every colony state)
is `{available, by}`. It is available when an account this bot controls is op: main first, else the first op body. `by` names it.

- **Op detection.** A 1.12.2 server tells each account its op level as Entity Status 24..28 on its own player, on login and on
  every /op and /deop (level = status − 24). Op means level 2 or more. A b1.8.1 server never says, so there an account is op only
  when `Supervisor:Viewer:OpAccounts` lists it. Otherwise it's unknown, and an unknown account doesn't count.
- **Main not op.** Main should be op, because it runs the whitelist. When the server says main isn't, the bot logs a warning once and
  each colony gets one alert.
- **The command.** `POST /api/story/time {time}` sends `/time set <time>` from that account. Main's goes through the command gate's
  scoped exception (only that exact shape, never when the config blocks `/time`, audited), and another body's after the same
  blocklist check, audited. The request is the authenticated story-role owner's. At most one change a second (429 otherwise,
  with `Retry-After: 1`). Each change logs one line.

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
- **A job** is `{id, type, kind, label, at?:"x,y,z", fetch?:[{from, item, n}], stash?:[{to, items}], ...}`.
  - Kinds: `go|chop|dig|harvest|collect|place|attack|eat|craft|smelt|collect_furnace|give|blueprint|sleep|idle|hold|end`.
  - The script withdraws the `fetch` items, runs the kind with the existing primitives (`go`, `chop`, `dig`, `collect`, `place`,
    `attack`, `eat`, `craft`, `smelt`, `collect_furnace`, `handTo`, `build`, `wait`), deposits the `stash` items and reports.

### Designations

- Add: `POST …/designations` `{op:"add", kind: chop|cut|harvest|mine|haul|hunt|deconstruct|forbid|allow, area?:[x1,y1,z1,x2,y2,z2] | blocks?:[[x,y,z]], filter?: all|ores|stone|soil}`
  → `{count, targets:[[x,y,z]] | [entity id]}`.
- Remove: `{op:"remove"|"cancel", kind?, area? | blocks? | ids?}` → `{count, removed:[id]}`.

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

Designation: `{id, kind, work_type, target:[x,y,z]|{entity}, blocks, label, created, status: open|failed, reason, fails}`.

A chop designation, order or zone means whole trees. Any log or leaf of a tree stands for it: the canopy top a top-down click lands
on (or the air just above it), a branch, or a box drawn only over the crowns. Each resolves to the tree's trunk base (the lowest log
standing on ground), and the box's columns are searched 32 blocks below its bottom for the trunks. Branches and crowns are never trees
of their own.

A chop job also carries `wood` (the version's log item names), `wood_blocks`, `planks` and `logs` (the tree's logs, lowest first).
`story_pawn` uses them to work a tree from an empty inventory. It fells what it can reach. If some logs came down but not all, it
crafts planks from them in the 2x2 grid (clutch and pillar blocks for the work up high) and goes once more at the lowest log still
standing. The fall-safety rules of `chop_tree` are unchanged. Logs still out of reach after that make a partial job: reported done
with a note starting `partial:`. The designation leaves the map and an alert says what was left.

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

## Construction

`POST …/builds`:
- `{op:"add", items:[Item]}` → `{ids}`. At most 64 items, each at most 4096 blocks.
- `{op:"remove", ids}`, `{op:"pause", ids}` or `{op:"resume", ids}` → `{ids}`.
- `{op:"reorder", ids}` → `{ids}`: the whole queue order, with the named ones first and the rest after them in their order.

Item: `{kind: wall|floor|roof|door|window|stairs|fence|block|remove|crafting_table|furnace|chest|bed|torch, area?:[x1,y1,z1,x2,y2,z2],
blocks?:[[x,y,z]], material?: <block name> | [id,meta], height?:1..12, roof?:{style: flat|slab|gable|none, material?, overhang?:0|1},
facing?: n|e|s|w, level?:y, name?, group?}`.

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
- **group:** the items of one placed design. A design with a basement (Design Studio dug cells) is queued as a `remove`
  item of its dug cells first, then its block items, all with the same `group`: its blocks wait until that dig is done.
  The design's ground floor goes on the clicked level and the basement under it.

Build: `{id, kind, label, status: queued|active|paused|blocked|done, reason, blocks_total, blocks_done, material, order, created}`.
Ghosts: `[x,y,z,id,meta,build_id,state]`, with state `todo|leased|done|blocked`.

Execution:
- Every ghost is one job for a pawn with Build on, by the picker and lease rules.
- Order: the first unpaused build in queue order gives out its jobs, only from its lowest unfinished level (support first).
  Blocks touching something solid go first, then the nearest. When all of those are leased, the next build's jobs go out.
  A build that only digs goes top-down instead (a buried cell opens once the one above it is gone). A build of a `group`
  waits while an earlier build of that group still has cells to dig (a dig that keeps failing doesn't hold it).
- Never a block where a player stands.
- Material: the pawn uses its own, or fetches it from the nearest storage that holds it. With none anywhere, the build is
  `blocked` with `reason:"need 34 wooden planks"` and an alert. It goes on by itself once some is stored (a Chop, Mine or Haul
  pawn, or a bill, can supply it).
- A ghost that failed 3 times is `blocked`.
- Facing uses the place tool's own placement. Scaffolding and pillaring are the place and goto tools' own; there is no extra
  scaffold.

## Storage

Storage is chests; there are no ground stockpiles. `POST …/storage`:
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

Hauling:
- A pawn that carries loot (after chopping, mining, harvesting, hunting or picking up drops) delivers each stack to the
  accepting chest with the highest priority, then the nearest, that has room. A full chest is skipped.
- When nothing accepts a stack, the colony gets an alert "No storage accepts X" and the pawn keeps it. Loot is never dropped.
- Tools, weapons, armour and one food stack stay with the pawn.
- Background haul jobs, after every other haul: higher-priority chests pull matching items out of lower-priority ones, and a
  chest whose filter changed gives up what it no longer accepts.

## Workbenches & bills

Workbenches:
- Crafting tables and furnaces in the home zone (or 12 blocks around home) are found every 30 s, or registered by their first
  bill.
- Brewing stands, anvils and enchanting tables are listed (`supported:false`) but take no bills yet: there is no primitive.
- Workbench: `{pos, kind, label, supported, loaded, ready_at, bills:[Bill]}`.

Recipes: `GET /api/story/recipes?workbench=crafting_table|furnace|brewing_stand|anvil|enchanting_table` →
`[{id, label, workbench, work_type: craft|cook, output:[id,meta,count], inputs:[{options:[[id,meta]], count}], fuel}]`.
Ids are `craft:<output name>` and `smelt:<input name>`. Furnace food is Cook work; the rest is Craft.

`POST …/bills`:
- `{op:"add", bench:[x,y,z], bill}` → `{id}`
- `{op:"update", id, …bill fields}`, `{op:"remove"|"pause"|"resume", id}`
- `{op:"reorder", bench, ids}` → `{ids}`. Bills run top to bottom per bench.

Bill: `{id, recipe, mode: count|until|forever, target, done, paused, suspended_reason, ingredients:{allow, deny}, radius, pawn: "any"|name,
min_skill, store: "best"|{chest:[x,y,z]}}`. `until` counts the product in every storage plus the pawns' inventories.

Execution:
- A pawn with the bill's work type on, and allowed by `pawn` and `min_skill`, takes the first runnable bill of the nearest free
  bench. One pawn works a bench at a time.
- It fetches the ingredients from storages within `radius` of the bench (the ingredient filter applies), crafts, and stores the
  product in the bill's chest, else by the storage rules.
- A furnace is loaded and left to smelt (10 s an item). Its fuel comes from storage: coal or charcoal, then planks, then logs.
  A later job of the same work type collects it, and the output is stored.
- Missing ingredients or fuel set `suspended_reason` and raise an alert. Resume clears it.

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
  director expands an area to its targets and answers the count.
- **Storage is chests.** There is no ground stockpile (loose loot despawns). Chests get the genre's storage settings: a
  priority and a filter over a loot category tree. State: `categories` and `storages`.
- **Bills.** Workbenches (crafting table, furnace, brewing stand, anvil, enchanting table) take bills: a recipe, repeat
  mode (count, until, forever), ingredient filter and radius, worker restriction, where the product goes. State:
  `workbenches`; recipes come from `GET /api/story/recipes`.
- **AI bodies.** Main, the hives and their bodies sit in the top bar after the pawns. With them in the selection, a tool,
  order, build or bill also goes to them as a directive (`POST /api/story/directives`) with the same payload; the chat bar
  talks to them as the signed-in player. State: `ai` and `directives`.

## Web: the Story Mode tab

- **Map.** A top-down orthographic view (the world viewer's `top` camera) over the colony.
  - The mouse wheel zooms around the cursor, out to 112 blocks of view height. Pan with WASD/arrows, a middle-drag or the
    screen edge.
  - In the top-down and angled views the viewer draws the whole ground the picture shows (its footprint's half diagonal,
    capped at 192 blocks) whatever the draw distance or the software quality lever says, with the fog beyond it; mobs keep
    the plain draw distance, so a zoomed-out map does not pay for far entities. Zoomed out past the draw distance, sections
    more than 32 blocks below the look-at point are skipped. The cave-culling walk of a camera far above the world starts
    just above it along the view ray (started at the camera it culled every section: the ground vanished past about 47
    blocks of zoom).
  - With no colony yet, the map opens over the main bot (else the signed-in user's player), not the world origin.
  - Pawns draw with name labels and job icons, and zones and designations are coloured overlays.
- **Selection.**
  - A left-drag box selects many pawns, or objects (trees, blocks, drops) in designation mode.
  - A click selects one, shift adds, a double-click selects every visible pawn of the same kind, and Esc clears.
- **Orders.** A right-click with pawns selected opens a context menu for the target:
  - a block gives move, mine or chop
  - a mob gives attack
  - a drop gives haul
  - a design gives build
- **Draft.** A draft toggle, with R as the hotkey. Shift queues orders.
- **Formations.** With drafted pawns selected, a right-drag draws a line; on release every drafted pawn gets its own move
  order (`POST .../orders`, one pawn each) to a distinct standable cell spread evenly along it, at least a cell apart. Pawns
  and slots are paired in order along the line, so their paths do not cross. While dragging, the line and a ghost token
  per slot (the pawn's colour and initials) show where they go; a slot on an unstandable cell (water, a wall, no headroom)
  moves to the nearest standable one within 3 cells, never two pawns on one cell. Undrafted pawns in the selection are left
  out (the genre's rule; the toast says so). A right-click without a drag (under 5 px) is the order menu as before.
- **Time of day.** A slider under the clock over one day (06:00 at tick 0; dawn, noon, dusk and midnight marked) sets the
  world time on release, through the bot's op account. Without one it is disabled with "No op account — op the main bot
  to change time". Needs the `story` role.
- **Bottom bar:**
  - pawn portraits with hp, food and job, where clicking selects (and centres the map when the pawn is off it) and a
    double-click always centres the map
  - a designate palette: chop, mine, harvest, haul, build, deconstruct, cancel
  - a zones palette
  - Work, Schedule and Colony panels
- **Work panel.** A grid of pawns by work type. Click to cycle 1–4 and off, with skill shown as the cell tint and a
  passion mark.
- **Schedule panel.** A grid of pawns by the 24 hours. Paint with the brush (work, sleep, anything, free) by dragging
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
- **Permissions.** Needs the `admin` role, or a new `story` role, for writes. Viewers can watch.
- **Performance.** It must stay smooth with software WebGL at 40 fps or more. It uses the shared renderer, and the
  overlays are cheap.

## Construction (added 2026-11-03)

Building is queued, colony-sim style. Pawns with Build enabled fill queued builds in queue order, bottom-up.
- **Architect palette:** wall (height 1–12), floor, door, window, stairs, fence, roof (flat, slab, gable or none, with an
  optional overhang), single block, and deconstruct. Each takes a material.
- **Placement:** drag on the map to place, and Q/E rotates. Blueprints draw as ghost blocks, coloured todo, leased, done
  or blocked.
- **Build queue panel:** progress, the reason a build is blocked (such as missing materials), reorder, pause and cancel.
- **Multi-floor levels:** PgUp/PgDn change the working level. Everything above it, roofs included, is x-rayed in
  top-down, and the level below is dimmed.
- **Angled view:** a button (V) switches to an angled 3D view with the same tools and queue, for precise 3D building.
  Placement snaps to block faces. Top-down stays the default.
- **Bot side:** each build item expands into per-block placement tasks, leased across builder pawns. Pawns fetch
  materials from stockpiles. A material shortage marks the build blocked and raises an alert. Routes:
  `POST /api/story/colonies/{id}/builds` (add, remove, pause, resume, reorder), with `builds` and `ghosts` in the colony
  state.
