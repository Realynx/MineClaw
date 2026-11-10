# Redstone designer: simulator, debugger and gate library

A redstone design is a Design Studio canvas of kind `redstone`: a build plot (64×64, y -16..47, flat grass) on which the
user draws a circuit, simulates it game tick by game tick, debugs it with probes, breakpoints and a timeline, and places
proven gates from a library. It is saved like any design. Pawns cannot build one yet (see "Hand-off to pawns").

## Where it lives

| Part | Home |
|---|---|
| Simulator, orientation table, gate library | `web/packages/redstone` (`@mineclaw/redstone`): no dependencies, no DOM, imported by the web app only. `src/api.ts` and `src/library/types.ts` are the contract. |
| Studio integration | `web/apps/web/src/features/studio/redstone/`: the simulation controller (`RedstoneSimController`), the component controller (`RedstoneComponentController`, `ports.ts`, `componentData.ts`), the panel and its Library tab, the power overlay, the Configure tool. |
| Debugger UI | `features/studio/redstone/debugger/`: Simulate, Probes (watch list, timeline, breakpoints) and Inspector tabs, and the keys. |
| Kind and persistence (bot) | `DesignerCanvas` (`KindRedstone`), `DesignerRoutes`, `DesignerStore`, `DesignerSave`, `DesignHistory`, `DesignerRedstoneAuthoring.cs`; the bridge's `DesignViews.KindOf`. |

## Fidelity

The owner's target: **logic and timing exact to the game tick** for the supported parts, in both b1.8.1 and 1.12.2.
Update-order tricks are **flagged in the diagnostics, not emulated**.

One game tick runs, in order: queued user inputs, the clock, the scheduled ticks due now (1.12.2 orders them by due tick,
priority and insertion and runs at most 65,536; b1.8.1 by due tick and insertion, at most 1,000, and skips an entry whose
block changed), the 1.12.2 block events in rounds (b1.8.1 runs a piston's event at once), moving blocks (a move lands on
its third update), then the record (changed cells, breakpoints, probe samples, history). Neighbour updates go west, east,
down, up, north, south in both versions. The rules were read from the decompiled 1.12.2 sources and from Bukkit mc-dev at
218a2a71 for b1.8.1; none of that code is in the repository.

Supported parts: dust, redstone torch, repeater (with 1.12.2 locking), comparator, lever, buttons, pressure plates,
pistons and sticky pistons (push limit 12, quasi-connectivity, sticky drop on a 2 gt pulse), lamp, doors, trapdoors,
fence gates, dispenser, dropper, note block, TNT, hopper, observer, redstone block, and as manual sources containers,
daylight sensors, trapped chests and detector rails. Powered and activator rails and tripwire are reported as
unsupported.

### Stated simplifications

The panel's help popover lists these (`features/studio/redstone/simplifications.ts`):

1. Dust settles to the game's levels as a fixed point per network, then the parts around it hear of the change in a fixed
   order (y, z, x). Vanilla's order comes from hash-set iteration, so order-dependent circuits (locational tricks, zero-tick
   pulses, some update detectors) are not guaranteed.
2. A piston move takes two game ticks with no positions in between; the moving block is shown at its destination. No
   entities, drops, sounds or particles.
3. Slime blocks move as plain blocks, with a diagnostic.
4. TNT is primed and removed (no explosion); dispensers, droppers and note blocks report "fired" or "played"; hoppers only
   show whether they are held; no items move.
5. Containers, weighted plates, daylight sensors, trapped chests and detector rails give a level the user sets. Tripwire is
   inert. Wooden buttons and plates are released by the user or their timer.
6. A click lands between two game ticks, where a player's packet does in both versions.
7. No random ticks.
8. The ground under the plot is solid, conducts like stone and never moves.
9. A memory cell powers up in whichever state the start order gives it; in the world that depends on placement order.

Diagnostics flag what the engine can see: floating parts, parts the version lacks, unsupported parts, slime in a piston's
path, an unstable circuit (more than 100,000 updates in one game tick stops the tick), and three update-order patterns
(a b1.8.1 piston that misses an update behind the per-kind guard, a b1.8.1 piston moving while dust tells its neighbours,
a 1.12.2 piston whose extend event finds its power already gone).

