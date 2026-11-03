#!/usr/bin/env node
// Packs the committed assets (assets/<version>/) into the bundles the viewer loads (generated/<version>/), at package build time:
//
// 1.12.2
//   blocks.png   every textures/blocks/*.png (frame 0 of animated ones) in a 16 px grid
//   items.png    every textures/items/*.png in a 16 px grid
//   anim.png     every frame of every animated block/item texture, one row per animation
//   entity.png   every textures/entity/**.png and models/armor/*.png, shelf-packed
//   env/*.png    sun, moon phases, clouds, rain, snow (copied)
//   pack.json    { tiles, items, anims, entity, blockstates, models, itemModels, biomes, colormaps }
// b1.8.1
//   terrain.png, items.png (copied: already 16×16-tile atlases), entity.png (mob/*, char.png, armor/*), env/*.png, pack.json
//
//   node scripts/pack-assets.mjs            (both versions)

import { readFileSync, writeFileSync, mkdirSync, readdirSync, existsSync, statSync, rmSync } from 'node:fs';
import { dirname, join, resolve, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';

const here = dirname(fileURLToPath(import.meta.url));
const pkg = resolve(here, '..');
const assets = join(pkg, 'assets');
const out = join(pkg, 'generated');
const repoRoot = resolve(pkg, '..', '..', '..');

const readPng = (p) => PNG.sync.read(readFileSync(p));
const writePng = (p, png) => writeFileSync(p, PNG.sync.write(png, { colorType: 6 }));

function walk(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir).flatMap((n) => {
    const p = join(dir, n);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });
}

function blit(dst, src, sx, sy, w, h, dx, dy) {
  for (let y = 0; y < h; y++) {
    const s = ((sy + y) * src.width + sx) * 4, d = ((dy + y) * dst.width + dx) * 4;
    src.data.copy(dst.data, d, s, s + w * 4);
  }
}

/** Nearest-neighbour resample of a w×h region to tw×th (textures that are not 16 px, e.g. 32 px clock frames). */
function resample(src, sx, sy, w, h, tw, th) {
  const p = new PNG({ width: tw, height: th });
  for (let y = 0; y < th; y++)
    for (let x = 0; x < tw; x++) {
      const s = ((sy + Math.floor((y * h) / th)) * src.width + sx + Math.floor((x * w) / tw)) * 4, d = (y * tw + x) * 4;
      src.data.copy(p.data, d, s, s + 4);
    }
  return p;
}

const minify = (p) => JSON.parse(readFileSync(p, 'utf8'));

/** Grid atlas of 16 px tiles from a folder; animated textures contribute frame 0 and an animation record. */
function gridAtlas(dir, prefix, cols, anims, animFrames) {
  const files = walk(dir).filter((f) => f.endsWith('.png')).sort();
  const rows = Math.ceil(files.length / cols);
  const png = new PNG({ width: cols * 16, height: rows * 16 });
  png.data.fill(0);
  const tiles = {};
  files.forEach((f, i) => {
    const name = prefix + relative(dir, f).replace(/\\/g, '/').replace(/\.png$/, '');
    const src = readPng(f);
    const c = i % cols, r = Math.floor(i / cols);
    const fw = src.width, fh = existsSync(f + '.mcmeta') ? fw : Math.min(src.width, src.height);
    const frame0 = fw === 16 && fh === 16 ? src : resample(src, 0, 0, fw, fh, 16, 16);
    blit(png, frame0, 0, 0, 16, 16, c * 16, r * 16);
    tiles[name] = [c, r];
    if (existsSync(f + '.mcmeta')) {
      const meta = minify(f + '.mcmeta').animation ?? {};
      const count = Math.floor(src.height / fw);
      const frames = (meta.frames ?? [...Array(count).keys()]).map((fr) => (typeof fr === 'number' ? { index: fr, time: meta.frametime ?? 1 } : { index: fr.index, time: fr.time ?? meta.frametime ?? 1 }));
      const strip = [];
      for (let k = 0; k < count; k++) strip.push(fw === 16 ? { src, y: k * fw } : { src: resample(src, 0, k * fw, fw, fw, 16, 16), y: 0 });
      anims.push({ name, cell: [c, r], frames, interpolate: !!meta.interpolate, count, row: animFrames.length });
      animFrames.push(strip);
    }
  });
  return { png, tiles, cols, rows };
}

function animSheet(animFrames) {
  const w = Math.max(1, ...animFrames.map((s) => s.length)) * 16;
  const png = new PNG({ width: w, height: Math.max(1, animFrames.length) * 16 });
  png.data.fill(0);
  animFrames.forEach((strip, row) => strip.forEach((fr, k) => blit(png, fr.src, 0, fr.y, 16, 16, k * 16, row * 16)));
  return png;
}

/** Shelf packer for differently sized textures (entities); rects in pixels. */
function shelfPack(files, root, width = 2048) {
  const items = files.map((f) => ({ f, png: readPng(f), name: relative(root, f).replace(/\\/g, '/').replace(/\.png$/, '') })).sort((a, b) => b.png.height - a.png.height || b.png.width - a.png.width);
  let x = 0, y = 0, shelf = 0;
  const rects = {};
  for (const it of items) {
    if (x + it.png.width > width) {
      x = 0;
      y += shelf;
      shelf = 0;
    }
    rects[it.name] = [x, y, it.png.width, it.png.height];
    it.at = [x, y];
    x += it.png.width;
    shelf = Math.max(shelf, it.png.height);
  }
  const height = Math.max(1, y + shelf);
  const png = new PNG({ width, height });
  png.data.fill(0);
  for (const it of items) blit(png, it.png, 0, 0, it.png.width, it.png.height, it.at[0], it.at[1]);
  return { png, rects, width, height };
}

