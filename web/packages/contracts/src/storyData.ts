/**
 * Story Mode reference data shared by the web and the mocks: item labels, the default loot categories and the recipe book.
 * The director owns the real ones (it sends `categories` in the colony state and answers `/api/story/recipes`); these are
 * what the mocks serve and what the web falls back to for labels.
 */
import type { StoryCategory, StoryRecipe } from './story.js';

/** Labels of the items and blocks Story Mode shows ("id:meta", or "id" for every meta). */
export const STORY_ITEM_LABELS: Readonly<Record<string, string>> = {
  '1': 'Stone', '2': 'Grass block', '3': 'Dirt', '4': 'Cobblestone', '5': 'Planks', '6': 'Sapling', '12': 'Sand', '13': 'Gravel',
  '14': 'Gold ore', '15': 'Iron ore', '16': 'Coal ore', '17': 'Log', '18': 'Leaves', '20': 'Glass', '24': 'Sandstone', '26': 'Bed',
  '35': 'Wool', '37': 'Dandelion', '38': 'Rose', '45': 'Bricks', '50': 'Torch', '54': 'Chest', '56': 'Diamond ore', '58': 'Crafting table',
  '61': 'Furnace', '65': 'Ladder', '81': 'Cactus', '85': 'Fence', '86': 'Pumpkin', '98': 'Stone bricks', '103': 'Melon block',
  '256': 'Iron shovel', '257': 'Iron pickaxe', '258': 'Iron axe', '260': 'Apple', '261': 'Bow', '262': 'Arrow', '263': 'Coal',
  '264': 'Diamond', '265': 'Iron ingot', '266': 'Gold ingot', '267': 'Iron sword', '268': 'Wooden sword', '269': 'Wooden shovel',
  '270': 'Wooden pickaxe', '271': 'Wooden axe', '272': 'Stone sword', '273': 'Stone shovel', '274': 'Stone pickaxe', '275': 'Stone axe',
  '276': 'Diamond sword', '278': 'Diamond pickaxe', '280': 'Stick', '287': 'String', '288': 'Feather', '289': 'Gunpowder',
  '290': 'Wooden hoe', '291': 'Stone hoe', '292': 'Iron hoe', '295': 'Seeds', '296': 'Wheat', '297': 'Bread', '298': 'Leather cap',
  '299': 'Leather tunic', '300': 'Leather pants', '301': 'Leather boots', '306': 'Iron helmet', '307': 'Iron chestplate',
  '308': 'Iron leggings', '309': 'Iron boots', '319': 'Raw porkchop', '320': 'Cooked porkchop', '323': 'Sign', '324': 'Wooden door',
  '325': 'Bucket', '331': 'Redstone', '334': 'Leather', '337': 'Clay', '338': 'Sugar cane', '341': 'Slimeball', '344': 'Egg',
  '349': 'Raw fish', '350': 'Cooked fish', '352': 'Bone', '353': 'Sugar', '355': 'Bed', '360': 'Melon slice', '363': 'Raw beef',
  '364': 'Steak', '365': 'Raw chicken', '366': 'Cooked chicken', '367': 'Rotten flesh', '368': 'Ender pearl', '375': 'Spider eye',
  '391': 'Carrot', '392': 'Potato', '393': 'Baked potato',
};

/** A label for an item, a block or a category key ("17:-1"). */
export function storyItemLabel(id: number, meta = 0): string {
  return STORY_ITEM_LABELS[`${id}:${meta}`] ?? STORY_ITEM_LABELS[String(id)] ?? `Item ${id}${meta > 0 ? `:${meta}` : ''}`;
}

const any = (...ids: number[]): Array<[number, number]> => ids.map((i) => [i, -1]);

