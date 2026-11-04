// The default Minecraft font (pure, unit-tested): FontRenderer's glyph table and advances (measured from the version's own
// font image at pack time: 1.12.2 textures/font/ascii.png, b1.8.1 font/default.png with font.txt), string widths,
// trimStringToWidth, the sign renderer's line cut (GuiUtilRenderComponents.splitText at 90 px, first line) and the glyph
// layout of renderStringAtPos with its § formatting codes, and the bitmap rasteriser that draws a laid-out line into RGBA
// pixels from the font sheet (glyph texels × the text colour; italic shear, bold double strike, underline, strikethrough).

import type { PackFont } from '../assets/bundle';
import type { SignRun } from '../types';

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

/** The bold state after the § code letter c: l sets it, r clears it, any other code keeps it. */
function boldAfter(c: string, bold: boolean): boolean {
  if (c === 'l' || c === 'L') return true;
  if (c === 'r' || c === 'R') return false;
  return bold;
}

/** trimStringToWidth(text, width, false). */
export function trimToWidth(f: FontTable, s: string, width: number): string {
  let out = '', w = 0, code = false, bold = false;
  for (let i = 0; i < s.length && w < width; i++) {
    const c = s[i], k = charWidth(f, c);
    if (code) {
      code = false;
      bold = boldAfter(c, bold);
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

/** The colour names of the 16 formatting colours (TextFormatting), by code 0..f. */
export const FORMAT_NAMES = ['black', 'dark_blue', 'dark_green', 'dark_aqua', 'dark_red', 'dark_purple', 'gold', 'gray', 'dark_gray', 'blue', 'green', 'aqua', 'red', 'light_purple', 'yellow', 'white'];

/** A run's style flags and their § codes, in the order getFormattedText writes them. */
const RUN_STYLES = [['bold', '§l'], ['italic', '§o'], ['underlined', '§n'], ['strikethrough', '§m'], ['obfuscated', '§k']] as const;

/** A styled sign line (the wire's `rich` runs) as the § text getFormattedText hands the font renderer: each run its colour
 * (§r for the default) then its styles (§l §o §n §m §k), then its text. */
export function richToLegacy(runs: ReadonlyArray<SignRun> | null | undefined): string {
  let s = '';
  for (const r of runs ?? []) if (r.text) s += runCodes(r) + r.text;
  return s;
}

/** The § codes in front of one run's text: its colour (§r for the default), then its styles. */
function runCodes(r: SignRun): string {
  const c = r.color ? FORMAT_NAMES.indexOf(r.color) : -1;
  let s = c >= 0 ? `§${c.toString(16)}` : '§r';
  for (const [flag, code] of RUN_STYLES) if (r[flag]) s += code;
  return s;
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
  const st: LineState = { rgb: baseRgb, bold: false, italic: false, under: false, strike: false, random: false, px: x, r: seed | 0 || 1 };
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '§' && i + 1 < s.length) applyCode(f, st, s[++i].toLowerCase(), baseRgb);
    else placeChar(f, st, st.random ? obfuscated(f, st, c) : c, glyphs, rules);
  }
  return { glyphs, rules, width: st.px - x };
}

/** What layoutLine carries along a line: the § style, the pen position and the obfuscation generator's state. */
interface LineState {
  rgb: number;
  bold: boolean;
  italic: boolean;
  under: boolean;
  strike: boolean;
  random: boolean;
  px: number;
  r: number;
}

/** The § code letters by index: 0..f colours (both versions), then 1.12.2's k l m n o r. */
const FORMAT_CODES = '0123456789abcdefklmnor';

/** A colour code's index (−1 = unknown) as its colour: unknown codes draw white. */
const codeColour = (m: number): number => FORMAT_RGB[m < 0 ? 15 : m];

/** Applies one § code (lower case): b1.8.1 knows colours only (anything else white); 1.12.2 colours reset the styles. */
function applyCode(f: FontTable, st: LineState, code: string, baseRgb: number): void {
  const m = FORMAT_CODES.indexOf(code);
  if (f.beta) st.rgb = codeColour(m < 16 ? m : -1);
  else if (m < 16) resetStyle(st, codeColour(m));
  else applyStyleCode(st, m, baseRgb);
}

/** 1.12.2's style codes by index: k obfuscated, l bold, m strikethrough, n underline, o italic, r reset to the base colour. */
function applyStyleCode(st: LineState, m: number, baseRgb: number): void {
  switch (m) {
    case 16: st.random = true; break;
    case 17: st.bold = true; break;
    case 18: st.strike = true; break;
    case 19: st.under = true; break;
    case 20: st.italic = true; break;
    default: resetStyle(st, baseRgb);
  }
}

/** Clears the 1.12.2 styles and sets the colour. */
function resetStyle(st: LineState, rgb: number): void {
  st.random = st.bold = st.strike = st.under = st.italic = false;
  st.rgb = rgb;
}

/** The obfuscation generator's next value below n (an LCG on the line's seed). */
function nextRandom(st: LineState, n: number): number {
  return ((st.r = (Math.imul(st.r, 1103515245) + 12345) | 0) >>> 0) % n;
}

/** §k's stand-in for c: a random glyph of the same width (up to 64 tries), else c itself. Only glyphs are replaced. */
function obfuscated(f: FontTable, st: LineState, c: string): string {
  if (glyphIndex(f, c) < 0) return c;
  const w = charWidth(f, c);
  for (let tries = 0; tries < 64; tries++) {
    const d = f.chars[nextRandom(st, f.chars.length)];
    if (d !== '\0' && charWidth(f, d) === w) return d;
  }
  return c;
}

/** Lays out one character at the pen in the current style (its glyph, strikethrough and underline) and advances the pen. */
function placeChar(f: FontTable, st: LineState, c: string, glyphs: GlyphRun[], rules: Rule[]): void {
  const cell = glyphIndex(f, c), px = st.px, rgb = st.rgb;
  let adv = Math.max(charWidth(f, c), 0);
  if (cell >= 0 && c !== ' ') glyphs.push({ cell, x: px, rgb, bold: st.bold, italic: st.italic });
  if (st.bold) adv++;
  if (st.strike) rules.push({ x0: px, x1: px + adv, y0: 3, y1: 4, rgb });
  if (st.under) rules.push({ x0: px - 1, x1: px + adv, y0: 8, y1: 9, rgb });
  st.px = px + adv;
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
  const target: RasterTarget = { out, w, h, s };
  for (const g of line.glyphs) rasterGlyph(target, font, g, ox, oy);
  for (const r of line.rules)
    for (let y = (oy + r.y0) * s; y < (oy + r.y1) * s; y++) for (let x = Math.round((ox + r.x0) * s); x < Math.round((ox + r.x1) * s); x++) putTexel(target, x, y, r.rgb, 255, 255, 255);
}

/** rasterLine's output buffer: w × h px at s px per font px. */
interface RasterTarget {
  out: Uint8ClampedArray | Uint8Array;
  w: number;
  h: number;
  s: number;
}

/** Writes one opaque pixel (the run colour × the texel colour), skipping pixels outside the buffer. */
function putTexel(t: RasterTarget, x: number, y: number, rgb: number, tr: number, tg: number, tb: number): void {
  if (x < 0 || y < 0 || x >= t.w || y >= t.h) return;
  const o = (y * t.w + x) * 4, out = t.out;
  out[o] = (((rgb >> 16) & 255) * tr) / 255;
  out[o + 1] = (((rgb >> 8) & 255) * tg) / 255;
  out[o + 2] = ((rgb & 255) * tb) / 255;
  out[o + 3] = 255;
}

/** Draws one glyph's texels (twice, 1 px apart, when bold), each row sheared when italic. */
function rasterGlyph(t: RasterTarget, font: FontPixels, g: GlyphRun, ox: number, oy: number): void {
  const k = font.cell, s = t.s, scale = 8 / k;
  const cx = (g.cell % 16) * k, cy = Math.floor(g.cell / 16) * k;
  for (let pass = 0; pass < (g.bold ? 2 : 1); pass++) {
    const gx = ox + g.x + pass;
    for (let sy = 0; sy < 8 * s; sy++) {
      const fy = (sy + 0.5) / s; // font px from the glyph top
      const ty = Math.min(k - 1, Math.floor(fy / scale));
      const shear = g.italic ? 1 - (2 * fy) / 7.99 : 0;
      rasterGlyphRow(t, font, g.rgb, (cy + ty) * font.width + cx, gx, shear, Math.floor(oy * s) + sy);
    }
  }
}

/** One output row of a glyph from the texel row starting at `row`: each texel that passes the alpha test (≥ 0.1) fills its
 * span from gx (+ the italic shear). */
function rasterGlyphRow(t: RasterTarget, font: FontPixels, rgb: number, row: number, gx: number, shear: number, y: number): void {
  const k = font.cell, scale = 8 / k, s = t.s, data = font.data;
  for (let tx = 0; tx < k; tx++) {
    const i = (row + tx) * 4;
    if (data[i + 3] < 26) continue;
    const x0 = Math.round((gx + tx * scale + shear) * s), x1 = Math.round((gx + (tx + 1) * scale + shear) * s);
    for (let x = x0; x < x1; x++) putTexel(t, x, y, rgb, data[i], data[i + 1], data[i + 2]);
  }
}
