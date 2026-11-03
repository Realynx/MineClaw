#!/usr/bin/env node
// Packs the committed assets (assets/<version>/) into the bundles the viewer loads (generated/<version>/), at package build time:
//
// 1.12.2
//   blocks.png   every textures/blocks/*.png (frame 0 of animated ones) in a 16 px grid
//   items.png    every textures/items/*.png in a 16 px grid
//   anim.png     every frame of every animated block/item texture, one row per animation
//   entity.png   every textures/entity/**.png, models/armor/*.png and painting/*.png, shelf-packed
//   env/*.png    sun, moon phases, clouds, rain, snow (copied)
//   pack.json    { tiles, items, anims, entity, blockstates, models, itemModels, biomes, colormaps }
// b1.8.1
//   terrain.png, items.png (copied: already 16×16-tile atlases), entity.png (mob/*, char.png, armor/*, item/{boat,cart,arrows,xporb}, art/kz),
//   env/*.png, pack.json
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

/** Per-tile alpha coverage (fraction of texels with alpha ≥ 128), semi-transparency (any alpha in 9..246) and the average
 * opaque colour: the mesher's cube classification (opaque / cut-out / blended) and the untextured palette. */
function tileInfo(png, cx, cy) {
  let n = 0, r = 0, g = 0, b = 0, semi = 0;
  for (let y = 0; y < 16; y++)
    for (let x = 0; x < 16; x++) {
      const o = ((cy + y) * png.width + cx + x) * 4, a = png.data[o + 3];
      if (a > 8 && a < 247) semi = 1;
      if (a < 128) continue;
      n++;
      r += png.data[o];
      g += png.data[o + 1];
      b += png.data[o + 2];
    }
  const avg = n ? (Math.round(r / n) << 16) | (Math.round(g / n) << 8) | Math.round(b / n) : 0;
  return [Math.round((n / 256) * 1000) / 1000, semi, avg];
}

/** Grows a grid atlas by whole rows and shelf-packs extra textures (sizes multiples of 16) into them: [col, row, wCells, hCells]. */
function appendSheets(atlas, sheets) {
  const cols = atlas.cols;
  const items = sheets.filter((s) => s.png).map((s) => ({ ...s, wc: Math.ceil(s.png.width / 16), hc: Math.ceil(s.png.height / 16) }));
  items.sort((a, b) => b.hc - a.hc || b.wc - a.wc);
  let c = 0, r = atlas.rows, shelf = 0;
  const out = {};
  for (const it of items) {
    if (c + it.wc > cols) {
      c = 0;
      r += shelf;
      shelf = 0;
    }
    it.at = [c, r];
    out[it.name] = [c, r, it.wc, it.hc];
    c += it.wc;
    shelf = Math.max(shelf, it.hc);
  }
  const rows = r + shelf;
  const png = new PNG({ width: cols * 16, height: rows * 16 });
  png.data.fill(0);
  blit(png, atlas.png, 0, 0, atlas.png.width, atlas.png.height, 0, 0);
  for (const it of items) blit(png, it.png, 0, 0, it.png.width, it.png.height, it.at[0] * 16, it.at[1] * 16);
  atlas.png = png;
  atlas.rows = rows;
  return out;
}

