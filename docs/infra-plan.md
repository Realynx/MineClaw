# Infrastructure for the giant sand pyramid (tools, portals, finding sand, hauling)

Large construction needs material collection, transport, food and tool supply. The pyramid itself is `docs/pyramid-plan.md` (the design, the slicer, the crew). This is what feeds it:
an illustrative target of 44,000 sand, potentially far from base, with renewable tools. Verification uses the offline rigs
(FakeGame and the script Rig), not a real server; simulation limits are called out below.

## The numbers that decide the design

| | |
|---|---|
| Sand needed | ~44,000 = 688 stacks = **26 chests** (27 stacks each) at the build site |
| A desert column | sand ~3-5 deep (an estimate from the 1.12.2 desert generator, sand over sandstone; check a column with `block()`), so ~4: 44,000 / 4 = ~11,000 columns = **~43 chunks of desert** |
| Break time per sand block | hand 0.75 s, wood 0.38, stone 0.19, iron 0.13, diamond 0.09 (hardness 0.5 x 1.5 / tool speed); the real cost adds look, pick-up and walking, an estimated ~1-2 s a block by hand |
| Shovel durability | wood 59, stone 131, iron 250, diamond 1561: one use per block |
| 44,000 blocks of shovel | **336 stone shovels** (336 cobblestone + 672 sticks = 84 logs), or 176 iron (176 ingots), or 28 diamond. Stone is the only one that needs no ore |
| A bag | 36 slots; table + cobblestone + sticks + shovel + food take ~5: **~1,800-2,000 sand a trip**, 25 trips for the whole pyramid |
| Walking | 4.3 m/s, x1.25 for the path = 3.4 m/s effective |

Hauling on foot, per body, a bag of 1,800 (dig time per block 1 s / 2 s), 16 bodies:

| distance | round trip | 44,000 done in (1 s dig) | (2 s dig) |
|---|---|---|---|
| 250 m | 2.4 min | 0.8 h | 1.6 h |
| 500 m | 4.8 min | 0.9 h | 1.7 h |
| 1,000 m | 9.7 min | 1.0 h | 1.8 h |
| 2,000 m | 19 min | 1.3 h | 2.0 h |
| 4,000 m | 39 min | 1.8 h | 2.5 h |

**Digging is the long pole, not the walk.** Up to ~2,000 m walking costs less than a third more than no walk at all. So: no rails, no
nether highway; walk, with the portal pair only as an optional shortcut for a desert 3,000+ m away (below).

## 1. Tool supply

**Exists:** `ensure('shovel')` (any tier: bag, then chests, hive, craft); `craft('stone_shovel', 16)` plans the whole tree (planks,
sticks, table; withdraws raw materials from known chests; smelts when the only way is a furnace); `replaceToolWhenBroken('shovel')` and
`gather(..., {tool:'shovel'})`; the planner replaces a tool with 3 uses left and, holding cobblestone + sticks + a table, **crafts a new
stone shovel on the spot** (`AcquisitionPlanner.UpgradeIfCheapAsync`/`ToolAsync`). `restock_loadout` refills tools/food from chests.

**Missing:** a job that *keeps N tools and the raw kit stocked in a chest*, and an iron path (iron is needed for only ~7 ingots: 2
buckets and a flint and steel; the planner says "iron+: nothing to bootstrap").

**Built (scripts, no new C#):** `tool_depot` (keep N shovels/pickaxes/axes plus cobblestone, sticks and tables in the depot chest;
counts what the chests hold with `where()`, crafts only the shortfall, in bags of 16, stops a tool it cannot make; `rounds`/`every` =
keep it stocked) and `iron_run` (mine ore, strip-mine once if none shows, smelt, craft 2 buckets + flint and steel, store the spare).
`far_sand_run` below takes the kit with each body: **a body carries the materials, not 300 shovels** (tools do not stack: 336 shovels
are 12 chests). Table + 64 cobblestone + 64 sticks = 32 stone shovels made out at the pit, 3 slots.

