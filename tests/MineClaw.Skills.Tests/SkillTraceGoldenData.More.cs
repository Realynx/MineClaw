namespace MineClaw.Skills.Tests;

internal static partial class SkillTraceGoldenData
{
    public const string Doors = """"
## wooden door: open, again, close by kind, toggle twice
> open_door {"pos":"0,65,0"}
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
= ok: door at 0,64,0 opened
  world 73061A76E64A9FBB @-1.5,64,0.5 violations 0
> open_door {"pos":"0,64,0"}
= ok: door at 0,64,0 already open
  world 73061A76E64A9FBB @-1.5,64,0.5 violations 0
> close_door {"kind":"door"}
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
= ok: door at 0,64,0 closed
  world 5E846EC1AD72259F @-1.5,64,0.5 violations 0
> toggle_door {}
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
= ok: door at 0,64,0 opened
  world 73061A76E64A9FBB @-1.5,64,0.5 violations 0
> toggle_door {"kind":"door"}
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
= ok: door at 0,64,0 closed
  world 5E846EC1AD72259F @-1.5,64,0.5 violations 0
> open_door {"kind":"gate"}
= failed: no door/gate/trapdoor nearby
  world 5E846EC1AD72259F @-1.5,64,0.5 violations 0
> open_door {"pos":"3,64,3"}
= failed: no door/gate/trapdoor at 3,64,3 (air)
  world 5E846EC1AD72259F @-1.5,64,0.5 violations 0
## locked door
> open_door {"pos":"0,64,0"}
  activate 0,64,0 -> ok clicked
  wait_until 1500 -> False
= failed: door at 0,64,0 didn't open: no block change from the server (locked or claimed?)
  world 5E846EC1AD72259F @-1.5,64,0.5 violations 0
> goto_via_doors {"pos":"2,64,0"}
  goto block (-1, 64, 0) drop - place  break  @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  activate 0,64,0 -> ok clicked
  wait_until 1500 -> False
= failed: stuck at door: door at 0,64,0 didn't open: no block change from the server (locked or claimed?)
  world 5E846EC1AD72259F @-0.5,64,0.5 violations 0
## fence gate
> open_door {"kind":"fence_gate"}
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
= ok: fence gate at 0,64,0 opened
  world C17415E465D3BF2A @-1.5,64,0.5 violations 0
> close_door {"kind":"gate"}
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
= ok: fence gate at 0,64,0 closed
  world 55B366E1A52A9DAE @-1.5,64,0.5 violations 0
> goto_via_doors {"pos":"2,64,0"}
  goto block (-1, 64, 0) drop - place  break  @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
  goto block (1, 64, 0) drop - place  break  @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
  goto block (2, 64, 0) drop - place  break  @2.5,64,0.5 -> ok arrived at (2.5, 64, 0.5)
= ok: at 2,64,0 through 1 door(s), 1 re-closed
  world 55B366E1A52A9DAE @2.5,64,0.5 violations 0
## iron door: no trigger, a button, a lever, a plate
> open_door {"pos":"0,64,0"}
= failed: iron door at 0,64,0 only opens with redstone and I don't know its button/lever/plate; needs_input: ask a player "how do I open the iron door at 0,64,0?" (default: go around)
  world 510D3D18F8C7EAC2 @-1.5,64,0.5 violations 0
> open_door {"kind":"iron_door"}
= failed: iron door at 0,64,0 only opens with redstone and I don't know its button/lever/plate; needs_input: ask a player "how do I open the iron door at 0,64,0?" (default: go around)
  world 510D3D18F8C7EAC2 @-1.5,64,0.5 violations 0
> open_door {"pos":"0,64,0"}
  activate -1,65,1 -> ok activated
  wait_until 1500 -> True
= ok: iron door at 0,64,0 opened via button (shuts in ~1s)
  world 7EF446D5FCB85F0C @-1.5,64,0.5 violations 0
> close_door {"pos":"0,64,0"}
= ok: iron door at 0,64,0 closes by itself (button)
  world 7EF446D5FCB85F0C @-1.5,64,0.5 violations 0
> use_lever {"pos":"-1,65,1","on":true}
  activate -1,65,1 -> ok activated
  wait_until 1500 -> True
= ok: lever at -1,65,1 on
  world BA16A6BAA35E0C42 @-1.5,64,0.5 violations 0
> use_lever {"pos":"-1,65,1","on":true}
= ok: lever at -1,65,1 already on
  world BA16A6BAA35E0C42 @-1.5,64,0.5 violations 0
> close_door {"pos":"0,64,0"}
  activate -1,65,1 -> ok activated
  wait_until 1500 -> True
= ok: iron door at 0,64,0 closed via lever
  world F8CB515BBDCE7FDD @-1.5,64,0.5 violations 0
> use_lever {"on":false}
= ok: lever at -1,65,1 already off
  world F8CB515BBDCE7FDD @-1.5,64,0.5 violations 0
> use_lever {"pos":"3,64,3"}
= failed: no lever there
  world F8CB515BBDCE7FDD @-1.5,64,0.5 violations 0
> press_button {"pos":"-1,65,1"}
= failed: no button there
  world F8CB515BBDCE7FDD @-1.5,64,0.5 violations 0
> open_door {"pos":"0,64,0"}
  goto block (-1, 64, 0) drop - place  break  @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  wait_until 1500 -> False
= failed: iron door at 0,64,0 didn't react to the pressure plate at -1,64,0 (not wired to it?)
  world 3D35C989448CBB5D @-0.5,64,0.5 violations 0
> close_door {"pos":"0,64,0"}
= ok: iron door at 0,64,0 already closed
  world 3D35C989448CBB5D @-0.5,64,0.5 violations 0
## goto via doors: through and back, leave open, an open door, no path
> goto_via_doors {"pos":"2,64,0"}
  goto block (-1, 64, 0) drop - place  break  @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
  goto block (1, 64, 0) drop - place  break  @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
  goto block (2, 64, 0) drop - place  break  @2.5,64,0.5 -> ok arrived at (2.5, 64, 0.5)
= ok: at 2,64,0 through 1 door(s), 1 re-closed
  world 5E846EC1AD72259F @2.5,64,0.5 violations 0
> goto_via_doors {"pos":"-2,64,0","leave_open":true}
  goto block (0, 64, 0) drop - place  break  @0.5,64,0.5 -> ok arrived at (0.5, 64, 0.5)
  activate 0,64,0 -> ok activated
  wait_until 1500 -> True
  goto block (-2, 64, 0) drop - place  break  @-1.5,64,0.5 -> ok arrived at (-1.5, 64, 0.5)
  goto block (-2, 64, 0) drop - place  break  @-1.5,64,0.5 -> ok arrived
= ok: at -2,64,0 through 1 door(s)
  world 73061A76E64A9FBB @-1.5,64,0.5 violations 1
> goto_via_doors {"pos":"-3,64,1"}
  goto block (-3, 64, 1) drop - place  break  @-2.5,64,1.5 -> ok arrived at (-2.5, 64, 1.5)
= ok: at -3,64,1
  world 73061A76E64A9FBB @-2.5,64,1.5 violations 1
> goto_via_doors {}
= failed: pos required
  world 73061A76E64A9FBB @-2.5,64,1.5 violations 1
> goto_via_doors {"pos":"2,64,0"}
  goto block (2, 64, 0) drop - place  break  @2.5,64,0.5 -> ok arrived at (2.5, 64, 0.5)
= ok: at 2,64,0
  world 73061A76E64A9FBB @2.5,64,0.5 violations 0
> goto_via_doors {"pos":"2,64,0"}
  goto block (2, 64, 0) drop - place  break  @-1.5,64,0.5 -> FAIL no path to block (2, 64, 0) (no route)
= failed: no path to block (2, 64, 0) (no route)
  world 510D3D18F8C7EAC2 @-1.5,64,0.5 violations 0
> goto_via_doors {"pos":"2,64,0"}
  goto block (-1, 64, 0) drop - place  break  @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  activate -1,65,1 -> ok activated
  wait_until 1500 -> True
  goto block (1, 64, 0) drop - place  break  @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  goto block (2, 64, 0) drop - place  break  @2.5,64,0.5 -> ok arrived at (2.5, 64, 0.5)
= ok: at 2,64,0 through 1 door(s), 0 re-closed
  world 7EF446D5FCB85F0C @2.5,64,0.5 violations 0
## trapdoor under me
> open_door {"pos":"0,63,0"}
= failed: won't open the trapdoor at 0,63,0: I'm standing on it
  world 1F9364AEE269E285 @0.5,63.19,0.5 violations 0
> open_door {"kind":"trapdoor"}
= failed: won't open the trapdoor at 0,63,0: I'm standing on it
  world 1F9364AEE269E285 @0.5,63.19,0.5 violations 0
"""";

