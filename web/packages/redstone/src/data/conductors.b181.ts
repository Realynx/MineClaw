/**
 * Beta 1.8.1: which blocks are full solid cubes. A full solid cube passes on the strong power it receives, holds dust,
 * torches, levers, buttons, repeaters and plates, and stops dust from stepping past it. The game calls it a "normal
 * cube": a solid material and a full render cube. Our own table, checked against the bot's `LightTables.IsNormalCube`,
 * which was written from the same rule.
 */

/** The game has block ids 1..109. */
export const LAST_ID_B181 = 109;

/**
 * The ids in 1..109 that are not full solid cubes: liquids, plants, glass, leaves, ice, TNT, slabs, stairs, farmland,
 * fences, doors, pistons, rails, and every thin part. Chests, furnaces, dispensers, note blocks, spawners and glowstone
 * are full solid cubes in this version.
 */
export const NOT_CUBES_B181: readonly number[] = [
  6, 8, 9, 10, 11, 18, 20, 26, 27, 28, 29, 30, 31, 32, 33, 34, 36, 37, 38, 39, 40, 44, 46, 50, 51, 53, 55, 59, 60, 63,
  64, 65, 66, 67, 68, 69, 70, 71, 72, 75, 76, 77, 78, 79, 81, 83, 85, 90, 92, 93, 94, 96, 101, 102, 104, 105, 106, 107,
  108, 109,
];

/** Blocks dust links to on any side: the game's "power source" blocks. A repeater is not one in this version. */
export const POWER_SOURCES_B181: readonly number[] = [28, 55, 69, 70, 72, 75, 76, 77];

/** Blocks with redstone behaviour of their own. One the engine has no handler for is reported, not simulated. */
export const PARTS_B181: readonly number[] = [
  23, 25, 27, 28, 29, 33, 34, 36, 46, 55, 64, 69, 70, 71, 72, 75, 76, 77, 93, 94, 96,
];

export const FENCES_B181: readonly number[] = [85];