/** The default loot tree (flat; `children` are ids; roots are the ones nobody lists). */
export const STORY_DEFAULT_CATEGORIES: StoryCategory[] = [
  { id: 'resources', label: 'Resources', children: ['wood', 'stone', 'soil', 'ores'], items: [] },
  { id: 'wood', label: 'Wood', children: [], items: any(17, 5, 280) },
  { id: 'stone', label: 'Stone', children: [], items: any(1, 4, 24, 98) },
  { id: 'soil', label: 'Dirt and sand', children: [], items: any(3, 12, 13, 337) },
  { id: 'ores', label: 'Ores and metals', children: [], items: any(14, 15, 16, 263, 265, 266, 264, 331) },
  { id: 'food', label: 'Food', children: ['food_raw', 'food_cooked', 'produce'], items: [] },
  { id: 'food_raw', label: 'Raw meat', children: [], items: any(319, 363, 365, 349) },
  { id: 'food_cooked', label: 'Meals', children: [], items: any(320, 364, 366, 350, 297, 393) },
  { id: 'produce', label: 'Produce', children: [], items: any(296, 260, 360, 391, 392, 338, 344, 353) },
  { id: 'plants', label: 'Seeds and plants', children: [], items: any(295, 6, 37, 38, 81, 86) },
  { id: 'gear', label: 'Gear', children: ['tools', 'weapons', 'armour'], items: [] },
  { id: 'tools', label: 'Tools', children: [], items: any(256, 257, 258, 269, 270, 271, 273, 274, 275, 278, 290, 291, 292, 325) },
  { id: 'weapons', label: 'Weapons', children: [], items: any(267, 268, 272, 276, 261, 262) },
  { id: 'armour', label: 'Armour', children: [], items: any(298, 299, 300, 301, 306, 307, 308, 309) },
  { id: 'mob_drops', label: 'Mob drops', children: [], items: any(367, 352, 287, 289, 288, 334, 375, 368, 341) },
  { id: 'building', label: 'Building', children: [], items: any(20, 35, 45, 50, 65, 85, 324, 323) },
  { id: 'furniture', label: 'Furniture', children: [], items: any(54, 58, 61, 355) },
];

const r = (
  id: string,
  label: string,
  workbench: string,
  workType: string,
  output: [number, number, number],
  inputs: StoryRecipe['inputs'],
  group: string,
  fuel?: boolean,
): StoryRecipe => ({ id, label, workbench, workType, output, inputs, group, ...(fuel ? { fuel: true } : {}) });

