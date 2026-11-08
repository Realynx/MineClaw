import type { Pack1122, PackAnim, PackBeta, PackFont } from './bundle';
import type { LocalPack } from './localStore';
import { readLocalPack, writeLocalPack } from './localStore';
import { readArchive } from './archive';
import type { GameVersion } from '../world/blocks';
// Numerical biome data from the vendored PrismarineJS minecraft-data (its license is in third_party/minecraft-data).
import biomes1122 from './biomes1122.json';

type Canvas = OffscreenCanvas;
const canvas = (width: number, height: number) => new OffscreenCanvas(Math.max(1, width), Math.max(1, height));
function ctx(c: Canvas): OffscreenCanvasRenderingContext2D {
  const context = c.getContext('2d');
  if (!context) throw new Error('This browser cannot prepare the texture atlas.');
  context.imageSmoothingEnabled = false;
  return context;
}
const blob = (c: Canvas) => {
  // Empty optional atlases still need an initialized backing store for convertToBlob in Chromium.
  ctx(c);
  return c.convertToBlob({ type: 'image/png' });
};
const json = async (b: Blob | undefined) => (b ? (JSON.parse(await b.text()) as Record<string, unknown>) : {});
async function bitmap(file: Blob, maxWidth = 4096): Promise<ImageBitmap> {
  const h = new DataView(await file.slice(0, 24).arrayBuffer());
  if (h.byteLength < 24 || h.getUint32(0) !== 0x89504e47 || h.getUint32(4) !== 0x0d0a1a0a)
    throw new Error('Invalid PNG in game resources.');
  const width = h.getUint32(16),
    height = h.getUint32(20);
  if (!width || !height || width > maxWidth || height > 16384 || width * height > 8_388_608)
    throw new Error('A game texture is too large.');
  return createImageBitmap(file, { premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
}

/** Extract locally, prepare atlases, and atomically replace just this game's installed browser pack. */
export async function importGameJar(file: File, progress?: (message: string) => void): Promise<LocalPack> {
  progress?.('Reading game resources…');
  const resources = await readArchive(file, (f) => progress?.(`Reading game resources… ${Math.round(f * 100)}%`));
  const modern = resources.has('assets/minecraft/textures/blocks/stone.png');
  const beta = resources.has('terrain.png') && resources.has('gui/items.png');
  if (!modern && !beta)
    throw new Error(
      'Choose a Minecraft Java 1.12.2 or Beta 1.8.1 client JAR. Other texture formats are not supported by this viewer.',
    );
  progress?.('Preparing texture atlases…');
  const version: GameVersion = modern ? '1.12.2' : 'b1.8.1';
  const images: Record<string, Blob> = {};
  const pack = modern ? await pack1122(resources, images) : await packBeta(resources, images);
  const old = await readLocalPack(version);
  const sounds = { ...old?.sounds };
  for (const [path, data] of resources)
    if (path.startsWith('assets/minecraft/sounds/') && path.endsWith('.ogg')) sounds[path.slice(24, -4)] = data;
  const soundEvents = resources.has('assets/minecraft/sounds.json')
    ? await json(resources.get('assets/minecraft/sounds.json'))
    : (old?.soundEvents ?? {});
  const saved: LocalPack = { version, name: file.name, importedAt: Date.now(), pack, images, sounds, soundEvents };
  progress?.('Saving on this device…');
  await writeLocalPack(saved);
  return saved;
}

interface Grid {
  image: Canvas;
  columns: number;
  rows: number;
  tiles: Record<string, [number, number]>;
  info: Record<string, [number, number, number]>;
}
interface AnimationStrip {
  frames: Canvas[];
  size: number;
}
function info(context: OffscreenCanvasRenderingContext2D, x: number, y: number): [number, number, number] {
  const data = context.getImageData(x, y, 16, 16).data;
  let count = 0,
    red = 0,
    green = 0,
    blue = 0,
    semi = 0;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i + 3] > 8 && data[i + 3] < 247) semi = 1;
    if (data[i + 3] < 128) continue;
    count++;
    red += data[i];
    green += data[i + 1];
    blue += data[i + 2];
  }
  return [
    Math.round((count / 256) * 1000) / 1000,
    semi,
    count ? (Math.round(red / count) << 16) | (Math.round(green / count) << 8) | Math.round(blue / count) : 0,
  ];
}
async function grid(
  resources: Map<string, Blob>,
  prefix: string,
  atlas: 'blocks' | 'items',
  anims: PackAnim[],
  strips: AnimationStrip[],
): Promise<Grid> {
  const names = [...resources.keys()].filter((p) => p.startsWith(prefix) && p.endsWith('.png')).sort();
  const columns = 32,
    rows = Math.max(1, Math.ceil(names.length / columns)),
    image = canvas(columns * 16, rows * 16),
    context = ctx(image);
  const tiles: Grid['tiles'] = {},
    infos: Grid['info'] = {};
  for (const [i, path] of names.entries()) {
    const source = await bitmap(resources.get(path)!);
    try {
      const name = `${atlas}/${path.slice(prefix.length, -4)}`,
        cell: [number, number] = [i % columns, Math.floor(i / columns)];
      tiles[name] = cell;
      context.drawImage(
        source,
        0,
        0,
        source.width,
        Math.min(source.width, source.height),
        cell[0] * 16,
        cell[1] * 16,
        16,
        16,
      );
      infos[name] = info(context, cell[0] * 16, cell[1] * 16);
      if (resources.has(path + '.mcmeta')) {
        const data = await json(resources.get(path + '.mcmeta'));
        const meta = (data.animation ?? {}) as {
          frames?: Array<number | { index: number; time?: number }>;
          frametime?: number;
          interpolate?: boolean;
        };
        const count = Math.min(1024, Math.floor(source.height / source.width));
        const frames = (meta.frames ?? Array.from({ length: count }, (_, k) => k))
          .map((f) =>
            typeof f === 'number'
              ? { index: f, time: meta.frametime ?? 1 }
              : { index: f.index, time: f.time ?? meta.frametime ?? 1 },
          )
          .filter(
            (f) =>
              Number.isInteger(f.index) && f.index >= 0 && f.index < count && Number.isFinite(f.time) && f.time > 0,
          );
        if (count && frames.length) {
          const strip = Array.from({ length: count }, (_, k) => {
            const out = canvas(16, 16);
            ctx(out).drawImage(source, 0, k * source.width, source.width, source.width, 0, 0, 16, 16);
            return out;
          });
          anims.push({ name, atlas, cell, frames, interpolate: !!meta.interpolate, count, row: strips.length });
          strips.push({ frames: strip, size: 16 });
        }
      }
    } finally {
      source.close();
    }
    if (i % 30 === 0) await new Promise((r) => setTimeout(r, 0));
  }
  return { image, columns, rows, tiles, info: infos };
}

