# The giant sand pyramid (survival, a crew of bodies)

A survival sand-pyramid workflow using the blueprint primitive, crew construction, and staged material collection.

## How a build works (read this before changing anything)

- A build is a **design**: the coordinating brain's `design{what, at}` has the author model write a blueprint DSL (`BlueprintAuthor.cs`
  `BlueprintPrompts.System`), the checker (`DesignChecks.Evaluate`) vets it against the real site (bill vs inventory + chests,
  floating blocks, sand over air at `DesignChecks.cs:187`, protected cells), a trusted player says "build it", then
  `design build{id}`. The slicer (`Building/BuildPlanner.cs`) orders the blocks layer by layer, support first (a sand block is only
  "ready" with a solid block under it: `BuildPlanner.cs:366`, `ConstructionPlanner.cs:125`), and picks a stand and a click face for
  each. The first 5 placements show the start and ask once (the gate); a trusted yes carries on.
- **Sizes**: 64 a side, 32 high (`BlueprintParser.MaxSide/MaxHeight`), 50,000 blocks a design
  (`BlueprintNormalizer.MaxBlocks`; was 32 / 6,000, before that 1,500): the base-63 pyramid (63x32x63, 43,680) is one design. A
  design past `SectionBlocks` (6,000) is built in bottom-up layer bands, each a plan of its own (see "The massive pyramid").
  Planning is zero tokens: the walk flood after each placement is partial now (exact, `FloodLocal`), so it no longer grows with
  the open ground around the site; a crew plans its strips in parallel.
- **Materials mid-build**: before the first block a body fetches the bill (`ConstructionExecutor.PrefetchAsync` :484, lanes:
  `CrewExecutor.PrefetchAsync` :722), and when its bag runs dry it fetches again (`HaveAsync` :506 / :739), through
  `GameBuildBody.EnsureAsync` (`BuildBody.cs:170`) = the acquisition planner: inventory, **every known chest** in batches the bag
  holds, then craft, gen, ask. A fetch is at most 256 items (`Math.Min(count, 64 * 4)`); in a crew only one body is at the chests
  at a time (`CrewContext.Storage`). A shortfall never ends a build: everything else is built, then it pauses resumably
  ("missing sand (need N, have 0)") and `design build{id}` carries on once the chest is refilled.
- **Several bodies on one design**: a design (or band) of 400+ blocks is cut into strips first and each strip planned on its
  own (`CrewSlicer.Strips`, below); smaller ones: `CrewSlicer` cuts the sliced steps into one lane per body by (x,z) column strips along the
  long axis, waits only on steps another lane must do first (`CrewSlicer.Waits` :155), reserves stands (`StandBoard`) and claims
  so no two bodies share a cell. Default crew = main + every idle body when the design has more than 48 blocks
  (`BlueprintService.PickCrew` :763, `Bot:Blueprints:SplitAboveBlocks`); `design build{id, crew:1}` = main alone,
  `bodies:["67","69"]` = those. A lane that gets stuck ends the round; the rest is re-sliced over the others and finished by one
  body (the finale), so a crew build degrades to a single builder, it does not fail.

## What was missing, and what changed

| Gap | Change |
|---|---|
| A 21-wide pyramid could not be a design (1,500 block limit) | `MaxBlocks` 6,000 (`BlueprintNormalizer.cs:13`): a 31-wide solid pyramid is 5,456 |
| The author would have to draw 11-16 layers of 21-31 rows by hand | DSL `pyramid X x,y,z base [core:Y]` (`BlueprintParser.cs:302`, `BlueprintNormalizer.cs:93`), in the author's cheat-sheet |
| Sand needs a block under it | By construction: each layer lies wholly on the one below, and the slicer builds layer by layer; with `core:Y` the outer two rows of every layer are sand and the deeper cells are Y, so a sand block rests on sand or Y, never air |
| No offline proof for falling blocks | `FakeServer.FallingBlocks`: sand/gravel placed over air falls at once; `SimBuildBody` records `Falls` and restocks from a `SimChest` |

