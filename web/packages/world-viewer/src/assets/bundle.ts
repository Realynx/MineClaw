// Browser-local Minecraft resources imported from the user's own game installation.

import type { GameVersion } from '../world/blocks';
import { ASSETS_CHANGED, readLocalPack } from './localStore';
import { fallbackBundle } from './fallback';

/** One animated texture (.mcmeta): its atlas cell, its frames (index into its strip in anim.png, time in ticks). */
export interface PackAnim {
  name: string;
  atlas: 'blocks' | 'items';
  cell: [number, number];
  frames: Array<{ index: number; time: number }>;
  interpolate: boolean;
  /** Frames in its strip. */
  count: number;
  /** Row of its strip in anim.png (16 px rows, frame k at x = size·k). */
  row: number;
  /** Frame size in px when not 16 (the 32 px flowing water/lava sheets, cell = their 2×2 sheet origin). */
  size?: number;
}

/** The default font: the character drawn at each of the 256 glyph cells of font.png ("\0" = none) and FontRenderer's
 * advance per cell (glyph width + spacing), measured from the image at pack time. */
export interface PackFont {
  chars: string;
  widths: number[];
}

export interface PackBiome {
  name: string;
  temp: number;
  rain: number;
  precip: string | null;
  grass: number | null;
  foliage: number | null;
  water: number | null;
}

/** 1.12.2 pack.json. Texture names are "blocks/stone", "items/apple"; model names "block/stone", "item/apple". */
export interface Pack1122 {
  version: '1.12.2';
  tileSize: number;
  /** `info`: per texture [alpha coverage 0..1, semi-transparent 0/1, average 0xRRGGBB]; `sheets`: tile-entity textures
   * (chests, beds, signs, banners, skulls, shulker boxes) appended to blocks.png at native size: [col, row, wCells, hCells]. */
  blocks: {
    columns: number;
    rows: number;
    tiles: Record<string, [number, number]>;
    info: Record<string, [number, number, number]>;
    sheets: Record<string, [number, number, number, number]>;
  };
  items: { columns: number; rows: number; tiles: Record<string, [number, number]> };
  anims: PackAnim[];
  /** Entity textures (path under textures/, e.g. "entity/zombie/zombie", "models/armor/iron_layer_1") → [x, y, w, h] px. */
  entity: { width: number; height: number; rects: Record<string, [number, number, number, number]> };
  env: string[];
  blockstates: Record<string, unknown>;
  models: Record<string, unknown>;
  itemModels: Record<string, unknown>;
  biomes: Record<string, PackBiome>;
  font?: PackFont;
}

/** b1.8.1 pack.json: terrain.png / items.png are 16×16-tile atlases already; entity textures "mob/zombie", "armor/iron_1". */
export interface PackBeta {
  version: 'b1.8.1';
  tileSize: number;
  /** `info`: per tile index [alpha coverage, semi-transparent, average rgb]. */
  terrain: { columns: number; rows: number; info?: Array<[number, number, number]> };
  items: { columns: number; rows: number };
  entity: { width: number; height: number; rects: Record<string, [number, number, number, number]> };
  env: string[];
  font?: PackFont;
}

export type Pack = Pack1122 | PackBeta;

export interface AssetBundle {
  /** True for the original flat-colour placeholders when no local game JAR is installed. */
  fallback?: boolean;
  version: GameVersion;
  pack: Pack;
  /** Block atlas: 1.12.2 blocks.png (32 columns) or beta terrain.png (16×16). */
  blocks: ImageBitmap;
  items: ImageBitmap;
  /** 1.12.2 animation frames (anim.png); null for beta (procedural FX). */
  anim: ImageBitmap | null;
  entity: ImageBitmap;
  env: Record<string, ImageBitmap>;
  /** The default font sheet (1.12.2 textures/font/ascii.png, b1.8.1 font/default.png); null when it failed to load. */
  font?: ImageBitmap | null;
}

const cache = new Map<GameVersion, Promise<AssetBundle>>();
const urls = new Map<GameVersion, Record<string, string>>();
let generation = 0;
if (typeof window !== 'undefined') window.addEventListener(ASSETS_CHANGED, () => {
  generation++;
  cache.clear();
  for (const old of urls.values()) for (const url of Object.values(old)) URL.revokeObjectURL(url);
  urls.clear();
});

/** Object URLs of the installed pack, available after loadBundle. Never points at a server or bundled game artwork. */
export function bundleUrls(version: GameVersion): Record<string, string> { return urls.get(version) ?? {}; }

/** Loads the browser's own pack once, or renders original placeholders until a JAR is imported in Settings. */
export function loadBundle(version: GameVersion): Promise<AssetBundle> {
  let promise = cache.get(version);
  if (!promise) {
    const started = generation;
    promise = (async () => {
      const local = await readLocalPack(version).catch(() => null);
      if (started !== generation) return loadBundle(version);
      if (!local) {
        const fallback = await fallbackBundle(version);
        if (started === generation) return fallback;
        for (const image of [fallback.blocks, fallback.items, fallback.entity]) image.close();
        return loadBundle(version);
      }
      const images: Record<string, ImageBitmap> = {}, objectUrls: Record<string, string> = {};
      const discard = () => {
        for (const image of Object.values(images)) image.close();
        for (const url of Object.values(objectUrls)) URL.revokeObjectURL(url);
      };
      try {
        for (const [name, file] of Object.entries(local.images)) {
          images[name] = await createImageBitmap(file, { premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
          if (started !== generation) { discard(); return loadBundle(version); }
          objectUrls[name] = URL.createObjectURL(file);
        }
        urls.set(version, objectUrls);
        const env = Object.fromEntries(Object.entries(images).filter(([k]) => k.startsWith('env/')).map(([k, image]) => [k.slice(4), image]));
        return { version, pack: local.pack, blocks: images.blocks, items: images.items, entity: images.entity, anim: images.anim ?? null, font: images.font ?? null, env };
      } catch (error) {
        discard();
        if (started !== generation) return loadBundle(version);
        throw error;
      }
    })();
    void promise.catch(() => { if (cache.get(version) === promise) cache.delete(version); });
    cache.set(version, promise);
  }
  return promise;
}