```js
// purpose: keep the tool depot chest stocked: stone tools, plus cobblestone, sticks and tables for bodies to craft their own on site
const P = params({ chest: '10,64,12', shovel: 24, pickaxe: 4, axe: 0, cobblestone: 512, sticks: 256, tables: 4, batch: 16, rounds: 1, every: 300 })
const HEAD = { shovel: 1, pickaxe: 3, axe: 3 }, STICKS = { shovel: 2, pickaxe: 2, axe: 2 }
// what the known chests hold of an item: where() says "name x12: chest@3,64,2 (...); x5: ... | total 17"
function inChests(item) {
  const w = where(item)
  if (!w) return 0
  const t = /\| total (\d+)/.exec(w)
  if (t) return Number(t[1])
  let n = 0
  for (const m of w.matchAll(/\bx(\d+):/g)) n += Number(m[1])
  return n
}
const held = item => inChests(item) + inv(item)
function tools(kind, target) {
  const item = 'stone_' + kind
  let made = 0, need = target - held(item)
  while (need > 0) {
    const n = Math.min(need, P.batch)
    gather('cobblestone', HEAD[kind] * n)                       // raw materials into the bag: chests first, then the quarry
    gather('log', Math.ceil(STICKS[kind] * n / 8) + 1)          // a log is 4 planks is 8 sticks (+1 spare for a table)
    const c = craft(item, n)
    const k = inv(item)
    if (!ok(c) || k === 0) { log(`depot: ${item}: ${c.msg}`); break }
    stash([item], { to: P.chest })
    made += k
    need -= k
  }
  return made
}
function kitItem(item, target) {
  const need = target - held(item)
  if (need <= 0) return 0
  if (item === 'stick') gather('log', Math.ceil(need / 8) + 1)
  if (item === 'crafting_table') gather('log', need + 1)
  if (item === 'cobblestone') return gather(item, need, { stash: P.chest }).n   // quarried as the bag fills, stored each time
  const before = inv(item), c = craft(item, need)                               // gather's craft route is for "have n", not "n more"
  if (!ok(c)) { log(`depot: ${item}: ${c.msg}`); return 0 }
  const made = inv(item) - before
  stash([item], { to: P.chest })
  return made
}
function pass() {
  const made = []
  for (const kind of ['shovel', 'pickaxe', 'axe']) if (P[kind] > 0) made.push(`${tools(kind, P[kind])} ${kind}`)
  for (const [item, key] of [['cobblestone', 'cobblestone'], ['stick', 'sticks'], ['crafting_table', 'tables']])
    if (P[key] > 0) made.push(`${kitItem(item, P[key])} ${item}`)
  return made.join(', ')
}
function main() {
  let last = pass()
  for (let i = 1; i < P.rounds; i++) { wait(Math.min(P.every, 600)); last = pass() }
  return `depot stocked: made ${last}`
}
```

```js
// purpose: get iron for a portal: mine ore, smelt it, craft the buckets and flint and steel, leave the spare ingots in the chest
const P = params({ ore: 16, chest: '10,64,12', r: 32, buckets: 2, flint: 1 })
function main() {
  ensure('pickaxe')                                              // stone or better mines iron
  const need = P.buckets * 3 + P.flint                           // a bucket is 3 ingots, flint and steel 1
  if (inv('iron_ingot') < need) {
    let got = gather('iron_ore', P.ore, { from: 'mine', r: P.r })
    if (got.n === 0) {                                           // none showing: strip-mine a corridor, then look again
      skill('strip_mine', { length: 32 })
      got = gather('iron_ore', P.ore, { from: 'mine', r: P.r })
    }
    if (inv('iron_ore') === 0) fail(`no iron ore found (${got.msg})`)
    skill('smelt_batch', { item: 'iron_ore', count: inv('iron_ore'), wait: true })
    skill('collect_furnace')
  }
  if (inv('iron_ingot') < need) fail(`only ${inv('iron_ingot')} iron ingots, need ${need}`)
  expect(craft('bucket', P.buckets))
  if (P.flint > 0) {
    gather('flint', P.flint, { from: 'source' })                 // 1 in 10 gravel drops flint
    expect(craft('flint_and_steel', P.flint))
  }
  const spare = inv('iron_ingot')
  if (spare > 0) stash(['iron_ingot'], { to: P.chest })
  return `iron: ${inv('bucket')} buckets, ${inv('flint_and_steel')} flint and steel, ${spare} ingots stored`
}
```