    public const string Misc = """"
## smelting
> smelt_batch {"item":"stone","count":16}
= failed: no cobblestone to smelt
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> smelt_batch {"item":"stone","count":16}
= failed: no cobblestone to smelt
  world 854008C4102DF0DB @0.5,64,0.5 violations 0
> smelt_batch {"item":"stone","count":16}
= failed: no fuel (coal, charcoal, planks, logs)
  world DEAB175AC772CB3A @0.5,64,0.5 violations 0
> smelt_batch {"item":"stone","count":16}
  smelt 4 16 fuel 263 wait False at 2,64,2 -> ok smelting 16
= ok: smelting 16 cobblestone in 1 furnace(s) -> stone; collect_furnace later
  world 275955FCC831A642 @0.5,64,0.5 violations 0
> smelt_batch {"item":"cobblestone","wait":true}
  smelt 4 4 fuel 263 wait True at 2,64,2 -> ok smelting 4
= ok: smelting 4 cobblestone in 1 furnace(s) -> stone
  world E378B4EBD20773DC @0.5,64,0.5 violations 0
> smelt_batch {"item":"dirt"}
= failed: 'dirt' doesn't smelt
  world E378B4EBD20773DC @0.5,64,0.5 violations 0
> smelt_batch {"item":"unobtainium"}
= failed: 'unobtainium' doesn't smelt
  world E378B4EBD20773DC @0.5,64,0.5 violations 0
> refuel_furnace {}
  open 2,64,2 -> FAIL no chest at (2, 64, 2)
= failed: no chest at (2, 64, 2)
  world E378B4EBD20773DC @0.5,64,0.5 violations 0
> refuel_furnace {"pos":"2,64,2","fuel":"coal","count":2}
  open 2,64,2 -> FAIL no chest at (2, 64, 2)
= failed: no chest at (2, 64, 2)
  world E378B4EBD20773DC @0.5,64,0.5 violations 0
> refuel_furnace {"pos":"3,64,3"}
= failed: no furnace nearby
  world E378B4EBD20773DC @0.5,64,0.5 violations 0
> collect_furnace {}
  collect_furnace 2,64,2 -> ok took 8 stone
= ok: took 8 stone
  world E378B4EBD20773DC @0.5,64,0.5 violations 0
> collect_furnace {"pos":"2,64,2"}
  collect_furnace 2,64,2 -> ok took 8 stone
= ok: took 8 stone
  world E378B4EBD20773DC @0.5,64,0.5 violations 0
> smelt_batch {"item":"charcoal"}
  smelt 17 8 fuel 5 wait False at -2,64,-2 -> ok smelting 8
= ok: smelting 8 oak wood in 1 furnace(s) -> charcoal; collect_furnace later
  world 7726C09FFB6E5AA1 @0.5,64,0.5 violations 0
> refuel_furnace {"fuel":"planks","count":9}
  open -2,64,-2 -> FAIL no chest at (-2, 64, -2)
= failed: no chest at (-2, 64, -2)
  world 7726C09FFB6E5AA1 @0.5,64,0.5 violations 0
> refuel_furnace {"fuel":"coal"}
= failed: no fuel
  world 7726C09FFB6E5AA1 @0.5,64,0.5 violations 0
> smelt_batch {"item":"cobblestone","count":138}
  smelt 4 64 fuel 280 wait False at -2,64,-2 -> ok smelting 64
  smelt 4 64 fuel 280 wait False at 2,64,2 -> ok smelting 64
= ok: smelting 128 cobblestone in 2 furnace(s) -> stone; collect_furnace later
  world 8FA89C6083E096EA @0.5,64,0.5 violations 0
> collect_furnace {}
= failed: no furnace nearby
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> refuel_furnace {}
= failed: no furnace nearby
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> refuel_furnace {"count":3}
  open 2,64,2 -> ok opened
  click 30 -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 30 -> ok clicked
  close -> ok closed
= ok: added 3 coal to furnace at 2,64,2
  world 9D3A8159C7FBA97F @0.5,64,0.5 violations 0
> refuel_furnace {"count":9}
  open 2,64,2 -> ok opened
  click 30 -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  close -> ok closed
= ok: added 2 coal to furnace at 2,64,2
  world 09CE42BF5DBA5086 @0.5,64,0.5 violations 0
> refuel_furnace {"fuel":"planks"}
  open 2,64,2 -> ok opened
  click 30 -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 1 right -> ok clicked
  click 30 -> ok clicked
  close -> ok closed
= ok: added 8 wooden planks to furnace at 2,64,2
  world C307A29260C7DE12 @0.5,64,0.5 violations 0
## repeater, jukebox
> set_repeater {"pos":"2,64,2","delay":3}
= failed: no repeater there
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> set_repeater {"pos":"2,64,2","delay":3}
  activate 2,64,2 -> ok activated
  wait_until 1500 -> True
  activate 2,64,2 -> ok activated
  wait_until 1500 -> True
= ok: repeater at 2,64,2 delay 3
  world F87E2372C7CD61A7 @0.5,64,0.5 violations 0
> set_repeater {"pos":"2,64,2","delay":3}
= ok: repeater at 2,64,2 delay 3
  world F87E2372C7CD61A7 @0.5,64,0.5 violations 0
> set_repeater {"pos":"2,64,2","delay":9}
  activate 2,64,2 -> ok activated
  wait_until 1500 -> True
= ok: repeater at 2,64,2 delay 4
  world 1957511105D1B3B3 @0.5,64,0.5 violations 0
> set_repeater {"pos":"2,64,2"}
  activate 2,64,2 -> ok activated
  wait_until 1500 -> True
= ok: repeater at 2,64,2 delay 1
  world 61FD4192D94DF000 @0.5,64,0.5 violations 0
> set_repeater {"pos":"2,64,2","delay":2}
= failed: repeater is protected (a protected block); say 'you can break it' if you want it gone
  world 61FD4192D94DF000 @0.5,64,0.5 violations 0
> use_jukebox {}
= failed: no jukebox nearby
  world 61FD4192D94DF000 @0.5,64,0.5 violations 0
> use_jukebox {}
= failed: no music disc
  world AF880761693D1F8C @0.5,64,0.5 violations 0
> use_jukebox {}
  activate -2,64,2 -> ok activated
  wait_until 1500 -> True
= ok: playing
  world 9A95DA8F48694D8F @0.5,64,0.5 violations 0
> use_jukebox {"eject":true}
  activate -2,64,2 -> ok activated
  wait_until 1500 -> True
= ok: ejected the disc
  world FA68D10BDC00AE0D @0.5,64,0.5 violations 0
> use_jukebox {"pos":"-2,64,2","eject":true}
= ok: jukebox already empty
  world FA68D10BDC00AE0D @0.5,64,0.5 violations 0
> use_jukebox {}
  activate -2,64,2 -> ok activated
  wait_until 1500 -> True
= ok: ejected the disc
  world FA68D10BDC00AE0D @0.5,64,0.5 violations 0
> use_jukebox {"pos":"-2,64,2","eject":false}
  activate -2,64,2 -> ok activated
  wait_until 1500 -> True
= ok: playing
  world 9A95DA8F48694D8F @0.5,64,0.5 violations 0
## projectiles and bows
> shoot_bow {}
= failed: shoot_bow: needs bow+arrows
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> shoot_bow {}
= failed: no hostile mob in range
  world 02E30E1605811C68 @0.5,64,0.5 violations 0
> shoot_bow {"shots":4}
  shoot 3.5,64,3.5 77 20 -> ok shot an arrow
  shoot 3.5,64,3.5 77 20 -> ok shot an arrow
= ok: shot 2 arrow(s) at zombie
  world 22B8139E431AE71E @0.5,64,0.5 violations 0
> shoot_bow {"target":"4,64,-3","shots":2}
  shoot 4.5,64.5,-2.5 - 20 -> ok shot an arrow
  shoot 4.5,64.5,-2.5 - 20 -> ok shot an arrow
= ok: shot 2 arrow(s) at 4,64,-3
  world 4572BC0701B99CBB @0.5,64,0.5 violations 0
> shoot_bow {"target":"zombie"}
  shoot 3.5,64,3.5 77 20 -> ok shot an arrow
= ok: shot 1 arrow(s) at zombie
  world 22B8139E431AE71E @0.5,64,0.5 violations 0
> throw_projectile {"count":3}
  look 3.5,65.13,3.5 -> ok looking
  use 332 -> ok used
  wait 4
  look 3.5,65.13,3.5 -> ok looking
  use 332 -> ok used
  wait 4
  look 3.5,65.13,3.5 -> ok looking
  use 332 -> ok used
  wait 4
= ok: threw 3 snowball at zombie
  world BA051BAD3BA86BBC @0.5,64,0.5 violations 0
> throw_projectile {"target":"3,64,-3","count":2}
  look 3.5,64.63,-2.5 -> ok looking
  use 332 -> ok used
  wait 4
= ok: threw 1 snowball at 3,64,-3
  world 22B8139E431AE71E @0.5,64,0.5 violations 0
> throw_projectile {"item":"egg"}
= failed: throw_projectile: needs snowballs/eggs
  world 22B8139E431AE71E @0.5,64,0.5 violations 0
> throw_projectile {"item":"egg","count":5}
  look 3.5,65.13,3.5 -> ok looking
  use 344 -> ok used
  wait 4
  look 3.5,65.13,3.5 -> ok looking
  use 344 -> ok used
  wait 4
= ok: threw 2 egg at zombie
  world 22B8139E431AE71E @0.5,64,0.5 violations 0
> throw_projectile {}
= failed: throw_projectile: needs snowballs/eggs
  world 22B8139E431AE71E @0.5,64,0.5 violations 0
> throw_projectile {}
= failed: throw_projectile: needs snowballs/eggs
  world 22B8139E431AE71E @0.5,64,0.5 violations 0
> shoot_bow {}
= failed: shoot_bow: needs bow+arrows
  world 22B8139E431AE71E @0.5,64,0.5 violations 0
## mobs: tame, shear, milk, ride
> tame_wolf {}
= failed: tame_wolf: needs bones
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> tame_wolf {"count":2}
  goto within 2 of (2.5, 64, 0.5) drop - place  break  @0.5,64,0.5 -> ok arrived
  use_entity 55 352 -> ok smoke
  wait 10
  use_entity 55 352 -> ok smoke
  wait 10
  use_entity 55 352 -> ok hearts
  goto within 2 of (-2.5, 64, 0.5) drop - place  break  @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  use_entity 56 352 -> ok smoke
  wait 10
  use_entity 56 352 -> ok smoke
  wait 10
  collect 6 dry False -> ok collected
= ok: fed 1 wolf
  world 4EDF54223CE4B24F @-0.5,64,0.5 violations 0
> shear_sheep {}
  goto within 2 of (1.5, 64, 2.5) drop - place  break  @0.5,64,1.5 -> ok arrived at (0.5, 64, 1.5)
  use_entity 60 359 -> ok used
  collect 6 dry False -> ok collected
= ok: sheared 1 sheep
  world 04886902761BBFE9 @0.5,64,1.5 violations 0
> shear_sheep {}
  goto within 2 of (1.5, 64, 2.5) drop - place  break  @0.5,64,1.5 -> ok arrived
  use_entity 60 359 -> ok used
  collect 6 dry False -> ok collected
= ok: sheared 1 sheep
  world 04886902761BBFE9 @0.5,64,1.5 violations 0
> milk_cow {}
  goto within 2 of (1.5, 64, -1.5) drop - place  break  @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  use_entity 61 325 -> ok used
  wait_until 2000 -> False
  collect 6 dry False -> ok collected
= failed: no cow to milk nearby
  world 386B67B492F72077 @1.5,64,0.5 violations 0
> ride_vehicle {}
  use_entity 90 - -> ok riding
  wait_until 1000 -> True
= ok: in the boat
  world 386B67B492F72077 @1.5,64,0.5 violations 0
> ride_vehicle {"exit":true}
  dismount -> ok got out
= ok: got out
  world 386B67B492F72077 @1.5,64,0.5 violations 0
## sleep, give
> sleep_in_bed {}
= failed: it's day (12:00); can only sleep at night
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> sleep_in_bed {}
= failed: it's day (12:00); can only sleep at night
  world 531B4B2D3AAAA139 @0.5,64,0.5 violations 0
> sleep_in_bed {}
= failed: skeleton within 8 of the bed; can't sleep
  world 531B4B2D3AAAA139 @0.5,64,0.5 violations 0
> sleep_in_bed {}
  goto reach (2, 64, 2) (2.5) drop - place False break False @1.5,64,1.5 -> ok arrived at (1.5, 64, 1.5)
  activate 2,64,2 -> ok activated
  wait_until 2000 -> True
= ok: sleeping in bed at 2,64,2 (spawn set; night skips when everyone sleeps)
  world 3E06B7E49FD10492 @1.5,64,1.5 violations 0
> sleep_in_bed {"pos":"-2,64,2"}
= failed: no bed nearby
  world 00AD11925B849F59 @1.5,64,1.5 violations 0
> sleep_in_bed {}
= failed: sleep_in_bed: only works in the overworld
  world 00AD11925B849F59 @1.5,64,1.5 violations 0
> give_items {"player":"nobody","item":"dirt"}
= failed: can't see nobody
  world B4C28066543D6BE7 @0.5,64,0.5 violations 0
> give_items {"player":"kiwi","item":"dirt","count":5}
  goto within 2.5 of (2.5, 64, 2.5) drop - place  break  @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  look 2.5,65.2,2.5 -> ok looking
  click 36 -> ok clicked
  click -999 right -> ok clicked
  click -999 right -> ok clicked
  click -999 right -> ok clicked
  click -999 right -> ok clicked
  click -999 right -> ok clicked
  click 36 -> ok clicked
  resync -> FAIL inventory resync unsupported
= ok: tossed 5 dirt to kiwi
  world 2E3C4082B5FD4A30 @1.5,64,0.5 violations 0
> give_items {"player":"Kiwi","item":"dirt","count":50}
  goto within 2.5 of (2.5, 64, 2.5) drop - place  break  @1.5,64,0.5 -> ok arrived
  look 2.5,65.2,2.5 -> ok looking
  click 36 -> ok clicked
  click -999 -> ok clicked
  resync -> FAIL inventory resync unsupported
= ok: tossed 5 dirt to Kiwi
  world 4EDF54223CE4B24F @1.5,64,0.5 violations 0
> give_items {"player":"kiwi","item":"diamond"}
= failed: no diamond
  world 4EDF54223CE4B24F @1.5,64,0.5 violations 0
> give_items {"player":"kiwi"}
= failed: unknown item ''
  world 4EDF54223CE4B24F @1.5,64,0.5 violations 0
> give_items {"item":"dirt"}
= failed: can't see
  world 4EDF54223CE4B24F @1.5,64,0.5 violations 0
## nether portal frame
> build_portal {}
= failed: build_portal: needs 10 obsidian+4 blocks
  world A0F265BE61AC6540 @0.5,64,0.5 violations 0
> build_portal {}
= failed: needs 10 obsidian (have 6); make_obsidian first
  world E1E542644B60F715 @0.5,64,0.5 violations 0
> build_portal {"pos":"2,64,-1"}
  place 2,64,-1 4 -> ok placed 4
  place 5,64,-1 4 -> ok placed 4
  place 2,65,-1 49 -> ok placed 49
  place 2,66,-1 49 -> ok placed 49
  place 3,64,-1 49 -> ok placed 49
  place 2,67,-1 49 -> ok placed 49
  place 4,64,-1 49 -> ok placed 49
  place 5,65,-1 49 -> ok placed 49
  place 5,66,-1 49 -> ok placed 49
  place 5,67,-1 49 -> ok placed 49
  place 2,68,-1 4 -> ok placed 4
  place 5,68,-1 4 -> ok placed 4
  place 3,68,-1 49 -> ok placed 49
  place 4,68,-1 49 -> ok placed 49
= ok: portal frame at 2,64,-1 (x axis); light_portal next
  world A88BC57A83211E99 @0.5,64,0.5 violations 0
> light_portal {}
  place 3,65,-1 259 -> ok portal
  wait_until 2000 -> True
= ok: portal lit at 3,65,-1
  world 6D01A1935492F9E7 @0.5,64,0.5 violations 0
> build_portal {"pos":"2,64,-1","axis":"z"}
= failed: needs 4 corner blocks (cobble/dirt) or 14 obsidian
  world 0B7C6BC94733D988 @0.5,64,0.5 violations 0
> build_portal {"pos":"9,64,9"}
= failed: needs 4 corner blocks (cobble/dirt) or 14 obsidian
  world 0B7C6BC94733D988 @0.5,64,0.5 violations 0
> build_portal {}
= failed: needs 4 corner blocks (cobble/dirt) or 14 obsidian
  world 0B7C6BC94733D988 @0.5,64,0.5 violations 0
## buckets
> fill_bucket {}
= failed: fill_bucket: needs bucket
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> fill_bucket {}
= failed: no water source within 24
  world 995D8BF2B5BB7B62 @0.5,64,0.5 violations 0
> fill_bucket {}
  look 3.2,63.98,3.2 -> ok looking
  use 325 -> ok filled
  wait_until 1500 -> True
= ok: filled water bucket at 3,63,3
  world 8C5B61417C44EC90 @0.5,64,0.5 violations 0
> fill_bucket {"liquid":"lava"}
= failed: no lava source within 24
  world 8C5B61417C44EC90 @0.5,64,0.5 violations 0
> fill_bucket {"liquid":"lava"}
  look -2.2,63.98,-2.2 -> ok looking
  use 325 -> ok filled
  wait_until 1500 -> True
= ok: filled lava bucket at -3,63,-3
  world EA6CF32E7C06847E @0.5,64,0.5 violations 0
> empty_bucket {}
= failed: pos required
  world EA6CF32E7C06847E @0.5,64,0.5 violations 0
> empty_bucket {"pos":"2,64,-2"}
  look 2.5,64,-1.5 -> ok looking
  use 326 -> ok poured
  wait_until 1500 -> True
= ok: poured water at 2,64,-2
  world 6CD374D0D6F41739 @0.5,64,0.5 violations 0
> empty_bucket {"pos":"9,70,9"}
= failed: empty_bucket: needs water bucket
  world 6CD374D0D6F41739 @0.5,64,0.5 violations 0
> fill_bucket {"pos":"3,63,3"}
  goto any(reach (2, 64, -2) (4.25) (visible source (2, 64, -2))) drop - place False break False @0.5,64,0.5 -> FAIL no path to any(reach (2, 64, -2) (4.25) (visible source (2, 64, -2))) (no route)
= failed: no reachable visible source in the pond at 2,64,-2: no path to any(reach (2, 64, -2) (4.25) (visible source (2, 64, -2))) (no route)
  world 6CD374D0D6F41739 @0.5,64,0.5 violations 0
> empty_bucket {"pos":"-2,64,2"}
= failed: empty_bucket: needs water bucket
  world 6CD374D0D6F41739 @0.5,64,0.5 violations 0
> empty_bucket {"pos":"-2,64,2"}
= failed: empty_bucket: needs water bucket
  world 6CD374D0D6F41739 @0.5,64,0.5 violations 0
> infinite_water {}
= failed: infinite_water: needs 2 water buckets
  world 6CD374D0D6F41739 @0.5,64,0.5 violations 0
> infinite_water {"pos":"1,63,-1"}
= failed: infinite_water: needs 2 water buckets
  world 6CD374D0D6F41739 @0.5,64,0.5 violations 0
> make_obsidian {}
= failed: make_obsidian: needs water bucket
  world 6CD374D0D6F41739 @0.5,64,0.5 violations 0
## promoted structure skill
> use_cobble_gen {"times":2}
  goto block (1, 64, 1) drop - place  break  @1.5,64,1.5 -> ok arrived at (1.5, 64, 1.5)
  dig 2,63,2 -> ok dug stone
  wait 10
  wait 10
  collect 6 dry False -> ok collected
  wait 40
  collect 8 dry False -> ok collected
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  collect 6 dry False -> ok collected
  goto block (0, 64, 0) drop - place  break  @0.5,64,0.5 -> ok arrived at (0.5, 64, 0.5)
  activate 2,64,-2 -> ok activated
  activate 2,64,-2 -> ok activated
  goto block (1, 64, 1) drop - place  break  @1.5,64,1.5 -> ok arrived at (1.5, 64, 1.5)
  wait 10
  wait 10
  wait 10
  collect 6 dry False -> ok collected
  wait 40
  collect 8 dry False -> ok collected
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  wait 10
  collect 6 dry False -> ok collected
  goto block (0, 64, 0) drop - place  break  @0.5,64,0.5 -> ok arrived at (0.5, 64, 0.5)
  activate 2,64,-2 -> ok activated
  activate 2,64,-2 -> ok activated
= ok: used cobble gen 2x
  world F4D6C6049340D0AA @0.5,64,0.5 violations 0
> use_bad_farm {}
  goto block (1, 64, 1) drop - place  break  @1.5,64,1.5 -> ok arrived at (1.5, 64, 1.5)
= failed: needs_input: don't know how to do step "dance a little" of bad farm
  world F4D6C6049340D0AA @1.5,64,1.5 violations 0
> use_gone {}
  activate 9,90,9 -> FAIL not interactable
= failed: gone step "press at 9,90,9": not interactable (0 rounds done)
  world F4D6C6049340D0AA @1.5,64,1.5 violations 0
## registry: unknown, stub, preconditions, acquire
> chop_the_moon {}
= failed: no skill 'chop_the_moon'; try plant_sapling
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> go_fishing {}
= failed: go_fishing is not implemented yet (stub): cast rod, reel on bite (bobber dip), repeat
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> shear_sheep {}
= failed: shear_sheep: needs client primitive UseEntity
  world 04886902761BBFE9 @0.5,64,0.5 violations 0
> shoot_bow {}
= failed: shoot_bow: needs bow+arrows
  world 04886902761BBFE9 @0.5,64,0.5 violations 0
> milk_cow {}
= failed: milk_cow: only works in the overworld
  world 04886902761BBFE9 @0.5,64,0.5 violations 0
> shoot_bow {}
= failed: shoot_bow: needs bow+arrows (fetched for shoot_bow: 261; fetched for shoot_bow: 262)
  world 9D680AD8B1904977 @0.5,64,0.5 violations 0
> shoot_bow {}
  shoot 3.5,64,3.5 77 20 -> ok shot an arrow
= ok: shot 1 arrow(s) at zombie
  world 9D680AD8B1904977 @0.5,64,0.5 violations 0
> expand_island {}
= failed: no building blocks
  world 9D680AD8B1904977 @0.5,64,0.5 violations 0
"""";
}
