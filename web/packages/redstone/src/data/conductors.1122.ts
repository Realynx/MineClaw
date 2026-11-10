/**
 * 1.12.2: what each block is to redstone. A block passes strong power on (a "normal cube") when it is a full cube of an
 * opaque material and is not itself a power source. Our own tables, checked against the bot's `Light1122`, which was
 * written from the same rules.
 */

/** Blocks that are not full cubes: plants, liquids, slabs, stairs, fences, doors, chests, pistons, every thin part. */
export const NOT_FULL_CUBES_1122: readonly number[] = [
  0, 6, 8, 9, 10, 11, 20, 26, 27, 28, 29, 30, 31, 32, 33, 34, 36, 37, 38, 39, 40, 44, 50, 51, 53, 54, 55, 59, 60, 63,
  64, 65, 66, 67, 68, 69, 70, 71, 72, 75, 76, 77, 78, 81, 83, 85, 90, 92, 93, 94, 95, 96, 101, 102, 104, 105, 106, 107,
  108, 109, 111, 113, 114, 115, 116, 117, 118, 119, 120, 122, 126, 127, 128, 130, 131, 132, 134, 135, 136, 138, 139,
  140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 154, 156, 157, 160, 163, 164, 167, 171, 175, 176, 177,
  178, 180, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 194, 195, 196, 197, 198, 199, 200, 203, 205,
  207, 208, 209, 217, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 253, 254,
];

/** Full cubes of a see-through material, which do not conduct: leaves, glass, glowstone, sea lantern, beacon, ice, TNT. */
export const TRANSLUCENT_1122: readonly number[] = [18, 20, 46, 79, 89, 95, 138, 161, 169, 212];

/** Blocks whose material nothing can stand in the way of: air, plants, circuits, liquids, fire, portals, web, carpet. */
export const NO_BODY_1122: readonly number[] = [
  0, 6, 8, 9, 10, 11, 27, 28, 30, 31, 32, 37, 38, 39, 40, 50, 51, 55, 59, 66, 69, 75, 76, 77, 78, 83, 90, 93, 94, 104,
  105, 106, 111, 115, 119, 131, 132, 141, 142, 143, 149, 150, 157, 171, 175, 207, 209, 217,
];

/**
 * Blocks that give power: dust links to one on any side, and a full cube among them (the redstone block, the observer)
 * does not conduct.
 */
export const POWER_SOURCES_1122: readonly number[] = [
  28, 55, 69, 70, 72, 75, 76, 77, 93, 94, 131, 143, 146, 147, 148, 149, 150, 151, 152, 178, 218,
];

/** Blocks with redstone behaviour of their own. One the engine has no handler for is reported, not simulated. */
export const PARTS_1122: readonly number[] = [
  23, 25, 27, 28, 29, 33, 34, 36, 46, 55, 64, 69, 70, 71, 72, 75, 76, 77, 93, 94, 96, 107, 123, 124, 131, 132, 143, 146,
  147, 148, 149, 150, 151, 152, 154, 157, 158, 167, 178, 183, 184, 185, 186, 187, 193, 194, 195, 196, 197, 218,
];

export const FENCES_1122: readonly number[] = [85, 113, 188, 189, 190, 191, 192];
export const SLABS_1122: readonly number[] = [44, 126, 182, 205];
export const STAIRS_1122: readonly number[] = [53, 67, 108, 109, 114, 128, 134, 135, 136, 156, 163, 164, 180, 203];

/** Blocks nothing hangs on although they are full: leaves, trapdoors, beacon, cauldron, glass, glowstone, ice, sea lantern. */
export const NEVER_HOLD_1122: readonly number[] = [
  18, 20, 79, 89, 95, 96, 118, 138, 161, 167, 169, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232,
  233, 234,
];
