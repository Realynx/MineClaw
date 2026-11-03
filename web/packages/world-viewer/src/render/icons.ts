// Inventory-style block icons from the bundled textures (the Design Studio's hotbar and palette): a cube in the inventory's
// isometric view (top, left and right faces shaded like vanilla's item renderer), or a flat sprite for blocks the game shows
// flat (doors, torches, ladders, panes, bars). b1.8.1 reads terrain.png through the same tile table as the mesher; 1.12.2
// names the textures of the design palette's blocks here (anything else falls back to the flat palette colour).

import type { AssetBundle, Pack1122 } from '../assets/bundle';
import { tileFor } from '../mesh/beta/textures';
import { flatColour } from '../world/blocks';

const FLAT = new Set([6, 27, 28, 30, 31, 32, 37, 38, 39, 40, 50, 55, 59, 64, 65, 66, 69, 71, 75, 76, 77, 83, 101, 102, 106, 143]);

const WOOL = ['white', 'orange', 'magenta', 'light_blue', 'yellow', 'lime', 'pink', 'gray', 'silver', 'cyan', 'purple', 'blue', 'brown', 'green', 'red', 'black'];
const WOOD = ['oak', 'spruce', 'birch', 'jungle', 'acacia', 'big_oak'];

/** 1.12.2 textures [top, side] (or one for every face) of a state; null = unknown. */
function textures1122(id: number, meta: number): [string, string] | null {
  const one = (t: string): [string, string] => [t, t];
  switch (id) {
    case 1: return one('stone');
    case 2: return ['grass_top', 'grass_side'];
    case 3: return one('dirt');
    case 4: return one('cobblestone');
    case 5: return one(`planks_${WOOD[meta % 6]}`);
    case 12: return one('sand');
    case 13: return one('gravel');
    case 17: return [`log_${WOOD[meta & 3]}_top`, `log_${WOOD[meta & 3]}`];
    case 19: return one('sponge');
    case 20: return one('glass');
    case 22: return one('lapis_block');
    case 23: return ['furnace_top', 'dispenser_front_horizontal'];
    case 24: return ['sandstone_top', 'sandstone_normal'];
    case 25: return one('noteblock');
    case 35: return one(`wool_colored_${WOOL[meta & 15]}`);
    case 41: return one('gold_block');
    case 42: return one('iron_block');
    case 43:
    case 44: return [['stone_slab_top', 'sandstone_top', 'planks_oak', 'cobblestone', 'brick', 'stonebrick'][meta & 7] ?? 'stone_slab_top', ['stone_slab_side', 'sandstone_normal', 'planks_oak', 'cobblestone', 'brick', 'stonebrick'][meta & 7] ?? 'stone_slab_side'];
    case 45: return one('brick');
    case 47: return ['planks_oak', 'bookshelf'];
    case 48: return one('cobblestone_mossy');
    case 49: return one('obsidian');
    case 50: return one('torch_on');
    case 53: return one('planks_oak');
    case 54: return one('planks_oak');
    case 57: return one('diamond_block');
    case 58: return ['crafting_table_top', 'crafting_table_front'];
    case 61: return ['furnace_top', 'furnace_front_off'];
    case 64: return one('door_wood_lower');
    case 65: return one('ladder');
    case 67: return one('cobblestone');
    case 71: return one('door_iron_lower');
    case 80: return one('snow');
    case 82: return one('clay');
    case 84: return ['jukebox_top', 'jukebox_side'];
    case 85: return one('planks_oak');
    case 86: return ['pumpkin_top', 'pumpkin_face_off'];
    case 87: return one('netherrack');
    case 88: return one('soul_sand');
    case 89: return one('glowstone');
    case 91: return ['pumpkin_top', 'pumpkin_face_on'];
    case 96: return one('trapdoor');
    case 98: return one(['stonebrick', 'stonebrick_mossy', 'stonebrick_cracked', 'stonebrick_carved'][meta & 3] ?? 'stonebrick');
    case 101: return one('iron_bars');
    case 102: return one('glass');
    case 103: return ['melon_top', 'melon_side'];
    case 107: return one('planks_oak');
    case 108: return one('brick');
    case 109: return one('stonebrick');
  }
  return null;
}

