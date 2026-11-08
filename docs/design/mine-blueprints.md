# Mine blueprints: what to dig, measured

How to lay out a Design Studio mine (docs/web/api.md "Mine designs") so the pawns get the most ore for the blocks they dig.
The answer comes from a layout study on the studio's own terrain generator, reproducible with:

```
dotnet run -c Release --project tools/MineStudy        # 40 seeds per version; an argument sets the seed count
```

## The recommended blueprint

1. **Access:** a spiral shaft (`spiral_shaft`, 4×4 around a 2×2 core, or 3×3 around 1×1) from the surface to a landing at the
   mining level. Every access digs about 3 blocks per level of depth, so the compact spiral costs no more than a straight stair,
   and it keeps the mine on a small footprint.
2. **Level:** feet at **y 11** for diamond, redstone and gold, and **y 14–16** for lapis and iron. For diamonds, y 7–11 all measure about
   the same and y 13 and above fall off fast. y 11 is the lowest of them that stays above the lava lakes (y 10 and below) in
   the real game.
3. **Layout:** a **1×2 main corridor** with **1×2 branches 4 solid blocks apart** on both sides (`strip_mine`'s defaults, and the
   presets `<version>-diamond-y11` and `<version>-lapis-…-y16`).
4. **Only when the plot, not the digging, is scarce:** branches 2 apart (`<version>-diamond-y11-thorough`). Every block between
   two branches touches one. It digs about 70% more than the 4-apart mine for about 30% more ore.

The AI designer is told these rules (`DesignerTools.LayoutRules`). It checks a plot's levels with `ore_levels` (the best feet
levels for a 1×2 strip mine across *this* plot, per ore) and its result with `mine_stats` (blocks to mine, ore in sight, ore
found by following the veins, ore per 100 blocks mined). The studio panel shows "per 100" per ore next to mined and in walls.

## How it is measured

- **Terrain:** `MineTerrain`, the studio's own generator. It uses the ore table's bands, veins per chunk and vein sizes, with
  each vein a random walk. It has no lava or water, and the real world differs, so the figures rank layouts; they don't predict a
  real mine's haul.
- **Yield:** `OreVeins`. A miner who sees one block of a vein mines the whole vein, so a layout finds every block of every vein
  that touches a dug cell or one of its walls. The study reports that per 100 blocks mined (`MineStats.ToMine`; bedrock and
  open cave cells cost nothing). It also reports, in brackets, the share of the plot's ore of that kind the layout finds.
- **Layout:** the studio's strip template across the whole 64×64 plot: a corridor along x at z 31 and branches to both edges.
  For shapes the template doesn't draw, parallel tunnels with the same corridor.

## Results (40 seeds, diamonds per 100 blocks mined, % of the plot's diamonds found)

Level, 1×2 branches 4 apart:

| feet y | 5 | 7 | 9 | 11 | 12 | 13 | 14 | 16 |
|---|---|---|---|---|---|---|---|---|
| 1.12.2 | 0.93 (22%) | 1.27 (30%) | 1.30 (31%) | 1.23 (29%) | 1.15 (27%) | 1.01 (23%) | 0.72 (17%) | 0.23 (5%) |
| b1.8.1 | 0.83 (20%) | 0.94 (23%) | 0.96 (23%) | 1.05 (25%) | 1.01 (25%) | 0.89 (22%) | 0.85 (21%) | 0.31 (8%) |

Lapis peaks at feet y 14 (1.12.2: 0.99 per 100) and iron at y 16 (7.44); gold is flat from y 11 to y 20.

Spacing (solid blocks between branches), feet y 11:

| apart | 1 | 2 | 3 | **4** | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|
| blocks mined | 4160 | 2774 | 2144 | **1640** | 1388 | 1262 | 1136 |
| 1.12.2 | 0.70 (41%) | 0.96 (38%) | 1.16 (35%) | **1.23 (29%)** | 1.20 (23%) | 1.21 (22%) | 1.15 (18%) |
| b1.8.1 | 0.64 (40%) | 0.85 (35%) | 0.96 (30%) | **1.05 (25%)** | 0.99 (20%) | 1.01 (19%) | 0.94 (16%) |

The yield per block peaks at 4 apart and stays flat to 6 while the plot's share keeps falling, so 4 is the best of both. In
b1.8.1 the veins are one block smaller, but that costs a little coverage, not efficiency: its preset used to put branches 2
apart, the least efficient spacing measured.

Everything else measured worse per block dug (1.12.2, diamonds):

| variant | per 100 | vs. the 1×2 / 4 apart mine |
|---|---|---|
| 2×3 main corridor, 4 apart | 1.14 | digs 14% more for 5% more diamonds |
| 1×3 tunnels, 3 apart | 0.87 | taller tunnels see one more layer for half again the digging |
| 2×2 tunnels, 3 apart | 0.77 | the second column sees almost nothing new |
| poke holes (1×1, every 4) | 1.01–1.12 | the pokes cost about what they reveal |
| a second level 3 above, staggered | 0.75 | the two levels' sights overlap |
| a second level at y 6, staggered | 1.03 | finds 63% of the plot's diamonds, but y 6 is in the real game's lava |