## 2. Nether portals

**Exists:** `build_portal` (needs 10 obsidian in the bag), `light_portal`, `enter_portal`, `return_portal`, `make_obsidian` (pours water on
lava sources), pair memory with 1:8 scaling, `hazard_scan`. Obsidian can only be *mined* with a diamond pickaxe, so `make_obsidian` +
`build_portal` stops at "needs 10 obsidian".

**Built:** `cast_portal{pos?, axis?, light?}` (`Skills/CastPortal.cs`): casts the frame **in place**. Per frame cell: lava source into the
cell from a bucket, water poured in the cell above it at once (1.12.2 `BlockLiquid.checkForMixing`, 1.7.10 `BlockLiquid.func_149805_n`
checks the four sides and the top, never the bottom: a lava *source* with water beside or above it becomes obsidian), water scooped
back, next cell. A one-block back plate behind the frame (24 cobble/dirt) gives every pour a face to click and keeps lava from running
backwards; it is dug out at the end. Order: bottom row, left column up, right column up, top row (the cell above is always free).
Needs: a water bucket, one more bucket (or lava buckets), lava within ~24 blocks of the site, 24 cobble/dirt, flint and steel to
light (`light:true` lights it). It fills every empty bucket from the lava first, scoops a failed pour back, and stops with "cast k/10".
**Not proven:** the live race. Lava placed in the open flows after 30 ticks (1.5 s); the water goes in within a few ticks, but the
FakeGame has no flow. **Supervise the first live cast**, and light-test it with one cell before the frame.

**Linking (`plan_portals{to, from?}`, `Skills/PortalPlan.cs`).** `Teleporter.placeInExistingPortal` searches **128 blocks (a square) in
the destination dimension** around the scaled spot and builds a new portal only if it finds none (vanilla portal linking).
Overworld to nether divides by 8:
- Two overworld portals **less than 1,024 blocks apart** (in x and z) share ONE nether portal. Entering the far one lands you at the
  home one's nether portal; leaving it lands at the overworld portal nearest to it = home. So **far -> home works, home -> far does not**
  (walk out, ride home: a one-way shortcut for carrying sand, no nether walk at all, 24 s).
- More than 1,024 apart: the far portal makes its own nether portal at far/8. Two-way, but you must walk the nether between the two
  (D/8 blocks, 500 for 4,000 m), a tunnel or bridge, once.
- Nothing needs a second obsidian build in the nether (water cannot be placed there, so casting there is impossible): build the far
  frame in the OVERWORLD at the desert, step in, and the game makes the nether side (an obsidian platform) itself.

When it is worth it (walking home vs the portal): 250 m 1.2 min vs - ; 1,000 m 4.9 min vs 0.4 min (shared nether portal, one way only);
4,000 m 19 min vs 3.3 min (two-way pair, 500-block nether tunnel built once). Under ~1,000 m do not bother (it cannot even link
two ways). Between 1,000 and 2,500 m the shared-nether shortcut home saves 5-14 min a load but the walk back stays; beyond 3,000 m a
two-way pair with a tunnel pays. The nether leg is the risky part (lava, ghasts, pigmen): `hazard_scan`, short legs with `go`, never
send a hungry or unarmed body, never hit pigmen. `plan_portals` prints these numbers for real coordinates.

