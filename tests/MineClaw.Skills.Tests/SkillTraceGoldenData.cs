namespace MineClaw.Skills.Tests;

/// <summary>The golden traces of <see cref="SkillTraceGoldenTests"/> (generated from the pre-refactor code).</summary>
internal static partial class SkillTraceGoldenData
{
    public const string ExcavationBeta = """"
## room from a pocket
> excavate {"box":"3,30,-1,6,32,2"}
  dig 1,31,0 -> ok dug stone
  dig 2,31,0 -> ok dug stone
  dig 3,31,0 -> ok dug stone
  dig 3,32,0 -> ok dug stone
  dig 4,32,0 -> ok dug stone
  dig 3,31,-1 -> ok dug stone
  dig 3,32,-1 -> ok dug stone
  dig 3,31,1 -> ok dug stone
  dig 4,31,-1 -> ok dug stone
  dig 4,31,0 -> ok dug stone
  dig 4,31,1 -> ok dug stone
  dig 3,30,0 -> ok dug stone
  dig 3,30,-1 -> ok dug stone
  dig 3,30,1 -> ok dug stone
  dig 4,30,0 -> ok dug stone
  dig 4,30,-1 -> ok dug stone
  dig 4,30,1 -> ok dug stone
  dig 1,30,0 -> ok dug stone
  goto block (1, 30, 0) drop 1 place False break False @1.5,30,0.5 -> ok arrived at (1.5, 30, 0.5)
  dig 3,32,1 -> ok dug stone
  dig 4,32,-1 -> ok dug stone
  dig 4,32,1 -> ok dug stone
  dig 5,32,-1 -> ok dug stone
  dig 5,32,0 -> ok dug stone
  dig 5,32,1 -> ok dug stone
  dig 5,31,-1 -> ok dug stone
  dig 5,31,0 -> ok dug stone
  dig 5,31,1 -> ok dug stone
  dig 5,30,-1 -> ok dug stone
  dig 5,30,0 -> ok dug stone
  dig 5,30,1 -> ok dug stone
  dig 2,30,0 -> ok dug stone
  goto block (2, 30, 0) drop 1 place False break False @2.5,30,0.5 -> ok arrived at (2.5, 30, 0.5)
  dig 4,32,2 -> ok dug stone
  place 2,30,0 50 Top -> ok placed 50
  dig 5,32,2 -> ok dug stone
  dig 6,32,-1 -> ok dug stone
  dig 6,32,0 -> ok dug stone
  dig 6,32,1 -> ok dug stone
  dig 6,32,2 -> ok dug stone
  dig 4,31,2 -> ok dug stone
  dig 5,31,2 -> ok dug stone
  dig 6,31,-1 -> ok dug stone
  dig 6,31,0 -> ok dug stone
  dig 6,31,1 -> ok dug stone
  dig 6,31,2 -> ok dug stone
  goto block (3, 30, 0) drop 1 place False break False @3.5,30,0.5 -> ok arrived at (3.5, 30, 0.5)
  dig 3,32,2 -> ok dug stone
  dig 3,31,2 -> ok dug stone
  dig 3,30,2 -> ok dug stone
  dig 4,30,2 -> ok dug stone
  dig 5,30,2 -> ok dug stone
  dig 6,30,-1 -> ok dug stone
  dig 6,30,0 -> ok dug stone
  dig 6,30,1 -> ok dug stone
  dig 6,30,2 -> ok dug stone
= ok: dug box 4×4×3 x3..6 y30..32 z-1..2: dug 52 (incl. 4 access), sealed 0, torches 1, left 0
  world 0A651013D7771A54 @3.5,30,0.5 violations 0
## spiral stair learned and extended
> excavate {"op":"learn","name":"spiral stair","box":"-1,40,-1,2,62,2"}
= ok: learned 'spiral stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5: 8 repeats of 3 cells from 0,60,0; extend with to:Y, or at:x,y,z for a new one
  world AC6BE913A92D0A48 @1.5,51,0.5 violations 0
> excavate {"site":"spiral stair","repeats":12}
  dig 0,52,0 -> ok dug stone
  dig 0,51,0 -> ok dug stone
  dig 0,50,0 -> ok dug stone
  dig 0,51,1 -> ok dug stone
  dig 0,50,1 -> ok dug stone
  dig 0,49,1 -> ok dug stone
  goto block (0, 50, 0) drop 1 place False break False @0.5,50,0.5 -> ok arrived at (0.5, 50, 0.5)
  dig 1,50,1 -> ok dug stone
  dig 1,49,1 -> ok dug stone
  dig 1,48,1 -> ok dug stone
  goto block (0, 49, 1) drop 1 place False break False @0.5,49,1.5 -> ok arrived at (0.5, 49, 1.5)
  dig 1,49,0 -> ok dug stone
  dig 1,48,0 -> ok dug stone
  dig 1,47,0 -> ok dug stone
  place 0,49,1 50 Top -> ok placed 50
  goto block (1, 48, 1) drop 1 place False break False @1.5,48,1.5 -> ok arrived at (1.5, 48, 1.5)
  dig 0,48,0 -> ok dug stone
  dig 0,47,0 -> ok dug stone
  dig 0,46,0 -> ok dug stone
  goto block (1, 47, 0) drop 1 place False break False @1.5,47,0.5 -> ok arrived at (1.5, 47, 0.5)
  dig 0,47,1 -> ok dug stone
  dig 0,46,1 -> ok dug stone
  dig 0,45,1 -> ok dug stone
  goto block (0, 46, 0) drop 1 place False break False @0.5,46,0.5 -> ok arrived at (0.5, 46, 0.5)
  dig 1,46,1 -> ok dug stone
  dig 1,45,1 -> ok dug stone
  dig 1,44,1 -> ok dug stone
  goto block (0, 45, 1) drop 1 place False break False @0.5,45,1.5 -> ok arrived at (0.5, 45, 1.5)
  dig 1,45,0 -> ok dug stone
  dig 1,44,0 -> ok dug stone
  dig 1,43,0 -> ok dug stone
  place 0,45,1 50 Top -> ok placed 50
  goto block (1, 44, 1) drop 1 place False break False @1.5,44,1.5 -> ok arrived at (1.5, 44, 1.5)
  dig 0,44,0 -> ok dug stone
  dig 0,43,0 -> ok dug stone
  dig 0,42,0 -> ok dug stone
  goto block (1, 43, 0) drop 1 place False break False @1.5,43,0.5 -> ok arrived at (1.5, 43, 0.5)
  dig 0,43,1 -> ok dug stone
  dig 0,42,1 -> ok dug stone
  dig 0,41,1 -> ok dug stone
  goto block (0, 42, 0) drop 1 place False break False @0.5,42,0.5 -> ok arrived at (0.5, 42, 0.5)
  dig 1,42,1 -> ok dug stone
  dig 1,41,1 -> ok dug stone
  dig 1,40,1 -> ok dug stone
  goto block (0, 41, 1) drop 1 place False break False @0.5,41,1.5 -> ok arrived at (0.5, 41, 1.5)
  dig 1,41,0 -> ok dug stone
  dig 1,40,0 -> ok dug stone
  dig 1,39,0 -> ok dug stone
  place 0,41,1 50 Top -> ok placed 50
= ok: spiral stair repeats 8→20 of 20 (down to y39): dug 36, sealed 0, torches 3, left 0
  world D8B5E39578A3A71E @0.5,41,1.5 violations 0
## a cave under a column
> excavate {"op":"learn","name":"spiral stair","rule":"column 3 step 0,-1,0 turn -90 pivot 0.5,0.5","at":"0,60,0"}
= ok: remembered 'spiral stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5 from 0,60,0, 4 repeats already dug; extend with to:Y
  world E730C0BCC3C1EA09 @1.5,55,0.5 violations 0
> excavate {"site":"spiral stair","repeats":6,"torches":false}
  dig 0,56,0 -> ok dug stone
  dig 0,55,0 -> ok dug stone
  dig 0,54,0 -> ok dug stone
  dig 0,55,1 -> ok dug stone
  dig 0,54,1 -> ok dug stone
= ok: spiral stair repeats 4→5 of 10 (down to y54): dug 5, sealed 0, torches 0, left 1 (1 cave under it (drop 5), no block to seal it e.g. 0,53,1)
  world 986A3FD4FDCC86F3 @1.5,55,0.5 violations 0
> excavate {"site":"spiral stair","repeats":3,"torches":false}
  dig 0,53,1 -> ok dug stone
  goto block (0, 54, 0) drop 1 place False break False @0.5,54,0.5 -> ok arrived at (0.5, 54, 0.5)
  place 0,52,1 4 -> ok placed 4
  dig 1,54,1 -> ok dug stone
  dig 1,53,1 -> ok dug stone
  dig 1,52,1 -> ok dug stone
  goto block (0, 53, 1) drop 1 place False break False @0.5,53,1.5 -> ok arrived at (0.5, 53, 1.5)
  dig 1,53,0 -> ok dug stone
  dig 1,52,0 -> ok dug stone
  dig 1,51,0 -> ok dug stone
= ok: spiral stair repeats 5→8 of 8 (down to y51): dug 7, sealed 1, torches 0, left 0
  world 7542C8CDEC25C25D @0.5,53,1.5 violations 0
## strip mine with ores and signs
> strip_mine {"site":"v1 room y20","dir":"east","length":8,"spacing":3,"branch":4,"signs":true}
  dig 4,21,1 -> ok dug stone
  dig 5,21,1 -> ok dug stone
  dig 4,20,1 -> ok dug stone
  dig 5,20,1 -> ok dug stone
  dig 5,20,2 -> ok dug iron_ore
  goto block (2, 20, 1) drop 1 place False break False @2.5,20,1.5 -> ok arrived at (2.5, 20, 1.5)
  dig 6,21,1 -> ok dug stone
  dig 6,20,1 -> ok dug stone
  goto block (3, 20, 1) drop 1 place False break False @3.5,20,1.5 -> ok arrived at (3.5, 20, 1.5)
  dig 7,21,1 -> ok dug stone
  dig 7,21,2 -> ok dug stone
  dig 7,21,0 -> ok dug stone
  dig 7,20,1 -> ok dug stone
  dig 7,20,2 -> ok dug stone
  dig 7,20,0 -> ok dug stone
  goto block (4, 20, 1) drop 1 place False break False @4.5,20,1.5 -> ok arrived at (4.5, 20, 1.5)
  dig 8,21,1 -> ok dug stone
  dig 8,20,1 -> ok dug stone
  goto block (5, 20, 1) drop 1 place False break False @5.5,20,1.5 -> ok arrived at (5.5, 20, 1.5)
  dig 9,21,1 -> ok dug stone
  dig 9,20,1 -> ok dug stone
  goto block (6, 20, 1) drop 1 place False break False @6.5,20,1.5 -> ok arrived at (6.5, 20, 1.5)
  dig 10,21,1 -> ok dug stone
  dig 10,20,1 -> ok dug stone
  goto block (7, 20, 1) drop 1 place False break False @7.5,20,1.5 -> ok arrived at (7.5, 20, 1.5)
  dig 11,21,1 -> ok dug stone
  dig 7,21,3 -> ok dug stone
  dig 7,21,4 -> ok dug stone
  dig 7,21,5 -> ok dug stone
  dig 7,21,-1 -> ok dug stone
  dig 7,21,-2 -> ok dug stone
  dig 7,21,-3 -> ok dug stone
  dig 11,21,2 -> ok dug stone
  dig 11,21,0 -> ok dug stone
  dig 8,21,4 -> ok dug coal_ore
  dig 11,20,1 -> ok dug stone
  dig 7,20,3 -> ok dug stone
  dig 7,20,4 -> ok dug stone
  place 7,20,1 50 Top -> ok placed 50
  dig 7,20,5 -> ok dug stone
  dig 7,20,-1 -> ok dug stone
  dig 7,20,-2 -> ok dug stone
  dig 7,20,-3 -> ok dug stone
  dig 11,20,2 -> ok dug stone
  dig 11,20,0 -> ok dug stone
  goto block (11, 20, 1) drop 1 place False break False @11.5,20,1.5 -> ok arrived at (11.5, 20, 1.5)
  dig 11,21,3 -> ok dug stone
  dig 11,21,4 -> ok dug stone
  dig 11,21,5 -> ok dug stone
  dig 11,21,-1 -> ok dug stone
  dig 11,21,-2 -> ok dug stone
  dig 11,21,-3 -> ok dug stone
  dig 11,20,3 -> ok dug stone
  dig 11,20,4 -> ok dug stone
  dig 11,20,5 -> ok dug stone
  dig 11,20,-1 -> ok dug stone
  dig 11,20,-2 -> ok dug stone
  dig 11,20,-3 -> ok dug stone
  goto block (9, 20, 1) drop 1 place False break False @9.5,20,1.5 -> ok arrived at (9.5, 20, 1.5)
  dig 5,21,2 -> ok dug stone
  goto block (5, 20, 1) drop 1 place False break False @5.5,20,1.5 -> ok arrived at (5.5, 20, 1.5)
  dig 5,20,3 -> ok dug diamond_ore
  goto block (6, 20, 1) drop 1 place False break False @6.5,20,1.5 -> ok arrived at (6.5, 20, 1.5)
  place 9,20,1 50 Top -> ok placed 50
  place 3,21,0 323 South -> ok placed sign
  sign 3,21,0 [iron ore|x1|y20|] -> ok written
  place 3,21,2 323 West -> ok placed sign
  sign 3,21,2 [coal ore|x1|y20|] -> ok written
  goto reach (2, 21, 0) (3.65) drop - place False break False @5.5,20,1.5 -> ok arrived at (5.5, 20, 1.5)
  place 2,21,0 323 South -> ok placed sign
  sign 2,21,0 [diamond ore|x1|y20|] -> ok written
= ok: strip mine east off v1 room y20: main 8, 4 branches: dug 52 (incl. 1 access), sealed 0, torches 2, left 0; ores: iron ore 1, coal ore 1, diamond ore 1; signs: 3 (iron ore, coal ore, diamond ore)
  world 5230D82F74792964 @5.5,20,1.5 violations 0
## seal openings
> excavate {"op":"learn","name":"spiral stair","rule":"column 3 step 0,-1,0 turn -90 pivot 0.5,0.5","at":"0,60,0"}
= ok: remembered 'spiral stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5 from 0,60,0, 12 repeats already dug; extend with to:Y
  world CDC08B82768CCCF8 @0,0,0 violations 0
> seal_openings {"site":"spiral stair"}
  place -1,51,0 4 -> ok placed 4
  goto block (1, 51, 0) drop 1 place False break False @1.5,51,0.5 -> ok arrived at (1.5, 51, 0.5)
  place 0,49,0 4 -> ok placed 4
= ok: sealed spiral stair: 2 openings filled; 14 cobblestone left
  world A383162C9C218278 @1.5,51,0.5 violations 0
## dig a room off the stair, gap 1
> excavate {"op":"learn","name":"spiral stair","rule":"column 3 step 0,-1,0 turn -90 pivot 0.5,0.5","at":"0,60,0"}
= ok: remembered 'spiral stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5 from 0,60,0, 12 repeats already dug; extend with to:Y
  world CDC08B82768CCCF8 @0,0,0 violations 0
> dig_room {"site":"spiral stair","y":52,"gap":1}
  dig 2,53,1 -> ok dug stone
  dig 3,53,1 -> ok dug stone
  dig 3,54,1 -> ok dug stone
  dig 3,54,0 -> ok dug stone
  dig 3,54,2 -> ok dug stone
  dig 4,54,0 -> ok dug stone
  dig 4,54,1 -> ok dug stone
  dig 4,54,2 -> ok dug stone
  dig 5,54,0 -> ok dug stone
  dig 5,54,1 -> ok dug stone
  dig 5,54,2 -> ok dug stone
  dig 3,53,0 -> ok dug stone
  dig 3,53,2 -> ok dug stone
  dig 4,53,0 -> ok dug stone
  dig 4,53,1 -> ok dug stone
  dig 4,53,2 -> ok dug stone
  dig 5,53,0 -> ok dug stone
  dig 5,53,1 -> ok dug stone
  dig 5,53,2 -> ok dug stone
  dig 2,52,1 -> ok dug stone
  dig 3,52,0 -> ok dug stone
  dig 3,52,1 -> ok dug stone
  dig 3,52,2 -> ok dug stone
  dig 4,52,0 -> ok dug stone
  dig 4,52,1 -> ok dug stone
  dig 4,52,2 -> ok dug stone
  dig 5,52,0 -> ok dug stone
  dig 5,52,1 -> ok dug stone
  dig 5,52,2 -> ok dug stone
  goto block (2, 52, 1) drop 1 place False break False @2.5,52,1.5 -> ok arrived at (2.5, 52, 1.5)
  dig 4,54,-1 -> ok dug stone
  dig 5,54,-1 -> ok dug stone
  dig 6,54,-1 -> ok dug stone
  place 2,52,1 50 Top -> ok placed 50
  dig 6,54,0 -> ok dug stone
  dig 6,54,1 -> ok dug stone
  dig 6,54,2 -> ok dug stone
  dig 4,53,-1 -> ok dug stone
  dig 5,53,-1 -> ok dug stone
  dig 6,53,-1 -> ok dug stone
  dig 6,53,0 -> ok dug stone
  dig 6,53,1 -> ok dug stone
  dig 6,53,2 -> ok dug stone
  goto block (3, 52, 1) drop 1 place False break False @3.5,52,1.5 -> ok arrived at (3.5, 52, 1.5)
  dig 3,54,-1 -> ok dug stone
  dig 3,53,-1 -> ok dug stone
  dig 3,52,-1 -> ok dug stone
  dig 4,52,-1 -> ok dug stone
  dig 5,52,-1 -> ok dug stone
  dig 6,52,-1 -> ok dug stone
  dig 6,52,0 -> ok dug stone
  dig 6,52,1 -> ok dug stone
  dig 6,52,2 -> ok dug stone
= ok: room 4×4×3 x3..6 z-1..2 y52..54, east of the landing 1,52,1 (door 1×2 through the wall at 2,52,1): dug 50, sealed 0, torches 1, left 0
  world A2D8C4C67D8A2F78 @3.5,52,1.5 violations 0
## dig a room off the stair, gap 0
> excavate {"op":"learn","name":"spiral stair","rule":"column 3 step 0,-1,0 turn -90 pivot 0.5,0.5","at":"0,60,0"}
= ok: remembered 'spiral stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5 from 0,60,0, 12 repeats already dug; extend with to:Y
  world CDC08B82768CCCF8 @0,0,0 violations 0
> dig_room {"site":"spiral stair","y":52,"gap":0}
  dig 2,54,1 -> ok dug stone
  dig 2,54,0 -> ok dug stone
  dig 2,54,2 -> ok dug stone
  dig 3,54,0 -> ok dug stone
  dig 3,54,1 -> ok dug stone
  dig 3,54,2 -> ok dug stone
  dig 2,53,0 -> ok dug stone
  dig 2,54,-1 -> ok dug stone
  dig 3,54,-1 -> ok dug stone
  dig 2,53,-1 -> ok dug stone
  dig 2,53,1 -> ok dug stone
  dig 4,54,-1 -> ok dug stone
  dig 4,54,0 -> ok dug stone
  dig 4,54,1 -> ok dug stone
  dig 4,54,2 -> ok dug stone
  dig 5,54,0 -> ok dug stone
  dig 5,54,1 -> ok dug stone
  dig 5,54,2 -> ok dug stone
  dig 2,53,2 -> ok dug stone
  dig 3,53,-1 -> ok dug stone
  dig 3,53,0 -> ok dug stone
  dig 5,54,-1 -> ok dug stone
  dig 3,53,1 -> ok dug stone
  dig 3,53,2 -> ok dug stone
  dig 4,53,-1 -> ok dug stone
  dig 4,53,0 -> ok dug stone
  dig 4,53,1 -> ok dug stone
  dig 4,53,2 -> ok dug stone
  dig 5,53,-1 -> ok dug stone
  dig 5,53,0 -> ok dug stone
  dig 5,53,1 -> ok dug stone
  dig 5,53,2 -> ok dug stone
  place 2,53,1 50 Top -> ok placed 50
  dig 2,52,-1 -> ok dug stone
  dig 2,52,0 -> ok dug stone
  dig 2,52,1 -> ok dug stone
  dig 2,52,2 -> ok dug stone
  dig 3,52,-1 -> ok dug stone
  dig 3,52,0 -> ok dug stone
  dig 3,52,1 -> ok dug stone
  dig 3,52,2 -> ok dug stone
  dig 4,52,-1 -> ok dug stone
  dig 4,52,0 -> ok dug stone
  dig 4,52,1 -> ok dug stone
  dig 4,52,2 -> ok dug stone
  dig 5,52,0 -> ok dug stone
  dig 5,52,1 -> ok dug stone
  dig 5,52,2 -> ok dug stone
  goto block (2, 52, 1) drop 1 place False break False @2.5,52,1.5 -> ok arrived at (2.5, 52, 1.5)
  dig 5,52,-1 -> ok dug stone
= ok: room 4×4×3 x2..5 z-1..2 y52..54, east of the landing 1,52,1 (open onto the stair): dug 48, sealed 0, torches 1, left 0
  world 194F7B4123B76DC3 @2.5,52,1.5 violations 0
## falling gravel and lava behind a wall
> excavate {"box":"1,30,0,5,31,0"}
  dig 1,31,0 -> ok dug stone
  dig 2,31,0 -> ok dug stone
  dig 3,31,0 -> ok dug stone
  dig 3,31,0 -> ok dug gravel
  dig 3,31,0 -> ok dug gravel
  place 3,32,0 4 -> ok placed 4
  dig 4,31,0 -> ok dug stone
  dig 1,30,0 -> ok dug stone
  dig 2,30,0 -> ok dug stone
  dig 3,30,0 -> ok dug stone
  dig 4,30,0 -> ok dug stone
  goto block (1, 30, 0) drop 1 place False break False @1.5,30,0.5 -> ok arrived at (1.5, 30, 0.5)
  dig 5,31,0 -> ok dug stone
  dig 5,30,0 -> FAIL won't dig stone: lava next to it would flow out
  place 1,30,0 50 Top -> ok placed 50
= ok: dug box 5×1×2 x1..5 y30..31 z0..0: dug 9, sealed 1, torches 1, left 1 (1 lava behind it e.g. 5,30,0) (+2 gravel)
  world 8AF7F83684FDD891 @1.5,30,0.5 violations 0
## a room beside an open stair, no block to seal, ores on, protected cell
> excavate {"box":"2,52,0,4,54,2","ores":true,"torches":false}
  dig 2,54,1 -> ok dug stone
  dig 2,54,0 -> ok dug stone
  dig 2,54,2 -> ok dug stone
  dig 3,54,0 -> ok dug stone
  dig 3,54,1 -> ok dug stone
  dig 3,54,2 -> ok dug stone
  dig 2,53,0 -> ok dug stone
  dig 2,53,1 -> ok dug stone
  dig 4,54,0 -> ok dug stone
  dig 4,54,1 -> ok dug stone
  dig 4,54,2 -> FAIL protected
  dig 2,53,2 -> ok dug stone
  dig 3,53,0 -> ok dug stone
  dig 3,53,1 -> ok dug stone
  dig 3,53,2 -> ok dug stone
  dig 4,53,0 -> ok dug stone
  dig 4,53,1 -> ok dug stone
  dig 4,53,2 -> ok dug stone
  dig 2,52,0 -> ok dug stone
  dig 2,52,1 -> ok dug stone
  dig 2,52,2 -> ok dug stone
  dig 3,52,0 -> ok dug stone
  dig 3,52,1 -> ok dug stone
  dig 3,52,2 -> ok dug stone
  dig 4,52,0 -> ok dug stone
  dig 4,52,2 -> ok dug stone
= ok: dug box 3×3×3 x2..4 y52..54 z0..2: dug 25, sealed 0, torches 0, left 1 (1 protected e.g. 4,54,2); 1 opening not sealed e.g. 4,51,1
  world A4E32A27BE3703B5 @1.5,52,1.5 violations 0
> excavate {"box":"2,52,0,4,54,2","ores":true}
  dig 4,54,2 -> FAIL protected
  place 4,51,1 4 -> ok placed 4
= failed: dug box 3×3×3 x2..4 y52..54 z0..2: dug 0, sealed 1, torches 0, left 1 (1 protected e.g. 4,54,2)
  world F7252E33309F29FE @1.5,52,1.5 violations 0
"""";

