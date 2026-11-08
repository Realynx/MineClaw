import type { AssetBundle, Pack } from './bundle';
import type { GameVersion } from '../world/blocks';

/** Original, procedural placeholders. No Minecraft artwork is embedded or downloaded. Geometry uses the flat palette. */
export async function fallbackBundle(version: GameVersion): Promise<AssetBundle> {
  const sheet = new OffscreenCanvas(256, 256),
    ctx = sheet.getContext('2d')!;
  for (let i = 0; i < 256; i++) {
    const x = (i % 16) * 16,
      y = Math.floor(i / 16) * 16;
    ctx.fillStyle = `hsl(${(i * 137.5) % 360} 20% 45%)`;
    ctx.fillRect(x, y, 16, 16);
    ctx.fillStyle = '#ffffff30';
    ctx.fillRect(x + 2, y + 2, 12, 2);
  }
  const blocks = await createImageBitmap(sheet),
    items = await createImageBitmap(sheet),
    entity = await createImageBitmap(sheet);
  const common = { tileSize: 16, entity: { width: 256, height: 256, rects: {} }, env: [] };
  const pack: Pack =
    version === 'b1.8.1'
      ? { ...common, version, terrain: { columns: 16, rows: 16 }, items: { columns: 16, rows: 16 } }
      : {
          ...common,
          version,
          blocks: { columns: 16, rows: 16, tiles: {}, info: {}, sheets: {} },
          items: { columns: 16, rows: 16, tiles: {} },
          anims: [],
          blockstates: {},
          models: {},
          itemModels: {},
          biomes: {},
        };
  return { version, pack, blocks, items, entity, anim: null, env: {}, font: null, fallback: true };
}