## 3. Rails and minecarts

**Exists:** `ride_vehicle` (get in/out of a boat or plain minecart, `Skills/Misc.cs`); `place`/`use_block` can set rails. `ride_rails` is a
**stub** ("not implemented"). Nothing lays rails, powers them, loads a chest cart, or checks that a body moved by a server-driven
cart keeps its client state in step.

**Decision: not built.** A chest minecart holds 27 stacks = 1,728 sand, less than a body's bag (1,800-2,000), so a cart does not carry
more than a body; the rail is only a faster road. 1,000 m of rail is ~375 iron ingots (6 iron per 16 rails) plus gold and redstone
for powered rails (a booster every few dozen blocks), plus a body laying it at ~1 block a second (17 min) and a loader/unloader (hoppers, redstone) that the
chests-at-both-ends job cannot test offline. Against that, the walk costs 10 min a round trip at 1,000 m of 1-2 hours of digging. If
the sand is farther than ~4,000 m, the portal pair beats rails on iron and on risk.

**Hauling realistically:** walk, a bag at a time, chests at both ends (the pit chest is optional), `far_sand_run` below. Ender chests
need eyes of ender (blaze powder from the nether): not worth the trip.

## 4. Finding sand far away

**Exists:** `find('sand')` sees only the loaded chunks (~150 m); `scout('sand')` walks out until one shows.

**Built:** the client already decoded the 1.12.2 biome array of every full chunk and threw it away on unload. `BiomeMemory`
(`World/Chunks/BiomeMemory.cs`, on `WorldState.BiomeMemory`) now keeps the dominant biome of every chunk walked past (3 bytes a chunk,
per dimension, kept after unload and through respawns, **not saved across a restart**). Skills:
- `find_biome{biome?, max?, min_chunks?}`: nearest patch (chunks joined edge to edge) of `desert`, `sand` (desert + beach), `beach`,
  `mesa`/`badlands` (red sand), or any biome name/id: distance, compass direction, centre, chunk count, ~columns, bounds, and how many
  other patches. No name = a survey of what was seen. A patch of 43+ chunks is a sand source for the pyramid (`min_chunks:40`).
- `scout_biome{biome, dir?, max?, min_chunks?, leg?}`: walks out in 48 m legs (compass degrees, 0 north, 90 east; default the side
  with the fewest chunks seen), stopping the moment a patch of `min_chunks` (default 4) shows, turning 40 degrees when stuck. 600 m
  by default (`max`).
Per body: each body has its own memory (a scouting body tells the brain the coordinates).

## 5. Hauling: `far_sand_run`