    public const string Excavation1122 = """"
## room from a pocket
> excavate {"box":"3,11,-1,6,13,2"}
  dig 1,12,0 -> ok dug stone
  dig 2,12,0 -> ok dug stone
  dig 3,12,0 -> ok dug stone
  dig 3,13,0 -> ok dug stone
  dig 4,13,0 -> ok dug stone
  dig 3,12,-1 -> ok dug stone
  dig 3,13,-1 -> ok dug stone
  dig 3,12,1 -> ok dug stone
  dig 4,12,-1 -> ok dug stone
  dig 4,12,0 -> ok dug stone
  dig 4,12,1 -> ok dug stone
  dig 3,11,0 -> ok dug stone
  dig 3,11,-1 -> ok dug stone
  dig 3,11,1 -> ok dug stone
  dig 4,11,0 -> ok dug stone
  dig 4,11,-1 -> ok dug stone
  dig 4,11,1 -> ok dug stone
  dig 1,11,0 -> ok dug stone
  goto block (1, 11, 0) drop 1 place False break False @1.5,11,0.5 -> ok arrived at (1.5, 11, 0.5)
  dig 3,13,1 -> ok dug stone
  dig 4,13,-1 -> ok dug stone
  dig 4,13,1 -> ok dug stone
  dig 5,13,-1 -> ok dug stone
  dig 5,13,0 -> ok dug stone
  dig 5,13,1 -> ok dug stone
  dig 5,12,-1 -> ok dug stone
  dig 5,12,0 -> ok dug stone
  dig 5,12,1 -> ok dug stone
  dig 5,11,-1 -> ok dug stone
  dig 5,11,0 -> ok dug stone
  dig 5,11,1 -> ok dug stone
  dig 2,11,0 -> ok dug stone
  goto block (2, 11, 0) drop 1 place False break False @2.5,11,0.5 -> ok arrived at (2.5, 11, 0.5)
  dig 4,13,2 -> ok dug stone
  place 2,11,0 50 Top -> ok placed 50
  dig 5,13,2 -> ok dug stone
  dig 6,13,-1 -> ok dug stone
  dig 6,13,0 -> ok dug stone
  dig 6,13,1 -> ok dug stone
  dig 6,13,2 -> ok dug stone
  dig 4,12,2 -> ok dug stone
  dig 5,12,2 -> ok dug stone
  dig 6,12,-1 -> ok dug stone
  dig 6,12,0 -> ok dug stone
  dig 6,12,1 -> ok dug stone
  dig 6,12,2 -> ok dug stone
  goto block (3, 11, 0) drop 1 place False break False @3.5,11,0.5 -> ok arrived at (3.5, 11, 0.5)
  dig 3,13,2 -> ok dug stone
  dig 3,12,2 -> ok dug stone
  dig 3,11,2 -> ok dug stone
  dig 4,11,2 -> ok dug stone
  dig 5,11,2 -> ok dug stone
  dig 6,11,-1 -> ok dug stone
  dig 6,11,0 -> ok dug stone
  dig 6,11,1 -> ok dug stone
  dig 6,11,2 -> ok dug stone
= ok: dug box 4×4×3 x3..6 y11..13 z-1..2: dug 52 (incl. 4 access), sealed 0, torches 1, left 0
  world B9FDD600FD6D585B @3.5,11,0.5 violations 0
## spiral stair learned and extended
> excavate {"op":"learn","name":"spiral stair","box":"-1,20,-1,2,42,2"}
= ok: learned 'spiral stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5: 8 repeats of 3 cells from 0,40,0; extend with to:Y, or at:x,y,z for a new one
  world BF162A45BB1CA562 @1.5,31,0.5 violations 0
> excavate {"site":"spiral stair","repeats":12}
  dig 0,32,0 -> ok dug stone
  dig 0,31,0 -> ok dug stone
  dig 0,30,0 -> ok dug stone
  dig 0,31,1 -> ok dug stone
  dig 0,30,1 -> ok dug stone
  dig 0,29,1 -> ok dug stone
  goto block (0, 30, 0) drop 1 place False break False @0.5,30,0.5 -> ok arrived at (0.5, 30, 0.5)
  dig 1,30,1 -> ok dug stone
  dig 1,29,1 -> ok dug stone
  dig 1,28,1 -> ok dug stone
  goto block (0, 29, 1) drop 1 place False break False @0.5,29,1.5 -> ok arrived at (0.5, 29, 1.5)
  dig 1,29,0 -> ok dug stone
  dig 1,28,0 -> ok dug stone
  dig 1,27,0 -> ok dug stone
  place 0,29,1 50 Top -> ok placed 50
  goto block (1, 28, 1) drop 1 place False break False @1.5,28,1.5 -> ok arrived at (1.5, 28, 1.5)
  dig 0,28,0 -> ok dug stone
  dig 0,27,0 -> ok dug stone
  dig 0,26,0 -> ok dug stone
  goto block (1, 27, 0) drop 1 place False break False @1.5,27,0.5 -> ok arrived at (1.5, 27, 0.5)
  dig 0,27,1 -> ok dug stone
  dig 0,26,1 -> ok dug stone
  dig 0,25,1 -> ok dug stone
  goto block (0, 26, 0) drop 1 place False break False @0.5,26,0.5 -> ok arrived at (0.5, 26, 0.5)
  dig 1,26,1 -> ok dug stone
  dig 1,25,1 -> ok dug stone
  dig 1,24,1 -> ok dug stone
  goto block (0, 25, 1) drop 1 place False break False @0.5,25,1.5 -> ok arrived at (0.5, 25, 1.5)
  dig 1,25,0 -> ok dug stone
  dig 1,24,0 -> ok dug stone
  dig 1,23,0 -> ok dug stone
  place 0,25,1 50 Top -> ok placed 50
  goto block (1, 24, 1) drop 1 place False break False @1.5,24,1.5 -> ok arrived at (1.5, 24, 1.5)
  dig 0,24,0 -> ok dug stone
  dig 0,23,0 -> ok dug stone
  dig 0,22,0 -> ok dug stone
  goto block (1, 23, 0) drop 1 place False break False @1.5,23,0.5 -> ok arrived at (1.5, 23, 0.5)
  dig 0,23,1 -> ok dug stone
  dig 0,22,1 -> ok dug stone
  dig 0,21,1 -> ok dug stone
  goto block (0, 22, 0) drop 1 place False break False @0.5,22,0.5 -> ok arrived at (0.5, 22, 0.5)
  dig 1,22,1 -> ok dug stone
  dig 1,21,1 -> ok dug stone
  dig 1,20,1 -> ok dug stone
  goto block (0, 21, 1) drop 1 place False break False @0.5,21,1.5 -> ok arrived at (0.5, 21, 1.5)
  dig 1,21,0 -> ok dug stone
  dig 1,20,0 -> ok dug stone
  dig 1,19,0 -> ok dug stone
  place 0,21,1 50 Top -> ok placed 50
= ok: spiral stair repeats 8→20 of 20 (down to y19): dug 36, sealed 0, torches 3, left 0
  world 77C7A47B2852B137 @0.5,21,1.5 violations 0
## a cave under a column
> excavate {"op":"learn","name":"spiral stair","rule":"column 3 step 0,-1,0 turn -90 pivot 0.5,0.5","at":"0,60,0"}
= ok: remembered 'spiral stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5 from 0,60,0, 4 repeats already dug; extend with to:Y
  world C513930AB9C4E92A @1.5,55,0.5 violations 0
> excavate {"site":"spiral stair","repeats":6,"torches":false}
  dig 0,56,0 -> ok dug stone
  dig 0,55,0 -> ok dug stone
  dig 0,54,0 -> ok dug stone
  dig 0,55,1 -> ok dug stone
  dig 0,54,1 -> ok dug stone
= ok: spiral stair repeats 4→5 of 10 (down to y54): dug 5, sealed 0, torches 0, left 1 (1 cave under it (drop 5), no block to seal it e.g. 0,53,1)
  world 93ACA27663C061A0 @1.5,55,0.5 violations 0
> excavate {"site":"spiral stair","repeats":3,"torches":false}
  dig 0,53,1 -> ok dug stone
  goto block (0, 54, 0) drop 1 place False break False @0.5,54,0.5 -> ok arrived at (0.5, 54, 0.5)
  place 0,52,1 4 -> ok placed 4
  dig 1,54,1 -> ok dug stone
  dig 1,53,1 -> ok dug stone
  dig 1,52,1 -> ok dug stone
  goto block (0, 53, 1) drop 1 place False break False @0.5,53,1.5 -> ok arrived at (0.5, 53, 1.5)
  dig 1,53,0 -> ok dug stone
  dig 1,52,0 -> ok dug stone
  dig 1,51,0 -> ok dug stone
= ok: spiral stair repeats 5→8 of 8 (down to y51): dug 7, sealed 1, torches 0, left 0
  world C956485726C9B0A5 @0.5,53,1.5 violations 0
## strip mine with ores and signs
> strip_mine {"site":"v1 room y12","dir":"east","length":8,"spacing":3,"branch":4,"signs":true}
  dig 4,13,1 -> ok dug stone
  dig 5,13,1 -> ok dug stone
  dig 4,12,1 -> ok dug stone
  dig 5,12,1 -> ok dug stone
  dig 5,12,2 -> ok dug iron_ore
  goto block (2, 12, 1) drop 1 place False break False @2.5,12,1.5 -> ok arrived at (2.5, 12, 1.5)
  dig 6,13,1 -> ok dug stone
  dig 6,12,1 -> ok dug stone
  goto block (3, 12, 1) drop 1 place False break False @3.5,12,1.5 -> ok arrived at (3.5, 12, 1.5)
  dig 7,13,1 -> ok dug stone
  dig 7,13,2 -> ok dug stone
  dig 7,13,0 -> ok dug stone
  dig 7,12,1 -> ok dug stone
  dig 7,12,2 -> ok dug stone
  dig 7,12,0 -> ok dug stone
  goto block (4, 12, 1) drop 1 place False break False @4.5,12,1.5 -> ok arrived at (4.5, 12, 1.5)
  dig 8,13,1 -> ok dug stone
  dig 8,12,1 -> ok dug stone
  goto block (5, 12, 1) drop 1 place False break False @5.5,12,1.5 -> ok arrived at (5.5, 12, 1.5)
  dig 9,13,1 -> ok dug stone
  dig 9,12,1 -> ok dug stone
  goto block (6, 12, 1) drop 1 place False break False @6.5,12,1.5 -> ok arrived at (6.5, 12, 1.5)
  dig 10,13,1 -> ok dug stone
  dig 10,12,1 -> ok dug stone
  goto block (7, 12, 1) drop 1 place False break False @7.5,12,1.5 -> ok arrived at (7.5, 12, 1.5)
  dig 11,13,1 -> ok dug stone
  dig 7,13,3 -> ok dug stone
  dig 7,13,4 -> ok dug stone
  dig 7,13,5 -> ok dug stone
  dig 7,13,-1 -> ok dug stone
  dig 7,13,-2 -> ok dug stone
  dig 7,13,-3 -> ok dug stone
  dig 11,13,2 -> ok dug stone
  dig 11,13,0 -> ok dug stone
  dig 8,13,4 -> ok dug coal_ore
  dig 11,12,1 -> ok dug stone
  dig 7,12,3 -> ok dug stone
  dig 7,12,4 -> ok dug stone
  place 7,12,1 50 Top -> ok placed 50
  dig 7,12,5 -> ok dug stone
  dig 7,12,-1 -> ok dug stone
  dig 7,12,-2 -> ok dug stone
  dig 7,12,-3 -> ok dug stone
  dig 11,12,2 -> ok dug stone
  dig 11,12,0 -> ok dug stone
  goto block (11, 12, 1) drop 1 place False break False @11.5,12,1.5 -> ok arrived at (11.5, 12, 1.5)
  dig 11,13,3 -> ok dug stone
  dig 11,13,4 -> ok dug stone
  dig 11,13,5 -> ok dug stone
  dig 11,13,-1 -> ok dug stone
  dig 11,13,-2 -> ok dug stone
  dig 11,13,-3 -> ok dug stone
  dig 11,12,3 -> ok dug stone
  dig 11,12,4 -> ok dug stone
  dig 11,12,5 -> ok dug stone
  dig 11,12,-1 -> ok dug stone
  dig 11,12,-2 -> ok dug stone
  dig 11,12,-3 -> ok dug stone
  goto block (9, 12, 1) drop 1 place False break False @9.5,12,1.5 -> ok arrived at (9.5, 12, 1.5)
  dig 5,13,2 -> ok dug stone
  goto block (5, 12, 1) drop 1 place False break False @5.5,12,1.5 -> ok arrived at (5.5, 12, 1.5)
  dig 5,12,3 -> ok dug diamond_ore
  goto block (6, 12, 1) drop 1 place False break False @6.5,12,1.5 -> ok arrived at (6.5, 12, 1.5)
  place 9,12,1 50 Top -> ok placed 50
  place 3,13,0 323 South -> ok placed sign
  sign 3,13,0 [iron ore|x1|y12|] -> ok written
  place 3,13,2 323 West -> ok placed sign
  sign 3,13,2 [coal ore|x1|y12|] -> ok written
  goto reach (2, 13, 0) (3.65) drop - place False break False @5.5,12,1.5 -> ok arrived at (5.5, 12, 1.5)
  place 2,13,0 323 South -> ok placed sign
  sign 2,13,0 [diamond ore|x1|y12|] -> ok written
= ok: strip mine east off v1 room y12: main 8, 4 branches: dug 52 (incl. 1 access), sealed 0, torches 2, left 0; ores: iron ore 1, coal ore 1, diamond ore 1; signs: 3 (iron ore, coal ore, diamond ore)
  world 1AF35C9F73527975 @5.5,12,1.5 violations 0
"""";

