// Pure helpers for the block-entity layer (no DOM, unit-tested): sign text layout in the vanilla font's metrics, banner
// pattern codes, dye colours and the banner texture composite (LayeredColorMaskTexture's order and blend).

import { signLineText, stringWidth, type FontTable } from './font';

/** The default font's advance per character (glyph width + 1 px spacing), as FontRenderer measures ascii.png: the fallback
 * when a pack has no measured font table. */
export function charWidth(ch: string): number {
  switch (ch) {
    case ' ': return 4;
    case '!': case '.': case ',': case ':': case ';': case '|': case 'i': return 2;
    case "'": case 'l': case '`': return 3;
    case 'I': case 't': case '[': case ']': return 4;
    case '"': case '(': case ')': case '{': case '}': case '<': case '>': case '*': case 'f': case 'k': return 5;
    case '@': case '~': return 7;
    default: return 6;
  }
}

export const textWidth = (s: string) => [...s].reduce((a, c) => a + charWidth(c), 0) - (s.length ? 1 : 0);

/** The widest line TileEntitySignRenderer has room for (font px). */
export const SIGN_LINE_WIDTH = 90;

export interface SignLine {
  text: string;
  /** Left edge and top (font px) relative to the board's text origin: centred, lines at j·10 − 20. */
  x: number;
  y: number;
  width: number;
}

/** TileEntitySignRenderer's layout: each of the four lines centred (x = −width/2, Java's truncating division), at
 * y = j·10 − lines·5. With the version's font table: 1.12.2 cuts a line like splitText (at the last space that fits 90 px)
 * and keeps its § codes for the glyph layout; b1.8.1 draws the line as it is. Without one: § codes are dropped and the line
 * is cut character by character to the board width. */
export function signLayout(lines: ReadonlyArray<string | null | undefined>, font?: FontTable | null): SignLine[] {
  const four = [0, 1, 2, 3].map((j) => lines[j] ?? '');
  return four.map((raw, j) => {
    let text: string, width: number;
    if (font) {
      text = signLineText(font, raw);
      width = stringWidth(font, text);
    } else {
      text = raw.replace(/§./g, '');
      while (text.length && textWidth(text) > SIGN_LINE_WIDTH) text = text.slice(0, -1);
      width = textWidth(text);
    }
    return { text, x: -Math.trunc(width / 2), y: j * 10 - four.length * 5, width };
  });
}

/** EnumDyeColor.getColorValue (the banner/bed dye colours), by colour index 0 white … 15 black. */
export const DYE_RGB = [0xf9fffe, 0xf9801d, 0xc74ebd, 0x3ab3da, 0xfed83d, 0x80c71f, 0xf38baa, 0x474f52, 0x9d9d97, 0x169c9c, 0x8932b8, 0x3c44aa, 0x835432, 0x5e7c16, 0xb02e26, 0x1d1d21];

/** BannerPattern hash codes → texture names (entity/banner/<name>.png). */
export const BANNER_PATTERNS: Record<string, string> = {
  b: 'base', bl: 'square_bottom_left', br: 'square_bottom_right', tl: 'square_top_left', tr: 'square_top_right', bs: 'stripe_bottom',
  ts: 'stripe_top', ls: 'stripe_left', rs: 'stripe_right', cs: 'stripe_center', ms: 'stripe_middle', drs: 'stripe_downright',
  dls: 'stripe_downleft', ss: 'small_stripes', cr: 'cross', sc: 'straight_cross', bt: 'triangle_bottom', tt: 'triangle_top',
  bts: 'triangles_bottom', tts: 'triangles_top', ld: 'diagonal_left', rd: 'diagonal_up_right', lud: 'diagonal_up_left',
  rud: 'diagonal_right', mc: 'circle', mr: 'rhombus', vh: 'half_vertical', hh: 'half_horizontal', vhr: 'half_vertical_right',
  hhb: 'half_horizontal_bottom', bo: 'border', cbo: 'curly_border', cre: 'creeper', gra: 'gradient', gru: 'gradient_up',
  bri: 'bricks', sku: 'skull', flo: 'flower', moj: 'mojang',
};

/** A pattern code or name → its texture name, or null. */
export function bannerPatternName(code: string): string | null {
  const c = code.replace(/^minecraft:/, '').toLowerCase();
  if (BANNER_PATTERNS[c]) return BANNER_PATTERNS[c];
  return Object.values(BANNER_PATTERNS).includes(c) ? c : null;
}

/**
 * LayeredColorMaskTexture: start from banner_base (the pole and bar), then for each layer (the base colour first, then the
 * patterns bottom first) take the mask's blue channel as alpha and banner_base's texel × the dye colour as the colour, and
 * draw it over (alpha blend). RGBA, same size for all images.
 */