async function sheet(
  resources: Map<string, Blob>,
  paths: string[],
  prefix: string,
  width: number,
): Promise<{ image: Canvas; rects: Record<string, [number, number, number, number]> }> {
  const entries: Array<{ name: string; source: ImageBitmap }> = [];
  try {
    let pixels = 0;
    for (const path of paths) {
      const source = await bitmap(resources.get(path)!, width);
      pixels += source.width * source.height;
      if (pixels > 16_777_216 || source.height > 8192) {
        source.close();
        throw new Error('Too many decoded pixels in the texture atlas.');
      }
      entries.push({ name: path.slice(prefix.length, -4), source });
    }
    entries.sort(
      (a, b) => b.source.height - a.source.height || b.source.width - a.source.width || a.name.localeCompare(b.name),
    );
    const rects: Record<string, [number, number, number, number]> = {};
    let x = 0,
      y = 0,
      row = 0;
    for (const e of entries) {
      if (e.source.width > width) throw new Error('A texture is wider than the supported atlas.');
      if (x + e.source.width > width) {
        x = 0;
        y += row;
        row = 0;
      }
      rects[e.name] = [x, y, e.source.width, e.source.height];
      x += e.source.width;
      row = Math.max(row, e.source.height);
      if (y + row > 8192) throw new Error('Too many textures in the game archive.');
    }
    const image = canvas(width, y + row),
      context = ctx(image);
    for (const e of entries) {
      const [dx, dy] = rects[e.name];
      context.drawImage(e.source, dx, dy);
    }
    return { image, rects };
  } finally {
    for (const e of entries) e.source.close();
  }
}

