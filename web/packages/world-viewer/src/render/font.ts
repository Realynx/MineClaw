// The default Minecraft font (pure, unit-tested): FontRenderer's glyph table and advances (measured from the version's own
// font image at pack time: 1.12.2 textures/font/ascii.png, b1.8.1 font/default.png with font.txt), string widths,
// trimStringToWidth, the sign renderer's line cut (GuiUtilRenderComponents.splitText at 90 px, first line) and the glyph
// layout of renderStringAtPos with its § formatting codes, and the bitmap rasteriser that draws a laid-out line into RGBA
// pixels from the font sheet (glyph texels × the text colour; italic shear, bold double strike, underline, strikethrough).

import type { PackFont } from '../assets/bundle';

export interface FontTable {
  /** The character at each glyph cell ("\0" = none). */
  chars: string;
  /** Advance per glyph cell (glyph width + spacing). */
  widths: ArrayLike<number>;
  /** b1.8.1 FontRenderer rules: colour codes only, glyphs looked up from cell 32 on. */
  beta: boolean;
}

/** FontRenderer.colorCode for codes 0..f (no shadow). */
export const FORMAT_RGB = [
  0x000000, 0x0000aa, 0x00aa00, 0x00aaaa, 0xaa0000, 0xaa00aa, 0xffaa00, 0xaaaaaa, 0x555555, 0x5555ff, 0x55ff55, 0x55ffff, 0xff5555, 0xff55ff, 0xffff55, 0xffffff,
];

/** The table from a pack (null when the pack has no font: older packs). */
export function fontTable(f: PackFont | undefined, beta: boolean): FontTable | null {
  return f && f.chars && f.widths?.length === 256 ? { chars: f.chars, widths: f.widths, beta } : null;
}

/** The glyph cell of a character, or −1 (b1.8.1: ChatAllowedCharacters.indexOf + 32; 1.12.2: the table's index). */
export function glyphIndex(f: FontTable, ch: string): number {
  if (ch === ' ') return 32;
  if (!ch || ch === '\0') return -1;
  return f.chars.indexOf(ch, f.beta ? 32 : 0);
}

/** getCharWidth: § −1, space 4, a glyph its advance; characters outside the table 0 (1.12.2 draws them from the unicode
 * pages, which are not bundled; b1.8.1 skips them). */
export function charWidth(f: FontTable, ch: string): number {
  if (ch === '§') return -1;
  if (ch === ' ') return 4;
  const i = glyphIndex(f, ch);
  return i >= 0 ? f.widths[i] : 0;
}

/** getStringWidth: § and its code count 0; 1.12.2 §l makes later glyphs 1 wider until §r (colour codes keep it). */
export function stringWidth(f: FontTable, s: string): number {
  let w = 0, bold = false;
  for (let i = 0; i < s.length; i++) {
    let k = charWidth(f, s[i]);
    if (k < 0 && i < s.length - 1) {
      const c = s[++i].toLowerCase();
      if (!f.beta) {
        if (c === 'l') bold = true;
        else if (c === 'r') bold = false;
      }
      k = 0;
    }
    if (k < 0) k = 0;
    w += k;
    if (bold && k > 0) w++;
  }
  return w;
}

/** trimStringToWidth(text, width, false). */
export function trimToWidth(f: FontTable, s: string, width: number): string {
  let out = '', w = 0, code = false, bold = false;
  for (let i = 0; i < s.length && w < width; i++) {
    const c = s[i], k = charWidth(f, c);
    if (code) {
      code = false;
      if (c === 'l' || c === 'L') bold = true;
      else if (c === 'r' || c === 'R') bold = false;
    } else if (k < 0) code = true;
    else {
      w += k;
      if (bold) w++;
    }
    if (w > width) break;
    out += c;
  }
  return out;
}

/** The text TileEntitySignRenderer draws for one line: 1.12.2 the first line GuiUtilRenderComponents.splitText makes at 90 px
 * (cut at the last space that fits); b1.8.1 the line itself (its editor caps lines at 15 characters). */
export function signLineText(f: FontTable, raw: string): string {
  if (f.beta) return raw;
  let s = raw;
  const nl = s.indexOf('\n');
  if (nl >= 0) s = s.slice(0, nl);
  if (stringWidth(f, s) <= 90) return s;
  const fit = trimToWidth(f, s, 90);
  if (fit.length < s.length) {
    const l = fit.lastIndexOf(' ');
    if (l >= 0 && stringWidth(f, s.slice(0, l)) > 0) return s.slice(0, l);
  }
  return fit;
}

export interface GlyphRun {
  /** Glyph cell. */
  cell: number;
  x: number;
  rgb: number;
  bold: boolean;
  italic: boolean;
}

export interface Rule {
  x0: number;
  x1: number;
  /** Rows (relative to the line's top) the rule covers: [y0, y1). */
  y0: number;
  y1: number;
  rgb: number;
}

/** renderStringAtPos: glyph positions from x (font px) with the § codes (1.12.2: colours reset the styles, k obfuscated —
 * a fixed same-width stand-in, l bold, m strikethrough, n underline, o italic, r reset; b1.8.1: colours only). */
