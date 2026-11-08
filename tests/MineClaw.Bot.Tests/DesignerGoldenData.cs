namespace MineClaw.Bot.Tests;

/// <summary>The golden dump of <see cref="DesignerGoldenTests"/> (generated from the pre-refactor code).</summary>
internal static class DesignerGoldenData
{
    public const string Expected = """"""
== build b1.8.1
system You are the Design Studio builder: you co-design a Minecraft b1.8.1 build with PoofImaFox on a sandbox canvas (not the world; nothing is spent). Plot: x,z 0..63, y -16..47; +x east, +z south (north = -z); the view starts at the centre (32,0,32). y=0 is the first layer above flat grass; y=-1 is the grass layer and y -16..-1 is solid ground: dig it with remove (that air is dug out when built), then build floors, walls, stairs or ladders down there (basements, cellars, foundations); "earth" fills dug cells back. Edit only with the tools, coordinates inclusive. Prefer few big calls (box, line) over many places. A saved design is at most 32 wide/deep and 32 high, dug layers included: stay inside that.
Blocks: any b1.8.1 block by its minecraft name (oak_stairs), display name, or name:variant (wool:red, log:spruce, stone_slab:cobblestone, stone_bricks:mossy); unsure of a name: find_blocks. Add :<dir> for a facing: stairs = the way up, doors = the side they open to (place the lower half, the top is added; on a full block), beds = the head's side (the head is added), chests/furnaces/pumpkins = the front, gates = the way through; torch:floor (stands on the block below), torch:wall=<side>, ladder:wall=<side> (<side> = where the supporting block is, seen from the torch or ladder: torch:wall=west hangs on the solid block to its west, on that block's east face; it must be a full block). Doors and windows need openings in walls.
Building (34): stone cobblestone mossy_cobblestone planks log brick_block stone_bricks sandstone double_stone_slab stone_slab oak_stairs <material>_stairs obsidian glass glass_pane iron_bars gold_block iron_block …
Decoration (14): fence ladder torch bed standing_sign wall_sign chest crafting_table furnace jukebox bookshelf glowstone lit_pumpkin cake
Colored (16): wool
Nature (27): dirt farmland sand gravel clay soul_sand netherrack snow snow_layer sapling yellow_flower red_flower tallgrass deadbush brown_mushroom red_mushroom cactus sugar_cane …
Redstone (16): redstone_wire redstone_torch unpowered_repeater lever stone_button stone_pressure_plate wooden_pressure_plate piston sticky_piston dispenser noteblock tnt wooden_door iron_door trapdoor fence_gate
Transport (3): rail golden_rail detector_rail
Misc (2): water lava
wool colours: white orange magenta light_blue yellow lime pink gray silver(light_gray) cyan purple blue brown green red black; woods: oak spruce birch (logs only)
When the user names "the selection" use its box. Keep each reply to a few calls; one place takes at most about 100 cells (use box, line or replace for runs): a reply that hits the output limit loses the call it was writing. A tool error says what was wrong and the usage: fix that, don't repeat the call. Reply after the edits with ONE or two short sentences on what changed, in plain words (no coordinates unless asked).
run describe {} => r0 empty (plot x,z 0..63, y -16..47; y0 stands on the grass, below is solid ground)
run find_blocks {"q":"stairs"} => oak_stairs = Wooden Stairs (Building, facing)
stone_stairs = Cobblestone Stairs (Building, facing)
brick_stairs = Brick Stairs (Building, facing)
stone_brick_stairs = Stone Brick Stairs (Building, facing)
run find_blocks {"q":"red","n":3} => wool:red = Red Wool (Colored)
red_flower = Rose (Nature)
red_mushroom = Red Mushroom (Nature)
run find_blocks {"q":"plank","n":99} => planks = Wooden Planks (Building)
run find_blocks {"q":"zzqq"} => no b1.8.1 block matches 'zzqq'
run find_blocks {"q":"glas"} => glass = Glass (Building)
glass_pane = Glass Pane (Building)
run find_blocks {} => ERR error: $.q: required (usage: find_blocks {q, n?})
run place {"b":"planks","at":[[10,0,10],[11,0,10],[12,0,10]]} => ok, 3 cells changed (r1) | op place 'placed wooden planks' ai 3 cells ce0513031fb1eb4a first [10,0,10=5:0 11,0,10=5:0 12,0,10=5:0]
run place {"b":"planks"} => ERR error: $.at: required (usage: place {b, at})
run place {"b":"planks","at":[1,2,3]} => ok, 1 cells changed (r2) | op place 'placed wooden planks' ai 1 cells 66b2e49d96bea0e1 first [1,2,3=5:0]
run place {"b":"planks","at":[[1,2]]} => ERR error: a position is [x,y,z] (usage: place {b, at})
run place {"b":"planks","at":"x"} => ERR error: $.at: expected array (usage: place {b, at})
run place {"at":[[1,1,1]]} => ERR error: $.b: required (usage: place {b, at})
run place {"b":"grass","at":[[1,1,1]]} => ERR error: unknown b1.8.1 block 'grass'; did you mean glass, tallgrass:grass, tallgrass:fern? (usage: place {b, at})
run place {"b":"planks","at":[[70,0,10],[-1,0,0],[5,60,5],[5,0,5]]} => ok, 1 cells changed (r3) (3 cells skipped: outside the plot or not placeable) | op place 'placed wooden planks' ai 1 cells 7ce0d362ba737c59 first [5,0,5=5:0]
run box {"b":"cobblestone","from":[20,0,20],"to":[26,0,26]} => ok, 49 cells changed (r4) | op fill 'filled 7×1×7 with cobblestone' ai 49 cells 3903e3ce8284c3b6 first [20,0,20=4:0 21,0,20=4:0 22,0,20=4:0]
run box {"b":"planks","from":[20,1,20],"to":[26,4,26],"mode":"hollow"} => ok, 146 cells changed (r5) | op hollow 'hollow 7×4×7 with wooden planks' ai 146 cells 7605e188f9b9afda first [20,1,20=5:0 21,1,20=5:0 22,1,20=5:0]
run box {"b":"stone","from":[30,0,30],"to":[33,3,33],"mode":"walls"} => ok, 48 cells changed (r6) | op walls 'walls 4×4×4 with stone' ai 48 cells df4287ca225dca25 first [30,0,30=1:0 31,0,30=1:0 32,0,30=1:0]
run box {"b":"stone","from":[30,0,30],"to":[33,3,33],"mode":"weird"} => ERR error: $.mode: value is not in enum ("fill", "hollow", "walls") (usage: box {b, from, to, mode?})
run box {"b":"stone","from":[0,0,0],"to":[63,40,63]} => ERR error: box of 167936 cells is too big (max 40000) (usage: box {b, from, to, mode?})
run box {"b":"stone","from":[0,0,0]} => ERR error: $.to: required (usage: box {b, from, to, mode?})
run box {"b":"stone","from":[0,0,0],"to":[1,1,1],"mode":5} => ERR error: $.mode: expected string (usage: box {b, from, to, mode?})
run line {"b":"fence","from":[0,0,0],"to":[6,3,2]} => ok, 7 cells changed (r7) | op line 'line of fence' ai 7 cells 2b18746c072f79a1 first [0,0,0=85:0 1,0,0=85:0 2,1,1=85:0]
run line {"b":"cobblestone","from":[40,5,40],"to":[40,5,40]} => ok, 1 cells changed (r8) | op line 'line of cobblestone' ai 1 cells 290a5f0650eae5d7 first [40,5,40=4:0]
run place {"b":"wooden_door:south","at":[[23,1,26]]} => ok, 2 cells changed (r9) | op place 'placed wooden door' ai 2 cells bc25291d98aea4b7 first [23,1,26=64:3 23,2,26=64:11]
run place {"b":"bed:east","at":[[22,1,22]]} => ok, 2 cells changed (r10) | op place 'placed bed' ai 2 cells 667490bccf55e2bf first [22,1,22=26:3 23,1,22=26:11]
run place {"b":"chest:north","at":[[21,1,21]]} => ok, 1 cells changed (r11) | op place 'placed chest' ai 1 cells 9347fa97d46c14b4 first [21,1,21=54:2]
run place {"b":"chest:west","at":[[21,1,22]]} => ok, 1 cells changed (r12) | op place 'placed chest' ai 1 cells 7645daf0eee9de33 first [21,1,22=54:4]
run place {"b":"chest:west","at":[[21,1,23]]} => ok, 1 cells changed (r13) | op place 'placed chest' ai 1 cells 9e6f564f137a0c50 first [21,1,23=54:4]
run place {"b":"torch:wall=west","at":[[21,2,24]]} => ok, 1 cells changed (r14) | op place 'placed torch' ai 1 cells 25acd1548c1187e3 first [21,2,24=50:1]
run place {"b":"torch:wall=east","at":[[21,2,24]]} => ok, 1 cells changed (r15) (note: turned 1 wall torch around to hang on the solid side; wall=<side> names the side the supporting block is on) | op place 'placed torch' ai 1 cells 25acd1548c1187e3 first [21,2,24=50:1]
run place {"b":"torch:floor","at":[[24,1,24]]} => ok, 1 cells changed (r16) | op place 'placed torch' ai 1 cells 19650ef8c1ddb321 first [24,1,24=50:5]
run place {"b":"torch","at":[[24,1,23]]} => ok, 1 cells changed (r17) | op place 'placed torch' ai 1 cells dc336b30c140ee54 first [24,1,23=50:5]
run place {"b":"torch:north","at":[[50,0,50]]} => ok, 1 cells changed (r18) (note: stood 1 torch with no wall on the floor; wall=<side> names the side the supporting block is on) | op place 'placed torch' ai 1 cells e2ca068836ecde2e first [50,0,50=50:5]
run place {"b":"torch:on=up","at":[[50,0,51]]} => ERR error: 'torch:on=up': the side is north, east, south or west (where the supporting block is) (usage: place {b, at})
run place {"b":"torch:wall=sideways","at":[[50,0,51]]} => ERR error: 'torch:wall=sideways': the side is north, east, south or west (where the supporting block is) (usage: place {b, at})
run place {"b":"ladder:wall=south","at":[[22,2,25]]} => ok, 1 cells changed (r19) | op place 'placed ladder' ai 1 cells 14ef8b67330c7364 first [22,2,25=65:2]
run place {"b":"ladder:north","at":[[45,0,45]]} => ok, 1 cells changed (r20) | op place 'placed ladder' ai 1 cells fe0526aa501e3098 first [45,0,45=65:3]
run place {"b":"oak_stairs:north","at":[[24,1,21],[25,1,21]]} => ok, 2 cells changed (r21) | op place 'placed wooden stairs' ai 2 cells 4373ae3a3bda91f0 first [24,1,21=53:3 25,1,21=53:3]
run place {"b":"cobblestone_stairs:east","at":[[24,2,21]]} => ok, 1 cells changed (r22) | op place 'placed cobblestone stairs' ai 1 cells 7a03732d84aabf76 first [24,2,21=67:0]
run place {"b":"wool:red","at":[[24,3,21]]} => ok, 1 cells changed (r23) | op place 'placed red wool' ai 1 cells 354860b6f9e14507 first [24,3,21=35:14]
run place {"b":"log:spruce","at":[[25,3,21]]} => ok, 1 cells changed (r24) | op place 'placed spruce log' ai 1 cells 1a4d76e9bdadb252 first [25,3,21=17:1]
run place {"b":"glass","at":[[20,2,23],[20,3,23]]} => ok, 2 cells changed (r25) | op place 'placed glass' ai 2 cells 05ee019742576cc8 first [20,2,23=20:0 20,3,23=20:0]
run replace {"a":"oak_stairs","b":"cobblestone_stairs"} => ok, 2 cells changed (r26) | op replace 'replaced oak_stairs with cobblestone stairs' ai 2 cells 5341898b1ebc57d6 first [24,1,21=67:3 25,1,21=67:3]
run replace {"a":"wool","b":"wool:blue","from":[20,0,20],"to":[26,4,26]} => nothing changed
run replace {"a":"any","b":"stone_bricks","from":[30,0,30],"to":[31,1,31]} => ok, 6 cells changed (r27) | op replace 'replaced any with stone bricks' ai 6 cells 68aa18e4ae8d5a26 first [30,0,30=98:0 31,0,30=98:0 30,0,31=98:0]
run replace {"a":"glass","b":"planks","from":[0,0,0]} => ERR error: to is required ([x,y,z]) (usage: replace {a, b, from?, to?})
run replace {"b":"planks"} => ERR error: $.a: required (usage: replace {a, b, from?, to?})
run replace {"a":"nothing_here","b":"planks"} => ERR error: unknown b1.8.1 block 'nothing_here'; find_blocks looks names up (usage: replace {a, b, from?, to?})
run remove {"from":[30,-3,30],"to":[33,-1,33]} => ok, 48 cells changed (r28) | op remove 'removed 4×3×4' ai 48 cells 8dd6842273a57d6d first [30,-3,30=0:0 31,-3,30=0:0 32,-3,30=0:0]
run remove {"from":[31,0,31],"to":[32,3,32]} => nothing changed
run remove {"from":[31,0,31]} => ERR error: $.to: required (usage: remove {from, to})
run box {"b":"cobblestone","from":[30,-4,30],"to":[33,-4,33]} => ok, 16 cells changed (r29) | op fill 'filled 4×1×4 with cobblestone' ai 16 cells f2c6e5c07f871fed first [30,-4,30=4:0 31,-4,30=4:0 32,-4,30=4:0]
run place {"b":"cobblestone_stairs:north","at":[[31,-3,31]]} => ok, 1 cells changed (r30) | op place 'placed cobblestone stairs' ai 1 cells a6a24d2603d81d32 first [31,-3,31=67:3]
run place {"b":"earth","at":[[32,-1,32],[40,-1,40],[40,3,40]]} => ok, 1 cells changed (r31) | op place 'placed ground' ai 1 cells 2ad4790e3a233450 first [32,-1,32=-1:0]
run place {"b":"ground","at":[[33,-1,33]]} => ok, 1 cells changed (r32) | op place 'placed ground' ai 1 cells ffd161472126359c first [33,-1,33=-1:0]
run remove {"from":[23,1,26],"to":[23,1,26]} => ok, 2 cells changed (r33) | op remove 'removed 1×1×1' ai 2 cells f7a882bb8cbaac48 first [23,2,26=0:0 23,1,26=0:0]
run remove {"from":[22,1,22],"to":[22,1,22]} => ok, 2 cells changed (r34) | op remove 'removed 1×1×1' ai 2 cells 947792d42977c406 first [23,1,22=0:0 22,1,22=0:0]
run spiral_shaft {"at":[1,1],"bottom":5} => ERR error: spiral_shaft needs a mine canvas (switch the studio to Mine)
run nope {} => ERR error: no tool 'nope'; the tools are place, box, line, remove, replace, clear, describe, find_blocks
run describe {"y":0} => y0, rows z0..50, cols x0..50:
AA.................................................
...................................................
...................................................
...................................................
...................................................
.....B.............................................
...................................................
...................................................
...................................................
...................................................
..........BBB......................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
...................................................
...................................................
...................................................
..............................DDEE.................
..............................D..E.................
..............................E..E.................
..............................EEEE.................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
.............................................F.....
...................................................
...................................................
...................................................
...................................................
..................................................G
A=fence B=planks C=cobblestone D=stone_bricks E=stone F=ladder:3 G=torch
run describe {"y":-3} => y-3 (below ground), rows z0..50, cols x0..50:
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
##############################....#################
##############################.A..#################
##############################....#################
##############################....#################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
A=stone_stairs:3 #=ground .=dug
run describe {"y":"x"} => ERR error: $.y: expected integer (usage: describe {y?})
describe16 r34 276 blocks, box x0..50 y-4..5 z0..50 (51×10×51); wooden planks 138, cobblestone 66, stone 42, fence 7, stone bricks 6, cobblestone stairs 4, torch 4, chest 3, glass 2, ladder 2, red wool 1, spruce log 1; dug 45 below ground (y-3..-1); y-4 16 cobblestone · y-3 1 cobblestone stairs, 15 dug · y-2 16 dug · y-1 14 dug · y0 69 cobblestone+stone · y1 59 wooden planks+stone · y2 42 wooden planks+stone · y3 39 wooden planks+stone · y4 49 wooden planks · y5 1 cobblestone
describe3 r34 276 blocks, box x0..50 y-4..5 z0..50 (51×10×51); wooden planks 138, cobblestone 66, stone 42, …; dug 45 below ground (y-3..-1); y-4 16 cobblestone · y-3 1 cobblestone stairs, 15 dug · y-2 16 dug · y-1 14 dug · y0 69 cobblestone+stone · y1 59 wooden planks+stone · y2 42 wooden planks+stone · y3 39 wooden planks+stone · y4 49 wooden planks · y5 1 cobblestone
layer 0
y0, rows z0..50, cols x0..50:
AA.................................................
...................................................
...................................................
...................................................
...................................................
.....B.............................................
...................................................
...................................................
...................................................
...................................................
..........BBB......................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
...................................................
...................................................
...................................................
..............................DDEE.................
..............................D..E.................
..............................E..E.................
..............................EEEE.................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
.............................................F.....
...................................................
...................................................
...................................................
...................................................
..................................................G
A=fence B=planks C=cobblestone D=stone_bricks E=stone F=ladder:3 G=torch
layer 1
y1, rows z0..50, cols x0..50:
...................................................
..A................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
....................BBBBBBB........................
....................BCBBDDB........................
....................BE..BBB........................
....................BEBBFBB........................
....................BBBBFBB........................
....................BBBBBBB........................
....................BBB.BBB........................
...................................................
...................................................
...................................................
..............................GGHH.................
..............................G..H.................
..............................H..H.................
..............................HHHH.................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
A=fence B=planks C=chest D=stone_stairs:3 E=chest:4 F=torch G=stone_bricks H=stone
layer -1
y-1 (below ground), rows z0..50, cols x0..50:
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
##############################....#################
##############################....#################
##############################..#.#################
##############################...##################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
#=ground .=dug
layer -3
y-3 (below ground), rows z0..50, cols x0..50:
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
##############################....#################
##############################.A..#################
##############################....#################
##############################....#################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
A=stone_stairs:3 #=ground .=dug
layer -4
y-4 (below ground), rows z0..50, cols x0..50:
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
##############################AAAA#################
##############################AAAA#################
##############################AAAA#################
##############################AAAA#################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
A=cobblestone #=ground .=dug
wire ce95ab0e65f45c74 dug eca5c0418fa9f76a enc f4d8d4372dc073db rev 34
run clear {} => ok, 321 cells changed (r35) | op clear 'cleared the canvas' ai 321 cells 414f73515b374e15 first [10,0,10=0:0 11,0,10=0:0 12,0,10=0:0]
describe16 r35 empty (plot x,z 0..63, y -16..47; y0 stands on the grass, below is solid ground)
describe3 r35 empty (plot x,z 0..63, y -16..47; y0 stands on the grass, below is solid ground)
wire cbf29ce484222325 dug cbf29ce484222325 enc af63b64c8601abfa rev 35
== mine b1.8.1
system You are the Design Studio builder: you co-design an underground MINE for Minecraft b1.8.1 with PoofImaFox on a sandbox canvas over generated terrain (not the world). Plot x,z 0..63 (+x east, +z south), ABSOLUTE world y 1..127; the surface is y 63..73 here, bedrock y 0-4. The design is the cells to mine out: remove digs a box (never bedrock), "earth" fills dug cells back; blocks you place (torches, ladders, rails, chests) go in after their cell is dug. Prefer the templates: spiral_shaft (a walkable spiral staircase around a core, from the surface down), strip_mine (main corridor + 1x2 branches, spacing solid blocks apart), remove for rooms; tunnels 2 high, steps at most 1. Connect everything to the shaft. Ore heights: coal y0-127 best 48, iron y0-63 best 16, gold y0-31 best 11, redstone y0-15 best 11, diamond y0-15 best 11, lapis y0-30 peak 16 best 16; lava lakes y≤10; bedrock y0-4. Presets (mine_preset): b1.8.1-diamond-y11, b1.8.1-lapis-gold-y16. Blocks by minecraft name (torch:wall=<side of the supporting block>, ladder:wall=<side>, rail, chest:<front>); unsure: find_blocks. Keep each reply to a few calls; one place takes at most about 100 cells (use box, line or replace for runs): a reply that hits the output limit loses the call it was writing. A tool error says what was wrong and the usage: fix that, don't repeat the call. Reply after the edits with ONE or two short sentences on what changed, in plain words (no coordinates unless asked).
run describe {} => r0 empty mine (plot x,z 0..63, absolute y 1..127 over generated terrain: surface y63..73, bedrock y0-4)
run spiral_shaft {"at":[30,30],"bottom":11,"size":4,"core":2,"torch_every":6,"landings":[11,20,"x"]} => ERR error: $.landings[2]: expected integer (usage: spiral_shaft {at, size?, core?, top?, bottom, dir?, torch_every?, landings?})
run spiral_shaft {"at":[5,5],"bottom":30,"size":9,"core":1,"dir":"ccw","top":60} => ok, 158 cells changed (r1); entry 5,60,5 | op spiral_shaft 'spiral shaft 6×6 (core 1×1) y60→30 ccw' ai 158 cells d32a64aa49838e1c first [5,60,5=0:0 5,61,5=0:0 5,62,5=0:0]
run spiral_shaft {"at":[5,5],"bottom":30,"size":1} => ok, 91 cells changed (r2); entry 5,68,5 | op spiral_shaft 'spiral shaft 3×3 (core 1×1) y68→30' ai 91 cells 9673a1dde569cfd3 first [5,68,5=0:0 6,67,5=0:0 6,68,5=0:0]
run spiral_shaft {"at":[5],"bottom":30} => ERR error: at: [x,z] (usage: spiral_shaft {at, size?, core?, top?, bottom, dir?, torch_every?, landings?})
run spiral_shaft {"bottom":30} => ERR error: $.at: required (usage: spiral_shaft {at, size?, core?, top?, bottom, dir?, torch_every?, landings?})
run strip_mine {"at":[34,11,31],"length":20,"corridor":"2x3","spacing":3,"branch_length":6} => ok, 240 cells changed (r3); entry 34,11,31 | op strip_mine 'strip mine at y11 along x: 2×3 × 20, 5 branch rows 3 apart (6 long, both)' ai 240 cells c4a36e6bcdb3c151 first [34,11,31=0:0 34,12,31=0:0 34,13,31=0:0]
run strip_mine {"at":[10,14,50],"axis":"z","length":12,"sides":"left","torch_every":4} => ok, 84 cells changed (r4) (12 cells skipped: outside the plot or not placeable); entry 10,14,50 | op strip_mine 'strip mine at y14 along z: 1×2 × 12, 3 branch rows 3 apart (12 long, left)' ai 84 cells f2b71cf9a5c21ae9 first [10,14,50=0:0 10,15,50=0:0 10,14,51=0:0]
run strip_mine {"at":[10,20,10],"axis":"x","length":8,"sides":"none","corridor":"1x2"} => ok, 16 cells changed (r5); entry 10,20,10 | op strip_mine 'strip mine at y20 along x: 1×2 × 8, 2 branch rows 3 apart (12 long, none)' ai 16 cells 11406a2d904194ad first [10,20,10=0:0 10,21,10=0:0 11,20,10=0:0]
run strip_mine {"at":[10,20,10],"sides":"right","spacing":1,"branch_length":3,"length":10} => ok, 34 cells changed (r6); entry 10,20,10 | op strip_mine 'strip mine at y20 along x: 1×2 × 10, 5 branch rows 1 apart (3 long, right)' ai 34 cells 54b6225bdac3ae74 first [18,20,10=0:0 18,21,10=0:0 19,20,10=0:0]
run strip_mine {"length":10} => ERR error: $.at: required (usage: strip_mine {at, axis?, length?, corridor?, spacing?, branch_length?, sides?, torch_every?})
run mine_preset {"id":"nope"} => ERR error: no preset 'nope'; b1.8.1-diamond-y11, b1.8.1-lapis-gold-y16
run mine_preset {} => ERR error: $.id: required (usage: mine_preset {id})
run remove {"from":[40,2,40],"to":[41,3,41]} => ok, 3 cells changed (r7) (5 cells skipped: outside the plot or not placeable) | op remove 'removed 2×2×2' ai 3 cells 2b4cc348aa479415 first [40,2,41=0:0 41,2,41=0:0 41,3,40=0:0]
run remove {"from":[40,40,40],"to":[41,41,41]} => ok, 8 cells changed (r8) | op remove 'removed 2×2×2' ai 8 cells 98a341f7cb432d29 first [40,40,40=0:0 41,40,40=0:0 40,40,41=0:0]
run place {"b":"torch:wall=north","at":[[34,12,32]]} => ok, 1 cells changed (r9) (note: turned 1 wall torch around to hang on the solid side; wall=<side> names the side the supporting block is on) | op place 'placed torch' ai 1 cells 725c486d0722700c first [34,12,32=50:4]
run place {"b":"earth","at":[[34,11,31]]} => ok, 1 cells changed (r10) | op place 'placed ground' ai 1 cells 3456b777b819a7ef first [34,11,31=-1:0]
run describe {"y":11} => y11, rows z5..61, cols x0..53:
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
###################################...................
##################################....................
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#=ground .=dug
run mine_preset {"id":"1.12.2-diamond-y11"} => ok, 760 cells changed (r1); entry 30,65,30 | op mine_preset '1.12.2 diamond strip mine at y 11' ai 760 cells 961a2d2ba9f29b05 first [31,64,30=0:0 31,65,30=0:0 32,63,30=0:0]
run mine_preset {"id":"1.12.2-lapis-iron-y16"} => ok, 611 cells changed (r1); entry 30,65,30 | op mine_preset '1.12.2 lapis and iron strip mine at y 16' ai 611 cells a5bc5c1f74dac68b first [30,64,31=0:0 30,63,32=0:0 30,64,32=0:0]
run mine_preset {"id":"b1.8.1-diamond-y11"} => ok, 928 cells changed (r1); entry 30,65,30 | op mine_preset 'b1.8.1 diamond strip mine at y 11' ai 928 cells 7a0197c968f3fdb9 first [31,64,30=0:0 31,65,30=0:0 32,63,30=0:0]
run mine_preset {"id":"b1.8.1-lapis-gold-y16"} => ok, 779 cells changed (r1); entry 30,65,30 | op mine_preset 'b1.8.1 lapis and gold strip mine at y 16' ai 779 cells 1448cc91c122c4fb first [30,64,31=0:0 30,63,32=0:0 30,64,32=0:0]
describe16 r10 3 blocks, box x0..53 y2..68 z5..61 (54×67×57); torch 3; dug 630 to mine out (y2..68); y2 2 dug · y3 1 dug · y4-10 0 · y11 99 dug · y12 1 torch, 99 dug · y13 40 dug · y14 42 dug · y15 2 torch, 40 dug · y16-19 0 · y20-21 25 dug · y22-29 0 · y30 2 dug · y31 5 dug · y32 9 dug · y33 10 dug · y34-36 4 dug · y37-38 9 dug · y39 6 dug · y40 9 dug · y41 11 dug · y42-43 6 dug · y44-46 7 dug · y47 9 dug · y48 10 dug · y49 9 dug · y50-51 7 dug · y52 9 dug · y53 10 dug · y54 9 dug · y55-56 7 dug · y57 9 dug · y58 10 dug · y59 9 dug · y60 6 dug · y61-68 3 dug
describe3 r10 3 blocks, box x0..53 y2..68 z5..61 (54×67×57); torch 3; dug 630 to mine out (y2..68); y2 2 dug · y3 1 dug · y4-10 0 · y11 99 dug · y12 1 torch, 99 dug · y13 40 dug · y14 42 dug · y15 2 torch, 40 dug · y16-19 0 · y20-21 25 dug · y22-29 0 · y30 2 dug · y31 5 dug · y32 9 dug · y33 10 dug · y34-36 4 dug · y37-38 9 dug · y39 6 dug · y40 9 dug · y41 11 dug · y42-43 6 dug · y44-46 7 dug · y47 9 dug · y48 10 dug · y49 9 dug · y50-51 7 dug · y52 9 dug · y53 10 dug · y54 9 dug · y55-56 7 dug · y57 9 dug · y58 10 dug · y59 9 dug · y60 6 dug · y61-68 3 dug
layer 11
y11, rows z5..61, cols x0..53:
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
###################################...................
##################################....................
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#=ground .=dug
layer 12
y12, rows z5..61, cols x0..53:
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
##################################....................
##################################A...................
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
A=torch:4 #=ground .=dug
wire 9ebe274937dff6c8 dug 42bc78bda4df0611 enc 5b32295e0ae765da rev 10
run clear {} => ok, 633 cells changed (r11) | op clear 'cleared the canvas' ai 633 cells 325c7345a25fef85 first [10,15,54=-1:0 10,15,58=-1:0 34,12,32=-1:0]
describe16 r11 empty mine (plot x,z 0..63, absolute y 1..127 over generated terrain: surface y63..73, bedrock y0-4)
describe3 r11 empty mine (plot x,z 0..63, absolute y 1..127 over generated terrain: surface y63..73, bedrock y0-4)
wire cbf29ce484222325 dug cbf29ce484222325 enc af63b64c8601abfa rev 11
== build 1.12.2
system You are the Design Studio builder: you co-design a Minecraft 1.12.2 build with PoofImaFox on a sandbox canvas (not the world; nothing is spent). Plot: x,z 0..63, y -16..47; +x east, +z south (north = -z); the view starts at the centre (32,0,32). y=0 is the first layer above flat grass; y=-1 is the grass layer and y -16..-1 is solid ground: dig it with remove (that air is dug out when built), then build floors, walls, stairs or ladders down there (basements, cellars, foundations); "earth" fills dug cells back. Edit only with the tools, coordinates inclusive. Prefer few big calls (box, line) over many places. A saved design is at most 32 wide/deep and 32 high, dug layers included: stay inside that.
Blocks: any 1.12.2 block by its minecraft name (oak_stairs), display name, or name:variant (wool:red, stained_glass:light_blue, log:spruce, planks:birch, concrete:white); unsure of a name: find_blocks. Add :<dir> for a facing: stairs = the way up, doors = the side they open to (place the lower half, the top is added; on a full block), beds = the head's side (the head is added), chests/furnaces/pumpkins = the front, gates = the way through; torch:floor (stands on the block below), torch:wall=<side>, ladder:wall=<side> (<side> = where the supporting block is, seen from the torch or ladder: torch:wall=west hangs on the solid block to its west, on that block's east face; it must be a full block); :top for upper slabs and upside-down stairs. Doors and windows need openings in walls.
Building (97): stone cobblestone mossy_cobblestone planks log log2 brick_block stonebrick sandstone red_sandstone <material>_slab stone_slab2 <wood>_stairs <material>_stairs red_sandstone_stairs nether_brick red_nether_brick quartz_block …
Decoration (41): fence <wood>_fence nether_brick_fence cobblestone_wall ladder torch bed standing_sign wall_sign standing_banner wall_banner skull flower_pot chest ender_chest crafting_table furnace enchanting_table …
Colored (145): wool carpet stained_glass stained_glass_pane hardened_clay stained_hardened_clay concrete concrete_powder <colour>_glazed_terracotta <colour>_shulker_box
Nature (65): grass dirt mycelium grass_path farmland sand gravel clay soul_sand netherrack snow snow_layer ice packed_ice sapling leaves leaves2 yellow_flower …
Redstone (42): redstone_wire redstone_torch unpowered_repeater unpowered_comparator redstone_block redstone_lamp lever stone_button wooden_button <material>_pressure_plate piston sticky_piston observer dispenser dropper hopper daylight_detector daylight_detector_inverted …
Transport (4): rail <material>_rail
Misc (4): water lava beacon dragon_egg
colours: white orange magenta light_blue yellow lime pink gray silver(light_gray) cyan purple blue brown green red black; woods: oak spruce birch jungle acacia dark_oak
When the user names "the selection" use its box. Keep each reply to a few calls; one place takes at most about 100 cells (use box, line or replace for runs): a reply that hits the output limit loses the call it was writing. A tool error says what was wrong and the usage: fix that, don't repeat the call. Reply after the edits with ONE or two short sentences on what changed, in plain words (no coordinates unless asked).
run describe {} => r0 empty (plot x,z 0..63, y -16..47; y0 stands on the grass, below is solid ground)
run find_blocks {"q":"stairs"} => oak_stairs = Oak Stairs (Building, facing)
spruce_stairs = Spruce Stairs (Building, facing)
birch_stairs = Birch Stairs (Building, facing)
jungle_stairs = Jungle Stairs (Building, facing)
acacia_stairs = Acacia Stairs (Building, facing)
dark_oak_stairs = Dark Oak Stairs (Building, facing)
stone_stairs = Cobblestone Stairs (Building, facing)
brick_stairs = Brick Stairs (Building, facing)
stone_brick_stairs = Stone Brick Stairs (Building, facing)
nether_brick_stairs = Nether Brick Stairs (Building, facing)
sandstone_stairs = Sandstone Stairs (Building, facing)
red_sandstone_stairs = Red Sandstone Stairs (Building, facing)
quartz_stairs = Quartz Stairs (Building, facing)
purpur_stairs = Purpur Stairs (Building, facing)
run find_blocks {"q":"red","n":3} => red_sandstone = Red Sandstone (Building)
red_sandstone:chiseled = Chiseled Red Sandstone (Building)
red_sandstone:smooth = Smooth Red Sandstone (Building)
run find_blocks {"q":"plank","n":99} => planks:oak = Oak Planks (Building)
planks:spruce = Spruce Planks (Building)
planks:birch = Birch Planks (Building)
planks:jungle = Jungle Planks (Building)
planks:acacia = Acacia Planks (Building)
planks:dark_oak = Dark Oak Planks (Building)
run find_blocks {"q":"zzqq"} => no 1.12.2 block matches 'zzqq'
run find_blocks {"q":"glas"} => glass = Glass (Building)
glass_pane = Glass Pane (Building)
stained_glass:white = White Stained Glass (Colored)
stained_glass:orange = Orange Stained Glass (Colored)
stained_glass:magenta = Magenta Stained Glass (Colored)
stained_glass:light_blue = Light Blue Stained Glass (Colored)
stained_glass:yellow = Yellow Stained Glass (Colored)
stained_glass:lime = Lime Stained Glass (Colored)
stained_glass:pink = Pink Stained Glass (Colored)
stained_glass:gray = Gray Stained Glass (Colored)
stained_glass:silver = Light Gray Stained Glass (Colored)
stained_glass:cyan = Cyan Stained Glass (Colored)
stained_glass:purple = Purple Stained Glass (Colored)
stained_glass:blue = Blue Stained Glass (Colored)
stained_glass:brown = Brown Stained Glass (Colored)
run find_blocks {} => ERR error: $.q: required (usage: find_blocks {q, n?})
run place {"b":"planks","at":[[10,0,10],[11,0,10],[12,0,10]]} => ok, 3 cells changed (r1) | op place 'placed oak planks' ai 3 cells ce0513031fb1eb4a first [10,0,10=5:0 11,0,10=5:0 12,0,10=5:0]
run place {"b":"planks"} => ERR error: $.at: required (usage: place {b, at})
run place {"b":"planks","at":[1,2,3]} => ok, 1 cells changed (r2) | op place 'placed oak planks' ai 1 cells 66b2e49d96bea0e1 first [1,2,3=5:0]
run place {"b":"planks","at":[[1,2]]} => ERR error: a position is [x,y,z] (usage: place {b, at})
run place {"b":"planks","at":"x"} => ERR error: $.at: expected array (usage: place {b, at})
run place {"at":[[1,1,1]]} => ERR error: $.b: required (usage: place {b, at})
run place {"b":"grass","at":[[1,1,1]]} => ok, 1 cells changed (r3) | op place 'placed grass block' ai 1 cells 3ba74e25baa5bd3b first [1,1,1=2:0]
run place {"b":"planks","at":[[70,0,10],[-1,0,0],[5,60,5],[5,0,5]]} => ok, 1 cells changed (r4) (3 cells skipped: outside the plot or not placeable) | op place 'placed oak planks' ai 1 cells 7ce0d362ba737c59 first [5,0,5=5:0]
run box {"b":"cobblestone","from":[20,0,20],"to":[26,0,26]} => ok, 49 cells changed (r5) | op fill 'filled 7×1×7 with cobblestone' ai 49 cells 3903e3ce8284c3b6 first [20,0,20=4:0 21,0,20=4:0 22,0,20=4:0]
run box {"b":"planks","from":[20,1,20],"to":[26,4,26],"mode":"hollow"} => ok, 146 cells changed (r6) | op hollow 'hollow 7×4×7 with oak planks' ai 146 cells 7605e188f9b9afda first [20,1,20=5:0 21,1,20=5:0 22,1,20=5:0]
run box {"b":"stone","from":[30,0,30],"to":[33,3,33],"mode":"walls"} => ok, 48 cells changed (r7) | op walls 'walls 4×4×4 with stone' ai 48 cells df4287ca225dca25 first [30,0,30=1:0 31,0,30=1:0 32,0,30=1:0]
run box {"b":"stone","from":[30,0,30],"to":[33,3,33],"mode":"weird"} => ERR error: $.mode: value is not in enum ("fill", "hollow", "walls") (usage: box {b, from, to, mode?})
run box {"b":"stone","from":[0,0,0],"to":[63,40,63]} => ERR error: box of 167936 cells is too big (max 40000) (usage: box {b, from, to, mode?})
run box {"b":"stone","from":[0,0,0]} => ERR error: $.to: required (usage: box {b, from, to, mode?})
run box {"b":"stone","from":[0,0,0],"to":[1,1,1],"mode":5} => ERR error: $.mode: expected string (usage: box {b, from, to, mode?})
run line {"b":"fence","from":[0,0,0],"to":[6,3,2]} => ok, 7 cells changed (r8) | op line 'line of oak fence' ai 7 cells 2b18746c072f79a1 first [0,0,0=85:0 1,0,0=85:0 2,1,1=85:0]
run line {"b":"cobblestone","from":[40,5,40],"to":[40,5,40]} => ok, 1 cells changed (r9) | op line 'line of cobblestone' ai 1 cells 290a5f0650eae5d7 first [40,5,40=4:0]
run place {"b":"wooden_door:south","at":[[23,1,26]]} => ok, 2 cells changed (r10) | op place 'placed oak door' ai 2 cells bc25291d98aea4b7 first [23,1,26=64:3 23,2,26=64:11]
run place {"b":"bed:east","at":[[22,1,22]]} => ok, 2 cells changed (r11) | op place 'placed bed' ai 2 cells 667490bccf55e2bf first [22,1,22=26:3 23,1,22=26:11]
run place {"b":"chest:north","at":[[21,1,21]]} => ok, 1 cells changed (r12) | op place 'placed chest' ai 1 cells 9347fa97d46c14b4 first [21,1,21=54:2]
run place {"b":"chest:west","at":[[21,1,22]]} => ok, 1 cells changed (r13) | op place 'placed chest' ai 1 cells 7645daf0eee9de33 first [21,1,22=54:4]
run place {"b":"chest:west","at":[[21,1,23]]} => ok, 1 cells changed (r14) | op place 'placed chest' ai 1 cells 9e6f564f137a0c50 first [21,1,23=54:4]
run place {"b":"torch:wall=west","at":[[21,2,24]]} => ok, 1 cells changed (r15) | op place 'placed torch' ai 1 cells 25acd1548c1187e3 first [21,2,24=50:1]
run place {"b":"torch:wall=east","at":[[21,2,24]]} => ok, 1 cells changed (r16) (note: turned 1 wall torch around to hang on the solid side; wall=<side> names the side the supporting block is on) | op place 'placed torch' ai 1 cells 25acd1548c1187e3 first [21,2,24=50:1]
run place {"b":"torch:floor","at":[[24,1,24]]} => ok, 1 cells changed (r17) | op place 'placed torch' ai 1 cells 19650ef8c1ddb321 first [24,1,24=50:5]
run place {"b":"torch","at":[[24,1,23]]} => ok, 1 cells changed (r18) | op place 'placed torch' ai 1 cells dc336b30c140ee54 first [24,1,23=50:5]
run place {"b":"torch:north","at":[[50,0,50]]} => ok, 1 cells changed (r19) (note: stood 1 torch with no wall on the floor; wall=<side> names the side the supporting block is on) | op place 'placed torch' ai 1 cells e2ca068836ecde2e first [50,0,50=50:5]
run place {"b":"torch:on=up","at":[[50,0,51]]} => ERR error: 'torch:on=up': the side is north, east, south or west (where the supporting block is) (usage: place {b, at})
run place {"b":"torch:wall=sideways","at":[[50,0,51]]} => ERR error: 'torch:wall=sideways': the side is north, east, south or west (where the supporting block is) (usage: place {b, at})
run place {"b":"ladder:wall=south","at":[[22,2,25]]} => ok, 1 cells changed (r20) | op place 'placed ladder' ai 1 cells 14ef8b67330c7364 first [22,2,25=65:2]
run place {"b":"ladder:north","at":[[45,0,45]]} => ok, 1 cells changed (r21) | op place 'placed ladder' ai 1 cells fe0526aa501e3098 first [45,0,45=65:3]
run place {"b":"oak_stairs:north","at":[[24,1,21],[25,1,21]]} => ok, 2 cells changed (r22) | op place 'placed oak stairs' ai 2 cells 4373ae3a3bda91f0 first [24,1,21=53:3 25,1,21=53:3]
run place {"b":"cobblestone_stairs:east","at":[[24,2,21]]} => ok, 1 cells changed (r23) | op place 'placed cobblestone stairs' ai 1 cells 7a03732d84aabf76 first [24,2,21=67:0]
run place {"b":"wool:red","at":[[24,3,21]]} => ok, 1 cells changed (r24) | op place 'placed red wool' ai 1 cells 354860b6f9e14507 first [24,3,21=35:14]
run place {"b":"log:spruce","at":[[25,3,21]]} => ok, 1 cells changed (r25) | op place 'placed spruce log' ai 1 cells 1a4d76e9bdadb252 first [25,3,21=17:1]
run place {"b":"glass","at":[[20,2,23],[20,3,23]]} => ok, 2 cells changed (r26) | op place 'placed glass' ai 2 cells 05ee019742576cc8 first [20,2,23=20:0 20,3,23=20:0]
run replace {"a":"oak_stairs","b":"cobblestone_stairs"} => ok, 2 cells changed (r27) | op replace 'replaced oak_stairs with cobblestone stairs' ai 2 cells 5341898b1ebc57d6 first [24,1,21=67:3 25,1,21=67:3]
run replace {"a":"wool","b":"wool:blue","from":[20,0,20],"to":[26,4,26]} => nothing changed
run replace {"a":"any","b":"stone_bricks","from":[30,0,30],"to":[31,1,31]} => ok, 6 cells changed (r28) | op replace 'replaced any with stone bricks' ai 6 cells 68aa18e4ae8d5a26 first [30,0,30=98:0 31,0,30=98:0 30,0,31=98:0]
run replace {"a":"glass","b":"planks","from":[0,0,0]} => ERR error: to is required ([x,y,z]) (usage: replace {a, b, from?, to?})
run replace {"b":"planks"} => ERR error: $.a: required (usage: replace {a, b, from?, to?})
run replace {"a":"nothing_here","b":"planks"} => ERR error: unknown 1.12.2 block 'nothing_here'; find_blocks looks names up (usage: replace {a, b, from?, to?})
run remove {"from":[30,-3,30],"to":[33,-1,33]} => ok, 48 cells changed (r29) | op remove 'removed 4×3×4' ai 48 cells 8dd6842273a57d6d first [30,-3,30=0:0 31,-3,30=0:0 32,-3,30=0:0]
run remove {"from":[31,0,31],"to":[32,3,32]} => nothing changed
run remove {"from":[31,0,31]} => ERR error: $.to: required (usage: remove {from, to})
run box {"b":"cobblestone","from":[30,-4,30],"to":[33,-4,33]} => ok, 16 cells changed (r30) | op fill 'filled 4×1×4 with cobblestone' ai 16 cells f2c6e5c07f871fed first [30,-4,30=4:0 31,-4,30=4:0 32,-4,30=4:0]
run place {"b":"cobblestone_stairs:north","at":[[31,-3,31]]} => ok, 1 cells changed (r31) | op place 'placed cobblestone stairs' ai 1 cells a6a24d2603d81d32 first [31,-3,31=67:3]
run place {"b":"earth","at":[[32,-1,32],[40,-1,40],[40,3,40]]} => ok, 1 cells changed (r32) | op place 'placed ground' ai 1 cells 2ad4790e3a233450 first [32,-1,32=-1:0]
run place {"b":"ground","at":[[33,-1,33]]} => ok, 1 cells changed (r33) | op place 'placed ground' ai 1 cells ffd161472126359c first [33,-1,33=-1:0]
run remove {"from":[23,1,26],"to":[23,1,26]} => ok, 2 cells changed (r34) | op remove 'removed 1×1×1' ai 2 cells f7a882bb8cbaac48 first [23,2,26=0:0 23,1,26=0:0]
run remove {"from":[22,1,22],"to":[22,1,22]} => ok, 2 cells changed (r35) | op remove 'removed 1×1×1' ai 2 cells 947792d42977c406 first [23,1,22=0:0 22,1,22=0:0]
run spiral_shaft {"at":[1,1],"bottom":5} => ERR error: spiral_shaft needs a mine canvas (switch the studio to Mine)
run nope {} => ERR error: no tool 'nope'; the tools are place, box, line, remove, replace, clear, describe, find_blocks
run describe {"y":0} => y0, rows z0..50, cols x0..50:
AA.................................................
...................................................
...................................................
...................................................
...................................................
.....B.............................................
...................................................
...................................................
...................................................
...................................................
..........BBB......................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
...................................................
...................................................
...................................................
..............................DDEE.................
..............................D..E.................
..............................E..E.................
..............................EEEE.................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
.............................................F.....
...................................................
...................................................
...................................................
...................................................
..................................................G
A=fence B=planks:oak C=cobblestone D=stonebrick E=stone F=ladder:3 G=torch
run describe {"y":-3} => y-3 (below ground), rows z0..50, cols x0..50:
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
##############################....#################
##############################.A..#################
##############################....#################
##############################....#################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
A=stone_stairs:3 #=ground .=dug
run describe {"y":"x"} => ERR error: $.y: expected integer (usage: describe {y?})
describe16 r35 277 blocks, box x0..50 y-4..5 z0..50 (51×10×51); wooden planks 138, cobblestone 66, stone 42, fence 7, stone bricks 6, cobblestone stairs 4, torch 4, chest 3, glass 2, ladder 2, grass 1, red wool 1, spruce log 1; dug 45 below ground (y-3..-1); y-4 16 cobblestone · y-3 1 cobblestone stairs, 15 dug · y-2 16 dug · y-1 14 dug · y0 69 cobblestone+stone · y1 60 wooden planks+stone · y2 42 wooden planks+stone · y3 39 wooden planks+stone · y4 49 wooden planks · y5 1 cobblestone
describe3 r35 277 blocks, box x0..50 y-4..5 z0..50 (51×10×51); wooden planks 138, cobblestone 66, stone 42, …; dug 45 below ground (y-3..-1); y-4 16 cobblestone · y-3 1 cobblestone stairs, 15 dug · y-2 16 dug · y-1 14 dug · y0 69 cobblestone+stone · y1 60 wooden planks+stone · y2 42 wooden planks+stone · y3 39 wooden planks+stone · y4 49 wooden planks · y5 1 cobblestone
layer 0
y0, rows z0..50, cols x0..50:
AA.................................................
...................................................
...................................................
...................................................
...................................................
.....B.............................................
...................................................
...................................................
...................................................
...................................................
..........BBB......................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
....................CCCCCCC........................
...................................................
...................................................
...................................................
..............................DDEE.................
..............................D..E.................
..............................E..E.................
..............................EEEE.................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
.............................................F.....
...................................................
...................................................
...................................................
...................................................
..................................................G
A=fence B=planks C=cobblestone D=stone_bricks E=stone F=ladder:3 G=torch
layer 1
y1, rows z0..50, cols x0..50:
...................................................
.AB................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
....................CCCCCCC........................
....................CDCCEEC........................
....................CF..CCC........................
....................CFCCGCC........................
....................CCCCGCC........................
....................CCCCCCC........................
....................CCC.CCC........................
...................................................
...................................................
...................................................
..............................HHII.................
..............................H..I.................
..............................I..I.................
..............................IIII.................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
...................................................
A=grass:0 B=fence C=planks D=chest E=stone_stairs:3 F=chest:4 G=torch H=stone_bricks I=stone
layer -1
y-1 (below ground), rows z0..50, cols x0..50:
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
##############################....#################
##############################....#################
##############################..#.#################
##############################...##################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
#=ground .=dug
layer -3
y-3 (below ground), rows z0..50, cols x0..50:
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
##############################....#################
##############################.A..#################
##############################....#################
##############################....#################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
A=stone_stairs:3 #=ground .=dug
layer -4
y-4 (below ground), rows z0..50, cols x0..50:
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
##############################AAAA#################
##############################AAAA#################
##############################AAAA#################
##############################AAAA#################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
###################################################
A=cobblestone #=ground .=dug
wire a352bf286c42e6d2 dug eca5c0418fa9f76a enc f8c75b35a3705949 rev 35
run clear {} => ok, 322 cells changed (r36) | op clear 'cleared the canvas' ai 322 cells ecd3a37529b973f9 first [10,0,10=0:0 11,0,10=0:0 12,0,10=0:0]
describe16 r36 empty (plot x,z 0..63, y -16..47; y0 stands on the grass, below is solid ground)
describe3 r36 empty (plot x,z 0..63, y -16..47; y0 stands on the grass, below is solid ground)
wire cbf29ce484222325 dug cbf29ce484222325 enc af63b64c8601abfa rev 36
== mine 1.12.2
system You are the Design Studio builder: you co-design an underground MINE for Minecraft 1.12.2 with PoofImaFox on a sandbox canvas over generated terrain (not the world). Plot x,z 0..63 (+x east, +z south), ABSOLUTE world y 1..127; the surface is y 63..73 here, bedrock y 0-4. The design is the cells to mine out: remove digs a box (never bedrock), "earth" fills dug cells back; blocks you place (torches, ladders, rails, chests) go in after their cell is dug. Prefer the templates: spiral_shaft (a walkable spiral staircase around a core, from the surface down), strip_mine (main corridor + 1x2 branches, spacing solid blocks apart), remove for rooms; tunnels 2 high, steps at most 1. Connect everything to the shaft. Ore heights: coal y0-127 best 48, iron y0-63 best 16, gold y0-31 best 11, redstone y0-15 best 11, diamond y0-15 best 11, lapis y0-30 peak 16 best 16; lava lakes y≤10; bedrock y0-4. Presets (mine_preset): 1.12.2-diamond-y11, 1.12.2-lapis-iron-y16. Blocks by minecraft name (torch:wall=<side of the supporting block>, ladder:wall=<side>, rail, chest:<front>); unsure: find_blocks. Keep each reply to a few calls; one place takes at most about 100 cells (use box, line or replace for runs): a reply that hits the output limit loses the call it was writing. A tool error says what was wrong and the usage: fix that, don't repeat the call. Reply after the edits with ONE or two short sentences on what changed, in plain words (no coordinates unless asked).
run describe {} => r0 empty mine (plot x,z 0..63, absolute y 1..127 over generated terrain: surface y63..73, bedrock y0-4)
run spiral_shaft {"at":[30,30],"bottom":11,"size":4,"core":2,"torch_every":6,"landings":[11,20,"x"]} => ERR error: $.landings[2]: expected integer (usage: spiral_shaft {at, size?, core?, top?, bottom, dir?, torch_every?, landings?})
run spiral_shaft {"at":[5,5],"bottom":30,"size":9,"core":1,"dir":"ccw","top":60} => ok, 158 cells changed (r1); entry 5,60,5 | op spiral_shaft 'spiral shaft 6×6 (core 1×1) y60→30 ccw' ai 158 cells d32a64aa49838e1c first [5,60,5=0:0 5,61,5=0:0 5,62,5=0:0]
run spiral_shaft {"at":[5,5],"bottom":30,"size":1} => ok, 91 cells changed (r2); entry 5,68,5 | op spiral_shaft 'spiral shaft 3×3 (core 1×1) y68→30' ai 91 cells 9673a1dde569cfd3 first [5,68,5=0:0 6,67,5=0:0 6,68,5=0:0]
run spiral_shaft {"at":[5],"bottom":30} => ERR error: at: [x,z] (usage: spiral_shaft {at, size?, core?, top?, bottom, dir?, torch_every?, landings?})
run spiral_shaft {"bottom":30} => ERR error: $.at: required (usage: spiral_shaft {at, size?, core?, top?, bottom, dir?, torch_every?, landings?})
run strip_mine {"at":[34,11,31],"length":20,"corridor":"2x3","spacing":3,"branch_length":6} => ok, 240 cells changed (r3); entry 34,11,31 | op strip_mine 'strip mine at y11 along x: 2×3 × 20, 5 branch rows 3 apart (6 long, both)' ai 240 cells c4a36e6bcdb3c151 first [34,11,31=0:0 34,12,31=0:0 34,13,31=0:0]
run strip_mine {"at":[10,14,50],"axis":"z","length":12,"sides":"left","torch_every":4} => ok, 84 cells changed (r4) (12 cells skipped: outside the plot or not placeable); entry 10,14,50 | op strip_mine 'strip mine at y14 along z: 1×2 × 12, 3 branch rows 3 apart (12 long, left)' ai 84 cells f2b71cf9a5c21ae9 first [10,14,50=0:0 10,15,50=0:0 10,14,51=0:0]
run strip_mine {"at":[10,20,10],"axis":"x","length":8,"sides":"none","corridor":"1x2"} => ok, 16 cells changed (r5); entry 10,20,10 | op strip_mine 'strip mine at y20 along x: 1×2 × 8, 2 branch rows 3 apart (12 long, none)' ai 16 cells 11406a2d904194ad first [10,20,10=0:0 10,21,10=0:0 11,20,10=0:0]
run strip_mine {"at":[10,20,10],"sides":"right","spacing":1,"branch_length":3,"length":10} => ok, 34 cells changed (r6); entry 10,20,10 | op strip_mine 'strip mine at y20 along x: 1×2 × 10, 5 branch rows 1 apart (3 long, right)' ai 34 cells 54b6225bdac3ae74 first [18,20,10=0:0 18,21,10=0:0 19,20,10=0:0]
run strip_mine {"length":10} => ERR error: $.at: required (usage: strip_mine {at, axis?, length?, corridor?, spacing?, branch_length?, sides?, torch_every?})
run mine_preset {"id":"nope"} => ERR error: no preset 'nope'; 1.12.2-diamond-y11, 1.12.2-lapis-iron-y16
run mine_preset {} => ERR error: $.id: required (usage: mine_preset {id})
run remove {"from":[40,2,40],"to":[41,3,41]} => ok, 3 cells changed (r7) (5 cells skipped: outside the plot or not placeable) | op remove 'removed 2×2×2' ai 3 cells 2b4cc348aa479415 first [40,2,41=0:0 41,2,41=0:0 41,3,40=0:0]
run remove {"from":[40,40,40],"to":[41,41,41]} => ok, 8 cells changed (r8) | op remove 'removed 2×2×2' ai 8 cells 98a341f7cb432d29 first [40,40,40=0:0 41,40,40=0:0 40,40,41=0:0]
run place {"b":"torch:wall=north","at":[[34,12,32]]} => ok, 1 cells changed (r9) (note: turned 1 wall torch around to hang on the solid side; wall=<side> names the side the supporting block is on) | op place 'placed torch' ai 1 cells 725c486d0722700c first [34,12,32=50:4]
run place {"b":"earth","at":[[34,11,31]]} => ok, 1 cells changed (r10) | op place 'placed ground' ai 1 cells 3456b777b819a7ef first [34,11,31=-1:0]
run describe {"y":11} => y11, rows z5..61, cols x0..53:
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
###################################...................
##################################....................
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#=ground .=dug
run mine_preset {"id":"1.12.2-diamond-y11"} => ok, 760 cells changed (r1); entry 30,65,30 | op mine_preset '1.12.2 diamond strip mine at y 11' ai 760 cells 961a2d2ba9f29b05 first [31,64,30=0:0 31,65,30=0:0 32,63,30=0:0]
run mine_preset {"id":"1.12.2-lapis-iron-y16"} => ok, 611 cells changed (r1); entry 30,65,30 | op mine_preset '1.12.2 lapis and iron strip mine at y 16' ai 611 cells a5bc5c1f74dac68b first [30,64,31=0:0 30,63,32=0:0 30,64,32=0:0]
run mine_preset {"id":"b1.8.1-diamond-y11"} => ok, 928 cells changed (r1); entry 30,65,30 | op mine_preset 'b1.8.1 diamond strip mine at y 11' ai 928 cells 7a0197c968f3fdb9 first [31,64,30=0:0 31,65,30=0:0 32,63,30=0:0]
run mine_preset {"id":"b1.8.1-lapis-gold-y16"} => ok, 779 cells changed (r1); entry 30,65,30 | op mine_preset 'b1.8.1 lapis and gold strip mine at y 16' ai 779 cells 1448cc91c122c4fb first [30,64,31=0:0 30,63,32=0:0 30,64,32=0:0]
describe16 r10 3 blocks, box x0..53 y2..68 z5..61 (54×67×57); torch 3; dug 630 to mine out (y2..68); y2 2 dug · y3 1 dug · y4-10 0 · y11 99 dug · y12 1 torch, 99 dug · y13 40 dug · y14 42 dug · y15 2 torch, 40 dug · y16-19 0 · y20-21 25 dug · y22-29 0 · y30 2 dug · y31 5 dug · y32 9 dug · y33 10 dug · y34-36 4 dug · y37-38 9 dug · y39 6 dug · y40 9 dug · y41 11 dug · y42-43 6 dug · y44-46 7 dug · y47 9 dug · y48 10 dug · y49 9 dug · y50-51 7 dug · y52 9 dug · y53 10 dug · y54 9 dug · y55-56 7 dug · y57 9 dug · y58 10 dug · y59 9 dug · y60 6 dug · y61-68 3 dug
describe3 r10 3 blocks, box x0..53 y2..68 z5..61 (54×67×57); torch 3; dug 630 to mine out (y2..68); y2 2 dug · y3 1 dug · y4-10 0 · y11 99 dug · y12 1 torch, 99 dug · y13 40 dug · y14 42 dug · y15 2 torch, 40 dug · y16-19 0 · y20-21 25 dug · y22-29 0 · y30 2 dug · y31 5 dug · y32 9 dug · y33 10 dug · y34-36 4 dug · y37-38 9 dug · y39 6 dug · y40 9 dug · y41 11 dug · y42-43 6 dug · y44-46 7 dug · y47 9 dug · y48 10 dug · y49 9 dug · y50-51 7 dug · y52 9 dug · y53 10 dug · y54 9 dug · y55-56 7 dug · y57 9 dug · y58 10 dug · y59 9 dug · y60 6 dug · y61-68 3 dug
layer 11
y11, rows z5..61, cols x0..53:
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
###################################...................
##################################....................
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#=ground .=dug
layer 12
y12, rows z5..61, cols x0..53:
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
##################################....................
##################################A...................
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
#####################################.###.###.###.###.
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
######################################################
A=torch:4 #=ground .=dug
wire 9ebe274937dff6c8 dug 42bc78bda4df0611 enc 5b32295e0ae765da rev 10
run clear {} => ok, 633 cells changed (r11) | op clear 'cleared the canvas' ai 633 cells 325c7345a25fef85 first [10,15,54=-1:0 10,15,58=-1:0 34,12,32=-1:0]
describe16 r11 empty mine (plot x,z 0..63, absolute y 1..127 over generated terrain: surface y63..73, bedrock y0-4)
describe3 r11 empty mine (plot x,z 0..63, absolute y 1..127 over generated terrain: surface y63..73, bedrock y0-4)
wire cbf29ce484222325 dug cbf29ce484222325 enc af63b64c8601abfa rev 11
attached torch:wall=west => (50, 1)
attached torch:on=east => (50, 2)
attached torch:against-north => (50, 3)
attached torch:support_south => (50, 4)
attached ladder:wall=north => (65, 3)
attached ladder:east => (65, 4)
attached torch:floor => (50, 5)
attached torch:ground => (50, 5)
attached ladder:floor => null
attached torch:5 => null
attached torch:north => (50, 3)
attached stairs:north => null
attached torch => null
attached ladder:wall=up => THROW 'ladder:wall=up': the side is north, east, south or west (where the supporting block is)
attached torch:x=y => null
== saves b1.8.1
save '  ' new=False => ok False #0 r0 blocks 0 created False: a design needs a name
  session # '' off (0, 0, 0)
