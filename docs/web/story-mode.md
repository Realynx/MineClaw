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
| GET | `/api/story` | `{colonies:[{id, name, created, paused, active, pawns:[name], version}], ai:[Ai], directives:[Directive]}` |
| POST | `/api/story/colonies` | `{name (1..40 chars), pawns (1..12), home?:{x,y,z}}` → `{id}` (`story-<n>`). Spawns script-only bodies running `story_pawn` |
| POST | `/api/story/colonies/{id}/release` | `{}` → `{id, released}`. The pawns deposit and log out; the saved state is kept (`active:false`) |
| POST | `/api/story/colonies/{id}/resume` | `{}` → `{id, pawns, note}`. A released colony spawns its pawns again, their old accounts first |
| POST | `/api/story/colonies/{id}/pause` | `{paused}` → `{paused}` |
| GET | `/api/story/colonies/{id}?since=rev` | The state (below): full, or a delta when `since` is given |
| POST | `/api/story/colonies/{id}/priorities` | `{pawns:[name], work:{type: 0..4}}` (0 = off) → `{ok}` |
| POST | `/api/story/colonies/{id}/schedule` | `{pawns:[name], hours:{"0".."23": "work"|"sleep"|"anything"|"free"}}` → `{ok}` |
| POST | `/api/story/colonies/{id}/draft` | `{pawns:[name], drafted}` → `{ok}` |
| POST | `/api/story/colonies/{id}/orders` | `{pawns:[name], order:{kind: move|attack|chop|mine|haul|build|hold|stop, target?:{x,y,z}|{entity:id}|{area:[x1,y1,z1,x2,y2,z2]}, design?:id}, queue?:bool}` → `{ids:[order id]}` |
| POST | `/api/story/colonies/{id}/zones` | `{op: add|remove, kind: chop|mine|farm|home|forbidden, area}` → add: `{id}`; remove: `{removed:[id]}` (every zone of that kind, or of any kind, that overlaps the area) |
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
 pawns:[Pawn], zones:[{id, kind, area}], designations:[Designation], orders:[{id, pawn, kind, target, design, given, left}],
 alerts:[{at, level: info|warn, text, pawn}], builds:[Build], ghosts:{rev, blocks:[[x,y,z,id,meta,build_id,state]]},
 storages:[Storage], workbenches:[Workbench], categories:[Category] (full only), ai:[Ai], directives:[Directive],
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
- A failed target is skipped for 30 s.
- A designation that failed 3 times is dropped, with an alert.

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

Designation: `{id, kind, work_type, target:[x,y,z]|{entity}, blocks, label, created}`.

## Construction

`POST …/builds`:
- `{op:"add", items:[Item]}` → `{ids}`. At most 64 items, each at most 4096 blocks.
- `{op:"remove", ids}`, `{op:"pause", ids}` or `{op:"resume", ids}` → `{ids}`.
- `{op:"reorder", ids}` → `{ids}`: the whole queue order, with the named ones first and the rest after them in their order.

Item: `{kind: wall|floor|roof|door|window|stairs|fence|block|remove|crafting_table|furnace|chest|bed|torch, area?:[x1,y1,z1,x2,y2,z2],
blocks?:[[x,y,z]], material?: <block name> | [id,meta], height?:1..12, roof?:{style: flat|slab|gable|none, material?, overhang?:0|1},
facing?: n|e|s|w, level?:y, name?}`.

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

Build: `{id, kind, label, status: queued|active|paused|blocked|done, reason, blocks_total, blocks_done, material, order, created}`.
Ghosts: `[x,y,z,id,meta,build_id,state]`, with state `todo|leased|done|blocked`.

Execution:
- Every ghost is one job for a pawn with Build on, by the picker and lease rules.
- Order: the first unpaused build in queue order gives out its jobs, only from its lowest unfinished level (support first).
  Blocks touching something solid go first, then the nearest. When all of those are leased, the next build's jobs go out.
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

## Web: the Story Mode tab

- **Map.** A top-down orthographic view (the world viewer's `top` camera) over the colony.
  - The mouse wheel zooms around the cursor. Pan with WASD/arrows, a middle-drag or the screen edge.
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
- **Bottom bar:**
  - pawn portraits with hp, food and job, where clicking selects and a double-click centres the map
  - a designate palette: chop, mine, harvest, haul, build, deconstruct, cancel
  - a zones palette
  - Work, Schedule and Colony panels
- **Work panel.** A grid of pawns by work type. Click to cycle 1–4 and off, with skill shown as the cell tint and a
  passion mark.
- **Schedule panel.** A grid of pawns by the 24 hours. Paint with the brush (work, sleep, anything, free) by dragging
  across cells. Copy and paste between pawns.
- **Inspector.** The selected pawn's characteristics, skills, current job and queue, and orders.
- **Colony panel.** Create a colony (name, number of pawns, home = the current map centre), pause it or release it.
- **Alerts.** A feed of alerts, such as a pawn in danger or idle with nothing to do.
- **Permissions.** Needs the `admin` role, or a new `story` role, for writes. Viewers can watch.
- **Performance.** It must stay smooth with software WebGL at 40 fps or more. It uses the shared renderer, and the
  overlays are cheap.
