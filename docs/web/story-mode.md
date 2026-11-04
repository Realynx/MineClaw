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
- **Zones,** painted on the map RimWorld-style as cell sets (see "Zones"): chop, mine, farm (growing zones), fish (see
  "Fishing") are work zones;
  home and forbidden are overlay layers. Storage is chests with their own settings (see "Storage").
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
| POST | `/api/story/colonies/{id}/online` | `{pawns:[name]\|null, over_cap?:bool}` → `{started:[account], blocked:[{pawn, reason}]}`; 400 on a bad body, 404 for an unknown colony or pawn. Offline pawns are whitelisted again and (re)started now, the player cap permitting (see "Offline pawns") |
| POST | `/api/story/colonies/{id}/pawns` | `{op:"add", count:1..12}` \| `{op:"remove", pawn}` → `{pawns:[name], wanted, pending:[{reason}], leaving:[name], blocked:[{reason}]}`; 400 on a bad body, past 12, or the last pawn; 404 for an unknown colony or pawn. See "Adding and removing pawns" |
| POST | `/api/story/colonies/{id}/pause` | `{paused}` → `{paused}` |
| POST | `/api/story/colonies/{id}/home` | `{home:{x,y,z}}` (whole numbers) → `{home:[x,y,z]}`; 400 on a bad body, 404 for an unknown colony. Moves and saves the colony's home; pawns far from it are brought there (see "Home") |
| GET | `/api/story/colonies/{id}?since=rev` | The state (below): full, or a delta when `since` is given |
| POST | `/api/story/colonies/{id}/priorities` | `{pawns:[name], work:{type: 0..4}}` (0 = off) → `{ok}` |
| POST | `/api/story/colonies/{id}/schedule` | `{pawns:[name], hours:{"0".."23": "work"|"sleep"|"anything"|"free"}}` → `{ok}` |
| POST | `/api/story/colonies/{id}/draft` | `{pawns:[name], drafted}` → `{ok}` |
| POST | `/api/story/colonies/{id}/orders` | `{pawns:[name], order:{kind: move|attack|chop|mine|haul|build|hold|stop, target?:{x,y,z}|{entity:id}|{area:[x1,y1,z1,x2,y2,z2]}, design?:id}, queue?:bool}` → `{ids:[order id]}` |
| POST | `/api/story/colonies/{id}/zones` | `{op: add, kind: chop|mine|farm|fish|home|forbidden, area, into?, name?, plant?, sow?, harvest?}` paints the area (see "Zones"); `{op: remove, kind?: kind|any, area}` erases its cells; both → `{id?, created, merged:[{into, from}], grown, shrunk, split:[{from, into}], deleted, removed (= deleted), warnings?}`; `{op: remove, id}` → `{removed:[id], deleted:[id]}` (the whole zone); `{op: rename, id, name}` → `{id, name}` (name ≤ 40 chars, empty clears it); `{op: update, id, plant?, sow?, harvest?}` → `{id, warnings}`; `{op: seek_seeds, id}` → `{count, kind: cut|bill}`; `{op: bill_add, id, plant, mode?, target?, paused?}` → `{id, bill, ids, active_bill, warnings}`, `bill_update {id, bill, plant?, mode?, target?, paused?}`, `bill_remove|bill_pause|bill_resume {id, bill}`, `bill_reorder {id, ids}` (see "Farming") |
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
 pawns:[Pawn], seats:{online, wanted, cap_free, kept_for_regulars, pending}, zones:[{id, kind, area, rows, cells, name, plant?, active_plant?, bills?, active_bill?, sow?, harvest?, status?, seeds?, needs_hoe?}], plants:[Plant] (full only), beds:[{pos:[x,y,z], dim, pawn, zone}], designations:[Designation], orders:[{id, pawn, kind, target, design, given, left}],
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
{name, display, kind:"pawn", online, hp, food, pos:[x,y,z], dim, drafted, state: idle|working|moving|eating|sleeping|fleeing|fighting|drafted|offline, leaving, offline_reason,
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
  home, and the colony hears "X has no bed" once. That spot is its own (see "Idle behaviour"): resting pawns never stack on one
  cell, and each keeps its spot between its rests.

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
state carries `seats: {online, wanted, cap_free, kept_for_regulars}` (the last two null when unknown).

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
  after dark). With none, each stands at its own spread spot by home ("Idle (at home)") and stays there. Sleep hours are beds as
  before.
- **No home** (or another dimension): the pawn holds where it is ("idle: nothing to do"); new work cuts that hold as before.
- **Spread spots.** A no-bed rest, a flight home and a teleport home (founding, migration, a join far away) each get the pawn's own
  free cell nearest home, never one cell for all.

### Adding and removing pawns (added 2026-11-03)

Fox 11-03: "+ and − buttons to add or remove pawns in Story Mode."