save 'empty' new=False => ok False #0 r0 blocks 0 created False: the canvas is empty
  session # '' off (0, 0, 0)
run box {"b":"cobblestone","from":[30,0,30],"to":[34,0,34]} => ok, 25 cells changed (r1) | op fill 'filled 5×1×5 with cobblestone' ai 25 cells 73ab3386bf74f08e first [30,0,30=4:0 31,0,30=4:0 32,0,30=4:0]
run box {"b":"planks","from":[30,1,30],"to":[34,3,34],"mode":"walls"} => ok, 48 cells changed (r2) | op walls 'walls 5×3×5 with wooden planks' ai 48 cells 3bfbfb23bd4f4c3d first [30,1,30=5:0 31,1,30=5:0 32,1,30=5:0]
run place {"b":"wooden_door:south","at":[[32,1,34]]} => ok, 2 cells changed (r3) | op place 'placed wooden door' ai 2 cells 8070e0b1aea8aa9d first [32,1,34=64:3 32,2,34=64:11]
run place {"b":"torch:wall=west","at":[[31,2,32]]} => ok, 1 cells changed (r4) | op place 'placed torch' ai 1 cells 98c838cc926ae0a7 first [31,2,32=50:1]
run place {"b":"oak_stairs:north","at":[[33,1,31]]} => ok, 1 cells changed (r5) | op place 'placed wooden stairs' ai 1 cells 415d7a89d5d55d5a first [33,1,31=53:3]
run place {"b":"glass","at":[[30,2,32],[34,2,32]]} => ok, 2 cells changed (r6) | op place 'placed glass' ai 2 cells 8bffd919510a821f first [30,2,32=20:0 34,2,32=20:0]
run box {"b":"planks","from":[30,4,30],"to":[34,4,34]} => ok, 25 cells changed (r7) | op fill 'filled 5×1×5 with wooden planks' ai 25 cells 08bf33140507c9f7 first [30,4,30=5:0 31,4,30=5:0 32,4,30=5:0]
save '  "Fox's #hut"
 ' new=False => ok True #1 r1 blocks 100 created True: saved as design #1 r1 (b1.8.1): Fox's  hut 5×5×5, 100 blocks (palette: wooden planks, cobblestone, glass, wooden door, torch…)
  session #1 'Fox's  hut' off (0, 0, 0)
  rec #1 'Fox's  hut' status accepted origin - by poofimafox/poofimafox cost 0.25
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's  hut 5×5×5, 100 blocks (palette: wooden planks, cobblestone, glass, wooden door, torch…)
  eval drawn exactly
  bill wooden planks 69/0/0/69 ; cobblestone 25/0/0/25 ; glass 2/0/0/2 ; wooden door 1/0/0/1 ; torch 1/0/0/1 ; wooden stairs 1/0/0/1 
  dsl
