// The packed Minecraft assets that ship with the package (generated/<version>/ by scripts/pack-assets.mjs from the committed
// assets/<version>/). URLs are written out literally so bundlers (Vite, webpack, Turbopack) emit and rewrite every file.

import type { GameVersion } from '../world/blocks';

/** One animated texture (.mcmeta): its atlas cell, its frames (index into its strip in anim.png, time in ticks). */
export interface PackAnim {
  name: string;
  atlas: 'blocks' | 'items';
  cell: [number, number];
  frames: Array<{ index: number; time: number }>;
  interpolate: boolean;
  /** Frames in its strip. */
  count: number;
  /** Row of its strip in anim.png (16 px rows, frame k at x = 16k). */
  row: number;
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
}

export type Pack = Pack1122 | PackBeta;

export interface AssetBundle {
  version: GameVersion;
  pack: Pack;
  /** Block atlas: 1.12.2 blocks.png (32 columns) or beta terrain.png (16×16). */
  blocks: ImageBitmap;
  items: ImageBitmap;
  /** 1.12.2 animation frames (anim.png); null for beta (procedural FX). */
  anim: ImageBitmap | null;
  entity: ImageBitmap;
  env: Record<string, ImageBitmap>;
}

const URLS: Record<GameVersion, () => Record<string, URL>> = {
  '1.12.2': () => ({
    pack: new URL('../../generated/1.12.2/pack.json', import.meta.url),
    blocks: new URL('../../generated/1.12.2/blocks.png', import.meta.url),
    items: new URL('../../generated/1.12.2/items.png', import.meta.url),
    anim: new URL('../../generated/1.12.2/anim.png', import.meta.url),
    entity: new URL('../../generated/1.12.2/entity.png', import.meta.url),
    'env/sun': new URL('../../generated/1.12.2/env/sun.png', import.meta.url),
    'env/moon_phases': new URL('../../generated/1.12.2/env/moon_phases.png', import.meta.url),
    'env/clouds': new URL('../../generated/1.12.2/env/clouds.png', import.meta.url),
    'env/rain': new URL('../../generated/1.12.2/env/rain.png', import.meta.url),
    'env/snow': new URL('../../generated/1.12.2/env/snow.png', import.meta.url),
    'env/colormap_grass': new URL('../../generated/1.12.2/env/colormap_grass.png', import.meta.url),
    'env/colormap_foliage': new URL('../../generated/1.12.2/env/colormap_foliage.png', import.meta.url),
  }),
  'b1.8.1': () => ({
    pack: new URL('../../generated/b1.8.1/pack.json', import.meta.url),
    blocks: new URL('../../generated/b1.8.1/terrain.png', import.meta.url),
    items: new URL('../../generated/b1.8.1/items.png', import.meta.url),
    entity: new URL('../../generated/b1.8.1/entity.png', import.meta.url),
    'env/clouds': new URL('../../generated/b1.8.1/env/clouds.png', import.meta.url),
    'env/rain': new URL('../../generated/b1.8.1/env/rain.png', import.meta.url),
    'env/snow': new URL('../../generated/b1.8.1/env/snow.png', import.meta.url),
    'env/sun': new URL('../../generated/b1.8.1/env/sun.png', import.meta.url),
    'env/moon': new URL('../../generated/b1.8.1/env/moon.png', import.meta.url),
  }),
};

/** The URL of each bundled file (for apps that preload or serve them elsewhere). */
export function bundleUrls(version: GameVersion): Record<string, string> {
  return Object.fromEntries(Object.entries(URLS[version]()).map(([k, u]) => [k, u.href]));
}

async function image(url: URL | string): Promise<ImageBitmap> {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`asset ${String(url)}: HTTP ${r.status}`);
  return createImageBitmap(await r.blob(), { premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
}

const cache = new Map<GameVersion, Promise<AssetBundle>>();

/** Loads (once per page) the bundled assets of a version. */
export function loadBundle(version: GameVersion): Promise<AssetBundle> {
  let p = cache.get(version);
  if (!p) {
    p = (async () => {
      const u = URLS[version]();
      const packP = fetch(u.pack).then((r) => {
        if (!r.ok) throw new Error(`asset pack.json: HTTP ${r.status}`);
        return r.json() as Promise<Pack>;
      });
      const envKeys = Object.keys(u).filter((k) => k.startsWith('env/'));
      const [pack, blocks, items, anim, entity, ...env] = await Promise.all([
        packP,
        image(u.blocks),
        image(u.items),
        u.anim ? image(u.anim) : Promise.resolve(null),
        image(u.entity),
        ...envKeys.map((k) => image(u[k]).catch(() => null)),
      ]);
      const envMap: Record<string, ImageBitmap> = {};
      envKeys.forEach((k, i) => {
        const b = env[i];
        if (b) envMap[k.slice(4)] = b;
      });
      return { version, pack, blocks, items, anim, entity, env: envMap };
    })();
    p.catch(() => cache.delete(version));
    cache.set(version, p);
  }
  return p;
}