    public const string ExcavationServerBeta = """"
> excavate {"box":"10,60,7,12,61,9","torches":false}
  dig 9,61,8 -> ok dug stone at 9,61,8 (8 ticks, iron pickaxe)
  dig 10,61,8 -> ok dug stone at 10,61,8 (8 ticks, iron pickaxe)
  dig 10,61,7 -> ok dug stone at 10,61,7 (8 ticks, iron pickaxe)
  dig 10,61,9 -> ok dug stone at 10,61,9 (8 ticks, iron pickaxe)
  dig 11,61,7 -> ok dug stone at 11,61,7 (8 ticks, iron pickaxe)
  dig 11,61,8 -> ok dug stone at 11,61,8 (8 ticks, iron pickaxe)
  dig 11,61,9 -> ok dug stone at 11,61,9 (8 ticks, iron pickaxe)
  dig 12,61,7 -> ok dug stone at 12,61,7 (8 ticks, iron pickaxe)
  dig 12,61,8 -> ok dug stone at 12,61,8 (8 ticks, iron pickaxe)
  dig 12,61,9 -> ok dug stone at 12,61,9 (8 ticks, iron pickaxe)
  dig 10,60,7 -> ok dug stone at 10,60,7 (8 ticks, iron pickaxe)
  dig 10,60,8 -> ok dug stone at 10,60,8 (8 ticks, iron pickaxe)
  dig 10,60,9 -> ok dug stone at 10,60,9 (8 ticks, iron pickaxe)
  dig 11,60,7 -> ok dug stone at 11,60,7 (8 ticks, iron pickaxe)
  dig 11,60,8 -> ok dug stone at 11,60,8 (8 ticks, iron pickaxe)
  dig 11,60,9 -> ok dug stone at 11,60,9 (8 ticks, iron pickaxe)
  dig 12,60,7 -> ok dug stone at 12,60,7 (8 ticks, iron pickaxe)
  dig 12,60,8 -> ok dug stone at 12,60,8 (8 ticks, iron pickaxe)
  dig 12,60,9 -> ok dug stone at 12,60,9 (8 ticks, iron pickaxe)
  dig 9,60,8 -> ok dug stone at 9,60,8 (8 ticks, iron pickaxe)
= ok: dug box 3×3×2 x10..12 y60..61 z7..9: dug 20 (incl. 2 access), sealed 0, torches 0, left 0
  server D341C2D142626207
> excavate {"op":"learn","name":"stair","rule":"column 3 step 0,-1,0 turn -90 pivot 0.5,0.5","at":"8,62,8"}
= ok: remembered 'stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5 from 8,62,8, 2 repeats already dug; extend with to:Y
> excavate {"site":"stair","repeats":4,"torches":false}
  dig 9,60,9 -> ok dug stone at 9,60,9 (8 ticks, iron pickaxe)
  dig 9,59,9 -> ok dug stone at 9,59,9 (8 ticks, iron pickaxe)
  dig 9,58,9 -> ok dug stone at 9,58,9 (8 ticks, iron pickaxe)
  dig 9,59,8 -> ok dug stone at 9,59,8 (8 ticks, iron pickaxe)
  dig 9,58,8 -> ok dug stone at 9,58,8 (8 ticks, iron pickaxe)
  dig 9,57,8 -> ok dug stone at 9,57,8 (8 ticks, iron pickaxe)
  goto block (9, 58, 9) drop 1 place False break False -> ok arrived
  dig 8,58,8 -> ok dug stone at 8,58,8 (8 ticks, iron pickaxe)
  dig 8,57,8 -> ok dug stone at 8,57,8 (8 ticks, iron pickaxe)
  dig 8,56,8 -> ok dug stone at 8,56,8 (8 ticks, iron pickaxe)
  goto block (9, 57, 8) drop 1 place False break False -> ok arrived
  dig 8,57,9 -> ok dug stone at 8,57,9 (8 ticks, iron pickaxe)
  dig 8,56,9 -> ok dug stone at 8,56,9 (8 ticks, iron pickaxe)
  dig 8,55,9 -> ok dug stone at 8,55,9 (8 ticks, iron pickaxe)
= ok: stair repeats 2→6 of 6 (down to y55): dug 12, sealed 0, torches 0, left 0
  server 004032591DAC612A
"""";

