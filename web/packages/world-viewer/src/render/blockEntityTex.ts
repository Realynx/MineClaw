// Pure helpers for the block-entity layer (no DOM, unit-tested): sign text layout in the vanilla font's metrics, banner
// pattern codes, dye colours and the banner texture composite (LayeredColorMaskTexture's order and blend).

/** The default font's advance per character (glyph width + 1 px spacing), as FontRenderer measures ascii.png. */
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

/** TileEntitySignRenderer's layout: each of the four lines centred (x = −width/2), at y = j·10 − lines·5; a line wider than
 * the board is cut to fit. Formatting codes (§x) are dropped. */
export function signLayout(lines: ReadonlyArray<string | null | undefined>): SignLine[] {
  const four = [0, 1, 2, 3].map((j) => (lines[j] ?? '').replace(/§./g, ''));
  return four.map((raw, j) => {
    let text = raw;
    while (text.length && textWidth(text) > SIGN_LINE_WIDTH) text = text.slice(0, -1);
    const width = textWidth(text);
    return { text, x: -Math.floor(width / 2), y: j * 10 - four.length * 5, width };
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

/** Spawner mobs' size (MobSpawnerBaseLogic scales the display mob to 0.53125 / max(width, height)). */
const MOB_SIZE: Record<string, number> = {
  zombie: 1.95, skeleton: 1.99, creeper: 1.7, spider: 1.4, cave_spider: 0.7, cavespider: 0.7, pig: 0.9, cow: 1.4, sheep: 1.3,
  chicken: 0.7, blaze: 1.8, silverfish: 0.4, magma_cube: 2.04, lavaslime: 2.04, slime: 2.04, ghast: 4, enderman: 2.9,
  pig_zombie: 1.95, pigzombie: 1.95, zombie_pigman: 1.95, witch: 1.95, wolf: 0.85, squid: 0.8, endermite: 0.4, guardian: 0.85,
};

export function spawnerMobScale(mob: string): number {
  const n = mob.replace(/^minecraft:/, '').replace(/([a-z])([A-Z])/g, '$1_$2').toLowerCase();
  return 0.53125 / Math.max(0.6, MOB_SIZE[n] ?? 1.8);
}