/** Source-over composite of b onto a (same size), b multiplied by an rgb tint. */
function over(a, b, tint = 0xffffff) {
  const p = new PNG({ width: a.width, height: a.height });
  a.data.copy(p.data);
  const tr = (tint >> 16) & 255, tg = (tint >> 8) & 255, tb = tint & 255;
  for (let i = 0; i < p.data.length; i += 4) {
    const sa = b.data[i + 3] / 255, da = p.data[i + 3] / 255, oa = sa + da * (1 - sa);
    if (oa <= 0) continue;
    const mix = (s, d) => Math.round((s * sa + d * da * (1 - sa)) / oa);
    p.data[i] = mix((b.data[i] * tr) / 255, p.data[i]);
    p.data[i + 1] = mix((b.data[i + 1] * tg) / 255, p.data[i + 1]);
    p.data[i + 2] = mix((b.data[i + 2] * tb) / 255, p.data[i + 2]);
    p.data[i + 3] = Math.round(oa * 255);
  }
  return p;
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
  const info = {};
  for (const [name, [c, r]] of Object.entries(blocks.tiles)) info[name] = tileInfo(blocks.png, c * 16, r * 16);
  // Tile-entity textures the section mesher draws block geometry with (chests, beds, signs, banners, skulls, shulker boxes),
  // appended to the block atlas at their native size so one texture serves every section pass.
  const tex = (n) => (existsSync(join(src, 'textures', n + '.png')) ? readPng(join(src, 'textures', n + '.png')) : null);
  const colours = 'white orange magenta light_blue yellow lime pink gray silver cyan purple blue brown green red black'.split(' ');
  const sheetList = [
    ...['normal', 'trapped', 'ender', 'normal_double', 'trapped_double'].map((k) => ({ name: `entity/chest/${k}`, png: tex(`entity/chest/${k}`) })),
    ...colours.map((c) => ({ name: `entity/bed/${c}`, png: tex(`entity/bed/${c}`) })),
    ...colours.map((c) => ({ name: `entity/shulker/shulker_${c}`, png: tex(`entity/shulker/shulker_${c}`) })),
    { name: 'entity/sign', png: tex('entity/sign') },
    ...['skeleton/skeleton', 'skeleton/wither_skeleton', 'zombie/zombie', 'creeper/creeper', 'steve'].map((k) => ({ name: `entity/${k}`, png: tex(`entity/${k}`) })),
  ];
  const bannerBase = tex('entity/banner_base'), bannerMask = tex('entity/banner/base');
  if (bannerBase && bannerMask) sheetList.push({ name: 'mineclaw/banner_white', png: over(bannerBase, bannerMask, 0xffffff) });
  const sheets = appendSheets(blocks, sheetList);
  const itemAnims = [];
  const items = gridAtlas(join(src, 'textures', 'items'), 'items/', 32, itemAnims, animFrames);
  writePng(join(dest, 'blocks.png'), blocks.png);
  writePng(join(dest, 'items.png'), items.png);
  writePng(join(dest, 'anim.png'), animSheet(animFrames));
  const ent = shelfPack([...walk(join(src, 'textures', 'entity')), ...walk(join(src, 'textures', 'models', 'armor')), ...walk(join(src, 'textures', 'painting'))].filter((f) => f.endsWith('.png')), join(src, 'textures'));
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
    blocks: { columns: blocks.cols, rows: blocks.rows, tiles: blocks.tiles, info, sheets },
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
  const terrain = readPng(join(src, 'terrain.png'));
  const terrainInfo = [];
  for (let i = 0; i < 256; i++) terrainInfo.push(tileInfo(terrain, (i % 16) * 16, Math.floor(i / 16) * 16));
  writeFileSync(join(dest, 'items.png'), readFileSync(join(src, 'gui', 'items.png')));
  // Entity models' textures: mobs, armour, and the object textures the entity layer draws (boat, minecart, arrows, XP orb).
  const objects = [...['boat', 'cart', 'arrows', 'xporb'].map((n) => join(src, 'item', `${n}.png`)), join(src, 'art', 'kz.png')];
  const files = [...walk(join(src, 'mob')), ...walk(join(src, 'armor')), join(src, 'char.png'), ...objects].filter((f) => f.endsWith('.png') && existsSync(f));
  const ent = shelfPack(files, src, 1024);
  writePng(join(dest, 'entity.png'), ent.png);
  const env = copyEnv(join(src, 'environment'), join(dest, 'env'));
  for (const n of ['sun', 'moon']) if (existsSync(join(src, 'terrain', `${n}.png`))) {
    writeFileSync(join(dest, 'env', `${n}.png`), readFileSync(join(src, 'terrain', `${n}.png`)));
    env.push(n);
  }
  for (const m of ['misc']) if (existsSync(join(src, m))) for (const f of walk(join(src, m))) if (/(water|grasscolor|foliagecolor)\.png$/.test(f)) writeFileSync(join(dest, 'env', f.split(/[\\/]/).pop()), readFileSync(f));
  writeFileSync(join(dest, 'pack.json'), JSON.stringify({ version: 'b1.8.1', tileSize: 16, terrain: { columns: 16, rows: 16, info: terrainInfo }, items: { columns: 16, rows: 16 }, entity: { width: ent.width, height: ent.height, rects: ent.rects }, env }));
  report(dest);
}

function report(dir) {
  const files = walk(dir);
  const bytes = files.reduce((a, f) => a + statSync(f).size, 0);
  console.log(`${relative(pkg, dir)}: ${files.length} files, ${(bytes / 1048576).toFixed(2)} MB`);
}

pack1122();
packBeta();
