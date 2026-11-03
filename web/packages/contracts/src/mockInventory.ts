/**
 * Mock inventories (test and mock helpers, exported from `@mineclaw/contracts/testing`): realistic bodies for the mock modes
 * (MSW in the web app, Nest's mock bridge). The miner (main) and the crew are b1.8.1; the nether scout (`*_67`) is a 1.12.2 body
 * with enchanted gear and an off hand, so both versions' layouts and icon sets show. `tick` moves a few counts so the UI's
 * change animation can be seen; the result is the public (camelCase) form, `toSnake` it for the bridge wire.
 */
import type { BodyInventory, InventorySlot } from './inventory.js';

type Version = 'b1.8.1' | '1.12.2';
/** [id, meta, count, name, display, maxDurability (0 = none), enchanted]. */
type Item = [number, number, number, string, string, number?, boolean?];

function slotOf(slot: number, [id, meta, count, name, display, max = 0, enchanted = false]: Item): InventorySlot {
  return {
    slot,
    id,
    meta,
    count,
    name,
    display,
    durability: max > 0 ? { max, left: Math.max(0, Math.min(max, max - meta)) } : null,
    enchanted,
  };
}

function build(
  name: string,
  version: Version,
  held: number,
  items: Record<number, Item>,
  armor: Array<Item | null>,
  offhand: Item | null,
  online = true,
): BodyInventory {
  const slots = Object.entries(items)
    .map(([k, v]) => slotOf(Number(k), v))
    .sort((a, b) => a.slot - b.slot);
  const a = armor.map((it, i) => (it ? slotOf(39 - i, it) : null));
  return {
    name,
    version,
    heldSlot: held,
    slots,
    armor: [a[0] ?? null, a[1] ?? null, a[2] ?? null, a[3] ?? null],
    offhand: offhand ? slotOf(40, offhand) : null,
    at: Date.now(),
    online,
  };
}

function miner(name: string, tick: number): BodyInventory {
  const cobble = 20 + (tick % 45);
  return build(
    name,
    'b1.8.1',
    0,
    {
      0: [278, 340 + (tick % 7), 1, 'diamond_pickaxe', 'Diamond Pickaxe', 1561],
      1: [267, 41, 1, 'iron_sword', 'Iron Sword', 250],
      2: [4, 0, cobble, 'cobblestone', 'Cobblestone'],
      3: [50, 0, 37, 'torch', 'Torch'],
      4: [297, 0, 12, 'bread', 'Bread'],
      5: [326, 0, 1, 'water_bucket', 'Water Bucket'],
      7: [17, 0, 23, 'log', 'Oak Wood'],
      8: [320, 0, 5, 'cooked_porkchop', 'Cooked Porkchop'],
      9: [4, 0, 64, 'cobblestone', 'Cobblestone'],
      10: [4, 0, 64, 'cobblestone', 'Cobblestone'],
      11: [3, 0, 41, 'dirt', 'Dirt'],
      12: [263, 0, 18, 'coal', 'Coal'],
      13: [265, 0, 9, 'iron_ingot', 'Iron Ingot'],
      14: [331, 0, 22, 'redstone', 'Redstone'],
      18: [35, 14, 6, 'red_wool', 'Red Wool'],
      19: [351, 4, 3, 'lapis_lazuli', 'Lapis Lazuli'],
      22: [13, 0, 16, 'gravel', 'Gravel'],
      27: [5, 0, 30, 'planks', 'Wooden Planks'],
      28: [280, 0, 14, 'stick', 'Stick'],
      31: [274, 112, 1, 'stone_pickaxe', 'Stone Pickaxe', 131],
      35: [264, 0, 2, 'diamond', 'Diamond'],
    },
    [
      [306, 20, 1, 'iron_helmet', 'Iron Helmet', 165],
      [307, 96, 1, 'iron_chestplate', 'Iron Chestplate', 240],
      null,
      [301, 50, 1, 'leather_boots', 'Leather Boots', 65],
    ],
    null,
  );
}