export function compositeBanner(base: Uint8ClampedArray | Uint8Array, layers: Array<{ mask: Uint8ClampedArray | Uint8Array; rgb: number }>): Uint8ClampedArray {
  const out = new Uint8ClampedArray(base);
  for (const { mask, rgb } of layers) {
    const dr = (rgb >> 16) & 255, dg = (rgb >> 8) & 255, db = rgb & 255;
    for (let i = 0; i < out.length; i += 4) {
      const a = mask[i + 2] / 255; // the mask's blue channel (white where the pattern is)
      if (a <= 0 || mask[i + 3] === 0) continue;
      const r = (base[i] * dr) / 255, g = (base[i + 1] * dg) / 255, b = (base[i + 2] * db) / 255;
      const da = out[i + 3] / 255, oa = a + da * (1 - a);
      out[i] = (r * a + out[i] * da * (1 - a)) / oa;
      out[i + 1] = (g * a + out[i + 1] * da * (1 - a)) / oa;
      out[i + 2] = (b * a + out[i + 2] * da * (1 - a)) / oa;
      out[i + 3] = oa * 255;
    }
  }
  return out;
}

/** max(width, height) of each mob's 1.12.2 hitbox as a freshly made entity (slimes and magma cubes at size 1). */
const MOB_SIZE: Record<string, number> = {
  zombie: 1.95, husk: 1.95, zombie_villager: 1.95, skeleton: 1.99, stray: 1.99, wither_skeleton: 2.4, creeper: 1.7, spider: 1.4,
  cave_spider: 0.7, cavespider: 0.7, pig: 0.9, cow: 1.4, mooshroom: 1.4, sheep: 1.3, chicken: 0.7, blaze: 1.8, silverfish: 0.4,
  magma_cube: 0.51, lavaslime: 0.51, lava_slime: 0.51, slime: 0.51, ghast: 4, enderman: 2.9, pig_zombie: 1.95, pigzombie: 1.95,
  zombie_pigman: 1.95, witch: 1.95, wolf: 0.85, squid: 0.8, endermite: 0.4, guardian: 0.85, elder_guardian: 1.9975, villager: 1.95,
  villager_golem: 2.7, iron_golem: 2.7, snowman: 1.9, snow_golem: 1.9, horse: 1.6, donkey: 1.6, mule: 1.6, skeleton_horse: 1.6,
  zombie_horse: 1.6, ocelot: 0.7, rabbit: 0.5, polar_bear: 1.4, llama: 1.87, parrot: 0.9, bat: 0.9, shulker: 1, vex: 0.8,
  vindication_illager: 1.95, evocation_illager: 1.95, illusion_illager: 1.95, giant: 11.7, ender_dragon: 16, wither: 3.5,
};

/**
 * The display mob's scale: 1.12.2 MobSpawnerBaseLogic / TileEntityMobSpawnerRenderer.renderMob 0.53125, divided by
 * max(width, height) when that is over 1; b1.8.1's TileEntityMobSpawnerRenderer a flat 0.4375.
 */
export function spawnerMobScale(mob: string, beta = false): number {
  if (beta) return 0.4375;
  const n = mob.replace(/^minecraft:/, '').replace(/([a-z])([A-Z])/g, '$1_$2').toLowerCase();
  const g = MOB_SIZE[n] ?? 1.8;
  return g > 1 ? 0.53125 / g : 0.53125;
}

/**
 * Where the display mob's feet go and how it is turned, relative to the spawner block's bottom centre, for a spin of `spin`
 * radians: 1.12.2 translate(0, 0.4) · rotY(spin) · translate(0, −0.2) · rotX(−30°) (feet 0.2 up); b1.8.1 translate(0, 0.4)
 * · rotY(spin) · rotX(−30°) · translate(0, −0.4) (the tilt about 0.4 up, so the feet swing out 0.2).
 */
export function spawnerMobFeet(spin: number, beta: boolean): [number, number, number] {
  if (!beta) return [0, 0.2, 0];
  const t = (-30 * Math.PI) / 180;
  const y = -0.4 * Math.cos(t), z = -0.4 * Math.sin(t);
  return [z * Math.sin(spin), 0.4 + y, z * Math.cos(spin)];
}

/** The spawner's spin rate: MobSpawnerBaseLogic turns mobRotation by 1000 / (spawnDelay + 200) a tick and the renderer
 * draws it × 10°; the delay is not on the wire, so the middle of the client's countdown (200) is used: 25° a tick. */
export const SPAWNER_SPIN_RAD_S = ((10 * 1000) / (200 + 200)) * 20 * (Math.PI / 180);