    public const string ExcavationServer1122 = """"
> excavate {"box":"10,60,7,12,61,9","torches":false}
  dig 9,61,8 -> ok dug stone at 9,61,8 (8 ticks, iron pickaxe)
  dig 10,61,8 -> ok dug stone at 10,61,8 (8 ticks, iron pickaxe)
  dig 10,61,7 -> ok dug stone at 10,61,7 (8 ticks, iron pickaxe)
  dig 10,61,9 -> ok dug stone at 10,61,9 (8 ticks, iron pickaxe)
  dig 11,61,7 -> ok dug stone at 11,61,7 (8 ticks, iron pickaxe)
  dig 11,61,8 -> ok dug stone at 11,61,8 (8 ticks, iron pickaxe)
  dig 11,61,9 -> ok dug stone at 11,61,9 (8 ticks, iron pickaxe)
  dig 12,61,7 -> ok dug stone at 12,61,7 (8 ticks, iron pickaxe)
  dig 12,61,8 -> ok dug stone at 12,61,8 (8 ticks, iron pickaxe)
  dig 12,61,9 -> ok dug stone at 12,61,9 (8 ticks, iron pickaxe)
  dig 10,60,7 -> ok dug stone at 10,60,7 (8 ticks, iron pickaxe)
  dig 10,60,8 -> ok dug stone at 10,60,8 (8 ticks, iron pickaxe)
  dig 10,60,9 -> ok dug stone at 10,60,9 (8 ticks, iron pickaxe)
  dig 11,60,7 -> ok dug stone at 11,60,7 (8 ticks, iron pickaxe)
  dig 11,60,8 -> ok dug stone at 11,60,8 (8 ticks, iron pickaxe)
  dig 11,60,9 -> ok dug stone at 11,60,9 (8 ticks, iron pickaxe)
  dig 12,60,7 -> ok dug stone at 12,60,7 (8 ticks, iron pickaxe)
  dig 12,60,8 -> ok dug stone at 12,60,8 (8 ticks, iron pickaxe)
  dig 12,60,9 -> ok dug stone at 12,60,9 (8 ticks, iron pickaxe)
  dig 9,60,8 -> ok dug stone at 9,60,8 (8 ticks, iron pickaxe)
= ok: dug box 3×3×2 x10..12 y60..61 z7..9: dug 20 (incl. 2 access), sealed 0, torches 0, left 0
  server D341C2D142626207
> excavate {"op":"learn","name":"stair","rule":"column 3 step 0,-1,0 turn -90 pivot 0.5,0.5","at":"8,62,8"}
= ok: remembered 'stair': column 3 step 0,-1,0 turn -90 pivot 0.5,0.5 from 8,62,8, 2 repeats already dug; extend with to:Y
> excavate {"site":"stair","repeats":4,"torches":false}
  dig 9,60,9 -> ok dug stone at 9,60,9 (8 ticks, iron pickaxe)
  dig 9,59,9 -> ok dug stone at 9,59,9 (8 ticks, iron pickaxe)
  dig 9,58,9 -> ok dug stone at 9,58,9 (8 ticks, iron pickaxe)
  dig 9,59,8 -> ok dug stone at 9,59,8 (8 ticks, iron pickaxe)
  dig 9,58,8 -> ok dug stone at 9,58,8 (8 ticks, iron pickaxe)
  dig 9,57,8 -> ok dug stone at 9,57,8 (8 ticks, iron pickaxe)
  goto block (9, 58, 9) drop 1 place False break False -> ok arrived
  dig 8,58,8 -> ok dug stone at 8,58,8 (8 ticks, iron pickaxe)
  dig 8,57,8 -> ok dug stone at 8,57,8 (8 ticks, iron pickaxe)
  dig 8,56,8 -> ok dug stone at 8,56,8 (8 ticks, iron pickaxe)
  goto block (9, 57, 8) drop 1 place False break False -> ok arrived
  dig 8,57,9 -> ok dug stone at 8,57,9 (8 ticks, iron pickaxe)
  dig 8,56,9 -> ok dug stone at 8,56,9 (8 ticks, iron pickaxe)
  dig 8,55,9 -> ok dug stone at 8,55,9 (8 ticks, iron pickaxe)
= ok: stair repeats 2→6 of 6 (down to y55): dug 12, sealed 0, torches 0, left 0
  server 004032591DAC612A
"""";

    public const string Fields = """"
## standard x
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  373 actions 352A1DE304CDC1CE
= ok: field 9x10 x-axis at -10,64,-10: trench 10 dug, 6 water poured, 80 tilled, 42 fence+gate, 7 torches (trenches at 4); plant_seeds next
  world BA0010E4E0427420 @-4.5,65,-11.5 violations 0
## along z
> lay_field {"corner":"-10,64,-10","width":9,"length":8,"axis":"z","level":false,"fence":true}
  295 actions 0B1AD7DC4956CAB7
= ok: field 9x8 z-axis at -10,64,-10: trench 8 dug, 5 water poured, 64 tilled, 38 fence+gate, 6 torches (trenches at 4); plant_seeds next
  world EAB5EED700D3FC9D @-11.5,65,-2.5 violations 0
## 17 wide
> lay_field {"corner":"-10,64,-10","width":17,"length":6,"axis":"x","level":false,"fence":true}
  420 actions 930B469979E959F0
= ok: field 17x6 x-axis at -10,64,-10: trench 12 dug, 8 water poured, 90 tilled, 50 fence+gate, 7 torches (trenches at 4,12); plant_seeds next
  world FBE138CC63B9DBF0 @-2.5,65,-8.5 violations 0
## bumpy without level
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  0 actions E3B0C44298FC1C14
= failed: can't lay 9x10 x-axis at -10,64,-10: 9 of 90 cells aren't at y64 (worst -6,64,-8 2 above, -6,64,-7 2 above, -6,64,-6 2 above); level:true flattens it
  world 842932340A722CAE @3.5,65,3.5 violations 0
## bumpy, level
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":true,"fence":true}
  406 actions 3F2FF55A64390DB3
= ok: field 9x10 x-axis at -10,64,-10: levelled 18 dug/8 filled, trench 10 dug, 6 water poured, 80 tilled, 42 fence+gate, 7 torches (trenches at 4); plant_seeds next
  world EB0F01C3CB2ABF61 @-4.5,65,-11.5 violations 0
## flat, level
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":true,"fence":true}
  373 actions 352A1DE304CDC1CE
= ok: field 9x10 x-axis at -10,64,-10: trench 10 dug, 6 water poured, 80 tilled, 42 fence+gate, 7 torches (trenches at 4); plant_seeds next
  world BA0010E4E0427420 @-4.5,65,-11.5 violations 0
## steep / wet / lava
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":true,"fence":true}
  0 actions E3B0C44298FC1C14
= failed: can't lay 9x10 x-axis at -10,64,-10: -5,64,-5 is 6 above y64 (more than 4)
  world 962829D3CF4743A1 @3.5,65,3.5 violations 0
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":true,"fence":true}
  0 actions E3B0C44298FC1C14
= failed: can't lay 9x10 x-axis at -10,64,-10: water at -5,64,-8; water at -5,64,-8 is inside the field
  world 7D7739201F7AE0BD @3.5,65,3.5 violations 0
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  0 actions E3B0C44298FC1C14
= failed: can't lay 9x10 x-axis at -10,64,-10: lava at -5,64,-8; lava at -5,64,-8 is inside the field
  world 07AE05A22CE0A719 @3.5,65,3.5 violations 0
## protected
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":true,"fence":true}
  0 actions E3B0C44298FC1C14
= failed: can't lay 9x10 x-axis at -10,64,-10: -5,66,-5 is protected (a protected block); say 'you can break it' if you want it gone
  world 5D53D7C6515B2F47 @3.5,65,3.5 violations 0
## dip under the trench end
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  378 actions 9212A916971CCEC9
= ok: field 9x10 x-axis at -10,64,-10: levelled 0 dug/3 filled, trench 10 dug, 6 water poured, 80 tilled, 42 fence+gate, 7 torches (trenches at 4); plant_seeds next
  world 37759F6DD45E28E1 @-4.5,65,-11.5 violations 0
## hole under a trench cell
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  373 actions 647295C54A4C435B
= ok: field 9x10 x-axis at -10,64,-10: trench 10 dug, 6 water poured, 80 tilled, 42 fence+gate, 7 torches (trenches at 4); plant_seeds next
  world FE15B0FDBCF20313 @-4.5,65,-11.5 violations 0
## stray water
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  57 actions 72A020129A6C0AA6
= failed: field 9x10 x-axis at -10,64,-10 stopped: stray water outside the trench at -12,64,-6 (1 cells): scoop it before tilling (done so far: trench 10 dug, 6 water poured); run lay_field again to continue
  world AD07DC4EE6F80A81 @-3.5,65,-3.5 violations 0
## one bucket
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  0 actions E3B0C44298FC1C14
= failed: 9x10 x-axis at -10,64,-10: needs 2 water buckets, or a bucket plus an infinite water source within 48 (infinite_water makes one); have 1 water, 0 empty
  world 3B4672F5C90D3CDD @3.5,65,3.5 violations 0
## one bucket and a pond
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  379 actions E8F391B30C9C2B38
= ok: field 9x10 x-axis at -10,64,-10: trench 10 dug, 6 water poured, 80 tilled, 42 fence+gate, 7 torches (trenches at 4); plant_seeds next
  world 2C4729D3F71535DA @-4.5,65,-11.5 violations 0
## too few fences, then without
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  0 actions E3B0C44298FC1C14
= failed: 9x10 x-axis at -10,64,-10: short of 36 more fence, 1 more fence gate, 7 more torch (fence:false lays the field without them)
  world 553595E6109018EE @3.5,65,3.5 violations 0
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":false}
  280 actions A23A7849B364EDA9
= ok: field 9x10 x-axis at -10,64,-10: trench 10 dug, 6 water poured, 80 tilled (trenches at 4); plant_seeds next
  world 97FEB0A058FECBE8 @-7.5,65,-0.5 violations 0
## rerun, half built
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  373 actions 352A1DE304CDC1CE
= ok: field 9x10 x-axis at -10,64,-10: trench 10 dug, 6 water poured, 80 tilled, 42 fence+gate, 7 torches (trenches at 4); plant_seeds next
  world BA0010E4E0427420 @-4.5,65,-11.5 violations 0
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  2 actions FEFD93F3C420EF94
= ok: field 9x10 x-axis at -10,64,-10: nothing new (trenches at 4); plant_seeds next
  world BA0010E4E0427420 @-4.5,65,-11.5 violations 0
> lay_field {"corner":"-10,64,-10","width":9,"length":10,"axis":"x","level":false,"fence":true}
  264 actions 8C4320E579B15486
= ok: field 9x10 x-axis at -10,64,-10: trench 6 dug, 4 water poured, 50 tilled, 42 fence+gate, 7 torches (trenches at 4); plant_seeds next
  world D1A5E55558640527 @-4.5,65,-11.5 violations 0
"""";

