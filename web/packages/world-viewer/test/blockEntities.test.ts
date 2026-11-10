import { describe, expect, it } from 'vitest';
import { meshSection } from '../src/mesh/mesher';
import type { Models1122 } from '../src/mesh/models/index';
import { potContents, resolveState, skullType, type Nb } from '../src/mesh/models/states';
import { TEX_STARFIELD, TEX_TEXTURED } from '../src/mesh/sink';
import type { BlockEntityDto } from '../src/types';
import { bannerPatternName, compositeBanner, DYE_RGB, signLayout, SIGN_LINE_WIDTH, spawnerMobFeet, spawnerMobScale, textWidth } from '../src/render/blockEntityTex';
import { charWidth, fontTable, FORMAT_RGB, glyphIndex, layoutLine, rasterLine, signLineText, stringWidth, trimToWidth } from '../src/render/font';
import { javaRandomFloats, starLayerColours, starPasses } from '../src/render/starfield';
import { storeWith, type B } from './helpers';
import { noPacks, pack1122, packBeta, table1122 } from './pack';

const Y = 70;
const src = () => table1122().source as Models1122;

/** A section with these blocks and block entities; the cells (c + r·cols) and tile flags of every emitted quad. */
async function meshWith(blocks: B[], bes: BlockEntityDto[]) {
  const store = await storeWith(blocks, { height: 256 });
  store.setBlockEntities(0, 0, 0, bes);
  const m = meshSection(store, 0, 0, Y >> 4, 0, table1122());
  const cols = table1122().cols;
  const quads: Array<{ cell: number; flags: number; span: number }> = [];
  for (const pass of [m.solid, m.translucent])
    if (pass) for (let v = 0; v < pass.tile.length / 4; v += 4) quads.push({ cell: pass.tile[v * 4 + 1] * cols + pass.tile[v * 4], flags: pass.tile[v * 4 + 2], span: pass.tile[v * 4 + 3] });
  return { m, quads };
}

const sheetCell = (name: string) => {
  const s = pack1122().blocks.sheets[name];
  return s[1] * pack1122().blocks.columns + s[0];
};

const nbWith = (be: BlockEntityDto | undefined): Nb => ({ id: () => 0, meta: () => 0, wx: 0, wy: 0, wz: 0, be: () => be });