# drawn in the Design Studio
blueprint Fox's  hut
size 5x5x5
version b1.8.1
facing south
palette P=planks C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3
layer 0
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 1
PPPPP
P..OP
P...P
P...P
PPWPP
layer 2
PPPPP
P...P
GT..G
P...P
PPWPP
layer 3
PPPPP
P...P
P...P
P...P
PPPPP
layer 4
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 8c5d7dadc587439c snap 100 25 7a89a65ad2592441
run remove {"from":[31,-3,31],"to":[33,-1,33]} => ok, 27 cells changed (r8) | op remove 'removed 3×3×3' ai 27 cells 9f13c350e45becb7 first [31,-3,31=0:0 32,-3,31=0:0 33,-3,31=0:0]
run box {"b":"cobblestone","from":[31,-4,31],"to":[33,-4,33]} => ok, 9 cells changed (r9) | op fill 'filled 3×1×3 with cobblestone' ai 9 cells e5b9a942cd77ecbf first [31,-4,31=4:0 32,-4,31=4:0 33,-4,31=4:0]
run place {"b":"ladder:wall=north","at":[[32,-3,32]]} => ok, 1 cells changed (r10) | op place 'placed ladder' ai 1 cells a10c00331614c87c first [32,-3,32=65:3]
save 'Fox's hut' new=False => ok True #1 r2 blocks 110 created False: saved #1 r2 (b1.8.1): Fox's hut 5×5×9, 110 blocks (palette: wooden planks, cobblestone, glass, wooden door, torch…), 4 below ground
  session #1 'Fox's hut' off (0, 0, 0)
  rec #1 'Fox's hut' status accepted origin - by poofimafox/poofimafox cost 0.5
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's hut 5×5×9, 110 blocks (palette: wooden planks, cobblestone, glass, wooden door, torch…), 4 below ground
  eval drawn exactly
  bill wooden planks 69/0/0/69 ; cobblestone 34/0/0/34 ; glass 2/0/0/2 ; wooden door 1/0/0/1 ; torch 1/0/0/1 ; wooden stairs 1/0/0/1 ; ladder 1/0/0/1 
  dsl