async function pack1122(resources: Map<string, Blob>, images: Record<string, Blob>): Promise<Pack1122> {
  const root = 'assets/minecraft/',
    tex = root + 'textures/',
    anims: PackAnim[] = [],
    strips: AnimationStrip[] = [];
  const blocks = await grid(resources, tex + 'blocks/', 'blocks', anims, strips);
  const items = await grid(resources, tex + 'items/', 'items', anims, strips);
  const sheetPaths = [...resources.keys()].filter(
    (p) =>
      p.startsWith(tex) &&
      /\/(?:entity\/(?:chest|bed|shulker)\/[^/]+|entity\/(?:sign|steve)|entity\/(?:skeleton|zombie|creeper)\/[^/]+|environment\/end_sky|entity\/end_portal|entity\/enderdragon\/dragon)\.png$/.test(
        p,
      ),
  );
  const bannerBase = resources.get(tex + 'entity/banner_base.png'),
    bannerMask = resources.get(tex + 'entity/banner/base.png');
  if (bannerBase && bannerMask) {
    const base = await bitmap(bannerBase),
      mask = await bitmap(bannerMask);
    try {
      const image = canvas(base.width, base.height),
        context = ctx(image);
      context.drawImage(base, 0, 0);
      context.drawImage(mask, 0, 0);
      const name = tex + 'mineclaw/banner_white.png';
      resources.set(name, await blob(image));
      sheetPaths.push(name);
    } finally {
      base.close();
      mask.close();
    }
  }
  const extra = await sheet(resources, sheetPaths, tex, 512),
    combined = canvas(512, blocks.image.height + extra.image.height);
  ctx(combined).drawImage(blocks.image, 0, 0);
  ctx(combined).drawImage(extra.image, 0, blocks.image.height);
  const sheets: Pack1122['blocks']['sheets'] = {};
  for (const [name, [x, y, w, h]] of Object.entries(extra.rects))
    sheets[name] = [x / 16, (y + blocks.image.height) / 16, w / 16, h / 16];
  // Flowing water/lava need their native 32px sheet, not the downsampled 16px tile.
  let finalBlocks = combined;
  for (const name of ['water_flow', 'lava_flow']) {
    const file = resources.get(tex + 'blocks/' + name + '.png');
    if (!file) continue;
    const source = await bitmap(file);
    try {
      if (source.width <= 16) continue;
      const next = canvas(512, finalBlocks.height + source.width),
        context = ctx(next);
      context.drawImage(finalBlocks, 0, 0);
      context.drawImage(source, 0, 0, source.width, source.width, 0, finalBlocks.height, source.width, source.width);
      sheets[`blocks/${name}@${source.width}`] = [0, finalBlocks.height / 16, source.width / 16, source.width / 16];
      const data = await json(resources.get(tex + 'blocks/' + name + '.png.mcmeta'));
      const meta = (data.animation ?? {}) as {
        frames?: Array<number | { index: number; time?: number }>;
        frametime?: number;
        interpolate?: boolean;
      };
      const count = Math.min(1024, Math.floor(source.height / source.width));
      const frames = (meta.frames ?? Array.from({ length: count }, (_, i) => i))
        .map((f) =>
          typeof f === 'number'
            ? { index: f, time: meta.frametime ?? 1 }
            : { index: f.index, time: f.time ?? meta.frametime ?? 1 },
        )
        .filter(
          (f) => Number.isInteger(f.index) && f.index >= 0 && f.index < count && Number.isFinite(f.time) && f.time > 0,
        );
      if (frames.length) {
        const row = strips.reduce((sum, strip) => sum + strip.size, 0) / 16;
        anims.push({
          name: `blocks/${name}@${source.width}`,
          atlas: 'blocks',
          cell: [0, finalBlocks.height / 16],
          count,
          row,
          size: source.width,
          frames,
          interpolate: !!meta.interpolate,
        });
        strips.push({
          size: source.width,
          frames: Array.from({ length: count }, (_, i) => {
            const frame = canvas(source.width, source.width);
            ctx(frame).drawImage(
              source,
              0,
              i * source.width,
              source.width,
              source.width,
              0,
              0,
              source.width,
              source.width,
            );
            return frame;
          }),
        });
      }
      finalBlocks = next;
    } finally {
      source.close();
    }
  }
  const animation = canvas(
    Math.max(16, ...strips.map((s) => s.frames.length * s.size)),
    Math.max(
      16,
      strips.reduce((sum, s) => sum + s.size, 0),
    ),
  );
  let animationY = 0;
  for (const strip of strips) {
    for (const [col, frame] of strip.frames.entries()) ctx(animation).drawImage(frame, col * strip.size, animationY);
    animationY += strip.size;
  }
  const entities = await sheet(
    resources,
    [...resources.keys()].filter((p) => p.startsWith(tex) && /\/(entity|models\/armor|painting)\/.+\.png$/.test(p)),
    tex,
    2048,
  );
  images.blocks = await blob(finalBlocks);
  images.items = await blob(items.image);
  images.anim = await blob(animation);
  images.entity = await blob(entities.image);
  const env: string[] = [];
  for (const [path, data] of resources) {
    if (path.startsWith(tex + 'environment/') && path.endsWith('.png')) {
      const name = path.slice((tex + 'environment/').length, -4);
      images['env/' + name] = data;
      env.push(name);
    }
    if (path.startsWith(tex + 'colormap/') && path.endsWith('.png'))
      images['env/colormap_' + path.slice((tex + 'colormap/').length, -4)] = data;
  }
  const glint = resources.get(tex + 'misc/enchanted_item_glint.png');
  if (glint) images['env/glint'] = glint;
  const blockstates: Record<string, unknown> = {},
    models: Record<string, unknown> = {},
    itemModels: Record<string, unknown> = {};
  for (const [path, data] of resources) {
    if (path.startsWith(root + 'blockstates/') && path.endsWith('.json'))
      blockstates[path.slice((root + 'blockstates/').length, -5)] = await json(data);
    else if (path.startsWith(root + 'models/block/') && path.endsWith('.json'))
      models[path.slice((root + 'models/').length, -5)] = await json(data);
    else if (path.startsWith(root + 'models/item/') && path.endsWith('.json'))
      itemModels[path.slice((root + 'models/').length, -5)] = await json(data);
  }
  const font = await fontPack(resources.get(tex + 'font/ascii.png'), false, images);
  return {
    version: '1.12.2',
    tileSize: 16,
    blocks: { columns: 32, rows: Math.ceil(finalBlocks.height / 16), tiles: blocks.tiles, info: blocks.info, sheets },
    items: { columns: 32, rows: items.rows, tiles: items.tiles },
    anims,
    entity: { width: entities.image.width, height: entities.image.height, rects: entities.rects },
    env,
    blockstates,
    models,
    itemModels,
    biomes: biomes1122,
    font,
  };
}