    public const string Floors = """"
## grow a rectangle
> expand_island {"area":"-2,-2,6,2"}
  goto reach (3, 63, 0) (3.65) drop - place False break False @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  place 3,63,0 4 -> ok placed 4
  place 3,63,-1 4 -> ok placed 4
  place 3,63,-2 4 -> ok placed 4
  place 3,63,1 4 -> ok placed 4
  place 3,63,2 4 -> ok placed 4
  goto reach (4, 63, 2) (3.65) drop - place False break False @2.5,64,0.5 -> ok arrived at (2.5, 64, 0.5)
  place 4,63,2 4 -> ok placed 4
  place 4,63,1 4 -> ok placed 4
  place 4,63,0 4 -> ok placed 4
  place 4,63,-1 4 -> ok placed 4
  place 4,63,-2 4 -> ok placed 4
  goto reach (5, 63, -2) (3.65) drop - place False break False @3.5,64,0.5 -> ok arrived at (3.5, 64, 0.5)
  place 5,63,-2 4 -> ok placed 4
  place 5,63,-1 4 -> ok placed 4
  place 5,63,0 4 -> ok placed 4
  place 5,63,1 4 -> ok placed 4
  place 5,63,2 4 -> ok placed 4
  goto reach (6, 63, 2) (3.65) drop - place False break False @4.5,64,0.5 -> ok arrived at (4.5, 64, 0.5)
  place 6,63,2 4 -> ok placed 4
  place 6,63,1 4 -> ok placed 4
  place 6,63,0 4 -> ok placed 4
  place 6,63,-1 4 -> ok placed 4
  place 6,63,-2 4 -> ok placed 4
= ok: filled 20/20 cells of -2..6,-2..2 at y63; the floor is complete; 44 cobblestone on me
  world D4FC0C7F3B660540 @4.5,64,0.5 violations 0
## a second run picks up
> expand_island {"area":"3,-2,6,2","y":63}
  goto reach (3, 63, 0) (3.65) drop - place False break False @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  place 3,63,0 4 -> ok placed 4
  place 3,63,-1 4 -> ok placed 4
  place 3,63,-2 4 -> ok placed 4
  place 3,63,1 4 -> ok placed 4
  place 3,63,2 4 -> ok placed 4
  goto reach (4, 63, 2) (3.65) drop - place False break False @2.5,64,0.5 -> ok arrived at (2.5, 64, 0.5)
  place 4,63,2 4 -> ok placed 4
  place 4,63,1 4 -> ok placed 4
= ok: filled 7/20 cells of 3..6,-2..2 at y63; 13 left (out of cobblestone); 0 cobblestone on me
  world 8FAF33D088875F3A @2.5,64,0.5 violations 0
> expand_island {"area":"3,-2,6,2","y":63}
  place 4,63,0 4 -> ok placed 4
  place 4,63,-1 4 -> ok placed 4
  place 4,63,-2 4 -> ok placed 4
  goto reach (5, 63, 1) (3.65) drop - place False break False @3.5,64,0.5 -> ok arrived at (3.5, 64, 0.5)
  place 5,63,1 4 -> ok placed 4
  place 5,63,2 4 -> ok placed 4
  goto reach (6, 63, 2) (3.65) drop - place False break False @4.5,64,0.5 -> ok arrived at (4.5, 64, 0.5)
  place 6,63,2 4 -> ok placed 4
  place 6,63,1 4 -> ok placed 4
  place 5,63,0 4 -> ok placed 4
  place 5,63,-1 4 -> ok placed 4
  place 5,63,-2 4 -> ok placed 4
  place 6,63,-2 4 -> ok placed 4
  place 6,63,-1 4 -> ok placed 4
  place 6,63,0 4 -> ok placed 4
= ok: filled 13/13 cells of 3..6,-2..2 at y63; the floor is complete; 17 cobblestone on me
  world 716DC81EA41CA09D @4.5,64,0.5 violations 0
> expand_island {"area":"3,-2,6,2","y":63}
= ok: filled 0/0 cells of 3..6,-2..2 at y63: already solid
  world 716DC81EA41CA09D @4.5,64,0.5 violations 0
## refusals
> expand_island {"area":"10,10,12,12","y":63}
= failed: nothing solid at y63 in or beside 10..12,10..12: no edge to grow the floor from
  world 08122BB431DE5FEA @0.5,64,0.5 violations 0
> expand_island {"area":"1,2,3"}
= failed: area must be x0,z0,x1,z1
  world 08122BB431DE5FEA @0.5,64,0.5 violations 0
> expand_island {"area":"0,0,40,40"}
= failed: area 0..40,0..40 is 1681 cells; at most 1024 per call
  world 08122BB431DE5FEA @0.5,64,0.5 violations 0
> expand_island {"area":"3,-2,6,2"}
= failed: filled 0/20 cells of 3..6,-2..2 at y63; 20 left (zombie within 6: not working at the edge); 64 cobblestone on me
  world 08122BB431DE5FEA @0.5,64,0.5 violations 0
## a design partly covered, obtain hook
> expand_island {"area":"3,-2,6,2"}
  goto reach (3, 63, 0) (3.65) drop - place False break False @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  place 3,63,0 4 -> ok placed 4
  place 3,63,-1 4 -> ok placed 4
  place 3,63,-2 4 -> ok placed 4
  place 3,63,1 4 -> ok placed 4
  place 3,63,2 4 -> ok placed 4
  goto reach (4, 63, 2) (3.65) drop - place False break False @2.5,64,0.5 -> ok arrived at (2.5, 64, 0.5)
  place 4,63,2 4 -> ok placed 4
  place 4,63,1 4 -> ok placed 4
  place 4,63,0 4 -> ok placed 4
  place 4,63,-1 4 -> ok placed 4
  place 4,63,-2 4 -> ok placed 4
  goto reach (5, 63, -2) (3.65) drop - place False break False @3.5,64,0.5 -> ok arrived at (3.5, 64, 0.5)
  place 5,63,-2 4 -> ok placed 4
  place 5,63,-1 4 -> ok placed 4
  place 5,63,0 4 -> ok placed 4
  place 5,63,1 4 -> ok placed 4
  place 5,63,2 4 -> ok placed 4
  goto reach (6, 63, 2) (3.65) drop - place False break False @4.5,64,0.5 -> ok arrived at (4.5, 64, 0.5)
  place 6,63,2 4 -> ok placed 4
  place 6,63,1 4 -> ok placed 4
  place 6,63,0 4 -> ok placed 4
  place 6,63,-1 4 -> ok placed 4
  place 6,63,-2 4 -> ok placed 4
= ok: filled 20/20 cells of 3..6,-2..2 at y63; the floor is complete; x 7..8 of design #4 still uncovered (its area=3,-2,8,2); 44 cobblestone on me
  world D4FC0C7F3B660540 @4.5,64,0.5 violations 0
> expand_island {"area":"3,-2,6,2","y":63}
  goto reach (3, 63, 0) (3.65) drop - place False break False @1.5,64,0.5 -> ok arrived at (1.5, 64, 0.5)
  place 3,63,0 4 -> ok placed 4
  place 3,63,-1 4 -> ok placed 4
  place 3,63,-2 4 -> ok placed 4
  place 3,63,1 4 -> ok placed 4
  place 3,63,2 4 -> ok placed 4
  goto reach (4, 63, 2) (3.65) drop - place False break False @2.5,64,0.5 -> ok arrived at (2.5, 64, 0.5)
  place 4,63,2 4 -> ok placed 4
  place 4,63,1 4 -> ok placed 4
  place 4,63,0 4 -> ok placed 4
  place 4,63,-1 4 -> ok placed 4
  place 4,63,-2 4 -> ok placed 4
  goto reach (5, 63, -2) (3.65) drop - place False break False @3.5,64,0.5 -> ok arrived at (3.5, 64, 0.5)
  place 5,63,-2 4 -> ok placed 4
  place 5,63,-1 4 -> ok placed 4
  place 5,63,0 4 -> ok placed 4
  place 5,63,1 4 -> ok placed 4
  place 5,63,2 4 -> ok placed 4
  goto reach (6, 63, 2) (3.65) drop - place False break False @4.5,64,0.5 -> ok arrived at (4.5, 64, 0.5)
  place 6,63,2 4 -> ok placed 4
  place 6,63,1 4 -> ok placed 4
  place 6,63,0 4 -> ok placed 4
  place 6,63,-1 4 -> ok placed 4
  place 6,63,-2 4 -> ok placed 4
= ok: filled 20/20 cells of 3..6,-2..2 at y63; the floor is complete; 0 cobblestone on me
  world 9ABE73E6B69D57CB @4.5,64,0.5 violations 0
## grow the edge, walls, lines
> expand_island {"dir":"east","width":2}
  place 0,63,-4 4 -> ok placed 4
  place -1,63,-4 4 -> ok placed 4
  place -2,63,-4 4 -> ok placed 4
  place -3,63,-4 4 -> ok placed 4
  place 4,63,0 4 -> ok placed 4
  place 4,63,-1 4 -> ok placed 4
  place 4,63,-2 4 -> ok placed 4
  place 4,63,-3 4 -> ok placed 4
  place -4,63,0 4 -> ok placed 4
  place -4,63,1 4 -> ok placed 4
  place -4,63,2 4 -> ok placed 4
  place -4,63,3 4 -> ok placed 4
  place 0,63,4 4 -> ok placed 4
  place -1,63,4 4 -> ok placed 4
  place -2,63,4 4 -> ok placed 4
  place -3,63,4 4 -> ok placed 4
  place 1,63,4 4 -> ok placed 4
  place 2,63,4 4 -> ok placed 4
  place 3,63,4 4 -> ok placed 4
  place -4,63,-1 4 -> ok placed 4
  place -4,63,-2 4 -> ok placed 4
  place -4,63,-3 4 -> ok placed 4
  place 1,63,-4 4 -> ok placed 4
  place 2,63,-4 4 -> ok placed 4
  place 3,63,-4 4 -> ok placed 4
  place 4,63,1 4 -> ok placed 4
  place 4,63,2 4 -> ok placed 4
  place 4,63,3 4 -> ok placed 4
= ok: expanded island by 28 block(s) at y 63; 36 cobblestone left
  world 2ACCAED652797A5E @0.5,64,0.5 violations 0
> expand_island {}
  place 0,63,-5 4 -> ok placed 4
  place 1,63,-5 4 -> ok placed 4
  place 2,63,-5 4 -> ok placed 4
  place 3,63,-5 4 -> ok placed 4
  place -5,63,0 4 -> ok placed 4
  place -5,63,-1 4 -> ok placed 4
  place -5,63,-2 4 -> ok placed 4
  place -5,63,-3 4 -> ok placed 4
  place 0,63,5 4 -> ok placed 4
  place -1,63,5 4 -> ok placed 4
  place -2,63,5 4 -> ok placed 4
  place -3,63,5 4 -> ok placed 4
  place 5,63,0 4 -> ok placed 4
  place 5,63,-1 4 -> ok placed 4
  place 5,63,-2 4 -> ok placed 4
  place 5,63,-3 4 -> ok placed 4
  place -5,63,1 4 -> ok placed 4
  place -5,63,2 4 -> ok placed 4
  place -5,63,3 4 -> ok placed 4
  place 1,63,5 4 -> ok placed 4
  place 2,63,5 4 -> ok placed 4
  place 3,63,5 4 -> ok placed 4
  place 5,63,1 4 -> ok placed 4
  place 5,63,2 4 -> ok placed 4
  place 5,63,3 4 -> ok placed 4
  place -1,63,-5 4 -> ok placed 4
  place -2,63,-5 4 -> ok placed 4
  place -3,63,-5 4 -> ok placed 4
  place 4,63,-4 4 -> ok placed 4
  place -4,63,-4 4 -> ok placed 4
  place -4,63,4 4 -> ok placed 4
  place 4,63,4 4 -> ok placed 4
= ok: expanded island by 32 block(s) at y 63; 4 cobblestone left
  world EEE87EAA7CCA02E2 @0.5,64,0.5 violations 0
> place_line {"from":"-3,64,-3","to":"3,64,-3"}
  place 0,64,-3 4 -> ok placed 4
  place -1,64,-3 4 -> ok placed 4
  place 1,64,-3 4 -> ok placed 4
  place -2,64,-3 4 -> ok placed 4
= ok: line: placed 4, skipped 3 (out of cobblestone); 0 cobblestone left
  world 4BF8A34194F106CF @0.5,64,0.5 violations 0
> fill_floor {"from":"-2,64,1","to":"0,64,3"}
= failed: no building blocks
  world 4BF8A34194F106CF @0.5,64,0.5 violations 0
> build_wall {"from":"-3,65,3","to":"3,65,3","height":2}
= failed: no building blocks
  world 4BF8A34194F106CF @0.5,64,0.5 violations 0
> pillar_down {"depth":2}
= failed: no safe staircase 2 down from here (void, liquid, sand/gravel above or protected)
  world 4BF8A34194F106CF @0.5,64,0.5 violations 0
"""";