type Tile = { img: CanvasImageSource; sx: number; sy: number; size: number; tint: number } | { colour: number };

function tileOf(bundle: AssetBundle, id: number, meta: number, face: number): Tile {
  if (bundle.version === 'b1.8.1') {
    const t = tileFor(id, meta, face);
    const ts = bundle.pack.tileSize;
    if (t.tile < 0) return { colour: flatColour('b1.8.1', id, meta, face === 1) };
    return { img: bundle.blocks, sx: (t.tile % 16) * ts, sy: Math.floor(t.tile / 16) * ts, size: ts, tint: t.tint };
  }
  const names = textures1122(id, meta);
  const pack = bundle.pack as Pack1122;
  const name = names ? (face === 1 ? names[0] : names[1]) : null;
  const cell = name ? pack.blocks.tiles[`blocks/${name}`] : undefined;
  if (!cell) return { colour: flatColour('1.12.2', id, meta, face === 1) };
  const ts = pack.tileSize;
  return { img: bundle.blocks, sx: cell[0] * ts, sy: cell[1] * ts, size: ts, tint: id === 2 && face === 1 ? 0x91bd59 : 0 };
}

function drawTile(g: CanvasRenderingContext2D, t: Tile, shade: number): void {
  if ('colour' in t) {
    g.fillStyle = `#${t.colour.toString(16).padStart(6, '0')}`;
    g.fillRect(0, 0, 16, 16);
  } else {
    g.drawImage(t.img, t.sx, t.sy, t.size, t.size, 0, 0, 16, 16);
    if (t.tint) {
      g.globalCompositeOperation = 'multiply';
      g.fillStyle = `#${t.tint.toString(16).padStart(6, '0')}`;
      g.fillRect(0, 0, 16, 16);
      g.globalCompositeOperation = 'destination-in';
      g.drawImage(t.img, t.sx, t.sy, t.size, t.size, 0, 0, 16, 16);
      g.globalCompositeOperation = 'source-over';
    }
  }
  if (shade < 1) {
    g.globalCompositeOperation = 'source-atop';
    g.fillStyle = `rgba(0,0,0,${(1 - shade).toFixed(2)})`;
    g.fillRect(0, 0, 16, 16);
    g.globalCompositeOperation = 'source-over';
  }
}

const cache = new Map<string, string>();

/**
 * A data URL of the block's icon (`size` px square), cached per version and state. Needs a DOM canvas (call it in the browser).
 */
export function blockIconUrl(bundle: AssetBundle, id: number, meta: number, size = 32): string {
  const k = `${bundle.version}:${id}:${meta}:${size}`;
  const hit = cache.get(k);
  if (hit) return hit;
  const c = document.createElement('canvas');
  c.width = c.height = size;
  const g = c.getContext('2d');
  if (!g) return '';
  g.imageSmoothingEnabled = false;
  const face = (f: number, shade: number, transform: [number, number, number, number, number, number]) => {
    const tmp = document.createElement('canvas');
    tmp.width = tmp.height = 16;
    const tg = tmp.getContext('2d')!;
    tg.imageSmoothingEnabled = false;
    drawTile(tg, tileOf(bundle, id, meta, f), shade);
    g.setTransform(...transform);
    g.drawImage(tmp, 0, 0);
  };
  const s = size / 32;
  if (FLAT.has(id)) {
    face(3, 1, [s * 1.6, 0, 0, s * 1.6, s * 3.2, s * 3.2]);
  } else {
    const h = id === 44 ? 0.5 : 1; // a slab is half high
    // Top rhombus, then the left (south) and right (east) faces, as the inventory draws a block.
    face(1, 1, [s * 0.875, s * 0.4375, -s * 0.875, s * 0.4375, s * 16, s * (2 + (1 - h) * 14)]);
    face(3, 0.8, [s * 0.875, s * 0.4375, 0, s * 0.875 * h, s * 2, s * (9 + (1 - h) * 14)]);
    face(5, 0.6, [s * 0.875, -s * 0.4375, 0, s * 0.875 * h, s * 16, s * (16 + (1 - h) * 14)]);
  }
  g.setTransform(1, 0, 0, 1, 0, 0);
  const url = c.toDataURL('image/png');
  cache.set(k, url);
  return url;
}