describe.skipIf(noPacks)('block entities in the section mesher', () => {
  it('beds take their colour from the block entity (red without one)', async () => {
    const blue = await meshWith([[4, Y, 4, 26, 0]], [{ x: 4, y: Y, z: 4, kind: 'bed', color: 'blue' }]);
    expect(blue.quads.every((q) => q.cell === sheetCell('entity/bed/blue'))).toBe(true);
    const plain = await meshWith([[4, Y, 4, 26, 0]], []);
    expect(plain.quads.every((q) => q.cell === sheetCell('entity/bed/red'))).toBe(true);
    const fourteen = await meshWith([[4, Y, 4, 26, 0]], [{ x: 4, y: Y, z: 4, kind: 'bed', color: 14 }]);
    expect(fourteen.quads[0].cell).toBe(sheetCell('entity/bed/red'));
  });

  it('skulls: type picks the texture (hat layer for players), rotation turns floor skulls by 22.5° steps', async () => {
    const creeper = await meshWith([[4, Y, 4, 144, 1]], [{ x: 4, y: Y, z: 4, kind: 'skull', type: 4, rotation: 0 }]);
    expect(creeper.quads.every((q) => q.cell === sheetCell('entity/creeper/creeper'))).toBe(true);
    const player = await meshWith([[4, Y, 4, 144, 1]], [{ x: 4, y: Y, z: 4, kind: 'skull', type: 'player' }]);
    expect(player.quads.length).toBe(12); // head + hat
    const dragon = await meshWith([[4, Y, 4, 144, 1]], [{ x: 4, y: Y, z: 4, kind: 'skull', type: 'dragon' }]);
    expect(dragon.quads.length).toBeGreaterThan(30);
    expect(skullType('wither_skeleton')).toBe('wither');
    expect(skullType(3)).toBe('player');
    // Rotation 4 (90°) turns the head: the face (north side at rotation 0) moves to the east or west.
    const at = (rot: number) => src().bake(144, resolveState(144, 1, nbWith({ x: 0, y: 0, z: 0, kind: 'skull', type: 0, rotation: rot }), () => false));
    const r0 = at(0), r4 = at(4), r8 = at(8);
    const front = (qs: ReturnType<typeof at>) => qs.map((q) => q.dir).join(',');
    expect(front(r4)).not.toBe(front(r0));
    expect(front(r8)).not.toBe(front(r4));
    // 180° maps the face quad's uv rectangle onto the opposite side.
    const north0 = r0.find((q) => q.dir === 2)!, south8 = r8.find((q) => q.dir === 3)!;
    expect(south8.uv).toEqual(north0.uv);
  });

  it('flower pots show their plant (item id or name form)', () => {
    const models = (item: unknown) => src().modelsOf(resolveState(140, 0, nbWith({ x: 0, y: 0, z: 0, kind: 'flower_pot', item: item as never }), () => false)).map((m) => m.model);
    expect(models([38, 0])).toEqual(['flower_pot_rose']);
    expect(models([38, 3])).toEqual(['flower_pot_houstonia']);
    expect(models('minecraft:cactus')).toEqual(['flower_pot_cactus']);
    expect(models('red_mushroom')).toEqual(['flower_pot_mushroom_red']);
    expect(models([6, 2])).toEqual(['flower_pot_birch']);
    expect(models(null)).toEqual(['flower_pot']);
    expect(potContents({ id: 37, meta: 0 })).toBe('dandelion');
    expect(potContents([1, 0])).toBe('empty');
  });

  it('banners with a block entity are left to the block-entity layer; without one they stay in the mesh', async () => {
    const withBe = await meshWith([[4, Y, 4, 176, 0]], [{ x: 4, y: Y, z: 4, kind: 'banner', base: 11, patterns: [['bs', 0]] }]);
    expect(withBe.quads.length).toBe(0);
    expect((await meshWith([[4, Y, 4, 176, 0]], [])).quads.length).toBeGreaterThan(0);
  });

  it('end portals and gateways carry the starfield flag', async () => {
    const { quads } = await meshWith([[4, Y, 4, 119], [6, Y, 4, 209]], []);
    expect(quads.length).toBe(1 + 6);
    expect(quads.every((q) => (q.flags & TEX_STARFIELD) !== 0 && (q.flags & TEX_TEXTURED) !== 0)).toBe(true);
  });

  it('1.12.2 flowing water and lava sample their native 32 px textures on 2×2 cells', async () => {
    const { quads } = await meshWith([[4, Y, 4, 8, 1], [3, Y, 4, 9, 0], [8, Y, 8, 10, 2]], []);
    const water = sheetCell('blocks/water_flow@32'), lava = sheetCell('blocks/lava_flow@32');
    const flows = quads.filter((q) => q.cell === water || q.cell === lava);
    expect(flows.length).toBeGreaterThan(0);
    expect(flows.every((q) => q.span === ((2 << 8) | 2))).toBe(true);
    expect(quads.some((q) => q.cell === lava)).toBe(true);
    const anim = pack1122().anims.find((a) => a.name === 'blocks/water_flow@32')!;
    expect(anim.size).toBe(32);
    expect(anim.cell[1] * pack1122().blocks.columns + anim.cell[0]).toBe(water);
  });
});

describe.skipIf(noPacks)('fast leaves (opt-in)', () => {
  it('make leaves opaque cubes that cull each other and ignore texel alpha', async () => {
    const { buildStateTable } = await import('../src/mesh/states');
    const { meshAssets } = await import('../src/atlas/assets');
    const { TEX_OPAQUE } = await import('../src/mesh/sink');
    const { F_OPAQUE } = await import('../src/world/blocks');
    const fast = buildStateTable('1.12.2', meshAssets(pack1122()), true, true);
    expect(fast.flags[18 << 4] & F_OPAQUE).toBeTruthy();
    expect(table1122().flags[18 << 4] & F_OPAQUE).toBeFalsy();
    expect(fast.source.quads(18 << 4).every((q) => q.fx === TEX_OPAQUE)).toBe(true);
    const store = await storeWith([[4, Y, 4, 18], [5, Y, 4, 18]], { height: 256 });
    expect(meshSection(store, 0, 0, Y >> 4, 0, fast).solid!.quads).toBe(10);
    expect(meshSection(store, 0, 0, Y >> 4, 0, table1122()).solid!.quads).toBe(12);
  });
});

const FONT_1122 = noPacks ? null! : fontTable(pack1122().font, false)!;
const FONT_BETA = noPacks ? null! : fontTable(packBeta().font, true)!;