    public const string Light = """"
## light the island
> light_area {"area":"-9,-9,9,9","y":"64"}
  place 0,64,0 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-3, 64, -2) (3.65) drop - place False break False @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  place -3,64,-2 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-6, 64, -2) (3.65) drop - place False break False @-2.5,64,-0.5 -> ok arrived at (-2.5, 64, -0.5)
  place -6,64,-2 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-4, 64, 4) (3.65) drop - place False break False @-2.5,64,1.5 -> ok arrived at (-2.5, 64, 1.5)
  place -4,64,4 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (3, 64, 5) (3.65) drop - place False break False @0.5,64,4.5 -> ok arrived at (0.5, 64, 4.5)
  place 3,64,5 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (3, 64, -5) (3.65) drop - place False break False @2.5,64,-1.5 -> ok arrived at (2.5, 64, -1.5)
  place 3,64,-5 50 Top -> ok placed 50
  wait_until 1500 -> True
= ok: lit area (x -9..9, z -9..9, y 64): placed 6 torches; 0 spawnable cells left (was 215); 58 torches left
  world F126735080CEC228 @2.5,64,-1.5 violations 0
## spawn map
> spawn_map {"area":"-9,-9,9,9","y":"64"}
= ok: area (x -9..9, z -9..9, y 64): 215 spawnable cells after dark, e.g. -7,64,-7 -7,64,-6 -7,64,-5 …; 6 torches would light all
  world 563AD2634A43F70B @0.5,64,0.5 violations 0
> light_area {"area":"-9,-9,9,9","y":"64","report":true}
= ok: area (x -9..9, z -9..9, y 64): 215 spawnable cells after dark, e.g. -7,64,-7 -7,64,-6 -7,64,-5 …; 6 torches would light all
  world 563AD2634A43F70B @0.5,64,0.5 violations 0
> spawn_map {"area":"-9,-9,9,9","y":"64"}
= ok: area (x -9..9, z -9..9, y 64): 215 spawnable cells after dark (0 now), e.g. -7,64,-7 -7,64,-6 -7,64,-5 …; 6 torches would light all
  world 563AD2634A43F70B @0.5,64,0.5 violations 0
## a design's deck
> light_area {"design":"#4"}
  place 0,64,0 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-3, 64, 2) (3.65) drop - place False break False @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  place -3,64,2 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-6, 64, 2) (3.65) drop - place False break False @-2.5,64,1.5 -> ok arrived at (-2.5, 64, 1.5)
  place -6,64,2 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-3, 64, -5) (3.65) drop - place False break False @-2.5,64,-1.5 -> ok arrived at (-2.5, 64, -1.5)
  place -3,64,-5 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (3, 64, -5) (3.65) drop - place False break False @0.5,64,-3.5 -> ok arrived at (0.5, 64, -3.5)
  place 3,64,-5 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (3, 64, 5) (3.65) drop - place False break False @2.5,64,2.5 -> ok arrived at (2.5, 64, 2.5)
  place 3,64,5 50 Top -> ok placed 50
  wait_until 1500 -> True
= ok: lit design #4 floor (x -8..7, z -8..7, y 62..66): placed 6 torches; 0 spawnable cells left (was 211); excluded design #4 decks y71,73 (no DSL roles: its own floors above the base); 58 torches left
  world D66F1344BE1CE307 @2.5,64,2.5 violations 0
> spawn_map {"design":"4","y":"71"}
= ok: design #4 floor (x -8..7, z -8..7, y 71): 0 spawnable cells after dark; excluded design #4 decks y71,73 (no DSL roles: its own floors above the base): 36 dark there
  world D66F1344BE1CE307 @2.5,64,2.5 violations 0
> light_area {"design":"#4","exclude":"none"}
= ok: design #4 floor (x -8..7, z -8..7, y 62..66): no spawnable cells after dark
  world D66F1344BE1CE307 @2.5,64,2.5 violations 0
## obtain hook, none
> light_area {"area":"-9,-9,9,9","y":"64"}
  place 0,64,0 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-3, 64, -2) (3.65) drop - place False break False @-0.5,64,0.5 -> ok arrived at (-0.5, 64, 0.5)
  place -3,64,-2 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-6, 64, -2) (3.65) drop - place False break False @-2.5,64,-0.5 -> ok arrived at (-2.5, 64, -0.5)
  place -6,64,-2 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (-4, 64, 4) (3.65) drop - place False break False @-2.5,64,1.5 -> ok arrived at (-2.5, 64, 1.5)
  place -4,64,4 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (3, 64, 5) (3.65) drop - place False break False @0.5,64,4.5 -> ok arrived at (0.5, 64, 4.5)
  place 3,64,5 50 Top -> ok placed 50
  wait_until 1500 -> True
  goto reach (3, 64, -5) (3.65) drop - place False break False @2.5,64,-1.5 -> ok arrived at (2.5, 64, -1.5)
  place 3,64,-5 50 Top -> ok placed 50
  wait_until 1500 -> True
= ok: lit area (x -9..9, z -9..9, y 64): placed 6 torches; 0 spawnable cells left (was 215); 0 torches left
  world 6C0F7EF4065A3D3E @2.5,64,-1.5 violations 0
> light_area {"area":"-9,-9,9,9","y":"64"}
= failed: lit area (x -9..9, z -9..9, y 64): placed 0 torches; 215 spawnable cells left (was 215), e.g. -7,64,-7 -7,64,-6 …; stopped: out of torches (~6 more needed); 0 torches left
  world 563AD2634A43F70B @0.5,64,0.5 violations 0
> light_area {}
= failed: lit around me (x -12..12, z -12..12, y 60..67): placed 0 torches; 215 spawnable cells left (was 215), e.g. -7,64,-7 -7,64,-6 …; stopped: out of torches (~6 more needed); 0 torches left
  world 563AD2634A43F70B @0.5,64,0.5 violations 0
> light_area {"area":"-9,-9,9"}
= failed: area must be x0,z0,x1,z1 (with y=) or x0,y0,z0,x1,y1,z1
  world 563AD2634A43F70B @0.5,64,0.5 violations 0
"""";

    public const string Storage = """"
## move items, one visit per chest
> move_items {"moves":[{"item":"iron ingot","count":"all","from":"chest@3,64,0","to":"inv"},{"item":"cobblestone","count":64,"from":"inv","to":"chest@-3,64,0"},{"item":"log","count":10,"from":"chest@3,64,0","to":"chest@0,64,4"}]}
  open 3,64,0 -> ok opened
  click 0 shift -> ok clicked
  click 1 -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 1 -> ok clicked
  close -> ok closed
  open -3,64,0 -> ok opened
  click 54 shift -> ok clicked
  close -> ok closed
  open 0,64,4 -> ok opened
  click 27 shift -> ok clicked
  close -> ok closed
= ok: 3 moves, 3 chests, 15 clicks, Ns
  world A44B471973388EC2 @0.5,64,0.5 violations 0
## move items replans
> move_items {"moves":[{"item":"sand","count":5,"from":"chest@3,64,0","to":"inv"}]}
  open 3,64,0 -> ok opened
  click 0 -> ok clicked
  click 27 right -> FAIL click rejected by server (resynced)
  wait 2
  click 0 -> ok clicked
  click 0 -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 0 -> ok clicked
  close -> ok closed
= ok: 1 moves, 1 chest, 10 clicks, Ns, 1 rejected+replanned
  world 0DB1FEF6DA113808 @0.5,64,0.5 violations 0
## move items refusals, labels, hotbar
> move_items {"moves":[{"item":"diamond pickaxe","from":"inv","to":"chest@3,64,0"}]}
= failed: diamond pickaxe is reserved; won't move it out
  world D9A6ED648548CA2B @0.5,64,0.5 violations 0
> move_items {"moves":[{"item":"dirt","from":"inv","to":"Nowhere storage"}]}
= failed: unknown destination 'Nowhere storage'
  world D9A6ED648548CA2B @0.5,64,0.5 violations 0
> move_items {"moves":[{"item":"obsidian","from":"inv","to":"chest@3,64,0"}]}
  open 3,64,0 -> ok opened
  close -> ok closed
= failed: 0/1 moves done, 0 moves, 1 chest, 0 clicks, Ns; some moves couldn't be done (claimed/protected containers or no room)
  world D9A6ED648548CA2B @0.5,64,0.5 violations 0
> move_items {}
= failed: moves required
  world D9A6ED648548CA2B @0.5,64,0.5 violations 0
> move_items {"moves":[{"item":"iron ingot","count":"all","from":"inv","to":"Iron storage"},{"item":"bread","count":"all","from":"inv","to":"hotbar","slot":4}]}
  click 20 -> ok clicked
  click 40 -> ok clicked
  open 3,64,0 -> ok opened
  click 54 shift -> ok clicked
  close -> ok closed
= ok: 2 moves, 1 chest, 3 clicks, Ns
  world C8A34DE599FDF41D @0.5,64,0.5 violations 0
## arrange chest
> arrange_chest {"chest":"3,64,0","layout":"cobblestone*2,dirt"}
  open 3,64,0 -> ok opened
  click 6 -> ok clicked
  click 7 -> ok clicked
  click 5 -> ok clicked
  click 0 -> ok clicked
  click 2 -> ok clicked
  click 7 -> ok clicked
  click 1 -> ok clicked
  close -> ok closed
= ok: chest 3,64,0 arranged (cobblestone*2,dirt): 3 moves, 1 chest, 7 clicks, Ns
  world 33967D6D558565EA @0.5,64,0.5 violations 0
> arrange_chest {"chest":"3,64,0","layout":"by_category"}
  open 3,64,0 -> ok opened
  click 10 -> ok clicked
  click 0 -> ok clicked
  click 20 -> ok clicked
  click 2 -> ok clicked
  click 1 -> ok clicked
  close -> ok closed
= ok: chest 3,64,0 arranged (by_category): 3 moves, 1 chest, 5 clicks, Ns
  world C0BF40A2C0FF2CD3 @0.5,64,0.5 violations 0
> arrange_chest {"chest":"3,64,0","layout":"dirt*12"}
  open 3,64,0 -> ok opened
  close -> ok closed
= failed: chest 3,64,0: layout row 'dirt*12' has 12 cells (max 9); 0 moves, 1 chest, 0 clicks, Ns
  world C0BF40A2C0FF2CD3 @0.5,64,0.5 violations 0
## organize storage
> organize_storage {}
  open -3,64,0 -> ok opened
  click 1 -> ok clicked
  click 0 -> ok clicked
  close -> ok closed
  open 3,64,0 -> ok opened
  close -> ok closed
  open 0,64,4 -> ok opened
  close -> ok closed
  open 3,64,0 -> ok opened
  click 1 shift -> ok clicked
  close -> ok closed
  open -3,64,0 -> ok opened
  click 62 shift -> ok clicked
  close -> ok closed
  open 3,64,0 -> ok opened
  close -> ok closed
  open 0,64,4 -> ok opened
  click 4 -> ok clicked
  click 0 -> ok clicked
  close -> ok closed
= ok: 2 moves, 3 chests, 6 clicks, Ns; wood@-3,64,0, ores@3,64,0, food@0,64,4
  world 556565B5EC4F6173 @0.5,64,0.5 violations 0
> organize_storage {}
  open -3,64,0 -> ok opened
  close -> ok closed
  open 3,64,0 -> ok opened
  close -> ok closed
  open 0,64,4 -> ok opened
  close -> ok closed
  open -3,64,0 -> ok opened
  close -> ok closed
  open 3,64,0 -> ok opened
  close -> ok closed
  open 0,64,4 -> ok opened
  close -> ok closed
= ok: 0 moves, 3 chests, 0 clicks, Ns; wood@-3,64,0, ores@3,64,0, food@0,64,4
  world 556565B5EC4F6173 @0.5,64,0.5 violations 0
> organize_storage {}
  open -3,64,0 -> ok opened
  close -> ok closed
  open 3,64,0 -> ok opened
  close -> ok closed
  open 0,64,4 -> ok opened
  close -> ok closed
  open -3,64,0 -> ok opened
  close -> ok closed
  open 3,64,0 -> ok opened
  close -> ok closed
  open 0,64,4 -> ok opened
  close -> ok closed
= ok: 0 moves, 3 chests, 0 clicks, Ns; ores@-3,64,0, wood@3,64,0, stone@0,64,4
  world B5E6844F8203E88C @0.5,64,0.5 violations 0
## deposit and restock
> deposit_everything {}
  open -3,64,0 -> ok opened
  click 54 shift -> ok clicked
  click 55 shift -> ok clicked
  close -> ok closed
= ok: inventory deposited: 2 moves, 1 chest, 2 clicks, Ns
  world 9AE94C7BD1016E51 @0.5,64,0.5 violations 0
> restock_loadout {"items":"pickaxe torches"}
  open -3,64,0 -> ok opened
  close -> ok closed
  open 3,64,0 -> ok opened
  close -> ok closed
  open 0,64,4 -> ok opened
  click 0 shift -> ok clicked
  click 1 -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 27 right -> ok clicked
  click 1 -> ok clicked
  close -> ok closed
= ok: took 1 stone pickaxe, 16 torch; still short: axe; 2 moves, 3 chests, 19 clicks, Ns
  world 6E2343BA0EFF8EBA @0.5,64,0.5 violations 0
"""";