export function layoutLine(f: FontTable, s: string, x: number, baseRgb = 0, seed = 1): { glyphs: GlyphRun[]; rules: Rule[]; width: number } {
  const glyphs: GlyphRun[] = [], rules: Rule[] = [];
  let rgb = baseRgb, bold = false, italic = false, under = false, strike = false, random = false;
  let px = x;
  let r = seed | 0 || 1;
  const rand = (n: number) => ((r = (Math.imul(r, 1103515245) + 12345) | 0) >>> 0) % n;
  for (let i = 0; i < s.length; i++) {
    let c = s[i];
    if (c === '§' && i + 1 < s.length) {
      const code = s[i + 1].toLowerCase();
      i++;
      if (f.beta) {
        let m = '0123456789abcdef'.indexOf(code);
        if (m < 0) m = 15;
        rgb = FORMAT_RGB[m];
        continue;
      }
      let m = '0123456789abcdefklmnor'.indexOf(code);
      if (m < 16) {
        random = bold = strike = under = italic = false;
        if (m < 0) m = 15;
        rgb = FORMAT_RGB[m];
      } else if (m === 16) random = true;
      else if (m === 17) bold = true;
      else if (m === 18) strike = true;
      else if (m === 19) under = true;
      else if (m === 20) italic = true;
      else {
        random = bold = strike = under = italic = false;
        rgb = baseRgb;
      }
      continue;
    }
    let cell = glyphIndex(f, c);
    if (random && cell >= 0) {
      const w = charWidth(f, c);
      for (let tries = 0; tries < 64; tries++) {
        const j = rand(f.chars.length);
        const d = f.chars[j];
        if (d !== '\0' && charWidth(f, d) === w) {
          c = d;
          cell = glyphIndex(f, d);
          break;
        }
      }
    }
    let adv = charWidth(f, c);
    if (adv < 0) adv = 0;
    if (cell >= 0 && c !== ' ') glyphs.push({ cell, x: px, rgb, bold, italic });
    if (bold) adv++;
    if (strike) rules.push({ x0: px, x1: px + adv, y0: 3, y1: 4, rgb });
    if (under) rules.push({ x0: px - 1, x1: px + adv, y0: 8, y1: 9, rgb });
    px += adv;
  }
  return { glyphs, rules, width: px - x };
}

/** The font sheet's pixels (RGBA, 16×16 cells of `cell` px). */
export interface FontPixels {
  data: Uint8ClampedArray | Uint8Array;
  width: number;
  cell: number;
}

/**
 * Draws glyph runs and rules into an RGBA buffer `out` (w × h px at `s` px per font px), the line's top at (ox, oy) font px:
 * each glyph cell's texels with alpha ≥ 0.1 (the alpha test) take the run colour × the texel colour; italic shears the
 * glyph like renderDefaultChar (top +1 px, bottom −1 px); bold draws it again 1 px right.
 */
export function rasterLine(out: Uint8ClampedArray | Uint8Array, w: number, h: number, s: number, font: FontPixels, line: { glyphs: GlyphRun[]; rules: Rule[] }, ox: number, oy: number): void {
  const put = (x: number, y: number, rgb: number, tr: number, tg: number, tb: number) => {
    if (x < 0 || y < 0 || x >= w || y >= h) return;
    const o = (y * w + x) * 4;
    out[o] = (((rgb >> 16) & 255) * tr) / 255;
    out[o + 1] = (((rgb >> 8) & 255) * tg) / 255;
    out[o + 2] = ((rgb & 255) * tb) / 255;
    out[o + 3] = 255;
  };
  const k = font.cell, scale = 8 / k;
  for (const g of line.glyphs) {
    const cx = (g.cell % 16) * k, cy = Math.floor(g.cell / 16) * k;
    for (let pass = 0; pass < (g.bold ? 2 : 1); pass++) {
      const gx = ox + g.x + pass;
      for (let sy = 0; sy < 8 * s; sy++) {
        const fy = (sy + 0.5) / s; // font px from the glyph top
        const ty = Math.min(k - 1, Math.floor(fy / scale));
        const shear = g.italic ? 1 - (2 * fy) / 7.99 : 0;
        for (let tx = 0; tx < k; tx++) {
          const t = ((cy + ty) * font.width + cx + tx) * 4;
          if (font.data[t + 3] < 26) continue;
          const x0 = Math.round((gx + tx * scale + shear) * s), x1 = Math.round((gx + (tx + 1) * scale + shear) * s);
          for (let x = x0; x < x1; x++) put(x, Math.floor(oy * s) + sy, g.rgb, font.data[t], font.data[t + 1], font.data[t + 2]);
        }
      }
    }
  }
  for (const r of line.rules)
    for (let y = (oy + r.y0) * s; y < (oy + r.y1) * s; y++) for (let x = Math.round((ox + r.x0) * s); x < Math.round((ox + r.x1) * s); x++) put(x, y, r.rgb, 255, 255, 255);
}