# anchor 2,4,0
# drawn in the Design Studio
blueprint Fox's hut
size 5x9x5
version b1.8.1
ground 4
facing south
palette P=planks C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3 A=ladder:3
layer 0
_____
_CCC_
_CCC_
_CCC_
_____
layer 1
_____
_..._
_.A._
_..._
_____
layer 2-3
_____
_..._
_..._
_..._
_____
layer 4
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 5
PPPPP
P..OP
P...P
P...P
PPWPP
layer 6
PPPPP
P...P
GT..G
P...P
PPWPP
layer 7
PPPPP
P...P
P...P
P...P
PPPPP
layer 8
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r2 kiwi player 'saved from the Design Studio by kiwi' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 0453cb2d93ff0846 snap 110 51 1defbbb11d39a11d
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 8c5d7dadc587439c snap 100 25 7a89a65ad2592441
save 'Fox's hut' new=False => ok True #1 r2 blocks 110 created False: saved #1 r2 (b1.8.1): Fox's hut 5×5×9, 110 blocks (palette: wooden planks, cobblestone, glass, wooden door, torch…), 4 below ground
  session #1 'Fox's hut' off (0, 0, 0)
  rec #1 'Fox's hut' status accepted origin - by poofimafox/poofimafox cost 0.75
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's hut 5×5×9, 110 blocks (palette: wooden planks, cobblestone, glass, wooden door, torch…), 4 below ground
  eval drawn exactly
  bill wooden planks 69/0/0/69 ; cobblestone 34/0/0/34 ; glass 2/0/0/2 ; wooden door 1/0/0/1 ; torch 1/0/0/1 ; wooden stairs 1/0/0/1 ; ladder 1/0/0/1 
  dsl