    public const string Building = """"
## grow one side, two rows
> expand_island {"side":"east","rows":2}
  place 4,63,0 4 -> ok placed 4
  place 4,63,-1 4 -> ok placed 4
  place 4,63,-2 4 -> ok placed 4
  place 4,63,-3 4 -> ok placed 4
  place 4,63,1 4 -> ok placed 4
  place 4,63,2 4 -> ok placed 4
  place 4,63,3 4 -> ok placed 4
  place 5,63,0 4 -> ok placed 4
  place 5,63,-1 4 -> ok placed 4
  place 5,63,-2 4 -> ok placed 4
  place 5,63,-3 4 -> ok placed 4
  place 5,63,1 4 -> ok placed 4
  place 5,63,2 4 -> ok placed 4
  place 5,63,3 4 -> ok placed 4
= ok: expanded island by 14 block(s) at y 63; 50 cobblestone left
  world D619BCC9F8D6DD2B @0.5,64,0.5 violations 0
> expand_island {"side":"n","rows":1,"max":3}
  place 0,63,-4 4 -> ok placed 4
  place 1,63,-4 4 -> ok placed 4
  place -1,63,-4 4 -> ok placed 4
= ok: expanded island by 3 block(s) at y 63; 47 cobblestone left
  world 77D1B4D377C0A326 @0.5,64,0.5 violations 0
> expand_island {"side":"up"}
  place -4,63,0 4 -> ok placed 4
  place -4,63,-1 4 -> ok placed 4
  place -4,63,-2 4 -> ok placed 4
  place -4,63,-3 4 -> ok placed 4
  place 0,63,4 4 -> ok placed 4
  place -1,63,4 4 -> ok placed 4
  place -2,63,4 4 -> ok placed 4
  place -3,63,4 4 -> ok placed 4
  place 1,63,4 4 -> ok placed 4
  place 2,63,4 4 -> ok placed 4
  place 3,63,4 4 -> ok placed 4
  place 4,63,4 4 -> ok placed 4
  place 5,63,4 4 -> ok placed 4
  place -4,63,1 4 -> ok placed 4
  place -4,63,2 4 -> ok placed 4
  place -4,63,3 4 -> ok placed 4
  place 2,63,-4 4 -> ok placed 4
  place 3,63,-4 4 -> ok placed 4
  place 4,63,-4 4 -> ok placed 4
  place 5,63,-4 4 -> ok placed 4
  place -2,63,-4 4 -> ok placed 4
  place -3,63,-4 4 -> ok placed 4
  place 0,63,-5 4 -> ok placed 4
  place -1,63,-5 4 -> ok placed 4
  place 1,63,-5 4 -> ok placed 4
  place 6,63,0 4 -> ok placed 4
  place 6,63,-1 4 -> ok placed 4
  place 6,63,-2 4 -> ok placed 4
  place 6,63,-3 4 -> ok placed 4
  place 6,63,1 4 -> ok placed 4
  place 6,63,2 4 -> ok placed 4
  place 6,63,3 4 -> ok placed 4
= ok: expanded island by 32 block(s) at y 63; 15 cobblestone left
  world 88B8A35B1167CA10 @0.5,64,0.5 violations 0
> expand_island {"rows":3,"max":10,"block":"dirt"}
  place -5,63,0 4 -> ok placed 4
  place -5,63,1 4 -> ok placed 4
  place -5,63,2 4 -> ok placed 4
  place 0,63,5 4 -> ok placed 4
  place -1,63,5 4 -> ok placed 4
  place -2,63,5 4 -> ok placed 4
  place -5,63,-1 4 -> ok placed 4
  place -5,63,-2 4 -> ok placed 4
  place 1,63,5 4 -> ok placed 4
  place 2,63,-5 4 -> ok placed 4
= ok: expanded island by 10 block(s) at y 63; 5 cobblestone left
  world 4F68C8E08CD1BAEF @0.5,64,0.5 violations 0
## grow with a mob near, out of blocks
> expand_island {"side":"west"}
= failed: expanded island by 0 block(s) at y 63 (zombie within 6: not working at the edge); 64 cobblestone left
  world 08122BB431DE5FEA @0.5,64,0.5 violations 0
> expand_island {"rows":2}
  place 0,63,-3 4 -> ok placed 4
  place -1,63,-3 4 -> ok placed 4
  place -2,63,-3 4 -> ok placed 4
  place 0,63,3 4 -> ok placed 4
  place 1,63,3 4 -> ok placed 4
= ok: expanded island by 5 block(s) at y 63, skipped 15 (out of cobblestone); 0 cobblestone left
  world 583178DF7DBE9F58 @0.5,64,0.5 violations 0
> expand_island {}
= failed: no building blocks
  world 30153C5CBD43D816 @0.5,64,0.5 violations 0
## lines, floors, walls, pillars, ladders, torches
> place_line {"from":"-3,64,-3","to":"3,64,-3"}
  place 0,64,-3 4 -> ok placed 4
  place -1,64,-3 4 -> ok placed 4
  place 1,64,-3 4 -> ok placed 4
  place -2,64,-3 4 -> ok placed 4
  place 2,64,-3 4 -> ok placed 4
  place -3,64,-3 4 -> ok placed 4
  place 3,64,-3 4 -> ok placed 4
= ok: line: placed 7; 121 cobblestone left
  world A3C00308CD98EC55 @0.5,64,0.5 violations 0
> fill_floor {"from":"-2,64,1","to":"0,64,3"}
  place 0,64,1 4 -> ok placed 4
  place -1,64,1 4 -> ok placed 4
  place 0,64,2 4 -> ok placed 4
  place -2,64,1 4 -> ok placed 4
  place -1,64,2 4 -> ok placed 4
  place -2,64,2 4 -> ok placed 4
  place 0,64,3 4 -> ok placed 4
  place -1,64,3 4 -> ok placed 4
  place -2,64,3 4 -> ok placed 4
= ok: floor: placed 9; 112 cobblestone left
  world 1A4B1F8ADD157DA0 @0.5,64,0.5 violations 0
> build_wall {"from":"-4,64,4","to":"4,64,4","height":3}
  place 0,64,4 4 -> ok placed 4
  place -1,64,4 4 -> ok placed 4
  place 1,64,4 4 -> ok placed 4
  place 0,65,4 4 -> ok placed 4
  place -1,65,4 4 -> ok placed 4
  place 1,65,4 4 -> ok placed 4
  place -2,64,4 4 -> ok placed 4
  place 2,64,4 4 -> ok placed 4
  place 0,66,4 4 -> ok placed 4
  place -2,65,4 4 -> ok placed 4
  place 2,65,4 4 -> ok placed 4
  place -1,66,4 4 -> ok placed 4
  place 1,66,4 4 -> ok placed 4
  place -2,66,4 4 -> ok placed 4
  place 2,66,4 4 -> ok placed 4
  place -3,64,4 4 -> ok placed 4
  place 3,64,4 4 -> ok placed 4
  place -3,65,4 4 -> ok placed 4
  place 3,65,4 4 -> ok placed 4
  place -3,66,4 4 -> ok placed 4
  place 3,66,4 4 -> ok placed 4
  place -4,64,4 4 -> ok placed 4
  place 4,64,4 4 -> ok placed 4
  place -4,65,4 4 -> ok placed 4
  place 4,65,4 4 -> ok placed 4
  place -4,66,4 4 -> ok placed 4
  place 4,66,4 4 -> ok placed 4
= ok: wall: placed 27; 85 cobblestone left
  world 3D299A6BE26FDF13 @0.5,64,0.5 violations 0
> place_ladder {"pos":"2,64,3","height":3}
  place 2,64,3 65 -> ok placed 65
  wait_until 1500 -> True
  place 2,65,3 65 -> ok placed 65
  wait_until 1500 -> True
  place 2,66,3 65 -> ok placed 65
  wait_until 1500 -> True
= ok: ladder x3 at 2,64,3
  world E6B998692899ADCA @0.5,64,0.5 violations 0
> place_torch {"pos":"1,64,0"}
  place 1,64,0 50 -> ok placed 50
= ok: placed 50
  world 0D66D7B3FBD74F36 @0.5,64,0.5 violations 0
> place_torch {}
  place -1,64,0 50 -> ok placed 50
= ok: placed 50
  world 44F737D0D91D7DCD @0.5,64,0.5 violations 0
> pillar_up {"height":3}
  goto block (0, 67, 0) drop - place True break False @0.5,67,0.5 -> ok arrived at (0.5, 67, 0.5)
= ok: pillared up 3 to y 67
  world 2E541266831FC865 @0.5,67,0.5 violations 0
## pillar down
> pillar_down {"depth":3}
  dig 1,63,0 -> ok dug dirt
  goto block (1, 63, 0) drop - place  break  @1.5,63,0.5 -> ok arrived at (1.5, 63, 0.5)
  dig 2,63,0 -> ok dug dirt
  dig 2,62,0 -> ok dug stone
  goto block (2, 62, 0) drop - place  break  @2.5,62,0.5 -> ok arrived at (2.5, 62, 0.5)
  dig 3,63,0 -> ok dug dirt
  dig 3,62,0 -> ok dug stone
  dig 3,61,0 -> ok dug stone
  goto block (3, 61, 0) drop - place  break  @3.5,61,0.5 -> ok arrived at (3.5, 61, 0.5)
= ok: down 3 to y 61
  world EF86956B792A9E8B @3.5,61,0.5 violations 0
> pillar_down {"depth":2,"side":"west"}
  dig 4,62,0 -> ok dug stone
  dig 4,61,0 -> ok dug stone
  dig 4,60,0 -> ok dug stone
  goto block (4, 60, 0) drop - place  break  @4.5,60,0.5 -> ok arrived at (4.5, 60, 0.5)
  dig 5,61,0 -> ok dug stone
  dig 5,60,0 -> ok dug stone
  dig 5,59,0 -> ok dug stone
  goto block (5, 59, 0) drop - place  break  @5.5,59,0.5 -> ok arrived at (5.5, 59, 0.5)
= ok: down 2 to y 59
  world 0500A76982C202A6 @5.5,59,0.5 violations 0
> pillar_down {"depth":3}
= failed: no safe staircase 3 down from here (void, liquid, sand/gravel above or protected)
  world 1AFD2F42F137B277 @0.5,64,0.5 violations 0
> pillar_down {"depth":1}
  dig 1,64,0 -> ok dug sand
  dig 1,63,0 -> ok dug dirt
  goto block (1, 63, 0) drop - place  break  @1.5,63,0.5 -> ok arrived at (1.5, 63, 0.5)
= ok: down 1 to y 63
  world 4D25521EC2E5F526 @1.5,63,0.5 violations 0
"""";