describe('the bitmap font (FontRenderer)', () => {
  it.skipIf(noPacks)('glyph tables and advances measured from each version’s own font image', () => {
    expect(FONT_1122.widths.length).toBe(256);
    expect(glyphIndex(FONT_1122, 'A')).toBe(65);
    expect(glyphIndex(FONT_1122, 'é')).toBe(130);
    expect(glyphIndex(FONT_BETA, 'A')).toBe(65);
    expect(glyphIndex(FONT_BETA, '`')).toBe(-1); // font.txt has a second ' where ` would be
    for (const f of [FONT_1122, FONT_BETA]) {
      expect([...'il!I tf@'].map((c) => charWidth(f, c))).toEqual([2, 3, 2, 4, 4, 4, 5, 7]);
      expect(charWidth(f, '§')).toBe(-1);
      expect(stringWidth(f, 'MineClaw')).toBe(6 * 6 + 2 + 3);
    }
    expect(charWidth(FONT_1122, 'あ')).toBe(0); // unicode pages are not bundled
  });

  it.skipIf(noPacks)('§ codes: zero width, 1.12.2 bold adds 1 per glyph until §r; colours reset the styles when drawing', () => {
    expect(stringWidth(FONT_1122, '§4red')).toBe(stringWidth(FONT_1122, 'red'));
    expect(stringWidth(FONT_1122, '§lab')).toBe(stringWidth(FONT_1122, 'ab') + 2);
    expect(stringWidth(FONT_1122, '§la§rb')).toBe(stringWidth(FONT_1122, 'ab') + 1);
    expect(stringWidth(FONT_BETA, '§lab')).toBe(stringWidth(FONT_BETA, 'ab'));
    const l = layoutLine(FONT_1122, '§4R§lB§nU§6c', 0);
    expect(l.glyphs.map((g) => [g.rgb, g.bold])).toEqual([[FORMAT_RGB[4], false], [FORMAT_RGB[4], true], [FORMAT_RGB[4], true], [FORMAT_RGB[6], false]]);
    expect(l.rules).toEqual([{ x0: 12, x1: 20, y0: 8, y1: 9, rgb: FORMAT_RGB[4] }]); // U's underline, from x − 1 to its bold advance
    expect(l.width).toBe(6 + 7 + 7 + 6);
    // b1.8.1: colours only; an unknown code is white.
    expect(layoutLine(FONT_BETA, '§zx§ly', 0).glyphs.map((g) => [g.rgb, g.bold])).toEqual([[0xffffff, false], [0xffffff, false]]);
  });

  it.skipIf(noPacks)('sign lines: 1.12.2 cuts at the last space that fits 90 px (splitText), b1.8.1 draws the line as is', () => {
    const long = 'a much longer line that cannot fit';
    const cut = signLineText(FONT_1122, long);
    expect(stringWidth(FONT_1122, cut)).toBeLessThanOrEqual(90);
    expect(long.startsWith(cut)).toBe(true);
    expect(long[cut.length]).toBe(' ');
    expect(trimToWidth(FONT_1122, 'WWWWWWWWWWWWWWWWWWWWW', 90).length).toBe(15);
    expect(signLineText(FONT_1122, 'WWWWWWWWWWWWWWWWWWWWW').length).toBe(15); // no space: cut to width
    expect(signLineText(FONT_1122, 'one\ntwo')).toBe('one');
    expect(signLineText(FONT_BETA, long)).toBe(long);
    const s = signLayout(['MineClaw', '§4red', long, 'i'], FONT_1122);
    expect(s.map((x) => x.y)).toEqual([-20, -10, 0, 10]);
    expect(s[0]).toMatchObject({ text: 'MineClaw', width: 41, x: -20 });
    expect(s[1]).toMatchObject({ text: '§4red', width: stringWidth(FONT_1122, 'red') });
    expect(s[2].text).toBe(cut);
  });

  it('rasterises glyph texels in the run colour, bold twice, italic sheared, rules filled', () => {
    // A 128×128 sheet whose glyph 65 ('A') is a 1×8 column at x = 0.
    const sheet = new Uint8ClampedArray(128 * 128 * 4);
    for (let y = 0; y < 8; y++) sheet.set([255, 255, 255, 255], ((32 + y) * 128 + 8) * 4);
    const font = { data: sheet, width: 128, cell: 8 };
    const W = 16, H = 10;
    const px = (out: Uint8Array, x: number, y: number) => Array.from(out.subarray((y * W + x) * 4, (y * W + x) * 4 + 4));
    const out = new Uint8Array(W * H * 4);
    rasterLine(out, W, H, 1, font, { glyphs: [{ cell: 65, x: 2, rgb: 0xff0000, bold: true, italic: false }], rules: [{ x0: 0, x1: 4, y0: 8, y1: 9, rgb: 0x0000ff }] }, 0, 0);
    expect(px(out, 2, 0)).toEqual([255, 0, 0, 255]);
    expect(px(out, 3, 7)).toEqual([255, 0, 0, 255]); // the bold copy
    expect(px(out, 4, 3)[3]).toBe(0);
    expect(px(out, 1, 8)).toEqual([0, 0, 255, 255]);
    const it2 = new Uint8Array(W * H * 4);
    rasterLine(it2, W, H, 1, font, { glyphs: [{ cell: 65, x: 4, rgb: 0, bold: false, italic: true }], rules: [] }, 0, 0);
    expect(px(it2, 5, 0)[3]).toBe(255); // the top leans right
    expect(px(it2, 3, 7)[3]).toBe(255); // the bottom left
  });
});