async function packBeta(resources: Map<string, Blob>, images: Record<string, Blob>): Promise<PackBeta> {
  const terrain = await bitmap(resources.get('terrain.png')!),
    picture = canvas(256, 256);
  ctx(picture).drawImage(terrain, 0, 0, 256, 256);
  terrain.close();
  images.blocks = await blob(picture);
  images.items = resources.get('gui/items.png')!;
  const entities = await sheet(
    resources,
    [...resources.keys()].filter((p) =>
      /^(?:(?:mob|armor)\/.*|char|item\/(?:boat|cart|arrows|xporb)|art\/kz)\.png$/.test(p),
    ),
    '',
    1024,
  );
  images.entity = await blob(entities.image);
  const env: string[] = [];
  for (const [path, data] of resources)
    if (/^(environment\/.*|terrain\/(sun|moon))\.png$/.test(path)) {
      const name = path.split('/').pop()!.slice(0, -4);
      images['env/' + name] = data;
      env.push(name);
    }
  const font = await fontPack(resources.get('font/default.png'), true, images, await resources.get('font.txt')?.text());
  return {
    version: 'b1.8.1',
    tileSize: 16,
    terrain: {
      columns: 16,
      rows: 16,
      info: Array.from({ length: 256 }, (_, i) => info(ctx(picture), (i % 16) * 16, Math.floor(i / 16) * 16)),
    },
    items: { columns: 16, rows: 16 },
    entity: { width: entities.image.width, height: entities.image.height, rects: entities.rects },
    env,
    font,
  };
}
const CHARS =
  'ÀÁÂÈÊËÍÓÔÕÚßãõğİıŒœŞşŴŵžȇ\0\0\0\0\0\0\0 !"#$%&\'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~\0ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜø£Ø×ƒáíóúñÑªº¿®¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αβΓπΣσμτΦΘΩδ∞∅∈∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■\0';