    public const string Portal = """"
## cast
> cast_portal {"pos":"-2,64,-4"}
  198 actions A90543174D3DC8EF
= ok: portal frame cast at -2,64,-4 (x axis, 10 lava buckets, plate dug 24/24); light_portal next
  world C0A78944CAF7D155 @0.5,65,-3.5 violations 0
## refusals
> cast_portal {"pos":"-2,64,-4"}
  0 actions E3B0C44298FC1C14
= failed: cast_portal: needs water bucket + 2nd bucket + lava nearby + 24 blocks
  world 4DC26D72801C2A7A @0.5,64,4.5 violations 0
> cast_portal {"pos":"-2,64,-4"}
  0 actions E3B0C44298FC1C14
= failed: no lava source within 24
  world 8EBCFBAE6973BE13 @0.5,64,4.5 violations 0
> cast_portal {"pos":"-2,64,-4"}
  0 actions E3B0C44298FC1C14
= failed: needs 24 cobblestone for the back plate (have 5)
  world 80AAEBC4E906170B @0.5,64,4.5 violations 0
> cast_portal {"pos":"-2,64,-4"}
  0 actions E3B0C44298FC1C14
= failed: 0,66,-4 is in the way (stone)
  world A1CDF7EBC4A63662 @0.5,64,4.5 violations 0
> cast_portal {"pos":"-2,64,-4"}
  0 actions E3B0C44298FC1C14
= failed: planks at 3,66,-4 would catch fire
  world C80099299947A3B8 @0.5,64,4.5 violations 0
> cast_portal {"pos":"-2,64,-4"}
  0 actions E3B0C44298FC1C14
= failed: no solid floor under -2,64,-3
  world 904FAFC0AB63E2D8 @0.5,64,4.5 violations 0
> cast_portal {"pos":"-2,64,-4"}
  0 actions E3B0C44298FC1C14
= failed: -1,64,-3 is protected (a protected block); say 'you can break it' if you want it gone
  world D5D81FF41E1ACC2F @0.5,64,4.5 violations 0
## find a spot along z, the water fails, light it
> cast_portal {"axis":"z"}
  198 actions E04170EBEFDB714C
= ok: portal frame cast at -2,64,4 (z axis, 10 lava buckets, plate dug 24/24); light_portal next
  world D4FE98D2DE1A9EB5 @-1.5,65,6.5 violations 0
> cast_portal {}
  198 actions DFF865C97D9F6AA7
= ok: portal frame cast at -2,64,4 (x axis, 10 lava buckets, plate dug 24/24); light_portal next
  world 266D53D6E526119A @0.5,65,4.5 violations 0
> cast_portal {"pos":"-2,64,-4"}
  64 actions 39F839F73DD7AD5D
= failed: cast 2/10 at -2,65,-4: water: -2,66,-4 is not empty (cobblestone); lava taken back
  world 5450940F24D54156 @-0.5,64,-4.5 violations 0
> cast_portal {"pos":"-2,64,-4","light":true}
  200 actions 6ED80BE593A465D3
= ok: portal frame cast at -2,64,-4 (x axis, 10 lava buckets, plate dug 24/24); portal lit at -1,65,-4
  world 53B4F6DD02EC05D5 @0.5,65,-3.5 violations 0
"""";

    public const string Farm = """"
## harvest wheat
> harvest_wheat {}
  dig 1,64,2 -> ok dug wheat
  collect 3 dry True -> ok collected
  use_on 1,63,2 Top 291 -> ok used on block
  place 1,64,2 295 -> ok placed 59
  dig 2,64,2 -> ok dug wheat
  collect 3 dry True -> ok collected
  use_on 2,63,2 Top 291 -> ok used on block
  place 2,64,2 295 -> ok placed 59
  collect 12 dry True -> ok collected
= ok: harvested 2 wheat, replanted 2 (wheat 2, seeds 2; 0 growing); field: 2/2 farmland planted; 0 drop(s) left behind
  world F13F06715A1B409F @0.5,64,0.5 violations 0
> harvest_wheat {}
  dig 1,64,2 -> ok dug wheat
  collect 3 dry True -> ok collected
  use_on 1,63,2 Top 290 -> ok used on block
  wait_until 1500 -> True
  place 1,64,2 295 -> ok placed 59
  collect 12 dry True -> ok collected
= ok: harvested 1 wheat, replanted 1 (re-tilled 1) (wheat 1, seeds 1; 0 growing); field: 1/1 farmland planted; 0 drop(s) left behind
  world E2C80D7B17A2BB70 @0.5,64,0.5 violations 0
> harvest_wheat {}
  dig 1,64,2 -> ok dug wheat
  collect 3 dry True -> ok collected
  use_on 1,63,2 Top 291 -> ok used on block
  place 1,64,2 295 -> ok placed 59
  collect 12 dry True -> ok collected
= ok: harvested 1 wheat, replanted 1 (wheat 1, seeds 1; 0 growing); field: 1/1 farmland planted; 0 drop(s) left behind; crafted stone hoe
  world 35BBF8FA814F771F @0.5,64,0.5 violations 0
> harvest_wheat {}
  dig 1,64,2 -> ok dug wheat
  collect 3 dry True -> ok collected
  use_on 1,63,2 Top 291 -> ok used on block
  place 1,64,2 295 -> ok placed 59
  dig 2,64,2 -> ok dug wheat
  collect 3 dry True -> ok collected
  use_on 2,63,2 Top 291 -> ok used on block
  place 2,64,2 295 -> ok placed 59
  collect 12 dry True -> ok collected
  place 1,64,3 295 -> ok placed 59
  wait_until 1500 -> True
  place 2,64,3 295 -> ok placed 59
  wait_until 1500 -> True
= ok: harvested 2 wheat, replanted 4 (wheat 2, seeds 0; 0 growing); field: 4/6 planted, 2 empty: out of seeds (need 2 more); 0 drop(s) left behind
  world D4D2EEC362A53059 @0.5,64,0.5 violations 0
> harvest_wheat {}
= failed: no ripe wheat within 12 (1 still growing)
  world D8C80494281493CF @0.5,64,0.5 violations 0
> harvest_wheat {}
  dig 1,64,2 -> ok dug wheat
  collect 3 dry True -> ok collected
  place 1,64,2 295 -> ok placed 59
  collect 12 dry True -> ok collected
= ok: harvested 1 wheat, replanted 1 (no hoe: planted only on farmland) (wheat 1, seeds 1; 0 growing); field: 1/1 farmland planted; 0 drop(s) left behind
  world 38A3E9C0AB71A025 @0.5,64,0.5 violations 0
## till, bonemeal, sapling, cane, cactus, melons
> till_soil {"count":2}
  use_on 0,63,0 Top 291 -> ok used on block
  wait_until 1500 -> True
  place 0,64,0 295 -> ok placed 59
  use_on 0,63,1 Top 291 -> ok used on block
  wait_until 1500 -> True
  place 0,64,1 295 -> ok placed 59
= ok: tilled 2, planted 2
  world 444E74F11149078A @0.5,64,0.5 violations 0
> till_soil {"pos":"2,63,2","count":4}
  use_on 1,63,2 Top 291 -> ok used on block
  wait_until 1500 -> True
  use_on 2,63,1 Top 291 -> ok used on block
  wait_until 1500 -> True
  goto reach (2, 63, 3) (3.65) drop - place False break False @0.5,64,1.5 -> ok arrived at (0.5, 64, 1.5)
  use_on 2,63,3 Top 291 -> ok used on block
  wait_until 1500 -> True
  goto reach (3, 63, 2) (3.65) drop - place False break False @1.5,64,1.5 -> ok arrived at (1.5, 64, 1.5)
  use_on 3,63,2 Top 291 -> ok used on block
  wait_until 1500 -> True
= ok: tilled 4, planted 0
  world D8901A06C312CEFE @1.5,64,1.5 violations 0
> till_soil {"pos":"2,63,2","count":4}
  use_on 1,63,3 Top 291 -> ok used on block
  wait_until 1500 -> True
  use_on 1,63,1 Top 291 -> ok used on block
  wait_until 1500 -> True
  use_on 3,63,1 Top 291 -> ok used on block
  wait_until 1500 -> True
  use_on 3,63,3 Top 291 -> ok used on block
  wait_until 1500 -> True
= ok: tilled 4, planted 0
  world 66907AAA1F1A857D @1.5,64,1.5 violations 0
> apply_bonemeal {"count":2}
  use_on 1,64,2 Top 351 -> ok used on block
  wait_until 1500 -> True
= ok: bonemealed 1 (1 bone meal)
  world 87E5EEEA50397D24 @0.5,64,0.5 violations 0
> plant_sapling {"pos":"1,64,1"}
  place 1,64,1 6 -> ok placed 6
= ok: planted sapling at 1,64,1
  world 8E698CBE9DA2F831 @0.5,64,0.5 violations 0
> plant_sapling {"pos":"2,64,2"}
  place 2,64,2 6 -> ok placed 6
= ok: planted sapling at 2,64,2
  world 2290F261B9A25812 @0.5,64,0.5 violations 0
> harvest_cane {}
  dig 2,65,2 -> ok dug 2
  collect 12 dry False -> ok collected
= ok: cut 1 sugar_cane column(s), bottoms left; +2 (2 total)
  world 0456C64B0F40F8D8 @0.5,64,0.5 violations 0
> harvest_cactus {}
  dig -2,65,-2 -> ok dug 1
  collect 12 dry False -> ok collected
= ok: cut 1 cactus column(s), bottoms left; +1 (1 total)
  world C0AA1A947F972031 @0.5,64,0.5 violations 0
> harvest_melons {}
  dig 1,64,3 -> ok dug melon_block
  collect 12 dry False -> ok collected
= ok: harvested 1 (melon slices 4, pumpkins 0); stems kept
  world DDF17ED11D91842E @0.5,64,0.5 violations 0
## liquids
> infinite_water {"pos":"2,63,2"}
  dig 2,63,2 -> ok dug dirt
  dig 3,63,2 -> ok dug dirt
  dig 2,63,3 -> ok dug dirt
  dig 3,63,3 -> ok dug dirt
  goto reach (2, 63, 2) (4.2) (pour stand for 2,63,2) drop - place  break  @1.5,64,1.5 -> ok arrived at (1.5, 64, 1.5)
  look 2.5,63,2.5 -> ok looking
  use 326 -> ok poured
  wait_until 1500 -> True
  look 4,63.75,3.25 -> ok looking
  use 326 -> ok poured
  wait_until 1500 -> True
  wait_until 3000 -> True
= ok: infinite water at 2,63,2 (4 sources)
  world 68C911D341772D69 @1.5,64,1.5 violations 0
> fill_bucket {"pos":"2,63,2"}
  look 2.2,63.98,2.2 -> ok looking
  use 325 -> ok filled
  wait_until 1500 -> True
= ok: filled water bucket at 2,63,2
  world 888E8C732675A047 @1.5,64,1.5 violations 0
> empty_bucket {"pos":"-2,63,-2"}
= failed: -2,63,-2 is not empty (dirt)
  world 888E8C732675A047 @1.5,64,1.5 violations 0
> infinite_water {"pos":"-3,63,2","shape":"line"}
  dig -3,63,2 -> ok dug dirt
  dig -2,63,2 -> ok dug dirt
  dig -1,63,2 -> ok dug dirt
  look -3,63.9,2.5 -> ok looking
  use 326 -> ok poured
  wait_until 1500 -> True
  look -0.5,63.5,3 -> ok looking
  use 326 -> ok poured
  wait_until 1500 -> True
  wait_until 3000 -> True
= ok: infinite water at -3,63,2 (3 sources)
  world 17E0E2DC5C0350B2 @0.5,64,0.5 violations 0
> infinite_water {}
= failed: infinite_water: needs 2 water buckets
  world 4EDF54223CE4B24F @0.5,64,0.5 violations 0
> make_obsidian {"pos":"2,63,2"}
  look 1.5,64,2.5 -> ok looking
  use 326 -> ok poured
  wait_until 1500 -> True
  wait_until 3000 -> True
  goto any(reach (1, 64, 2) (4.25) (visible source (1, 64, 2))) drop - place False break False @0.5,64,0.5 -> FAIL no path to any(reach (1, 64, 2) (4.25) (visible source (1, 64, 2))) (no route)
= ok: made 1 obsidian
  world 176C30DC6A76DB2D @0.5,64,0.5 violations 0
> make_obsidian {}
= failed: make_obsidian: needs water bucket
  world 176C30DC6A76DB2D @0.5,64,0.5 violations 0
"""";
}