# anchor 2,4,0
# drawn in the Design Studio
blueprint Fox's hut
size 5x9x5
version b1.8.1
ground 4
facing south
palette P=planks C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3 A=ladder:3
layer 0
_____
_CCC_
_CCC_
_CCC_
_____
layer 1
_____
_..._
_.A._
_..._
_____
layer 2-3
_____
_..._
_..._
_..._
_____
layer 4
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 5
PPPPP
P..OP
P...P
P...P
PPWPP
layer 6
PPPPP
P...P
GT..G
P...P
PPWPP
layer 7
PPPPP
P...P
P...P
P...P
PPPPP
layer 8
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r2 kiwi player 'saved from the Design Studio by kiwi' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 0453cb2d93ff0846 snap 110 51 1defbbb11d39a11d
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 8c5d7dadc587439c snap 100 25 7a89a65ad2592441
save 'a very long design name that goes on and on past forty-eight characters' new=True => ok True #2 r1 blocks 110 created True: saved as design #2 r1 (b1.8.1): a very long design name that goes on and on past 5×5×9, 110 blocks (palette: wooden planks, cobblestone, glass, wooden door, torch…), 4 below ground
  session #2 'a very long design name that goes on and on past' off (0, 0, 0)
  rec #2 'a very long design name that goes on and on past' status accepted origin - by kiwi/kiwi cost 0
  desc copy
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary a very long design name that goes on and on past 5×5×9, 110 blocks (palette: wooden planks, cobblestone, glass, wooden door, torch…), 4 below ground
  eval drawn exactly
  bill wooden planks 69/0/0/69 ; cobblestone 34/0/0/34 ; glass 2/0/0/2 ; wooden door 1/0/0/1 ; torch 1/0/0/1 ; wooden stairs 1/0/0/1 ; ladder 1/0/0/1 
  dsl
# drawn in the Design Studio
blueprint a very long design name that goes on and on past
size 5x9x5
version b1.8.1
ground 4
facing south
palette P=planks C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3 A=ladder:3
layer 0
_____
_CCC_
_CCC_
_CCC_
_____
layer 1
_____
_..._
_.A._
_..._
_____
layer 2-3
_____
_..._
_..._
_..._
_____
layer 4
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 5
PPPPP
P..OP
P...P
P...P
PPWPP
layer 6
PPPPP
P...P
GT..G
P...P
PPWPP
layer 7
PPPPP
P...P
P...P
P...P
PPPPP
layer 8
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r1 kiwi player 'copy' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 6d98485ecd1de51a snap 110 51 1defbbb11d39a11d
load #1 r- => why - off (32, -64, 32) anchor - pref - rev 2 design 1
  build b1.8.1 0
describe16 r0 110 blocks, box x30..34 y-4..4 z30..34 (5×9×5); wooden planks 69, cobblestone 34, glass 2, torch 1, wooden stairs 1, wooden door 1, ladder 1; dug 26 below ground (y-3..-1); y-4 9 cobblestone · y-3 1 ladder, 8 dug · y-2--1 9 dug · y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
describe3 r0 110 blocks, box x30..34 y-4..4 z30..34 (5×9×5); wooden planks 69, cobblestone 34, glass 2, …; dug 26 below ground (y-3..-1); y-4 9 cobblestone · y-3 1 ladder, 8 dug · y-2--1 9 dug · y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
layer 0
y0, rows z30..34, cols x30..34:
AAAAA
AAAAA
AAAAA
AAAAA
AAAAA
A=cobblestone
layer -1
y-1 (below ground), rows z30..34, cols x30..34:
#####
#...#
#...#
#...#
#####
#=ground .=dug
wire d97c1b1c7ce7924d dug ab8762629d1ae57c enc 56c23d2970949ef0 rev 0
load #1 r1 => why - off (32, -1, 32) anchor - pref - rev 1 design 1
  build b1.8.1 0
describe16 r0 100 blocks, box x30..34 y0..4 z30..34 (5×5×5); wooden planks 69, cobblestone 25, glass 2, torch 1, wooden stairs 1, wooden door 1; y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
describe3 r0 100 blocks, box x30..34 y0..4 z30..34 (5×5×5); wooden planks 69, cobblestone 25, glass 2, …; y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
layer 0
y0, rows z30..34, cols x30..34:
AAAAA
AAAAA
AAAAA
AAAAA
AAAAA
A=cobblestone
layer -1
y-1 (below ground), rows z30..34, cols x30..34:
#####
#####
#####
#####
#####
#=ground .=dug
wire e9763792e4930f2c dug cbf29ce484222325 enc abe8540cf6a729c2 rev 0
load #1 r99 => why #1 has no revision r99 off (0, 0, 0) anchor - pref - rev 0 design 1
load #999 r- => why no design #999 off (0, 0, 0) anchor - pref - rev 0 design -
save 'Fox's hut' new=False => ok True #1 r3 blocks 111 created False: saved #1 r3 (b1.8.1): Fox's hut 5×5×10, 111 blocks (palette: wooden planks, cobblestone, glass, torch, wooden stairs…), 4 below ground
  session #1 'Fox's hut' off (32, -64, 32)
  rec #1 'Fox's hut' status accepted origin - by poofimafox/poofimafox cost 0.75
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's hut 5×5×10, 111 blocks (palette: wooden planks, cobblestone, glass, torch, wooden stairs…), 4 below ground
  eval drawn exactly
  bill wooden planks 69/0/0/69 ; cobblestone 34/0/0/34 ; glass 2/0/0/2 ; torch 1/0/0/1 ; wooden stairs 1/0/0/1 ; wooden door 1/0/0/1 ; ladder 1/0/0/1 ; red wool 1/0/0/1 
  dsl
# drawn in the Design Studio
blueprint Fox's hut
size 5x10x5
version b1.8.1
ground 4
facing south
palette P=planks C=cobblestone G=glass R=wool:red T=torch:1 O=oak_stairs:3 W=wooden_door:3 A=ladder:3
layer 0
_____
_CCC_
_CCC_
_CCC_
_____
layer 1
_____
_..._
_.A._
_..._
_____
layer 2-3
_____
_..._
_..._
_..._
_____
layer 4
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 5
PPPPP
P..OP
P...P
P...P
PPWPP
layer 6
PPPPP
P...P
GT..G
P...P
PPWPP
layer 7
PPPPP
P...P
P...P
P...P
PPPPP
layer 8
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP
layer 9
_____
_____
__R__
_____
_____

  /dsl
  r3 poofimafox player 'a red block on the roof' tag - origin - hash 664d3f867f91dd67 blocks 111 dsl eeb2877c25eb0727 snap 111 51 7d8890f9e046ceef
  r2 kiwi player 'saved from the Design Studio by kiwi' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 0453cb2d93ff0846 snap 110 51 1defbbb11d39a11d
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 8c5d7dadc587439c snap 100 25 7a89a65ad2592441
load #1 r1 => why - off (32, -1, 32) anchor - pref - rev 1 design 1
  build b1.8.1 0
describe16 r0 100 blocks, box x30..34 y0..4 z30..34 (5×5×5); wooden planks 69, cobblestone 25, glass 2, torch 1, wooden stairs 1, wooden door 1; y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
describe3 r0 100 blocks, box x30..34 y0..4 z30..34 (5×5×5); wooden planks 69, cobblestone 25, glass 2, …; y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
layer 0
y0, rows z30..34, cols x30..34:
AAAAA
AAAAA
AAAAA
AAAAA
AAAAA
A=cobblestone
layer -1
y-1 (below ground), rows z30..34, cols x30..34:
#####
#####
#####
#####
#####
#=ground .=dug
wire e9763792e4930f2c dug cbf29ce484222325 enc abe8540cf6a729c2 rev 0
save 'Fox's hut' new=False => ok True #1 r4 blocks 100 created False: saved #1 r4 (b1.8.1): Fox's hut 5×5×5, 100 blocks (palette: wooden planks, cobblestone, glass, torch, wooden stairs…)
  session #1 'Fox's hut' off (32, -1, 32)
  rec #1 'Fox's hut' status accepted origin - by poofimafox/poofimafox cost 0.75
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's hut 5×5×5, 100 blocks (palette: wooden planks, cobblestone, glass, torch, wooden stairs…)
  eval drawn exactly
  bill wooden planks 69/0/0/69 ; cobblestone 25/0/0/25 ; glass 2/0/0/2 ; torch 1/0/0/1 ; wooden stairs 1/0/0/1 ; wooden door 1/0/0/1 
  dsl