async function fontPack(
  file: Blob | undefined,
  beta: boolean,
  images: Record<string, Blob>,
  table?: string,
): Promise<PackFont | undefined> {
  if (!file) return undefined;
  const source = await bitmap(file),
    image = canvas(source.width, source.height);
  ctx(image).drawImage(source, 0, 0);
  source.close();
  const data = ctx(image).getImageData(0, 0, image.width, image.height).data,
    cw = image.width / 16,
    ch = image.height / 16;
  const widths = Array.from({ length: 256 }, (_, n) => {
    if (n === 32) return 4;
    let q = cw - 1;
    for (; q >= 0; q--) {
      let empty = true;
      for (let s = 0; s < ch && empty; s++)
        if (data[((Math.floor(n / 16) * ch + s) * image.width + (n % 16) * cw + q) * 4 + (beta ? 2 : 3)]) empty = false;
      if (!empty) break;
    }
    return beta ? q + 2 : Math.floor(0.5 + ((q + 1) * 8) / cw) + 1;
  });
  images.font = file;
  return {
    chars: beta
      ? '\0'.repeat(32) +
        (table ?? '')
          .split(/\r?\n/)
          .filter((l) => !l.startsWith('#'))
          .join('')
      : CHARS,
    widths,
  };
}

/** Vanilla launcher assets use hash-named files under assets/objects, indexed by assets/indexes/<version>.json. */
export async function importSoundFolder(files: File[], version: GameVersion): Promise<number> {
  const current = await readLocalPack(version);
  if (!current) throw new Error('Import this version’s game JAR first.');
  const byPath = new Map(files.map((f) => [(f.webkitRelativePath || f.name).replaceAll('\\', '/'), f]));
  const candidates = [...byPath].filter(([p]) => /\/indexes\/[^/]+\.json$/.test(p));
  const wanted = version === '1.12.2' ? '1.12' : 'legacy';
  const candidate =
    candidates.find(([p]) => p.endsWith(`/${wanted}.json`)) ?? candidates.find(([p]) => p.endsWith(`/${version}.json`));
  const sounds = { ...current.sounds };
  let events = current.soundEvents,
    count = 0,
    bytes = 0;
  if (!candidate) throw new Error(`Select the Minecraft assets folder containing indexes/${wanted}.json and objects/.`);
  const index = (await json(candidate[1])) as { objects?: Record<string, { hash?: string; size?: number }> };
  for (const [name, entry] of Object.entries(index.objects ?? {})) {
    if (name !== 'minecraft/sounds.json' && !/^minecraft\/sounds\/.+\.ogg$/.test(name)) continue;
    if (!entry.hash || !/^[a-f0-9]{40}$/.test(entry.hash)) continue;
    const suffix = `/objects/${entry.hash.slice(0, 2)}/${entry.hash}`,
      file = [...byPath].find(([p]) => p.endsWith(suffix))?.[1];
    if (!file) continue;
    bytes += file.size;
    if (file.size > 32 * 1024 * 1024 || bytes > 384 * 1024 * 1024)
      throw new Error('Sound assets exceed the supported size.');
    if (name === 'minecraft/sounds.json') events = await json(file);
    else {
      sounds[name.slice('minecraft/sounds/'.length, -4)] = file.slice(0, file.size, 'audio/ogg');
      count++;
    }
  }
  if (!count || !Object.keys(events).length)
    throw new Error('No complete sound set found. Choose the assets folder from a game version you have launched.');
  await writeLocalPack({ ...current, sounds, soundEvents: events });
  return count;
}