- **+** (`POST …/pawns {op:"add", count}`): the colony's wanted size grows (at most 12, joining pawns included). As many as the free
  seats allow come now (never the seats kept for offline regulars) through the usual spawn path: managed `maid_` accounts,
  whitelisted through main. The rest are pending (`seats.pending`, `pending:[{reason}]`) and join with the seat retry as seats free
  up. One log line and one alert per change.
- **−** (`{op:"remove", pawn}`): the pawn's job, leases, idle reservation and bed go back at once and it is `leaving`. It takes what
  it carries to storage by the filters (what no chest accepts goes with its logout's deposit, with an alert), its script ends, then
  it logs out and its account is released (whitelist removal through main). It leaves the state (`removed.pawns`). An offline pawn
  leaves at once; one that doesn't finish within 2 minutes is released anyway. The wanted size drops by one. The last pawn can't be
  removed (release the colony instead).
- **Web.** The colonist bar ends with **+** (Shift+click asks how many) and **−** (the one selected pawn, after "Remove Nils J.?
  Their items go to storage."). Pending pawns show as greyed "joining…" tiles.

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
  - `story.idle(job)` is an idle leg (walk to its cell, linger) inside `story.during`.
- **A job** is `{id, type, kind, label, at?:"x,y,z", fetch?:[{from, item, n}], stash?:[{to, items}], ...}`.
  - Kinds: `go|chop|dig|harvest|till|collect|place|attack|eat|craft|smelt|collect_furnace|give|blueprint|sleep|idle|hold|end`.
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
- Home areas and keep-out areas are **overlay layers** (RimWorld's home area and allowed areas are layers too): painting one never
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
active plant's. The chain stays automatic: till (a hoe, else a hoe bill) → sow → harvest → replant or the next bill.

**Web.** The Zones tool's Growing zone shows a plant picker (icons from the item atlas): the new zone's first bill. The Colony tab
opens a Growing zones window beside it: each zone's cells, Extend, what it sows now ("Sowing: Carrots"), its planting bills (Add bill
with the plant picker; mode and target; drag or the arrows to reorder; pause; remove; the active one marked "sowing", met ones
"met"), status, seeds (have, cells to sow, where from, what is missing), Sow and Harvest switches, "needs a hoe", and the action
button ("Designate grass cutting nearby" or "Craft seeds (a bill)"). The map labels a growing zone with what it sows.

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
carried, else fetched from storage (the job's `fetch`), else a count bill for one at the colony's crafting table (3 sticks, 2
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
bobber: an object of type 90 whose spawn data is our entity id (b1.8.1's 0x17 thrower, 1.12.2's Spawn Object owner). A bite
(`BiteDetector`): once the bobber floats (its height still for 10 ticks), a fresh downward velocity of 0.12 blocks/tick or more
(b1.8.1 EntityFishHook: `motionY -= 0.2`; 1.12.2: `motionY = -0.4 * rand(0.6, 1)`; both velocity-tracked) or a drop 0.2 under the
floating height; the velocity held from the cast never counts. On a bite it reels in (use item again) and waits for the fish; no
bite in 45 s reels in and casts again. The fake server (`FakeServer.Fishing`) serves rods on both wires for the tests.

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
  bed's head, a double plant's top) comes with its first, never a job of its own. Blocks pawns can't place yet (crops,
  liquids, signs, banners, skulls) stay `unsupported` ghosts: shown, said once in an alert, never a job, and the build
  finishes around them.

Build: `{id, kind, label, status: queued|active|paused|blocked|done, reason, blocks_total, blocks_done, material, order, created}`.
Ghosts: `[x,y,z,id,meta,build_id,state,kind]`, with state `todo|leased|done|blocked|unsupported` and kind `dig|place` (a dig
ghost's id/meta is the block expected there, read from the world view when it was queued).

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

Hauling:
- A pawn that carries loot (after chopping, mining, harvesting, hunting or picking up drops) delivers each stack to the
  accepting chest with the highest priority, then the nearest, that has room. A full chest is skipped.
- When nothing accepts a stack, the colony gets an alert "No storage accepts X" and the pawn keeps it. Loot is never dropped.
- Tools, weapons, armour and one food stack stay with the pawn.
- Background haul jobs, after every other haul: higher-priority chests pull matching items out of lower-priority ones, and a
  chest whose filter changed gives up what it no longer accepts.

## Workbenches & bills

Workbenches (designated, added 2026-11-03):
- The colony uses only the benches it was given. A bench becomes the colony's when the owner designates it (the bench
  inspector's "Use as colony workbench" → `POST …/workbenches {op:"designate", pos:[x,y,z]}`), when the owner adds its first
  bill, or when the colony places one for a build (below). `{op:"release", pos}` gives it back: its bills go with it, the
  builds' auto bills move to another bench or wait. Answer `{pos, kind, designated}`; 400 for no bench at `pos`, or releasing one
  that isn't the colony's. Another player's table near home is never used.
- Detection is a colony option: with `detect_benches` on, crafting tables and furnaces in the home zones (or 12 blocks around
  home) join by themselves every 30 s, as before. Off by default (`POST …/options {detect_benches}`); colonies saved before keep
  the benches they had.
- Only designated benches unlock the Architect's tiers (below) and receive bills.
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
- An owner's bill leaves what the builds reserved: when the stored ingredient is only there for a build, the bill is suspended
  with "the … in storage is reserved for builds".

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
- **Auto bills.** Each step is a `count` bill "for build #N" (`for_build`, `for_item`, `step`) on the colony's crafting table or
  furnace nearest home, listed first on the bench (build order, then step: ingredients before products), radius 256. Its target
  is `done` + what is still to make, so it follows the build and goes when nothing is left to make (a loaded furnace's bill stays
  until collected). A step whose ingredient isn't there yet waits quietly (an earlier step makes it). The owner can only cancel an
  auto bill (`update`, `pause`, `resume`, `reorder` answer 400): the build then waits for that item (`no_craft`).
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
recipe chain and the raw shortfall on hover; the bench inspector lists the auto bills tagged "for build #N", cancel only.

`POST …/options` `{auto_place_benches?, detect_benches?}` → the options; the colony state carries `options`.

### Architect palette and tiers (added 2026-11-03)

`GET /api/story/colonies/{id}/architect` → `{entries, benches:{crafting_table, furnace}, tabs}`. Entries are every placeable the
version has (`StoryArchitect`: blocks, stairs, slabs, fences, doors, glass and panes, wool, beds, bookshelves, carpets, benches,
chests, torches, glowstone, ladders…; variants the version lacks are left out), by tab: structure, furniture, production,
storage, lighting, misc. Entry: `{id, label, tab, tier, unlocked, reason, item:[id,meta], material:[id,meta], kind, kinds, chain,
have, craftable, from, short}`.
- `tier` from the recipes: `gathered` (no recipe), `hand` (2×2 only), `workbench` (a 3×3 step), `furnace` (a smelting step).
- `unlocked`: gathered and hand always; workbench with a designated crafting table; furnace with a designated furnace (and a
  table when its chain has a 3×3 step). Else `reason`: "Needs a crafting table — place one or designate an existing one".
- `have` = unreserved stock, `craftable` = how many (up to 64) the unreserved stock makes now, `from` = what one takes, `short`
  = what one is short of when neither.

The web's Architect (B) has tabs Orders, Zones, Structure, Furniture, Production, Storage, Lighting, Misc. The build tools stay
where they were; beside them, the tab's placeables: icon, name, tier and "have N" / "craftable N from …" / "short: …". Locked ones
are greyed with a lock and the reason; picking an unlocked one arms its build tool with it as the material (a furniture entry its
furniture tool), and the queued build's auto bills craft what's missing.

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
    missing materials), striped red when blocked or unsupported. Cells to dig are faint red outlines. A built cell's
    hologram goes as soon as the block appears (matched by cell in the mesh worker). The cells are meshed in the worker,
    one merged mesh per state, rebuilt only when the ghosts or builds change or a block change lands on a queued cell. At
    most 20,000 cells draw; past that, and below 3 px per block, each build is one outline box. They are drawn after the
    opaque world with depth test on and depth writes off, and follow the level cut. A design being placed shows the same
    way, green where it fits and red where a block is in the way. The viewer setting "Build holograms" (default on) turns
    them off; the map then draws the flat ghosts as before. Measured (SwiftShader, 1440 × 900, top view at the default
    zoom, the mock colony): 58 fps before, 48 fps with a 2,000-cell build queued, 41 fps in the angled view.
- **Selection.**
  - A left-drag box selects many pawns, or objects (trees, blocks, drops) in designation mode.
  - A click selects one, shift adds, a double-click selects every visible pawn of the same kind, and Esc clears.
- **Orders.** A right-click with pawns selected opens a context menu for the target:
  - a block gives move, mine or chop
  - a mob gives attack
  - a drop gives haul
  - a design gives build
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
- **Time of day.** A slider under the clock over one day (06:00 at tick 0; dawn, noon, dusk and midnight marked) sets the
  world time on release, through the bot's op account. Without one it is disabled with "No op account — op the main bot
  to change time". Needs the `story` role.
- **Bottom bar:**
  - pawn portraits with hp, food and job, where clicking selects (and centres the map when the pawn is off it) and a
    double-click always centres the map
  - a designate palette: chop, mine, harvest, haul, build, deconstruct, cancel
  - a zones palette (see "Zones": work zones, overlay layers, Erase with its kind)
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