# drawn in the Design Studio
blueprint Fox's hut
size 5x5x5
version b1.8.1
facing south
palette P=planks C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3
layer 0
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 1
PPPPP
P..OP
P...P
P...P
PPWPP
layer 2
PPPPP
P...P
GT..G
P...P
PPWPP
layer 3
PPPPP
P...P
P...P
P...P
PPPPP
layer 4
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r4 poofimafox player 'revert to r1' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl f802218cade756f4 snap 100 25 7a89a65ad2592441
  r3 poofimafox player 'a red block on the roof' tag - origin - hash 664d3f867f91dd67 blocks 111 dsl eeb2877c25eb0727 snap 111 51 7d8890f9e046ceef
  r2 kiwi player 'saved from the Design Studio by kiwi' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 0453cb2d93ff0846 snap 110 51 1defbbb11d39a11d
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 8c5d7dadc587439c snap 100 25 7a89a65ad2592441
log #1 Fox's hut: r4 poofimafox just now 'revert to r1' · r3 poofimafox just now 'a red block on the roof' · r2 kiwi just now 'saved from the Design Studio by kiwi' · r1 poofimafox 5m ago 'first'
diff [r1 r2] r1→r2: +9 cobblestone, +1 ladder, dug 26 below ground; -1,-3,-1 +cobblestone; 0,-3,-1 +cobblestone; 1,-3,-1 +cobblestone; -1,-3,0 +cobblestone; 0,-3,0 +cobblestone; 1,-3,0 +cobblestone; +4 more
diff [r1..r4] r1→r4: no changes
diff [r4 r1] r1→r4: no changes
diff [head~1 head] r3→r4: −9 cobblestone, −1 ladder, −1 wool, filled 26 back; -1,60,-1 −cobblestone; 0,60,-1 −cobblestone; 1,60,-1 −cobblestone; -1,60,0 −cobblestone; 0,60,0 −cobblestone; 1,60,0 −cobblestone; +5 more
diff [r1 r9] no such revision (r1 r9); design log{id=1} lists them
diff [first, last] r1→r3: +9 cobblestone, +1 ladder, +1 wool, dug 26 below ground; -1,-3,-1 +cobblestone; 0,-3,-1 +cobblestone; 1,-3,-1 +cobblestone; -1,-3,0 +cobblestone; 0,-3,0 +cobblestone; 1,-3,0 +cobblestone; +5 more
diff [r2] r2: the world isn't in view (come closer)
diff [] r4: the world isn't in view (come closer)
diff [nope] no such revision 'nope'; design log{id=1} lists them
status #1 Fox's hut at r4 poofimafox just now 'revert to r1'; world not in view
resolve [] 4
resolve [r2] 2
resolve [2] 2
resolve [rev 2] null
resolve [HEAD] 4
resolve [head~2] 2
resolve [head^] 3
resolve [head-] 3
resolve [previous] 3
resolve [first] 1
resolve [original] 1
resolve [good] null
resolve [r77] null
resolve [#r1] 1
save 'long wall' new=False => ok True #3 r1 blocks 41 created True: saved as design #3 r1 (b1.8.1): long wall 41×1×1, 41 blocks (palette: stone)
  session #3 'long wall' off (0, 0, 0)
  rec #3 'long wall' status accepted origin - by poofimafox/poofimafox cost 0
  desc designed in the Design Studio by poofimafox
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary long wall 41×1×1, 41 blocks (palette: stone)
  eval drawn exactly
  bill stone 41/0/0/41 
  dsl
# drawn in the Design Studio
blueprint long wall
size 41x1x1
version b1.8.1
facing south
palette S=stone
layer 0
SSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSS

  /dsl
  r1 poofimafox player 'saved from the Design Studio by poofimafox' tag - origin - hash 5ad9229abd860044 blocks 41 dsl 16432ee7a9952189 snap 41 0 524ec0da8f4719e3
save 'deep strip' new=False => ok False #0 r0 blocks 0 created False: the canvas is empty: dig the mine first
  session # '' off (0, 0, 0)
save 'deep strip' new=False => ok True #4 r1 blocks 9 created True: saved as design #4 r1 (b1.8.1, anchor absolute y 11): deep strip mine 24×14×56 (y 11..66): 425 cells to dig, 9 torch, preferred for b1.8.1
  session #4 'deep strip' off (0, 0, 0)
  rec #4 'deep strip' status accepted origin - by poofimafox/poofimafox cost 0
  desc the first cut
  anchor a mine designed in the Design Studio: absolute y, no site yet (place it in Story Mode)
  summary deep strip mine 24×14×56 (y 11..66): 425 cells to dig, 9 torch, preferred for b1.8.1
  eval drawn exactly
  bill torch 9/0/0/9 
  dsl
# a mine drawn in the Design Studio: dig the clear boxes, then place the blocks
blueprint deep strip
size 24x56x14
version b1.8.1
kind mine
anchor absolute 30,11,25
entry 0,55,5
seed 4242
preferred_for b1.8.1
facing south
palette T=torch:4 A=torch:3
layer 3
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
layer 9
________________________
________________________
________________________
________________________
________________________
A_______________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
layer 15
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
layer 21
________________________
________________________
________________________
________________________
________________________
A_______________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
layer 27
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
layer 33
________________________
________________________
________________________
________________________
________________________
A_______________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
layer 39
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
layer 45
________________________
________________________
________________________
________________________
________________________
A_______________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
layer 51
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
clear 7,0,0 7,1,13
clear 11,0,0 11,1,13
clear 15,0,0 15,1,13
clear 19,0,0 19,1,13
clear 23,0,0 23,1,13
clear 0,0,5 3,1,8
clear 4,0,6 6,2,7
clear 8,0,6 10,2,7
clear 12,0,6 14,2,7
clear 16,0,6 18,2,7
clear 20,0,6 22,2,7
clear 7,2,6 7,2,7
clear 11,2,6 11,2,7
clear 15,2,6 15,2,7
clear 19,2,6 19,2,7
clear 23,2,6 23,2,7
clear 3,2,7 3,2,8
clear 2,2,8 2,2,8
clear 3,3,6 3,4,7
clear 3,4,5 3,6,5
clear 2,5,5 2,7,5
clear 3,5,6 3,5,6
clear 1,6,5 1,8,5
clear 0,7,5 0,8,5
clear 0,8,6 0,10,6
clear 0,9,7 0,11,7
clear 0,10,8 0,12,8
clear 1,11,8 1,13,8
clear 2,12,8 2,14,8
clear 3,13,8 3,14,8
clear 3,14,7 3,16,7
clear 3,15,6 3,17,6
clear 3,16,5 3,18,5
clear 2,17,5 2,19,5
clear 1,18,5 1,20,5
clear 0,19,5 0,20,5
clear 0,20,6 0,22,6
clear 0,21,7 0,23,7
clear 0,22,8 0,24,8
clear 1,23,8 1,25,8
clear 2,24,8 2,26,8
clear 3,25,8 3,26,8
clear 3,26,7 3,28,7
clear 3,27,6 3,29,6
clear 3,28,5 3,30,5
clear 2,29,5 2,31,5
clear 1,30,5 1,32,5
clear 0,31,5 0,32,5
clear 0,32,6 0,34,6
clear 0,33,7 0,35,7
clear 0,34,8 0,36,8
clear 1,35,8 1,37,8
clear 2,36,8 2,38,8
clear 3,37,8 3,38,8
clear 3,38,7 3,40,7
clear 3,39,6 3,41,6
clear 3,40,5 3,42,5
clear 2,41,5 2,43,5
clear 1,42,5 1,44,5
clear 0,43,5 0,44,5
clear 0,44,6 0,46,6
clear 0,45,7 0,47,7
clear 0,46,8 0,48,8
clear 1,47,8 1,49,8
clear 2,48,8 2,50,8
clear 3,49,8 3,50,8
clear 3,50,7 3,52,7
clear 3,51,6 3,53,6
clear 3,52,5 3,54,5
clear 2,53,5 2,55,5
clear 1,54,5 1,55,5
clear 0,55,5 0,55,5

  /dsl
  r1 poofimafox player 'the first cut' tag - origin - hash 82828d3fa8002d8a blocks 9 dsl ae5b7fe6aa7254a9 snap 9 425 458befaf4c9ffccd
load #4 r- => why - off (30, 0, 30) anchor (30, 66, 30) pref b1.8.1 rev 1 design 4
  mine b1.8.1 4242
describe16 r0 9 blocks, box x30..53 y11..66 z25..38 (24×56×14); torch 9; dug 425 to mine out (y11..66); y11-12 116 dug · y13 43 dug · y14 1 torch, 2 dug · y15-19 3 dug · y20 1 torch, 2 dug · y21-25 3 dug · y26 1 torch, 2 dug · y27-31 3 dug · y32 1 torch, 2 dug · y33-37 3 dug · y38 1 torch, 2 dug · y39-43 3 dug · y44 1 torch, 2 dug · y45-49 3 dug · y50 1 torch, 2 dug · y51-55 3 dug · y56 1 torch, 2 dug · y57-61 3 dug · y62 1 torch, 2 dug · y63-66 3 dug
describe3 r0 9 blocks, box x30..53 y11..66 z25..38 (24×56×14); torch 9; dug 425 to mine out (y11..66); y11-12 116 dug · y13 43 dug · y14 1 torch, 2 dug · y15-19 3 dug · y20 1 torch, 2 dug · y21-25 3 dug · y26 1 torch, 2 dug · y27-31 3 dug · y32 1 torch, 2 dug · y33-37 3 dug · y38 1 torch, 2 dug · y39-43 3 dug · y44 1 torch, 2 dug · y45-49 3 dug · y50 1 torch, 2 dug · y51-55 3 dug · y56 1 torch, 2 dug · y57-61 3 dug · y62 1 torch, 2 dug · y63-66 3 dug
layer 0
y0, rows z25..38, cols x30..53:
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
#=ground .=dug
layer -1
y-1, rows z25..38, cols x30..53:
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
#=ground .=dug
wire 81cb71a6224d8c6e dug f8fbe03b9a783012 enc 2e313f0634ca3ca8 rev 0
save 'deep strip' new=False => ok True #4 r2 blocks 9 created False: saved #4 r2 (b1.8.1, anchor absolute y 11): deep strip mine 4×4×56 (y 11..66): 185 cells to dig, 9 torch, preferred for b1.8.1
  session #4 'deep strip' off (0, 0, 0)
  rec #4 'deep strip' status accepted origin - by poofimafox/poofimafox cost 0
  desc the first cut
  anchor a mine designed in the Design Studio: absolute y, no site yet (place it in Story Mode)
  summary deep strip mine 4×4×56 (y 11..66): 185 cells to dig, 9 torch, preferred for b1.8.1
  eval drawn exactly
  bill torch 9/0/0/9 
  dsl
# a mine drawn in the Design Studio: dig the clear boxes, then place the blocks
blueprint deep strip
size 4x56x4
version b1.8.1
kind mine
anchor absolute 30,11,30
entry 0,55,0
seed 4242
preferred_for b1.8.1
facing south
palette T=torch:4 A=torch:3
layer 3
____
____
____
___T
layer 9
A___
____
____
____
layer 15
____
____
____
___T
layer 21
A___
____
____
____
layer 27
____
____
____
___T
layer 33
A___
____
____
____
layer 39
____
____
____
___T
layer 45
A___
____
____
____
layer 51
____
____
____
___T
clear 0,0,0 3,1,3
clear 3,2,2 3,2,3
clear 2,2,3 2,2,3
clear 3,3,1 3,4,2
clear 3,4,0 3,6,0
clear 2,5,0 2,7,0
clear 3,5,1 3,5,1
clear 1,6,0 1,8,0
clear 0,7,0 0,8,0
clear 0,8,1 0,10,1
clear 0,9,2 0,11,2
clear 0,10,3 0,12,3
clear 1,11,3 1,13,3
clear 2,12,3 2,14,3
clear 3,13,3 3,14,3
clear 3,14,2 3,16,2
clear 3,15,1 3,17,1
clear 3,16,0 3,18,0
clear 2,17,0 2,19,0
clear 1,18,0 1,20,0
clear 0,19,0 0,20,0
clear 0,20,1 0,22,1
clear 0,21,2 0,23,2
clear 0,22,3 0,24,3
clear 1,23,3 1,25,3
clear 2,24,3 2,26,3
clear 3,25,3 3,26,3
clear 3,26,2 3,28,2
clear 3,27,1 3,29,1
clear 3,28,0 3,30,0
clear 2,29,0 2,31,0
clear 1,30,0 1,32,0
clear 0,31,0 0,32,0
clear 0,32,1 0,34,1
clear 0,33,2 0,35,2
clear 0,34,3 0,36,3
clear 1,35,3 1,37,3
clear 2,36,3 2,38,3
clear 3,37,3 3,38,3
clear 3,38,2 3,40,2
clear 3,39,1 3,41,1
clear 3,40,0 3,42,0
clear 2,41,0 2,43,0
clear 1,42,0 1,44,0
clear 0,43,0 0,44,0
clear 0,44,1 0,46,1
clear 0,45,2 0,47,2
clear 0,46,3 0,48,3
clear 1,47,3 1,49,3
clear 2,48,3 2,50,3
clear 3,49,3 3,50,3
clear 3,50,2 3,52,2
clear 3,51,1 3,53,1
clear 3,52,0 3,54,0
clear 2,53,0 2,55,0
clear 1,54,0 1,55,0
clear 0,55,0 0,55,0

  /dsl
  r2 kiwi player 'shaft only' tag - origin - hash 210bd44e5d290faa blocks 9 dsl 9fb3e2efc63dcac6 snap 9 185 d4a3b6ed14b99e66
  r1 poofimafox player 'the first cut' tag - origin - hash 82828d3fa8002d8a blocks 9 dsl ae5b7fe6aa7254a9 snap 9 425 458befaf4c9ffccd
load #4 r1 => why - off (30, 0, 30) anchor (30, 66, 30) pref b1.8.1 rev 1 design 4
  mine b1.8.1 4242
describe16 r0 9 blocks, box x30..53 y11..66 z25..38 (24×56×14); torch 9; dug 425 to mine out (y11..66); y11-12 116 dug · y13 43 dug · y14 1 torch, 2 dug · y15-19 3 dug · y20 1 torch, 2 dug · y21-25 3 dug · y26 1 torch, 2 dug · y27-31 3 dug · y32 1 torch, 2 dug · y33-37 3 dug · y38 1 torch, 2 dug · y39-43 3 dug · y44 1 torch, 2 dug · y45-49 3 dug · y50 1 torch, 2 dug · y51-55 3 dug · y56 1 torch, 2 dug · y57-61 3 dug · y62 1 torch, 2 dug · y63-66 3 dug
describe3 r0 9 blocks, box x30..53 y11..66 z25..38 (24×56×14); torch 9; dug 425 to mine out (y11..66); y11-12 116 dug · y13 43 dug · y14 1 torch, 2 dug · y15-19 3 dug · y20 1 torch, 2 dug · y21-25 3 dug · y26 1 torch, 2 dug · y27-31 3 dug · y32 1 torch, 2 dug · y33-37 3 dug · y38 1 torch, 2 dug · y39-43 3 dug · y44 1 torch, 2 dug · y45-49 3 dug · y50 1 torch, 2 dug · y51-55 3 dug · y56 1 torch, 2 dug · y57-61 3 dug · y62 1 torch, 2 dug · y63-66 3 dug
layer 0
y0, rows z25..38, cols x30..53:
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
#=ground .=dug
layer -1
y-1, rows z25..38, cols x30..53:
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
#=ground .=dug
wire 81cb71a6224d8c6e dug f8fbe03b9a783012 enc 2e313f0634ca3ca8 rev 0
save 'pocket' new=False => ok True #5 r1 blocks 0 created True: saved as design #5 r1 (b1.8.1, anchor absolute y 20): pocket mine 3×3×2 (y 20..21): 18 cells to dig
  session #5 'pocket' off (0, 0, 0)
  rec #5 'pocket' status accepted origin - by poofimafox/poofimafox cost 0
  desc a mine designed in the Design Studio by poofimafox
  anchor a mine designed in the Design Studio: absolute y, no site yet (place it in Story Mode)
  summary pocket mine 3×3×2 (y 20..21): 18 cells to dig
  eval drawn exactly
  bill 
  dsl
# a mine drawn in the Design Studio: dig the clear boxes, then place the blocks
blueprint pocket
size 3x2x3
version b1.8.1
kind mine
anchor absolute 10,20,10
entry 0,1,0
seed 9
facing south
clear 0,0,0 2,1,2

  /dsl
  r1 poofimafox player 'saved from the Design Studio by poofimafox' tag - origin - hash c464692c87940d1d blocks 0 dsl ab260cdbe621d392 snap 0 18 e7bb178f29ccef20
== saves 1.12.2
save '  ' new=False => ok False #0 r0 blocks 0 created False: a design needs a name
  session # '' off (0, 0, 0)
save 'empty' new=False => ok False #0 r0 blocks 0 created False: the canvas is empty
  session # '' off (0, 0, 0)
run box {"b":"cobblestone","from":[30,0,30],"to":[34,0,34]} => ok, 25 cells changed (r1) | op fill 'filled 5×1×5 with cobblestone' ai 25 cells 73ab3386bf74f08e first [30,0,30=4:0 31,0,30=4:0 32,0,30=4:0]
run box {"b":"planks","from":[30,1,30],"to":[34,3,34],"mode":"walls"} => ok, 48 cells changed (r2) | op walls 'walls 5×3×5 with oak planks' ai 48 cells 3bfbfb23bd4f4c3d first [30,1,30=5:0 31,1,30=5:0 32,1,30=5:0]
run place {"b":"wooden_door:south","at":[[32,1,34]]} => ok, 2 cells changed (r3) | op place 'placed oak door' ai 2 cells 8070e0b1aea8aa9d first [32,1,34=64:3 32,2,34=64:11]
run place {"b":"torch:wall=west","at":[[31,2,32]]} => ok, 1 cells changed (r4) | op place 'placed torch' ai 1 cells 98c838cc926ae0a7 first [31,2,32=50:1]
run place {"b":"oak_stairs:north","at":[[33,1,31]]} => ok, 1 cells changed (r5) | op place 'placed oak stairs' ai 1 cells 415d7a89d5d55d5a first [33,1,31=53:3]
run place {"b":"glass","at":[[30,2,32],[34,2,32]]} => ok, 2 cells changed (r6) | op place 'placed glass' ai 2 cells 8bffd919510a821f first [30,2,32=20:0 34,2,32=20:0]
run box {"b":"planks","from":[30,4,30],"to":[34,4,34]} => ok, 25 cells changed (r7) | op fill 'filled 5×1×5 with oak planks' ai 25 cells 08bf33140507c9f7 first [30,4,30=5:0 31,4,30=5:0 32,4,30=5:0]
save '  "Fox's #hut"
 ' new=False => ok True #1 r1 blocks 100 created True: saved as design #1 r1 (1.12.2): Fox's  hut 5×5×5, 100 blocks (palette: oak planks, cobblestone, glass, oak door, torch…)
  session #1 'Fox's  hut' off (0, 0, 0)
  rec #1 'Fox's  hut' status accepted origin - by poofimafox/poofimafox cost 0.25
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's  hut 5×5×5, 100 blocks (palette: oak planks, cobblestone, glass, oak door, torch…)
  eval drawn exactly
  bill oak planks 69/0/0/69 ; cobblestone 25/0/0/25 ; glass 2/0/0/2 ; oak door 1/0/0/1 ; torch 1/0/0/1 ; oak stairs 1/0/0/1 
  dsl
# drawn in the Design Studio
blueprint Fox's  hut
size 5x5x5
version 1.12.2
facing south
palette P=planks:oak C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3
layer 0
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 1
PPPPP
P..OP
P...P
P...P
PPWPP
layer 2
PPPPP
P...P
GT..G
P...P
PPWPP
layer 3
PPPPP
P...P
P...P
P...P
PPPPP
layer 4
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 2f6bb376f38ba147 snap 100 25 7a89a65ad2592441
run remove {"from":[31,-3,31],"to":[33,-1,33]} => ok, 27 cells changed (r8) | op remove 'removed 3×3×3' ai 27 cells 9f13c350e45becb7 first [31,-3,31=0:0 32,-3,31=0:0 33,-3,31=0:0]
run box {"b":"cobblestone","from":[31,-4,31],"to":[33,-4,33]} => ok, 9 cells changed (r9) | op fill 'filled 3×1×3 with cobblestone' ai 9 cells e5b9a942cd77ecbf first [31,-4,31=4:0 32,-4,31=4:0 33,-4,31=4:0]
run place {"b":"ladder:wall=north","at":[[32,-3,32]]} => ok, 1 cells changed (r10) | op place 'placed ladder' ai 1 cells a10c00331614c87c first [32,-3,32=65:3]
save 'Fox's hut' new=False => ok True #1 r2 blocks 110 created False: saved #1 r2 (1.12.2): Fox's hut 5×5×9, 110 blocks (palette: oak planks, cobblestone, glass, oak door, torch…), 4 below ground
  session #1 'Fox's hut' off (0, 0, 0)
  rec #1 'Fox's hut' status accepted origin - by poofimafox/poofimafox cost 0.5
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's hut 5×5×9, 110 blocks (palette: oak planks, cobblestone, glass, oak door, torch…), 4 below ground
  eval drawn exactly
  bill oak planks 69/0/0/69 ; cobblestone 34/0/0/34 ; glass 2/0/0/2 ; oak door 1/0/0/1 ; torch 1/0/0/1 ; oak stairs 1/0/0/1 ; ladder 1/0/0/1 
  dsl
# anchor 2,4,0
# drawn in the Design Studio
blueprint Fox's hut
size 5x9x5
version 1.12.2
ground 4
facing south
palette P=planks:oak C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3 A=ladder:3
layer 0
_____
_CCC_
_CCC_
_CCC_
_____
layer 1
_____
_..._
_.A._
_..._
_____
layer 2-3
_____
_..._
_..._
_..._
_____
layer 4
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 5
PPPPP
P..OP
P...P
P...P
PPWPP
layer 6
PPPPP
P...P
GT..G
P...P
PPWPP
layer 7
PPPPP
P...P
P...P
P...P
PPPPP
layer 8
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r2 kiwi player 'saved from the Design Studio by kiwi' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 1458cdbf53b778c7 snap 110 51 1defbbb11d39a11d
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 2f6bb376f38ba147 snap 100 25 7a89a65ad2592441
save 'Fox's hut' new=False => ok True #1 r2 blocks 110 created False: saved #1 r2 (1.12.2): Fox's hut 5×5×9, 110 blocks (palette: oak planks, cobblestone, glass, oak door, torch…), 4 below ground
  session #1 'Fox's hut' off (0, 0, 0)
  rec #1 'Fox's hut' status accepted origin - by poofimafox/poofimafox cost 0.75
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's hut 5×5×9, 110 blocks (palette: oak planks, cobblestone, glass, oak door, torch…), 4 below ground
  eval drawn exactly
  bill oak planks 69/0/0/69 ; cobblestone 34/0/0/34 ; glass 2/0/0/2 ; oak door 1/0/0/1 ; torch 1/0/0/1 ; oak stairs 1/0/0/1 ; ladder 1/0/0/1 
  dsl
# anchor 2,4,0
# drawn in the Design Studio
blueprint Fox's hut
size 5x9x5
version 1.12.2
ground 4
facing south
palette P=planks:oak C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3 A=ladder:3
layer 0
_____
_CCC_
_CCC_
_CCC_
_____
layer 1
_____
_..._
_.A._
_..._
_____
layer 2-3
_____
_..._
_..._
_..._
_____
layer 4
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 5
PPPPP
P..OP
P...P
P...P
PPWPP
layer 6
PPPPP
P...P
GT..G
P...P
PPWPP
layer 7
PPPPP
P...P
P...P
P...P
PPPPP
layer 8
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r2 kiwi player 'saved from the Design Studio by kiwi' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 1458cdbf53b778c7 snap 110 51 1defbbb11d39a11d
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 2f6bb376f38ba147 snap 100 25 7a89a65ad2592441
save 'a very long design name that goes on and on past forty-eight characters' new=True => ok True #2 r1 blocks 110 created True: saved as design #2 r1 (1.12.2): a very long design name that goes on and on past 5×5×9, 110 blocks (palette: oak planks, cobblestone, glass, oak door, torch…), 4 below ground
  session #2 'a very long design name that goes on and on past' off (0, 0, 0)
  rec #2 'a very long design name that goes on and on past' status accepted origin - by kiwi/kiwi cost 0
  desc copy
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary a very long design name that goes on and on past 5×5×9, 110 blocks (palette: oak planks, cobblestone, glass, oak door, torch…), 4 below ground
  eval drawn exactly
  bill oak planks 69/0/0/69 ; cobblestone 34/0/0/34 ; glass 2/0/0/2 ; oak door 1/0/0/1 ; torch 1/0/0/1 ; oak stairs 1/0/0/1 ; ladder 1/0/0/1 
  dsl
# drawn in the Design Studio
blueprint a very long design name that goes on and on past
size 5x9x5
version 1.12.2
ground 4
facing south
palette P=planks:oak C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3 A=ladder:3
layer 0
_____
_CCC_
_CCC_
_CCC_
_____
layer 1
_____
_..._
_.A._
_..._
_____
layer 2-3
_____
_..._
_..._
_..._
_____
layer 4
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 5
PPPPP
P..OP
P...P
P...P
PPWPP
layer 6
PPPPP
P...P
GT..G
P...P
PPWPP
layer 7
PPPPP
P...P
P...P
P...P
PPPPP
layer 8
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r1 kiwi player 'copy' tag - origin - hash 4b069d7534134e22 blocks 110 dsl d823de8d27232573 snap 110 51 1defbbb11d39a11d
load #1 r- => why - off (32, -64, 32) anchor - pref - rev 2 design 1
  build 1.12.2 0
describe16 r0 110 blocks, box x30..34 y-4..4 z30..34 (5×9×5); wooden planks 69, cobblestone 34, glass 2, torch 1, wooden stairs 1, wooden door 1, ladder 1; dug 26 below ground (y-3..-1); y-4 9 cobblestone · y-3 1 ladder, 8 dug · y-2--1 9 dug · y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
describe3 r0 110 blocks, box x30..34 y-4..4 z30..34 (5×9×5); wooden planks 69, cobblestone 34, glass 2, …; dug 26 below ground (y-3..-1); y-4 9 cobblestone · y-3 1 ladder, 8 dug · y-2--1 9 dug · y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
layer 0
y0, rows z30..34, cols x30..34:
AAAAA
AAAAA
AAAAA
AAAAA
AAAAA
A=cobblestone
layer -1
y-1 (below ground), rows z30..34, cols x30..34:
#####
#...#
#...#
#...#
#####
#=ground .=dug
wire d97c1b1c7ce7924d dug ab8762629d1ae57c enc 56c23d2970949ef0 rev 0
load #1 r1 => why - off (32, -1, 32) anchor - pref - rev 1 design 1
  build 1.12.2 0
describe16 r0 100 blocks, box x30..34 y0..4 z30..34 (5×5×5); wooden planks 69, cobblestone 25, glass 2, torch 1, wooden stairs 1, wooden door 1; y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
describe3 r0 100 blocks, box x30..34 y0..4 z30..34 (5×5×5); wooden planks 69, cobblestone 25, glass 2, …; y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
layer 0
y0, rows z30..34, cols x30..34:
AAAAA
AAAAA
AAAAA
AAAAA
AAAAA
A=cobblestone
layer -1
y-1 (below ground), rows z30..34, cols x30..34:
#####
#####
#####
#####
#####
#=ground .=dug
wire e9763792e4930f2c dug cbf29ce484222325 enc abe8540cf6a729c2 rev 0
load #1 r99 => why #1 has no revision r99 off (0, 0, 0) anchor - pref - rev 0 design 1
load #999 r- => why no design #999 off (0, 0, 0) anchor - pref - rev 0 design -
save 'Fox's hut' new=False => ok True #1 r3 blocks 111 created False: saved #1 r3 (1.12.2): Fox's hut 5×5×10, 111 blocks (palette: oak planks, cobblestone, glass, torch, oak stairs…), 4 below ground
  session #1 'Fox's hut' off (32, -64, 32)
  rec #1 'Fox's hut' status accepted origin - by poofimafox/poofimafox cost 0.75
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's hut 5×5×10, 111 blocks (palette: oak planks, cobblestone, glass, torch, oak stairs…), 4 below ground
  eval drawn exactly
  bill oak planks 69/0/0/69 ; cobblestone 34/0/0/34 ; glass 2/0/0/2 ; torch 1/0/0/1 ; oak stairs 1/0/0/1 ; oak door 1/0/0/1 ; ladder 1/0/0/1 ; red wool 1/0/0/1 
  dsl
# drawn in the Design Studio
blueprint Fox's hut
size 5x10x5
version 1.12.2
ground 4
facing south
palette P=planks:oak C=cobblestone G=glass R=wool:red T=torch:1 O=oak_stairs:3 W=wooden_door:3 A=ladder:3
layer 0
_____
_CCC_
_CCC_
_CCC_
_____
layer 1
_____
_..._
_.A._
_..._
_____
layer 2-3
_____
_..._
_..._
_..._
_____
layer 4
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 5
PPPPP
P..OP
P...P
P...P
PPWPP
layer 6
PPPPP
P...P
GT..G
P...P
PPWPP
layer 7
PPPPP
P...P
P...P
P...P
PPPPP
layer 8
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP
layer 9
_____
_____
__R__
_____
_____

  /dsl
  r3 poofimafox player 'a red block on the roof' tag - origin - hash 664d3f867f91dd67 blocks 111 dsl c26e1664361ca680 snap 111 51 7d8890f9e046ceef
  r2 kiwi player 'saved from the Design Studio by kiwi' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 1458cdbf53b778c7 snap 110 51 1defbbb11d39a11d
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 2f6bb376f38ba147 snap 100 25 7a89a65ad2592441
load #1 r1 => why - off (32, -1, 32) anchor - pref - rev 1 design 1
  build 1.12.2 0
describe16 r0 100 blocks, box x30..34 y0..4 z30..34 (5×5×5); wooden planks 69, cobblestone 25, glass 2, torch 1, wooden stairs 1, wooden door 1; y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
describe3 r0 100 blocks, box x30..34 y0..4 z30..34 (5×5×5); wooden planks 69, cobblestone 25, glass 2, …; y0 25 cobblestone · y1 17 wooden planks+wooden stairs · y2 17 wooden planks+glass · y3 16 wooden planks · y4 25 wooden planks
layer 0
y0, rows z30..34, cols x30..34:
AAAAA
AAAAA
AAAAA
AAAAA
AAAAA
A=cobblestone
layer -1
y-1 (below ground), rows z30..34, cols x30..34:
#####
#####
#####
#####
#####
#=ground .=dug
wire e9763792e4930f2c dug cbf29ce484222325 enc abe8540cf6a729c2 rev 0
save 'Fox's hut' new=False => ok True #1 r4 blocks 100 created False: saved #1 r4 (1.12.2): Fox's hut 5×5×5, 100 blocks (palette: oak planks, cobblestone, glass, torch, oak stairs…)
  session #1 'Fox's hut' off (32, -1, 32)
  rec #1 'Fox's hut' status accepted origin - by poofimafox/poofimafox cost 0.75
  desc first
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary Fox's hut 5×5×5, 100 blocks (palette: oak planks, cobblestone, glass, torch, oak stairs…)
  eval drawn exactly
  bill oak planks 69/0/0/69 ; cobblestone 25/0/0/25 ; glass 2/0/0/2 ; torch 1/0/0/1 ; oak stairs 1/0/0/1 ; oak door 1/0/0/1 
  dsl
# drawn in the Design Studio
blueprint Fox's hut
size 5x5x5
version 1.12.2
facing south
palette P=planks:oak C=cobblestone G=glass T=torch:1 O=oak_stairs:3 W=wooden_door:3
layer 0
CCCCC
CCCCC
CCCCC
CCCCC
CCCCC
layer 1
PPPPP
P..OP
P...P
P...P
PPWPP
layer 2
PPPPP
P...P
GT..G
P...P
PPWPP
layer 3
PPPPP
P...P
P...P
P...P
PPPPP
layer 4
PPPPP
PPPPP
PPPPP
PPPPP
PPPPP

  /dsl
  r4 poofimafox player 'revert to r1' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 7448a50bf5ea57bf snap 100 25 7a89a65ad2592441
  r3 poofimafox player 'a red block on the roof' tag - origin - hash 664d3f867f91dd67 blocks 111 dsl c26e1664361ca680 snap 111 51 7d8890f9e046ceef
  r2 kiwi player 'saved from the Design Studio by kiwi' tag - origin - hash 4b069d7534134e22 blocks 110 dsl 1458cdbf53b778c7 snap 110 51 1defbbb11d39a11d
  r1 poofimafox player 'first' tag - origin - hash b7efe231eb7e9750 blocks 100 dsl 2f6bb376f38ba147 snap 100 25 7a89a65ad2592441
log #1 Fox's hut: r4 poofimafox just now 'revert to r1' · r3 poofimafox just now 'a red block on the roof' · r2 kiwi just now 'saved from the Design Studio by kiwi' · r1 poofimafox 5m ago 'first'
diff [r1 r2] r1→r2: +9 cobblestone, +1 ladder, dug 26 below ground; -1,-3,-1 +cobblestone; 0,-3,-1 +cobblestone; 1,-3,-1 +cobblestone; -1,-3,0 +cobblestone; 0,-3,0 +cobblestone; 1,-3,0 +cobblestone; +4 more
diff [r1..r4] r1→r4: no changes
diff [r4 r1] r1→r4: no changes
diff [head~1 head] r3→r4: −9 cobblestone, −1 ladder, −1 wool, filled 26 back; -1,60,-1 −cobblestone; 0,60,-1 −cobblestone; 1,60,-1 −cobblestone; -1,60,0 −cobblestone; 0,60,0 −cobblestone; 1,60,0 −cobblestone; +5 more
diff [r1 r9] no such revision (r1 r9); design log{id=1} lists them
diff [first, last] r1→r3: +9 cobblestone, +1 ladder, +1 wool, dug 26 below ground; -1,-3,-1 +cobblestone; 0,-3,-1 +cobblestone; 1,-3,-1 +cobblestone; -1,-3,0 +cobblestone; 0,-3,0 +cobblestone; 1,-3,0 +cobblestone; +5 more
diff [r2] r2: the world isn't in view (come closer)
diff [] r4: the world isn't in view (come closer)
diff [nope] no such revision 'nope'; design log{id=1} lists them
status #1 Fox's hut at r4 poofimafox just now 'revert to r1'; world not in view
resolve [] 4
resolve [r2] 2
resolve [2] 2
resolve [rev 2] null
resolve [HEAD] 4
resolve [head~2] 2
resolve [head^] 3
resolve [head-] 3
resolve [previous] 3
resolve [first] 1
resolve [original] 1
resolve [good] null
resolve [r77] null
resolve [#r1] 1
save 'long wall' new=False => ok True #3 r1 blocks 41 created True: saved as design #3 r1 (1.12.2): long wall 41×1×1, 41 blocks (palette: stone)
  session #3 'long wall' off (0, 0, 0)
  rec #3 'long wall' status accepted origin - by poofimafox/poofimafox cost 0
  desc designed in the Design Studio by poofimafox
  anchor designed in the Design Studio: no site yet (build a copy with at=…)
  summary long wall 41×1×1, 41 blocks (palette: stone)
  eval drawn exactly
  bill stone 41/0/0/41 
  dsl
# drawn in the Design Studio
blueprint long wall
size 41x1x1
version 1.12.2
facing south
palette S=stone
layer 0
SSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSSS

  /dsl
  r1 poofimafox player 'saved from the Design Studio by poofimafox' tag - origin - hash 5ad9229abd860044 blocks 41 dsl 5bcca53445e7cdcb snap 41 0 524ec0da8f4719e3
save 'deep strip' new=False => ok False #0 r0 blocks 0 created False: the canvas is empty: dig the mine first
  session # '' off (0, 0, 0)
save 'deep strip' new=False => ok True #4 r1 blocks 9 created True: saved as design #4 r1 (1.12.2, anchor absolute y 11): deep strip mine 24×14×56 (y 11..66): 425 cells to dig, 9 torch, preferred for 1.12.2
  session #4 'deep strip' off (0, 0, 0)
  rec #4 'deep strip' status accepted origin - by poofimafox/poofimafox cost 0
  desc the first cut
  anchor a mine designed in the Design Studio: absolute y, no site yet (place it in Story Mode)
  summary deep strip mine 24×14×56 (y 11..66): 425 cells to dig, 9 torch, preferred for 1.12.2
  eval drawn exactly
  bill torch 9/0/0/9 
  dsl
# a mine drawn in the Design Studio: dig the clear boxes, then place the blocks
blueprint deep strip
size 24x56x14
version 1.12.2
kind mine
anchor absolute 30,11,25
entry 0,55,5
seed 4242
preferred_for 1.12.2
facing south
palette T=torch:4 A=torch:3
layer 3
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
layer 9
________________________
________________________
________________________
________________________
________________________
A_______________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
layer 15
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
layer 21
________________________
________________________
________________________
________________________
________________________
A_______________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
layer 27
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
layer 33
________________________
________________________
________________________
________________________
________________________
A_______________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
layer 39
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
layer 45
________________________
________________________
________________________
________________________
________________________
A_______________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
layer 51
________________________
________________________
________________________
________________________
________________________
________________________
________________________
________________________
___T____________________
________________________
________________________
________________________
________________________
________________________
clear 7,0,0 7,1,13
clear 11,0,0 11,1,13
clear 15,0,0 15,1,13
clear 19,0,0 19,1,13
clear 23,0,0 23,1,13
clear 0,0,5 3,1,8
clear 4,0,6 6,2,7
clear 8,0,6 10,2,7
clear 12,0,6 14,2,7
clear 16,0,6 18,2,7
clear 20,0,6 22,2,7
clear 7,2,6 7,2,7
clear 11,2,6 11,2,7
clear 15,2,6 15,2,7
clear 19,2,6 19,2,7
clear 23,2,6 23,2,7
clear 3,2,7 3,2,8
clear 2,2,8 2,2,8
clear 3,3,6 3,4,7
clear 3,4,5 3,6,5
clear 2,5,5 2,7,5
clear 3,5,6 3,5,6
clear 1,6,5 1,8,5
clear 0,7,5 0,8,5
clear 0,8,6 0,10,6
clear 0,9,7 0,11,7
clear 0,10,8 0,12,8
clear 1,11,8 1,13,8
clear 2,12,8 2,14,8
clear 3,13,8 3,14,8
clear 3,14,7 3,16,7
clear 3,15,6 3,17,6
clear 3,16,5 3,18,5
clear 2,17,5 2,19,5
clear 1,18,5 1,20,5
clear 0,19,5 0,20,5
clear 0,20,6 0,22,6
clear 0,21,7 0,23,7
clear 0,22,8 0,24,8
clear 1,23,8 1,25,8
clear 2,24,8 2,26,8
clear 3,25,8 3,26,8
clear 3,26,7 3,28,7
clear 3,27,6 3,29,6
clear 3,28,5 3,30,5
clear 2,29,5 2,31,5
clear 1,30,5 1,32,5
clear 0,31,5 0,32,5
clear 0,32,6 0,34,6
clear 0,33,7 0,35,7
clear 0,34,8 0,36,8
clear 1,35,8 1,37,8
clear 2,36,8 2,38,8
clear 3,37,8 3,38,8
clear 3,38,7 3,40,7
clear 3,39,6 3,41,6
clear 3,40,5 3,42,5
clear 2,41,5 2,43,5
clear 1,42,5 1,44,5
clear 0,43,5 0,44,5
clear 0,44,6 0,46,6
clear 0,45,7 0,47,7
clear 0,46,8 0,48,8
clear 1,47,8 1,49,8
clear 2,48,8 2,50,8
clear 3,49,8 3,50,8
clear 3,50,7 3,52,7
clear 3,51,6 3,53,6
clear 3,52,5 3,54,5
clear 2,53,5 2,55,5
clear 1,54,5 1,55,5
clear 0,55,5 0,55,5

  /dsl
  r1 poofimafox player 'the first cut' tag - origin - hash 82828d3fa8002d8a blocks 9 dsl 9e2d6a73c68bd2a1 snap 9 425 458befaf4c9ffccd
load #4 r- => why - off (30, 0, 30) anchor (30, 66, 30) pref 1.12.2 rev 1 design 4
  mine 1.12.2 4242
describe16 r0 9 blocks, box x30..53 y11..66 z25..38 (24×56×14); torch 9; dug 425 to mine out (y11..66); y11-12 116 dug · y13 43 dug · y14 1 torch, 2 dug · y15-19 3 dug · y20 1 torch, 2 dug · y21-25 3 dug · y26 1 torch, 2 dug · y27-31 3 dug · y32 1 torch, 2 dug · y33-37 3 dug · y38 1 torch, 2 dug · y39-43 3 dug · y44 1 torch, 2 dug · y45-49 3 dug · y50 1 torch, 2 dug · y51-55 3 dug · y56 1 torch, 2 dug · y57-61 3 dug · y62 1 torch, 2 dug · y63-66 3 dug
describe3 r0 9 blocks, box x30..53 y11..66 z25..38 (24×56×14); torch 9; dug 425 to mine out (y11..66); y11-12 116 dug · y13 43 dug · y14 1 torch, 2 dug · y15-19 3 dug · y20 1 torch, 2 dug · y21-25 3 dug · y26 1 torch, 2 dug · y27-31 3 dug · y32 1 torch, 2 dug · y33-37 3 dug · y38 1 torch, 2 dug · y39-43 3 dug · y44 1 torch, 2 dug · y45-49 3 dug · y50 1 torch, 2 dug · y51-55 3 dug · y56 1 torch, 2 dug · y57-61 3 dug · y62 1 torch, 2 dug · y63-66 3 dug
layer 0
y0, rows z25..38, cols x30..53:
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
#=ground .=dug
layer -1
y-1, rows z25..38, cols x30..53:
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
#=ground .=dug
wire 81cb71a6224d8c6e dug f8fbe03b9a783012 enc 2e313f0634ca3ca8 rev 0
save 'deep strip' new=False => ok True #4 r2 blocks 9 created False: saved #4 r2 (1.12.2, anchor absolute y 11): deep strip mine 4×4×56 (y 11..66): 185 cells to dig, 9 torch, preferred for 1.12.2
  session #4 'deep strip' off (0, 0, 0)
  rec #4 'deep strip' status accepted origin - by poofimafox/poofimafox cost 0
  desc the first cut
  anchor a mine designed in the Design Studio: absolute y, no site yet (place it in Story Mode)
  summary deep strip mine 4×4×56 (y 11..66): 185 cells to dig, 9 torch, preferred for 1.12.2
  eval drawn exactly
  bill torch 9/0/0/9 
  dsl
# a mine drawn in the Design Studio: dig the clear boxes, then place the blocks
blueprint deep strip
size 4x56x4
version 1.12.2
kind mine
anchor absolute 30,11,30
entry 0,55,0
seed 4242
preferred_for 1.12.2
facing south
palette T=torch:4 A=torch:3
layer 3
____
____
____
___T
layer 9
A___
____
____
____
layer 15
____
____
____
___T
layer 21
A___
____
____
____
layer 27
____
____
____
___T
layer 33
A___
____
____
____
layer 39
____
____
____
___T
layer 45
A___
____
____
____
layer 51
____
____
____
___T
clear 0,0,0 3,1,3
clear 3,2,2 3,2,3
clear 2,2,3 2,2,3
clear 3,3,1 3,4,2
clear 3,4,0 3,6,0
clear 2,5,0 2,7,0
clear 3,5,1 3,5,1
clear 1,6,0 1,8,0
clear 0,7,0 0,8,0
clear 0,8,1 0,10,1
clear 0,9,2 0,11,2
clear 0,10,3 0,12,3
clear 1,11,3 1,13,3
clear 2,12,3 2,14,3
clear 3,13,3 3,14,3
clear 3,14,2 3,16,2
clear 3,15,1 3,17,1
clear 3,16,0 3,18,0
clear 2,17,0 2,19,0
clear 1,18,0 1,20,0
clear 0,19,0 0,20,0
clear 0,20,1 0,22,1
clear 0,21,2 0,23,2
clear 0,22,3 0,24,3
clear 1,23,3 1,25,3
clear 2,24,3 2,26,3
clear 3,25,3 3,26,3
clear 3,26,2 3,28,2
clear 3,27,1 3,29,1
clear 3,28,0 3,30,0
clear 2,29,0 2,31,0
clear 1,30,0 1,32,0
clear 0,31,0 0,32,0
clear 0,32,1 0,34,1
clear 0,33,2 0,35,2
clear 0,34,3 0,36,3
clear 1,35,3 1,37,3
clear 2,36,3 2,38,3
clear 3,37,3 3,38,3
clear 3,38,2 3,40,2
clear 3,39,1 3,41,1
clear 3,40,0 3,42,0
clear 2,41,0 2,43,0
clear 1,42,0 1,44,0
clear 0,43,0 0,44,0
clear 0,44,1 0,46,1
clear 0,45,2 0,47,2
clear 0,46,3 0,48,3
clear 1,47,3 1,49,3
clear 2,48,3 2,50,3
clear 3,49,3 3,50,3
clear 3,50,2 3,52,2
clear 3,51,1 3,53,1
clear 3,52,0 3,54,0
clear 2,53,0 2,55,0
clear 1,54,0 1,55,0
clear 0,55,0 0,55,0

  /dsl
  r2 kiwi player 'shaft only' tag - origin - hash 210bd44e5d290faa blocks 9 dsl 17531f24e6d352f2 snap 9 185 d4a3b6ed14b99e66
  r1 poofimafox player 'the first cut' tag - origin - hash 82828d3fa8002d8a blocks 9 dsl 9e2d6a73c68bd2a1 snap 9 425 458befaf4c9ffccd
load #4 r1 => why - off (30, 0, 30) anchor (30, 66, 30) pref 1.12.2 rev 1 design 4
  mine 1.12.2 4242
describe16 r0 9 blocks, box x30..53 y11..66 z25..38 (24×56×14); torch 9; dug 425 to mine out (y11..66); y11-12 116 dug · y13 43 dug · y14 1 torch, 2 dug · y15-19 3 dug · y20 1 torch, 2 dug · y21-25 3 dug · y26 1 torch, 2 dug · y27-31 3 dug · y32 1 torch, 2 dug · y33-37 3 dug · y38 1 torch, 2 dug · y39-43 3 dug · y44 1 torch, 2 dug · y45-49 3 dug · y50 1 torch, 2 dug · y51-55 3 dug · y56 1 torch, 2 dug · y57-61 3 dug · y62 1 torch, 2 dug · y63-66 3 dug
describe3 r0 9 blocks, box x30..53 y11..66 z25..38 (24×56×14); torch 9; dug 425 to mine out (y11..66); y11-12 116 dug · y13 43 dug · y14 1 torch, 2 dug · y15-19 3 dug · y20 1 torch, 2 dug · y21-25 3 dug · y26 1 torch, 2 dug · y27-31 3 dug · y32 1 torch, 2 dug · y33-37 3 dug · y38 1 torch, 2 dug · y39-43 3 dug · y44 1 torch, 2 dug · y45-49 3 dug · y50 1 torch, 2 dug · y51-55 3 dug · y56 1 torch, 2 dug · y57-61 3 dug · y62 1 torch, 2 dug · y63-66 3 dug
layer 0
y0, rows z25..38, cols x30..53:
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
#=ground .=dug
layer -1
y-1, rows z25..38, cols x30..53:
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
########################
#=ground .=dug
wire 81cb71a6224d8c6e dug f8fbe03b9a783012 enc 2e313f0634ca3ca8 rev 0
save 'pocket' new=False => ok True #5 r1 blocks 0 created True: saved as design #5 r1 (1.12.2, anchor absolute y 20): pocket mine 3×3×2 (y 20..21): 18 cells to dig
  session #5 'pocket' off (0, 0, 0)
  rec #5 'pocket' status accepted origin - by poofimafox/poofimafox cost 0
  desc a mine designed in the Design Studio by poofimafox
  anchor a mine designed in the Design Studio: absolute y, no site yet (place it in Story Mode)
  summary pocket mine 3×3×2 (y 20..21): 18 cells to dig
  eval drawn exactly
  bill 
  dsl
# a mine drawn in the Design Studio: dig the clear boxes, then place the blocks
blueprint pocket
size 3x2x3
version 1.12.2
kind mine
anchor absolute 10,20,10
entry 0,1,0
seed 9
facing south
clear 0,0,0 2,1,2

  /dsl
  r1 poofimafox player 'saved from the Design Studio by poofimafox' tag - origin - hash c464692c87940d1d blocks 0 dsl 3016d1abb92510bc snap 0 18 e7bb178f29ccef20
== history
capture captured 'stairs' as design #1: 3×3×3, 12 blocks (palette: planks, cobblestone, oak stairs); bill to rebuild: 9 planks (short 9), 2 cobblestone (short 2), 1 oak stairs (short 1); rebuild elsewhere: design build{id=1,at}
  rec #1 'stairs' status captured origin (0, 64, 0) by PoofImaFox/PoofImaFox cost 0
  desc captured from the world (x 0..2, y 64..66, z 0..2) for PoofImaFox
  anchor where it stands
  summary stairs 3×3×3, 12 blocks (palette: planks, cobblestone, oak stairs); bill to rebuild: 9 planks (short 9), 2 cobblestone (short 2), 1 oak stairs (short 1)
  eval captured 12/12 cells exact
  bill planks 9/0/0/9 craft from 3 logs from trees; cobblestone 2/0/0/2 gen ~4s; oak stairs 1/0/0/1 craft from 2 logs from trees
  dsl
# captured from the world at x 0..2, y 64..66, z 0..2: 12 blocks, drawn as it stands
blueprint stairs
size 3x3x3
origin 0,64,0
facing south
palette P=planks C=cobblestone O=oak_stairs
layer 0
PPP
PPP
PPP
layer 1
___
OC_
___
layer 2
___
_C_
___

  /dsl
  r1 PoofImaFox player 'captured by PoofImaFox' tag - origin (0, 64, 0) hash d790f4cd9a47da15 blocks 12 dsl 3f9cb1c17bcce15e snap 12 88 b3ee9ef329021f5f
status #1 stairs at r1 PoofImaFox just now 'captured by PoofImaFox'; world matches r1
commit clean => False nothing to commit: the world matches r1
revert clean => False #1 already matches r1
status #1 stairs at r1 PoofImaFox 3m ago 'captured by PoofImaFox'; world: +1 bed, +1 cobblestone, −1 cobblestone, −1 planks, ~1 oak stairs facing; commit{id=1} keeps it, revert{id=1} restores r1
diff r1→world: +1 bed, +1 cobblestone, −1 cobblestone, −1 planks, ~1 oak stairs facing; 2,65,0 +bed; 2,65,2 +cobblestone; 1,66,1 −cobblestone; 0,64,0 −planks; 0,65,1 ~oak stairs 0→2
diff r1 r1→world: +1 bed, +1 cobblestone, −1 cobblestone, −1 planks, ~1 oak stairs facing; 2,65,0 +bed; 2,65,2 +cobblestone; 1,66,1 −cobblestone; 0,64,0 −planks; 0,65,1 ~oak stairs 0→2
commit griefer => False committing #1 needs a trusted player's word (griefer asked); the change stays uncommitted (+1 bed, +1 cobblestone, −1 cobblestone, −1 planks, ~1 oak stairs facing)
commit fox => True committed #1 stairs as r2: +1 bed, +1 cobblestone, −1 cobblestone, −1 planks, ~1 oak stairs facing (1 not buildable, left out)
  rec #1 'stairs' status captured origin (0, 64, 0) by PoofImaFox/PoofImaFox cost 0
  desc captured from the world (x 0..2, y 64..66, z 0..2) for PoofImaFox
  anchor where it stands
  summary stairs 3×3×3, 12 blocks (palette: planks, cobblestone, oak stairs); bill to rebuild: 9 planks (short 9), 2 cobblestone (short 2), 1 oak stairs (short 1)
  eval captured 12/12 cells exact
  bill planks 9/0/0/9 craft from 3 logs from trees; cobblestone 2/0/0/2 gen ~4s; oak stairs 1/0/0/1 craft from 2 logs from trees
  dsl
# captured from the world at x 0..2, y 64..65, z 0..2: 11 blocks, drawn as it stands
blueprint stairs
size 3x2x3
origin 0,64,0
facing south
palette P=planks C=cobblestone O=oak_stairs:2
layer 0
_PP
PPP
PPP
layer 1
___
OC_
__C

  /dsl
  r2 PoofImaFox player 'a step on the landing' tag good origin (0, 64, 0) hash 86b1d29760333944 blocks 11 dsl 471e181c9c3fbff2 snap 11 88 d3eb3813faa63c1d
  r1 PoofImaFox player 'captured by PoofImaFox' tag - origin (0, 64, 0) hash d790f4cd9a47da15 blocks 12 dsl 3f9cb1c17bcce15e snap 12 88 b3ee9ef329021f5f
diff r1 r2 r1→r2: +1 cobblestone, −1 cobblestone, −1 planks, ~1 oak stairs facing; 2,65,2 +cobblestone; 1,66,1 −cobblestone; 0,64,0 −planks; 0,65,1 ~oak stairs 0→2
revert griefer => False reverting #1 needs a trusted player's word (griefer asked)
revert r9 => False no revision 'r9' of #1; design log{id=1} lists them
revert no world => False the build isn't in view: come closer first
commit no world => False the build isn't in view: come closer so I can see what changed
status no world #1 stairs at r2 PoofImaFox just now 'a step on the landing' [good]; world not in view
diff no world r1: the world isn't in view (come closer)
revert r1 => True reverting #1 stairs to r1 (now r3): +1 cobblestone, +1 planks, −1 bed, −1 cobblestone, ~1 oak stairs facing; building it now, a [task] reports the end
  rec #1 'stairs' status accepted origin (0, 64, 0) by PoofImaFox/PoofImaFox cost 0
  desc captured from the world (x 0..2, y 64..66, z 0..2) for PoofImaFox
  anchor where it stands
  summary stairs 3×3×3, 12 blocks (palette: planks, cobblestone, oak stairs); bill to rebuild: 9 planks (short 9), 2 cobblestone (short 2), 1 oak stairs (short 1)
  eval captured 12/12 cells exact
  bill planks 9/0/0/9 craft from 3 logs from trees; cobblestone 2/0/0/2 gen ~4s; oak stairs 1/0/0/1 craft from 2 logs from trees
  dsl
# captured from the world at x 0..2, y 64..66, z 0..2: 12 blocks, drawn as it stands
blueprint stairs
size 3x3x3
origin 0,64,0
facing south
palette P=planks C=cobblestone O=oak_stairs
layer 0
PPP
PPP
PPP
layer 1
__.
OC_
__.
layer 2
___
_C_
___

  /dsl
  r3 PoofImaFox player 'revert to r1' tag - origin (0, 64, 0) hash d790f4cd9a47da15 blocks 12 dsl a97d6ccb34f59e3e snap 12 88 b3ee9ef329021f5f
  r2 PoofImaFox player 'a step on the landing' tag good origin (0, 64, 0) hash 86b1d29760333944 blocks 11 dsl 471e181c9c3fbff2 snap 11 88 d3eb3813faa63c1d
  r1 PoofImaFox player 'captured by PoofImaFox' tag - origin (0, 64, 0) hash d790f4cd9a47da15 blocks 12 dsl 3f9cb1c17bcce15e snap 12 88 b3ee9ef329021f5f
revert prev refused => False reverting #1 stairs to r2 (now r4): +1 cobblestone; saved, but can't start: busy
revert head => False reverting #1 stairs to r4 (now r4): +1 cobblestone; saved, but can't start: busy
started 1,1,1
log #1 stairs: r4 PoofImaFox just now 'revert to r2' · r3 PoofImaFox just now 'revert to r1' · …
memory stairs 0,64,0 2,65,2 [captured as design #1 by PoofImaFox (x 0..2, y 64..66, z 0..2) | r2 of design #1: x 0..2, y 64..65, z 0..2 | r3 of design #1: x 0..2, y 64..66, z 0..2 | r4 of design #1: x 0..2, y 64..65, z 0..2 | r4 of design #1: x 0..2, y 64..65, z 0..2]
status own #1 stairs at r4 PoofImaFox just now 'revert to r2'; world: +1 glass, −1 cobblestone (me 1); commit{id=1} keeps it, revert{id=1} restores r4
commit own => True committed #1 stairs as r5: +1 glass, −1 cobblestone
commit own2 => False nothing to commit: the world matches r5
  rec #1 'stairs' status accepted origin (0, 64, 0) by PoofImaFox/PoofImaFox cost 0
  desc captured from the world (x 0..2, y 64..66, z 0..2) for PoofImaFox
  anchor where it stands
  summary stairs 3×3×3, 12 blocks (palette: planks, cobblestone, oak stairs); bill to rebuild: 9 planks (short 9), 2 cobblestone (short 2), 1 oak stairs (short 1)
  eval captured 12/12 cells exact
  bill planks 9/0/0/9 craft from 3 logs from trees; cobblestone 2/0/0/2 gen ~4s; oak stairs 1/0/0/1 craft from 2 logs from trees
  dsl
# captured from the world at x 0..2, y 64..67, z 0..2: 11 blocks, drawn as it stands
blueprint stairs
size 3x4x3
origin 0,64,0
facing south
palette P=planks C=cobblestone G=glass O=oak_stairs:2
layer 0
_PP
PPP
PPP
layer 1
___
O._
__C
layer 2
___
_._
___
layer 3
___
_G_
___

  /dsl
  r5 maid_chan bot 'world changes (me 1)' tag tagonly origin (0, 64, 0) hash 5d66542d783b706a blocks 11 dsl da40e0993ac85215 snap 11 113 066759949400a878
  r4 PoofImaFox player 'revert to r2' tag - origin (0, 64, 0) hash 86b1d29760333944 blocks 11 dsl 471e181c9c3fbff2 snap 11 88 d3eb3813faa63c1d
  r3 PoofImaFox player 'revert to r1' tag - origin (0, 64, 0) hash d790f4cd9a47da15 blocks 12 dsl a97d6ccb34f59e3e snap 12 88 b3ee9ef329021f5f
  r2 PoofImaFox player 'a step on the landing' tag good origin (0, 64, 0) hash 86b1d29760333944 blocks 11 dsl 471e181c9c3fbff2 snap 11 88 d3eb3813faa63c1d
  r1 PoofImaFox player 'captured by PoofImaFox' tag - origin (0, 64, 0) hash d790f4cd9a47da15 blocks 12 dsl 3f9cb1c17bcce15e snap 12 88 b3ee9ef329021f5f
"""""";
}