### Where the engine corrected the plan

Building and proving the engine showed several timings the hand walk in the plan had wrong. The engine follows the
sources, and the tests pin each case per version:

- A b1.8.1 torch that turns off queues a tick for itself (the chunk writes the new block before the old one tells its
  neighbours). A 2 gt pulse therefore does not set an RS latch in b1.8.1 either; both versions need 4 gt.
- The torch rising-edge detector and the pulse limiter give 4 gt in 1.12.2 but 2 gt in b1.8.1. The falling-edge detector
  needs a 6 gt input in 1.12.2.
- The 1.12.2 repeater-lock T flip-flop needs its rises at least 9 gt apart and T off for at least 3 gt (the plan said 4 and
  4).
- b1.8.1 doors and trapdoors do tell their neighbours when they change (mc-dev writes them with `setData`).
- A bare five-torch ring starts with every torch lit and burns out in 1.12.2; the library's torch clock has one 8 gt
  repeater in the ring.

## Gate library

`LIBRARY` holds 24 components: NOT, OR, NOR, AND, NAND, XOR, XNOR, RS latch, D latch, T flip-flop, repeater clock, torch
clock, pulse limiter, pulse extender, rising and falling edge detectors, half adder, full adder and 2:1 multiplexer in
both versions, and the 1.12.2 repeater-lock memory cell, lock T flip-flop, comparator subtractor, "is stronger" and
signal-strength hold. The T flip-flop for both versions is built from torches and repeaters (the D latch's hold half, set
by Q's own delayed copy, behind the 8 gt pulse limiter) and has a reset input R: a flip-flop without one could only be
proven from whatever state it powers up in. Q turns on 18 gt after T rises and off 16 gt after; T needs 4 gt on, 8 gt off
and rises 18 gt apart.

Every component is proven by the simulator in the package's tests (`test/library/*`), for each version it claims, against
its truth table, step sequence or clock period, in all 12 placements (four turns, plain and both mirrors) and with all
three input feeders (straight and bent left or right). A component that burns out a torch, raises a diagnostic or is still
changing after its settle time fails. Layout rules L1 to L7 (ports just outside the box, inputs that work from any side,
ports of different signals two cells apart, nothing floating, only the version's parts, no torch or repeater output on a
face except at an output port, all of it after every turn and mirror) are tested too.

## In the studio

- **Kind.** The header's Kind select offers Redstone. Build ↔ redstone keeps the canvas (the same plot); to or from a mine
  clears it, as before. A saved redstone design hides "Build in Controller" and "Ask the bot to build it".
- **Drawing.** A redstone hotbar and the block drawer opening on the Redstone category. The Configure tool (C) sets a
  repeater's delay, a comparator's mode and whether a lever starts on, each one undo step.
- **Simulating.** `RedstoneSimController` builds a simulation from the canvas 150 ms after the last edit. Simulate (or any
  step, or a click on a lever) makes it live: the pointer stops editing, clicks use levers, buttons and plates, and any
  canvas change (a key, undo, the AI, the bot's fixes) ends the simulation. The simulated states reach the viewer through
  `StudioWorld.setDisplay` as block deltas; the canvas always wins and stopping puts every overridden cell back.
- **Debugger.** Run or pause, step a game tick or a redstone tick, step back (keyframes plus replay, 2,000 gt deep by
  default), run until the next change, reset, speed 1 to 20 gt/s or Max. Probes with a live level and a timeline (click an
  earlier tick to step back to it); breakpoints on change, rising, falling, at least N or a state, checked once per game
  tick; the inspector decodes a cell, its power, its pending ticks and the chain that powers it. Keys: F8 run or pause,
  F10 step a redstone tick, Shift+F10 a game tick, Shift+F8 run until the next change, F9 breakpoint, Shift+F9 probe,
  Ctrl+Shift+Backspace reset; remappable per browser.
- **Components (Library tab).** A placed component owns every cell of its box: the layout's blocks, turned and mirrored,
  and air in the rest. Placing, moving, turning, mirroring, renaming, unlinking and removing it are one undo step each, and
  undo brings back both the cells and the list. A hand edit of an owned cell (any tool, the AI, the bot's fixes) detaches
  that instance and leaves its cells as plain blocks; undoing that edit attaches it again. Boxes keep one free cell between
  them, and a placement or move that would touch another box is refused. With Snap, a component is placed so that one of
  its input ports is the very cell of a nearby output port (in the gap), facing it, and one dust goes there. A component
  is placed by a click in the world (its box's corner goes on the free cell in front of the face clicked) or at typed
  coordinates.
- **Your own components.** "Save the selection as a component" takes the blocks in the selection's box and asks for
  ports by clicking cells just outside it. "Insert a saved redstone design" opens another saved circuit through the
  designer and takes its cells as a component without ports. Both join the design's own definitions; Keep copies one into
  this browser's quick library, which can be exported and imported as JSON. A definition drawn for the other game version
  can be used, with a warning.

## Owner decisions

| Question | Decision |
|---|---|
| Size limit | The studio's 32-per-side save limit stays for redstone designs; a custom component is at most 32 a side too. |
| TNT, dispensers, droppers built by pawns | Pawns do not place TNT; dispensers and droppers are placed empty. (Applies with the hand-off.) |
| Ownership after building | A built circuit is built once and released, so pistons and doors move without pawns "repairing" them. (Applies with the hand-off.) |
| Fidelity | Game-tick-accurate logic and timing; update-order tricks flagged, not emulated. |
| User components | A per-browser quick library, plus inserting any saved redstone design as a component. No server table. |
| Debugger keys | As listed above, remappable. |
| AI designer tools | None yet: the chat model has no redstone tools. |
| Locked repeaters | A studio-only marker over a locked repeater; the shared 1.12.2 mesher is not changed. |
| A design drawn for the other version | Allowed with a warning (the panel says so when the server runs the other version), never refused. |

## Persistence

- **Kind.** A saved circuit carries the comment line `# kind redstone` in its DSL, the same technique as `# anchor` and the
  mine's editor layout. Loading reads it; every server-side redraw (commit, revert, the brain's revise) carries it forward;
  the bridge's `KindOf` reports it, so the designs list and `designs.get` say `redstone`. Older code ignores the comment
  and reads the design as a build.
- **Editor data.** `redstoneAuthoring` on the session and the save request: one JSON text, at most 2,000,000 characters,
  marked `"redstone": 1`, stored per revision in `designer_redstone_authoring(design_id, rev, data)`. Its hash goes into
  the DSL as `# redstone layout <sha256>`, so a change of the editor data alone (a probe) is a new revision. A revision
  the bot draws itself (a commit, a revert, the brain's revise) writes no row: it carries the marker of the revision it
  was drawn from (a revert's target, else HEAD), and opening it reads the older row that marker names, else the newest
  older row. A revert to a circuit revision makes a circuit again even when HEAD was a build.

  ```json
  { "redstone": 1,
    "components": [{ "id": "c1", "def": "gate.xor", "at": [10, 0, 12], "turns": 1, "mirror": null, "label": "sum" }],
    "customDefs": [{ "id": "u.abc", "name": "my latch", "layout": { "size": [5, 2, 4], "cells": [], "ports": [] }, "version": "1.12.2" }],
    "probes": [], "breakpoints": [], "levels": [], "sim": { "speed": 10, "displayHz": 10 }, "origin": [29, 0, 30] }
  ```

  `turns` turn clockwise after `mirror` (`"x"` swaps east and west in the layout's own frame). Every part of the studio
  writes its own keys through one store (`authoring.ts`) and keeps keys it does not know, so a newer studio's data
  survives an older one. An instance whose cells no longer match the canvas when the design opens is dropped (its cells
  stay as plain blocks). Every cell the envelope names is a canvas cell, and the bot centres a design on the plot again
  whenever it opens: `origin` is the min corner of the canvas's blocks when the envelope was written (kept while it names
  any cell), and opening moves every component, probe, breakpoint and start level by how far that corner moved.
- **Unsaved work.** The component list is mirrored per session to this browser
  (`mineclaw.redstone.components.v1:<owner>:<session>:<version>`) and comes back on reopening the session only when every
  cell it owns still matches the canvas. The copy is dropped once the studio has nothing unsaved, and opening a design
  keeps only the account's 8 newest copies of other sessions. What a component held before it was placed is kept in memory only: after a
  reload, removing a component leaves air (ground below y 0).
- **Kind switch.** Build <-> redstone keeps every cell; the page remounts on the bot's reply but keeps the edits that were
  not saved as unsaved (the undo steps stay behind), and a kind that differs from the opened revision's is itself unsaved
  work until the next save. A kind sent with a version change but without `apply` is only part of the compatibility
  check: nothing changes.
- **Browser-only keys.** The quick library (`mineclaw.redstone.library.v1:<owner>`) and the debugger's key map
  (`mineclaw.redstone.debuggerKeys.v1`).

## Hand-off to pawns (deferred)

Building a circuit with pawns (WP8) is not done: it changes the colony's build pipeline, which another piece of work was
changing at the same time. Until it lands, nothing hands pawns a redstone design, because a half-built or half-configured
circuit is worse than none:

- `/story?design=N` (`features/story/mode/designParam.ts`, `pickUpDesign`) reads the design's kind before any saved plan
  and refuses a redstone design ("… is a redstone circuit: pawns can't build those yet"). The studio session's own kind is
  checked a second time when the cells come from there. When `designs.get` cannot be read, the pick-up stops instead of
  going on under the name "design #N", since an unknown kind could be a circuit.
- The bot's classic builder refuses a redstone design in `BlueprintService.WorldRefusal` (`RedstoneRefusal`), which also
  stops a pawn's `blueprint` order.
- The studio hides "Build in Controller" and "Ask the bot to build it" for a saved redstone design, and the Controller's
  design library labels one "Redstone".

The bridge must ship with the bot: an older bridge reports a redstone design as `build`, and the web guard would then let
it through to the bot's refusal only.

What WP8 is to do (report 4.8): build items gain `phase` (digs and supports, then wiring and consumers, then sources) and
`mechanism`; a mechanism group never reopens a done cell and is released when built; the colony derives the use clicks for
repeater delay, comparator mode and lever state from each cell's meta; pawns click after placing; TNT is left out; the web
runs the simulator as a pre-flight before queueing. The guards above are replaced then.

## Performance

The cost of a running simulation in the studio is re-meshing, not simulating. The controller sends the simulated states
at a display rate (10 Hz by default; 5 or 20 selectable) as one delta of the changed cells, halves the rate while the
viewer runs under 30 fps and gives it back after 5 s at 40 fps, runs the simulation in slices of at most 4 ms a frame,
and draws the overlays (dust levels, probe and breakpoint marks, lock marks) in at most three draw calls.

**Not measured:** the frame rate on the owner's software-WebGL browser with the 10 Hz default. The check, by hand: draw a
15-cell dust line with a lever, simulate it at 20 gt/s while flipping the lever, and read the studio's fps readout; it
should stay at or above 40. If it does not, lower the default display rate.

## Tests

- `web/packages/redstone/test/`: the engine per version (dust, torch, repeater, comparator, inputs, pistons, sinks,
  scheduler, history, breakpoints and trace, diagnostics) and the library proofs.
- `web/apps/web/test/`: the simulation controller, the display layer, the input rules, the kind switch, the debugger,
  the authoring envelope, the components (`redstoneComponents.test.ts`), the orientation parity between the simulator and
  the studio for every block, meta, turn and mirror (`redstoneOrientationParity.test.ts`), and the acceptance on the real
  engine (`redstoneAcceptance.test.ts`: a library NOT gate's torch flips 2 gt after its block is powered and stopping
  restores the view exactly; a rising breakpoint on the XOR's output pauses on the exact tick and a step back returns to
  the tick before, with the timeline's cursor there).
- C#: `DesignerRedstoneTests` (kind, tag round trip, per-revision editor data, every redstone palette entry saved and
  reloaded), `DesignKindTests` (the bridge), `RedstoneDesignBuildTests` (the builder's refusal).
- Not done: a Playwright run of the studio page.