```js
// purpose: dig sand at a far pit and carry it home in bag loads, on foot or through the portals; every body does n
const P = params({ n: 2500, pit: '0,64,0', home: '0,64,0', r: 40, load: 1800, portal: '', homePortal: '' })
const at = s => { const [x, y, z] = String(s).split(',').map(Number); return { x, y, z } }
// one go() is one 60 s walk; a trip of hundreds of blocks takes several
function travel(to, tries = 60) {
  let last = Infinity, stuck = 0
  for (let i = 0; i < tries; i++) {
    const d = dist(at(to))
    if (d <= 6) return { ok: true, msg: 'there' }
    stuck = d > last - 4 ? stuck + 1 : 0
    if (stuck >= 3) return { ok: false, msg: `stuck ${Math.round(d)} m short of ${to}` }
    last = d
    go(to, 4)
  }
  return { ok: false, msg: `${Math.round(dist(at(to)))} m short of ${to} after ${tries} legs` }
}
// portals: walk to the frame, step in (nether), step into the nether portal there (overworld again)
function viaPortal(frame) {
  const t = travel(frame)
  if (!ok(t)) return t
  const a = skill('enter_portal'), b = skill('enter_portal')
  return ok(a) && ok(b) && me.dim === 0 ? { ok: true, msg: 'through' } : { ok: false, msg: `portal: ${a.msg} / ${b.msg}` }
}
// what a body needs out there: a shovel, and the kit to make more (a table, 64 cobblestone, 64 sticks = 32 stone shovels)
function kit() {
  ensure('shovel')
  gather('crafting_table', 1)
  gather('cobblestone', 64)
  gather('stick', 64)
}
function hauler(a) {
  use(replaceToolWhenBroken('shovel'), keepFed({ below: 14, fetch: false }))   // a broken shovel is made again on site; food is carried
  kit()
  let done = 0, trips = 0
  while (done < a.n) {
    ensure('shovel')
    const r = gather('sand', Math.min(a.n - done, P.load), { from: 'mine', r: P.r })
    if (r.n <= 0) return `${me.body} ${done} sand, then none near ${P.pit} (${r.msg})`
    const home = P.portal ? viaPortal(P.portal) : { ok: true }
    if (ok(home)) { const t = travel(P.home); if (!ok(t)) return `${me.body} ${done} sand, then ${t.msg}` } else return `${me.body} ${done} sand, then ${home.msg}`
    const s = stash(['sand'], { to: P.home })
    if (!s.ok) return `${me.body} ${done} sand, then ${s.msg}`
    done += r.n
    trips++
    crew.inc('sand', r.n)
    if (done < a.n) { const back = P.homePortal ? viaPortal(P.homePortal) : travel(P.pit); if (!ok(back)) return `${me.body} ${done} sand, then ${back.msg}` }
  }
  return `${me.body} ${done} in ${trips} trips`
}
function main() {
  const r = everyBody('hauler', { n: P.n })
  if (!r.ok && /no crew/.test(r.msg)) return hauler({ n: P.n })
  return `far sand run: ${crew.get('sand')} sand (${r.msg})`
}
```

`n` per body, `pit` = a point in the desert (x,y,z), `home` = the chest at the build, `r` = dig radius (48 max), `load` = sand per
trip. `portal` = the far frame to walk into (it goes in, then into the nether portal, and must come out in the overworld at home);
`homePortal` = the home frame for the way back (two-way pairs only; blank = walk back). A trip that cannot get anywhere ends
the body's run with the reason, keeping the sand it holds. The body runs its own `replaceToolWhenBroken` and `keepFed` policies.

For the example target, 26 chests provide the required storage: `home` is one chest; `stash` falls back to the other known chests with
room, so place the chests in a row next to it (8 planks each, 208 planks = 52 logs) and let the bodies open each once.

## Example instructions (plain language)

1. **Scout.** Use `scout_biome desert min_chunks=40`, trying additional directions if needed; `find_biome desert` shows known
   patches. Record the centre and distance. Walking is usually sufficient below about 2,000 m.
2. **Tool depot.** Run `tool_depot` against a designated chest (`rounds:1`, or `rounds:6, every:300` for repeated restocking).
   Provide a table, logs and a quarry; each body carries a table, 64 cobblestone and 64 sticks.
3. **Iron.** Run `iron_run` for two buckets and flint and steel (7 ingots); skip equipment already available in storage.
4. **Chests.** For this example target, craft 26 chests (208 planks), place them near the site and open each once.
5. **Optional portals.** For distant sand, find lava with `find('lava')` or `scout('lava')` and use `cast_portal` in the overworld.
   It chooses a flat spot within 8 blocks; lava must be within about 24 blocks. Supervise and test the first cast, since fluid
   timing is not proven by the simulation. Use `plan_portals to=<doorway>` to inspect whether linking is one-way or two-way.
6. **Haul.** Run `far_sand_run` with the chosen `pit`, `home` and per-body `n`; set `portal` and `homePortal` only for a verified
   pair. Use `restock_loadout food` before departure: the feeding policy can eat carried food but cannot fetch it at the pit.
7. **Check.** Inspect each body's stored-sand count and stop reason, then compare with the chest inventory.
