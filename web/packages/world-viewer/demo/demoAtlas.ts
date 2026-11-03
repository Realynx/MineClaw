// A procedural stand-in atlas (noise tiles drawn on a canvas; no Mojang textures) in the ATLAS.md format, so the demo can
// exercise the textured path: cube faces, a tinted grass top, a grass side overlay, cut-out leaves and glass, wrapping water.

import type { AtlasMap, AtlasResource, AtlasStateEntry } from '../src';

const T = 16;
const CELLS: Array<[string, number, 'noise' | 'leaves' | 'glass' | 'planks' | 'fringe' | 'water' | 'log']> = [
  ['stone', 0x8a8a8a, 'noise'],
  ['dirt', 0x8b5a2b, 'noise'],
  ['grassTop', 0xb8b8b8, 'noise'], // grey: tinted
  ['grassSide', 0x8b5a2b, 'noise'],
  ['grassFringe', 0xb8b8b8, 'fringe'],
  ['cobble', 0x7a7a7a, 'noise'],
  ['planks', 0xb38c55, 'planks'],
  ['sand', 0xdbd29e, 'noise'],
  ['logSide', 0x6b5532, 'log'],
  ['logTop', 0xb8955e, 'noise'],
  ['leaves', 0xa0a0a0, 'leaves'],
  ['glass', 0xdff4ff, 'glass'],
  ['water', 0x3b76e6, 'water'],
  ['bedrock', 0x3c3c3c, 'noise'],
  ['wool', 0xe6e6e6, 'noise'],
  ['brick', 0x9b4b3b, 'planks'],
];

function rnd(seed: number) {
  let s = seed >>> 0;
  return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296);
}

export function makeDemoAtlas(): AtlasResource {
  const cols = 16, rows = 1;
  const canvas = document.createElement('canvas');
  canvas.width = cols * T;
  canvas.height = rows * T;
  const ctx = canvas.getContext('2d')!;
  const img = ctx.createImageData(canvas.width, canvas.height);
  CELLS.forEach(([, rgb, kind], i) => {
    const r = rnd(i * 7919 + 1);
    for (let y = 0; y < T; y++)
      for (let x = 0; x < T; x++) {
        let k = 0.82 + r() * 0.3;
        let a = 255;
        if (kind === 'planks') k *= y % 4 === 0 ? 0.75 : 1;
        if (kind === 'log') k *= x % 5 === 0 ? 0.7 : 1;
        if (kind === 'leaves' && r() < 0.28) a = 0;
        if (kind === 'glass') a = x === 0 || y === 0 || x === T - 1 || y === T - 1 || (x === y && x > 3 && x < 8) ? 255 : 0;
        if (kind === 'fringe') a = y < 3 + ((x * 7) % 3) ? 255 : 0;
        if (kind === 'water') {
          k = 0.85 + 0.15 * Math.sin((x + y) / 2.5);
          a = 190;
        }
        const o = (y * canvas.width + i * T + x) * 4;
        img.data[o] = Math.min(255, ((rgb >> 16) & 255) * k);
        img.data[o + 1] = Math.min(255, ((rgb >> 8) & 255) * k);
        img.data[o + 2] = Math.min(255, (rgb & 255) * k);
        img.data[o + 3] = a;
      }
  });
  ctx.putImageData(img, 0, 0);
  const cell = (name: string) => CELLS.findIndex((c) => c[0] === name);
  const all = (name: string, layer: AtlasStateEntry['layer'] = 'opaque'): AtlasStateEntry => ({ shape: 'cube', layer, faces: Array.from({ length: 6 }, () => ({ c: cell(name), r: 0 })) });
  const grassSide = { c: cell('grassSide'), r: 0, ov: { c: cell('grassFringe'), r: 0, tint: 0x91bd59 } };
  const map: AtlasMap = {
    format: 'mineclaw-atlas',
    formatVersion: 1,
    version: 'b1.8.1',
    tileSize: T,
    columns: cols,
    rows,
    defaultTints: { grass: 0x91bd59, foliage: 0x48b518 },
    states: {
      '1': all('stone'),
      '2': { shape: 'cube', layer: 'opaque', faces: [{ c: cell('dirt'), r: 0 }, { c: cell('grassTop'), r: 0, tint: 0x91bd59, biome: 'grass' }, grassSide, grassSide, grassSide, grassSide] },
      '3': all('dirt'),
      '4': all('cobble'),
      '5': all('planks'),
      '7': all('bedrock'),
      '12': all('sand'),
      '17': { shape: 'cube', layer: 'opaque', faces: [{ c: cell('logTop'), r: 0 }, { c: cell('logTop'), r: 0 }, ...Array.from({ length: 4 }, () => ({ c: cell('logSide'), r: 0 }))] },
      '18': { shape: 'cube', layer: 'cutout', faces: Array.from({ length: 6 }, () => ({ c: cell('leaves'), r: 0, tint: 0x48b518, biome: 'foliage' as const })) },
      '20': all('glass', 'cutout'),
      '35': all('wool'),
      '45': all('brick'),
      '8': { shape: 'fluid', layer: 'translucent', faces: Array.from({ length: 6 }, () => ({ c: cell('water'), r: 0, flags: 2 })) },
      '9': { shape: 'fluid', layer: 'translucent', faces: Array.from({ length: 6 }, () => ({ c: cell('water'), r: 0, flags: 2 })) },
    },
  };
  return { image: canvas, map };
}