function scout(name: string, tick: number): BodyInventory {
  return build(
    name,
    '1.12.2',
    2,
    {
      0: [276, 210, 1, 'diamond_sword', 'Diamond Sword', 1561, true],
      1: [261, 77, 1, 'bow', 'Bow', 384, true],
      2: [278, 802, 1, 'diamond_pickaxe', 'Diamond Pickaxe', 1561, true],
      3: [87, 0, 30 + (tick % 20), 'netherrack', 'Netherrack'],
      4: [49, 0, 10, 'obsidian', 'Obsidian'],
      5: [259, 12, 1, 'flint_and_steel', 'Flint and Steel', 64],
      6: [368, 0, 3, 'ender_pearl', 'Ender Pearl'],
      7: [396, 0, 12, 'golden_carrot', 'Golden Carrot'],
      8: [373, 0, 2, 'potion', 'Potion of Healing', 0, true],
      9: [262, 0, 48, 'arrow', 'Arrow'],
      10: [406, 0, 21, 'quartz', 'Nether Quartz'],
      11: [348, 0, 7, 'glowstone_dust', 'Glowstone Dust'],
      15: [384, 0, 4, 'experience_bottle', "Bottle o' Enchanting"],
      20: [403, 0, 1, 'enchanted_book', 'Enchanted Book', 0, true],
    },
    [
      [310, 31, 1, 'diamond_helmet', 'Diamond Helmet', 363, true],
      [311, 140, 1, 'diamond_chestplate', 'Diamond Chestplate', 528, true],
      [308, 12, 1, 'iron_leggings', 'Iron Leggings', 225],
      [317, 60, 1, 'golden_boots', 'Golden Boots', 91],
    ],
    [442, 44, 1, 'shield', 'Shield', 336],
  );
}

function farmer(name: string, tick: number): BodyInventory {
  return build(
    name,
    'b1.8.1',
    1,
    {
      0: [292, 180, 1, 'iron_hoe', 'Iron Hoe', 250],
      1: [295, 0, 20 + (tick % 30), 'seeds', 'Seeds'],
      2: [296, 0, 34, 'wheat', 'Wheat'],
      3: [351, 15, 18, 'bone_meal', 'Bone Meal'],
      4: [297, 0, 8, 'bread', 'Bread'],
      6: [338, 0, 11, 'reeds', 'Sugar Canes'],
      9: [360, 0, 19, 'melon', 'Melon'],
      10: [86, 0, 3, 'pumpkin', 'Pumpkin'],
      12: [3, 0, 16, 'dirt', 'Dirt'],
    },
    [null, [299, 30, 1, 'leather_chestplate', 'Leather Tunic', 80], null, null],
    null,
  );
}

function sorter(name: string): BodyInventory {
  return build(
    name,
    'b1.8.1',
    4,
    {
      4: [54, 0, 2, 'chest', 'Chest'],
      5: [65, 0, 12, 'ladder', 'Ladder'],
      9: [1, 0, 64, 'stone', 'Stone'],
      10: [12, 0, 52, 'sand', 'Sand'],
    },
    [null, null, null, null],
    null,
  );
}

function crew(name: string, i: number, tick: number): BodyInventory {
  const axe = 30 + ((i * 37 + tick) % 200);
  const items: Record<number, Item> = {
    0: [258, axe, 1, 'iron_axe', 'Iron Axe', 250],
    1: [17, 0, 4 + ((i * 11 + tick) % 60), 'log', 'Oak Wood'],
    2: [6, 0, 3 + (i % 9), 'sapling', 'Oak Sapling'],
    3: [260, 0, 2, 'apple', 'Apple'],
    8: [297, 0, 4, 'bread', 'Bread'],
  };
  if (i % 3 === 0) items[9] = [17, 2, 18, 'birch_wood', 'Birch Wood'];
  return build(name, 'b1.8.1', 0, items, [null, null, null, null], null);
}

/** A mock body's inventory by login ("maid_chan", "maid_chan_67", a crew body); `index` varies the crew. */
export function mockInventory(name: string, index = 0, tick = 0): BodyInventory {
  if (/_67$/.test(name)) return scout(name, tick);
  if (/_68$/.test(name)) return farmer(name, tick);
  if (/_70$/.test(name)) return sorter(name);
  if (/_\d+$/.test(name) && !/_109$/.test(name)) return crew(name, index, tick);
  return miner(name, tick);
}
