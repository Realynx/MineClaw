# @mineclaw/redstone

Redstone for the Design Studio: a simulator for Minecraft Beta 1.8.1 and 1.12.2 circuits, the data the studio's debugger
reads from it, and a library of gates that are proven against it.

The Design Studio's redstone kind draws a circuit on a build plot. This package takes that circuit as plain data
(`[x, y, z, id, meta]` cells, a version, the ground under the plot) and says what it does, game tick by game tick.

## Rules of the package

- **Plain data in, plain data out.** No DOM, no three.js, no React, no node built-ins, no dependencies: `src/` imports only
  its own files (ESLint enforces it), so the same code runs on the page, in a worker and in tests.
- **Only the web app imports it.** Next bundles it at build time. The API's runtime does not get this package.
- **Mojang's code is never copied here.** Behaviour is written from reading the reference sources and is described in our
  own words and our own tables.
- **Nothing unproven is listed.** A gate is in `LIBRARY` only when the simulator proves its truth table or its timing spec
  in a test, for every game version it has a layout for.

## Fidelity

The engine is exact at game-tick resolution (1 game tick = 1/20 s; 1 redstone tick = 2 game ticks) for the parts it models.
It does not reproduce the order of block updates inside one game tick, so a circuit that depends on that order can work
here and fail in the world; `diagnostics()` flags the cases it can see.

## Layout

| Path                        | What it is                                                                                                   |
| --------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `src/api.ts`                | The contract: cells, inputs, breakpoints, samples, `Simulation`. Change it on purpose only.                  |
| `src/library/types.ts`      | The contract of the gate library: ports, layouts, specs.                                                     |
| `src/breakpoints.ts`        | The breakpoint rule, shared by the engine and the scripted double.                                           |
| `src/testing.ts`            | `FakeSimulation`: a scripted `Simulation` for tests of the code around the engine.                           |
| `src/index.ts`              | The public surface. `test/surface.test.ts` pins its names and signatures.                                    |
| `src/simulation.ts`         | `createSimulation`: the contract on top of the engine core.                                                  |
| `src/engine.ts`             | The game tick and its phases, settling to tick 0, keyframes, the runaway guard.                              |
| `src/schedule.ts`           | Scheduled ticks, with each version's order and identity rules.                                               |
| `src/grid.ts`               | The cells of the box around a design, and the ground under it.                                               |
| `src/power.ts`              | Weak and strong power between cells.                                                                         |
| `src/profile.ts`            | What differs per version outside a single part (with the tables in `src/data/`).                             |
| `src/wire.ts`               | Redstone dust, solved per connected network.                                                                 |
| `src/components/`           | The other parts. `handler.ts` is what a part is to the engine.                                               |
| `src/orientation.ts`        | Directions, and how a meta turns and mirrors.                                                                |
| `src/diagnostics.ts`        | What the engine reports about a design: unsupported or floating parts, update-order patterns, runaway ticks. |
| `src/history.ts`            | Stepping back: a ring of keyframes plus the input log, replayed forward to the target tick.                  |
| `src/explain.ts`            | `explain`: the chain that powers a cell, walked back to its source.                                          |
| `src/data/`                 | Which blocks conduct power, per version.                                                                     |
| `src/library/index.ts`      | `LIBRARY`: every proven component, from the files below.                                                     |
| `src/library/logic.ts`      | NOT, OR, NOR, AND, NAND, XOR, XNOR.                                                                          |
| `src/library/memory.ts`     | RS latch, D latch and the torch T flip-flop with reset.                                                      |
| `src/library/timing.ts`     | Repeater and five-torch clocks, pulse limiter and extender, rising and falling edge detectors.               |
| `src/library/arithmetic.ts` | Half adder, full adder, 2:1 multiplexer.                                                                     |
| `src/library/modern.ts`     | 1.12.2 only: repeater-lock memory cell and T flip-flop, subtractor, "is stronger", signal hold.              |
| `src/library/draw.ts`       | Layouts drawn as text, composed from parts; internal.                                                        |
| `src/library/transform.ts`  | `transformLayout`: a layout mirrored, then turned.                                                           |
| `src/library/verify.ts`     | `verifyComponent`: the harness that proves a spec against the engine.                                        |

The engine models dust, torches, repeaters, comparators, levers, buttons, plates, pistons and sticky pistons, lamps,
doors, trapdoors, fence gates, dispensers, droppers, note blocks, TNT, hoppers, observers, redstone blocks and the manual
sources (containers, daylight sensors, trapped chests, detector rails). Anything else, powered and activator rails and
tripwire among them, stays as placed and is reported as `unsupported`.

`test/library/` proves every component in `LIBRARY` in all twelve placements (four turns, plain and both mirrors) with
all three feeders, and pins the timing traps its specs state.

`@mineclaw/redstone/testing` is a separate entry so the scripted double never reaches the app's bundle.

## Commands

Run from `web/`:

```
npm run build -w @mineclaw/redstone       # tsc to dist/ (the web app imports the built package)
npm run typecheck -w @mineclaw/redstone
npm run test -w @mineclaw/redstone        # vitest, node environment
npm run lint -w @mineclaw/redstone
```