Not changed (and why): the crew's lane contention (below), planner speed, `mine`'s 256-per-call cap (the SDK's `gather` loops it).

## Sand: finding it, digging it, hauling it

- **Find**: `find('sand', {r})` sees only loaded chunks; `scout('sand', {dir, max:240})` walks out until `find` sees some
  (`Sdk/js/20-world.js`). River and pond shores often hold thin layers
  (1-3 deep), a desert or a wide beach holds columns of it. 1,771 sand is ~1,771 surface cells on a shore, or a 10x10 pit.
- **Dig**: `mine{block:"sand", count, radius}` (`GameToolSet.cs:212`) digs exposed sand within the radius, nearest first, collects
  the drops; capped at 256 a call and 48 radius. It uses the best tool in the bag (a shovel is several times faster than a hand);
  `ensure('shovel')` / `gather(..., {tool:'shovel'})` gets one. `gather('sand', n, {from:'mine', stash: chest, r})` loops it and
  **stashes into the chest every time the bag fills** (`60-jobs.js`), so n can be far beyond the 2,304 a bag holds.
- **Haul**: `stash`/`deposit` into a chest next to the site. One chest = 27 stacks = 1,728 sand: use two chests (or a double
  chest) beside the pyramid for b=21 so the builder's refill trips are a few steps. A body walks to the nearest known chest with
  room; chests it has seen are in its memory, new ones are learned when opened.
- **Crew**: every body runs the same role: `everyBody('digger', ...)` (`70-crew.js:47`), late joiners included, a failed lane
  retried. The script below is run on the offline rig by `SandCrewScriptTests`.

```js
// purpose: every body digs n sand near the shore and stashes it in the staging chest as its bag fills
const P = params({ n: 2500, chest: '10,64,10', r: 40 })
function digger(a) {
  const r = gather('sand', a.n, { from: 'mine', stash: P.chest, r: P.r })
  crew.inc('sand', r.n)
  return `${me.body} ${r.n}`
}
function main() {
  const r = everyBody('digger', { n: P.n })
  return `sand run: ${crew.get('sand')} sand (${r.msg})`
}
```

Set `chest` to the real staging chest (x,y,z) and `n` to each body's share (total / bodies); first `scout('sand')` once, from main,
if no shore is within 40 blocks of the chest. Mining down into a sand bed leaves holes; `mine` never digs the block a body stands
on and never next to lava, so a bed is worked from the top down by several bodies without burying anyone.

## The pyramid: size, material, numbers

A stepped pyramid of odd base b has (b+1)/2 layers and the sum of odd squares up to b blocks. One sand block = one item; a stack
is 64; one chest holds 1,728.

| base | layers | solid sand | sand skin + core (`core:Y`) | stacks of sand (solid) | solo build estimate* |
|---|---|---|---|---|---|
| 11 | 6 | 286 | 202 + 84 | 5 | 2 min |
| 15 | 8 | 680 | 394 + 286 | 11 | 8 min |
| 19 | 10 | 1,330 | 650 + 680 | 21 | 16 min |
| **21** | 11 | **1,771** | **802 + 969** | 28 | **22 min** (2 bodies ~15) |
| 25 | 13 | 2,925 | 1,154 + 1,771 | 46 | 37 min |
| 31 | 16 | 5,456 | 1,802 + 3,654 | 86 | 65 min (2 bodies ~48) |

*the slicer's own estimate (`design show`), under load; walking, restocking and pauses at dusk come on top.

- **Recommended**: base 21, solid sand: big enough to read as a giant pyramid (21 x 21 x 11), 1,771 sand = 28 stacks, one design
  within every limit, ~22 minutes of placing. Base 31 (5,456) fits in a single band; larger designs use multiple bands, as described below.
- **Cheaper sand**: `core:D` (dirt) keeps the outside sand and puts dirt inside: b=21 needs 802 sand + 969 dirt. Dirt is dug
  anywhere at the same speed, so it only helps when sand is the scarce one.
- **Sandstone** (4 sand -> 1 sandstone, a 2x2 craft) does not fall, but costs 4x: 1,771 sandstone is 7,084 sand. Use it only for a
  thin skin if she wants the look, never for the body. Sand needs no support trick beyond the layering above.
- **Site**: flat, open, solid ground, 21 x 21 free (+ a ring to stand in). The checker refuses sand over a hole
  ("sand/gravel with nothing under it falls"); the planner digs anything in the footprint that is not part of the design
  (grass, flowers, trees), never protected blocks. A hillside: level it first with `excavate` (box) and `expand_island
  area=x0,z0,x1,z1 y=` (fills up to 1,024 cells a call).
- **Crew split**: sand digging is the parallel work (all 8 bodies, ~1.5-3 s per block each). Placing is parallel too since the
  strips (massive-build): each body plans and builds its own strip of columns, standing only there, so 8 bodies place side by side
  with no waits (sim, b=21: 8 lanes, 0 waits, 0 re-balances, 7.5 of 8 bodies placing at any time; before: 1,254 waiting steps, 2
  re-balances, the lanes timed out into turns). Default crew = every idle body (`design build{id}`); `crew:N` caps it.
- **Time (rough)**: dig 1,771 sand with 6 bodies ~15-30 min (hand), ~10 min with shovels; hauling folded in; build ~22 min; so
  well under an hour from "go" once a shore is found; the farm and the night rule (build stops at dusk with a rule) come on top.

## The massive pyramid (base 63)

Example target: stepped, solid sand, base 63, 32 layers (63x32x63), 43,680 sand = 683 stacks
= 25.3 single chests (13 double chests). One design (`pyramid S 0,0,0 63`, `size 63x32x63`), built in 10 bottom-up bands of at
most 6,000 blocks ([3969] [3721] [3481] [3249] [3025+2809] [2601+2401] [2209+2025] [1849..1521] [1369..841] [the top 14 layers,
3654]), each band its own plan, built by the whole crew side by side (strips, no waits).

Measured (offline sim, 8 bodies, Debug): complete, 0 re-balances, 0 two-bodies-in-a-cell, 0 sand fallen, the finale placed
nothing (every block by a lane), 7.66 of 8 bodies placing at any moment, 80 chest trips in all; 446 s of sim time, of which
slicing ~10-30 s per band. The slicer's own estimate of placing time: ≈72 min with 8 bodies (≈9.1 h alone); restocking, dusk
holds and walking to the chests come on top. Planning (Release, open ground): band 0 with 8 strips 16.8 s, one body 10.6 s; the
whole 43,680 at once as 8 strips 186 s, 10 MB retained, 256 MB peak working set (it is never sliced whole: bands).

Run it (plain words for the brain):
1. A flat open site of at least 69x69 (63 + a 3-block ring), solid ground, nothing protected in it; level it first if needed.
2. Sand into chests beside the site (2-3 blocks outside the footprint, along one side): the sand-run script above with every
   body, shovels first. It need not all be there at once: a band only needs its share (the bottom one 3,969); when the chests run
   dry the build pauses resumably ("missing sand"), the brain runs the sand script again and says `design build{id}`.
3. `design{what: "a solid stepped sand pyramid, base 63 (pyramid S 0,0,0 63, size 63x32x63)", at: <site>}`; a trusted "build it";
   the gate shows 5 blocks and asks once.
4. The crew is every idle body by default (`design build{id}`); `crew:N` caps it. No config needed (defaults: `SectionBlocks`
   6000, `CrewStripsAbove` 400, `FetchStacks` 16, `StorageBodies` 3, under `Bot:Blueprints`).
5. The brain is woken once per pause (missing sand, a dusk rule, a body lost) and once at the end; a healthy build never
   raises "stalled: build idle" (the build pulse).
