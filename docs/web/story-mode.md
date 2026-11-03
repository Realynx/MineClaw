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
- **Zones,** painted as areas: chop, mine, farm, stockpile (chests), home, forbidden.
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

They sit behind `Supervisor:Viewer:Enabled` and the token. Writes need `Supervisor:Viewer:AllowStory`, which defaults to
true when the viewer is on. JSON is snake_case.

| Method | Path | Body / result |
|---|---|---|
| GET | `/api/story` | `{colonies:[{id, name, created, paused, pawns:[name], version}]}` |
| POST | `/api/story/colonies` | `{name, pawns (1..12), home?:{x,y,z}}` → `{id}`. Spawns script-only bodies running `story_pawn` |
| POST | `/api/story/colonies/{id}/release` | Releases the pawns and keeps the saved state |
| POST | `/api/story/colonies/{id}/pause` | `{paused}` |
| GET | `/api/story/colonies/{id}?since=rev` | Full state, or a delta when `since` is given: `{rev, at, time_of_day, hour, paused, work_types:[...], pawns:[Pawn], zones:[Zone], designations:[Designation], orders:[Order], alerts:[{at, level, text, pawn?}]}` |
| POST | `/api/story/colonies/{id}/priorities` | `{pawns:[name], work:{type: 0..4}}` (0 = off) |
| POST | `/api/story/colonies/{id}/schedule` | `{pawns:[name], hours:{"0".."23": "work"|"sleep"|"anything"|"free"}}` |
| POST | `/api/story/colonies/{id}/draft` | `{pawns:[name], drafted}` |
| POST | `/api/story/colonies/{id}/orders` | `{pawns:[name], order:{kind: move|attack|chop|mine|haul|build|hold|stop, target?:{x,y,z}|{entity:id}|{area:[x1,y1,z1,x2,y2,z2]}, design?:id}, queue?:bool}` |
| POST | `/api/story/colonies/{id}/zones` | `{op: add|remove, kind, area:[x1,y1,z1,x2,y2,z2]}` |
| POST | `/api/story/colonies/{id}/designations` | `{op: add|remove, kind: chop|mine|harvest|haul|build|deconstruct, area?|blocks?:[[x,y,z]], design?}` |

The **Pawn** object:

```
{name, display, kind:"pawn", online, hp, food, pos:[x,y,z], dim, drafted, state: idle|working|moving|eating|sleeping|fleeing|fighting|drafted|offline,
 job:{type, label, target?, progress?, since}|null, queue:[Order],
 skills:{type:{level, xp, passion}}, traits:[{id, label, effect}], priorities:{type:0..4}, schedule:[24 × block], held, inventory:[[id,meta,count]] }
```

The bridge passes these through (allow-list). The live stream gets a `story` event with the colony delta at 2 Hz while
a client watches.

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