/** The mock recipe book (b1.8.1 recipes; the director has the real one). */
export const STORY_DEFAULT_RECIPES: StoryRecipe[] = [
  r('planks', 'Planks', 'crafting_table', 'craft', [5, 0, 4], [{ options: [[17, -1]], count: 1 }], 'wood'),
  r('sticks', 'Sticks', 'crafting_table', 'craft', [280, 0, 4], [{ category: 'wood', options: [[5, -1]], count: 2 }], 'wood'),
  r('torch', 'Torches', 'crafting_table', 'craft', [50, 0, 4], [{ options: [[263, -1]], count: 1 }, { options: [[280, 0]], count: 1 }], 'building'),
  r('chest', 'Chest', 'crafting_table', 'craft', [54, 0, 1], [{ options: [[5, -1]], count: 8 }], 'furniture'),
  r('crafting_table', 'Crafting table', 'crafting_table', 'craft', [58, 0, 1], [{ options: [[5, -1]], count: 4 }], 'furniture'),
  r('furnace', 'Furnace', 'crafting_table', 'craft', [61, 0, 1], [{ options: [[4, 0]], count: 8 }], 'furniture'),
  r('ladder', 'Ladders', 'crafting_table', 'craft', [65, 0, 3], [{ options: [[280, 0]], count: 7 }], 'building'),
  r('fence', 'Fence', 'crafting_table', 'craft', [85, 0, 2], [{ options: [[280, 0]], count: 6 }], 'building'),
  r('door', 'Wooden door', 'crafting_table', 'craft', [324, 0, 1], [{ options: [[5, -1]], count: 6 }], 'building'),
  r('wooden_pickaxe', 'Wooden pickaxe', 'crafting_table', 'craft', [270, 0, 1], [{ options: [[5, -1]], count: 3 }, { options: [[280, 0]], count: 2 }], 'tools'),
  r('stone_pickaxe', 'Stone pickaxe', 'crafting_table', 'craft', [274, 0, 1], [{ options: [[4, 0]], count: 3 }, { options: [[280, 0]], count: 2 }], 'tools'),
  r('stone_axe', 'Stone axe', 'crafting_table', 'craft', [275, 0, 1], [{ options: [[4, 0]], count: 3 }, { options: [[280, 0]], count: 2 }], 'tools'),
  r('stone_sword', 'Stone sword', 'crafting_table', 'craft', [272, 0, 1], [{ options: [[4, 0]], count: 2 }, { options: [[280, 0]], count: 1 }], 'weapons'),
  r('iron_pickaxe', 'Iron pickaxe', 'crafting_table', 'craft', [257, 0, 1], [{ options: [[265, 0]], count: 3 }, { options: [[280, 0]], count: 2 }], 'tools'),
  r('bow', 'Bow', 'crafting_table', 'craft', [261, 0, 1], [{ options: [[280, 0]], count: 3 }, { options: [[287, 0]], count: 3 }], 'weapons'),
  r('bread', 'Bread', 'crafting_table', 'cook', [297, 0, 1], [{ options: [[296, 0]], count: 3 }], 'food_cooked'),
  r('bucket', 'Bucket', 'crafting_table', 'craft', [325, 0, 1], [{ options: [[265, 0]], count: 3 }], 'tools'),
  r('cook_pork', 'Cooked porkchop', 'furnace', 'cook', [320, 0, 1], [{ options: [[319, 0]], count: 1 }], 'food_cooked', true),
  r('cook_beef', 'Steak', 'furnace', 'cook', [364, 0, 1], [{ options: [[363, 0]], count: 1 }], 'food_cooked', true),
  r('cook_chicken', 'Cooked chicken', 'furnace', 'cook', [366, 0, 1], [{ options: [[365, 0]], count: 1 }], 'food_cooked', true),
  r('cook_fish', 'Cooked fish', 'furnace', 'cook', [350, 0, 1], [{ options: [[349, 0]], count: 1 }], 'food_cooked', true),
  r('smelt_iron', 'Iron ingot', 'furnace', 'craft', [265, 0, 1], [{ options: [[15, 0]], count: 1 }], 'ores', true),
  r('smelt_gold', 'Gold ingot', 'furnace', 'craft', [266, 0, 1], [{ options: [[14, 0]], count: 1 }], 'ores', true),
  r('glass', 'Glass', 'furnace', 'craft', [20, 0, 1], [{ options: [[12, 0]], count: 1 }], 'building', true),
  r('stone', 'Smooth stone', 'furnace', 'craft', [1, 0, 1], [{ options: [[4, 0]], count: 1 }], 'stone', true),
  r('charcoal', 'Charcoal', 'furnace', 'craft', [263, 1, 1], [{ options: [[17, -1]], count: 1 }], 'ores', true),
];

/** Every item key a category covers, its children included ("id:meta" strings). */
export function categoryItems(categories: readonly StoryCategory[], id: string, seen = new Set<string>()): string[] {
  if (seen.has(id)) return [];
  seen.add(id);
  const byId = new Map<string, StoryCategory>();
  const index = (list: readonly (StoryCategory | string)[]) => {
    for (const c of list) {
      if (typeof c === 'string') continue;
      byId.set(c.id, c);
      index(c.children);
    }
  };
  index(categories);
  const c = byId.get(id);
  if (!c) return [];
  const out = c.items.map(([i, m]) => `${i}:${m}`);
  for (const ch of c.children) out.push(...categoryItems(categories, typeof ch === 'string' ? ch : ch.id, seen));
  return out;
}

/** Does a storage filter accept an item? Deny wins over allow; "id:-1" matches every meta; an empty allow list takes nothing. */
export function filterAccepts(categories: readonly StoryCategory[], filter: { allow: readonly string[]; deny: readonly string[] }, id: number, meta: number): boolean {
  const keys = (list: readonly string[]) => new Set(list.flatMap((k) => (/^-?\d+:-?\d+$/.test(k) ? [k] : categoryItems(categories, k))));
  const hit = (set: Set<string>) => set.has(`${id}:${meta}`) || set.has(`${id}:-1`);
  if (hit(keys(filter.deny))) return false;
  return hit(keys(filter.allow));
}