describe('block-entity layer helpers', () => {
  it('spawner display mobs: 1.12.2 0.53125 over the larger hitbox side when it exceeds a block, b1.8.1 0.4375; the feet', () => {
    expect(spawnerMobScale('minecraft:pig')).toBeCloseTo(0.53125);
    expect(spawnerMobScale('chicken')).toBeCloseTo(0.53125);
    expect(spawnerMobScale('Zombie')).toBeCloseTo(0.53125 / 1.95);
    expect(spawnerMobScale('CaveSpider')).toBeCloseTo(0.53125);
    expect(spawnerMobScale('minecraft:ghast')).toBeCloseTo(0.53125 / 4);
    expect(spawnerMobScale('Zombie', true)).toBe(0.4375);
    expect(spawnerMobFeet(1, false)).toEqual([0, 0.2, 0]);
    const [x, y, z] = spawnerMobFeet(0, true);
    expect(x).toBeCloseTo(0);
    expect(y).toBeCloseTo(0.4 - 0.4 * Math.cos(Math.PI / 6));
    expect(z).toBeCloseTo(0.2);
  });

  it('starfield passes by the squared distance (TileEntityEndPortalRenderer.getPasses), the gateway one more', () => {
    expect(starPasses(0)).toBe(16);
    expect(starPasses(17 * 17)).toBe(15);
    expect(starPasses(30 * 30)).toBe(14);
    expect(starPasses(50 * 50)).toBe(12);
    expect(starPasses(200 * 200)).toBe(2);
    expect(starPasses(0, true)).toBe(17);
  });

  it('sign text: four lines centred, at j·10 − 20, cut to the board width', () => {
    const l = signLayout(['MineClaw', '', 'a much longer line that cannot fit on a sign board at all', 'i']);
    expect(l.map((x) => x.y)).toEqual([-20, -10, 0, 10]);
    expect(l[0].x).toBe(-Math.floor(textWidth('MineClaw') / 2));
    expect(l[1]).toMatchObject({ text: '', width: 0 });
    expect(l[2].width).toBeLessThanOrEqual(SIGN_LINE_WIDTH);
    expect(l[2].text.length).toBeGreaterThan(5);
    expect(l[3].width).toBe(1);
    expect(signLayout(['§4red'])[0].text).toBe('red');
  });

  it('banner compositing: banner_base first, then the base colour, then the patterns in order (later on top)', () => {
    const px = (r: number, g: number, b: number, a = 255) => new Uint8ClampedArray([r, g, b, a]);
    const base = px(200, 200, 200);
    const full = px(255, 255, 255), none = px(0, 0, 0, 0);
    const red = DYE_RGB[14], blue = DYE_RGB[11];
    const a = compositeBanner(base, [{ mask: full, rgb: blue }, { mask: full, rgb: red }]);
    const b = compositeBanner(base, [{ mask: full, rgb: red }, { mask: full, rgb: blue }]);
    expect([...a.slice(0, 3)]).toEqual([Math.round((200 * 0xb0) / 255), Math.round((200 * 0x2e) / 255), Math.round((200 * 0x26) / 255)]);
    expect([...a]).not.toEqual([...b]);
    expect([...compositeBanner(base, [{ mask: none, rgb: red }])]).toEqual([...base]);
    expect(bannerPatternName('bs')).toBe('stripe_bottom');
    expect(bannerPatternName('minecraft:creeper')).toBe('creeper');
    expect(bannerPatternName('zz')).toBeNull();
  });

  it('starfield colours come from java.util.Random(31100), layer 0 the 0.15 grey sky', () => {
    expect(javaRandomFloats(0, 1)[0]).toBeCloseTo(0.7309677, 6);
    const c = starLayerColours();
    expect(c.length).toBe(17); // the gateway's extra layer too
    expect(c[0]).toEqual([0.15, 0.15, 0.15]);
    expect(c[15][2]).toBeGreaterThan(c[1][2]); // later layers are brighter (2 / (18 − j))
  });
});