function biomes1122() {
  const dir = join(repoRoot, 'third_party', 'minecraft-data', 'pc', '1.12.2');
  if (!existsSync(join(dir, 'biomes.json'))) return {};
  const list = minify(join(dir, 'biomes.json'));
  const tints = minify(join(dir, 'tints.json'));
  const pick = (kind, name) => tints[kind]?.data?.find((d) => d.keys.includes(name))?.color ?? tints[kind]?.default ?? null;
  const out = {};
  for (const b of list) out[b.id] = { name: b.name, temp: b.temperature, rain: b.rainfall ?? 0, precip: b.precipitation ?? null, grass: pick('grass', b.name), foliage: pick('foliage', b.name), water: pick('water', b.name) };
  return out;
}

function copyEnv(srcDir, dest) {
  mkdirSync(dest, { recursive: true });
  const env = [];
  for (const f of walk(srcDir).filter((f) => f.endsWith('.png'))) {
    const n = relative(srcDir, f).replace(/\\/g, '/');
    writeFileSync(join(dest, n.split('/').pop()), readFileSync(f));
    env.push(n.split('/').pop().replace(/\.png$/, ''));
  }
  return env;
}

function pack1122() {
  const src = join(assets, '1.12.2');
  if (!existsSync(src)) return console.warn('no assets/1.12.2 (run extract-assets.mjs)');
  const dest = join(out, '1.12.2');
  rmSync(dest, { recursive: true, force: true });
  mkdirSync(dest, { recursive: true });
  const anims = [], animFrames = [];
  const blocks = gridAtlas(join(src, 'textures', 'blocks'), 'blocks/', 32, anims, animFrames);
  const itemAnims = [];
  const items = gridAtlas(join(src, 'textures', 'items'), 'items/', 32, itemAnims, animFrames);
  writePng(join(dest, 'blocks.png'), blocks.png);
  writePng(join(dest, 'items.png'), items.png);
  writePng(join(dest, 'anim.png'), animSheet(animFrames));
  const ent = shelfPack([...walk(join(src, 'textures', 'entity')), ...walk(join(src, 'textures', 'models', 'armor'))].filter((f) => f.endsWith('.png')), join(src, 'textures'));
  writePng(join(dest, 'entity.png'), ent.png);
  const env = copyEnv(join(src, 'textures', 'environment'), join(dest, 'env'));
  for (const c of ['grass', 'foliage']) writeFileSync(join(dest, 'env', `colormap_${c}.png`), readFileSync(join(src, 'textures', 'colormap', `${c}.png`)));
  const blockstates = {}, models = {}, itemModels = {};
  for (const f of walk(join(src, 'blockstates'))) blockstates[relative(join(src, 'blockstates'), f).replace(/\.json$/, '')] = minify(f);
  for (const f of walk(join(src, 'models', 'block'))) models['block/' + relative(join(src, 'models', 'block'), f).replace(/\\/g, '/').replace(/\.json$/, '')] = minify(f);
  for (const f of walk(join(src, 'models', 'item'))) itemModels['item/' + relative(join(src, 'models', 'item'), f).replace(/\\/g, '/').replace(/\.json$/, '')] = minify(f);
  const packJson = {
    version: '1.12.2',
    tileSize: 16,
    blocks: { columns: blocks.cols, rows: blocks.rows, tiles: blocks.tiles },
    items: { columns: items.cols, rows: items.rows, tiles: items.tiles },
    anims: anims.map((a) => ({ ...a, atlas: 'blocks' })).concat(itemAnims.map((a) => ({ ...a, atlas: 'items' }))),
    entity: { width: ent.width, height: ent.height, rects: ent.rects },
    env,
    blockstates,
    models,
    itemModels,
    biomes: biomes1122(),
  };
  writeFileSync(join(dest, 'pack.json'), JSON.stringify(packJson));
  report(dest);
}

function packBeta() {
  const src = join(assets, 'b1.8.1');
  if (!existsSync(src)) return console.warn('no assets/b1.8.1 (run extract-assets.mjs)');
  const dest = join(out, 'b1.8.1');
  rmSync(dest, { recursive: true, force: true });
  mkdirSync(dest, { recursive: true });
  writeFileSync(join(dest, 'terrain.png'), readFileSync(join(src, 'terrain.png')));
  writeFileSync(join(dest, 'items.png'), readFileSync(join(src, 'gui', 'items.png')));
  const files = [...walk(join(src, 'mob')), ...walk(join(src, 'armor')), join(src, 'char.png')].filter((f) => f.endsWith('.png') && existsSync(f));
  const ent = shelfPack(files, src, 1024);
  writePng(join(dest, 'entity.png'), ent.png);
  const env = copyEnv(join(src, 'environment'), join(dest, 'env'));
  for (const m of ['misc']) if (existsSync(join(src, m))) for (const f of walk(join(src, m))) if (/(water|grasscolor|foliagecolor)\.png$/.test(f)) writeFileSync(join(dest, 'env', f.split(/[\\/]/).pop()), readFileSync(f));
  writeFileSync(join(dest, 'pack.json'), JSON.stringify({ version: 'b1.8.1', tileSize: 16, terrain: { columns: 16, rows: 16 }, items: { columns: 16, rows: 16 }, entity: { width: ent.width, height: ent.height, rects: ent.rects }, env }));
  report(dest);
}

function report(dir) {
  const files = walk(dir);
  const bytes = files.reduce((a, f) => a + statSync(f).size, 0);
  console.log(`${relative(pkg, dir)}: ${files.length} files, ${(bytes / 1048576).toFixed(2)} MB`);
}

pack1122();
packBeta();
