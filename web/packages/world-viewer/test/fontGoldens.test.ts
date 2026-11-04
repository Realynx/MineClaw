// Behaviour pin for the complexity refactor of render/font.ts and blockEntityTex.charWidth (docs/dev/complexity.md, WP12):
// the fallback advance table over every code point, and seeded random § strings through the font's width, trim, sign cut,
// rich-to-legacy, glyph layout and rasteriser with both versions' measured font tables and font sheets. The digests were
// taken from the code before it was reshaped.

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { PNG } from 'pngjs';
import { describe, expect, it } from 'vitest';
import type { Pack1122, PackBeta } from '../src/assets/bundle';
import { charWidth as fallbackWidth, textWidth } from '../src/render/blockEntityTex';
import { fontTable, layoutLine, rasterLine, richToLegacy, signLineText, stringWidth, trimToWidth, type FontPixels, type FontTable } from '../src/render/font';
import type { SignRun } from '../src/types';
import { Digest, rng } from './digest';

const gen = join(__dirname, '..', 'generated');
const FONTS: Array<[string, FontTable, FontPixels]> = (['1.12.2', 'b1.8.1'] as const).map((v) => {
  const pack = JSON.parse(readFileSync(join(gen, v, 'pack.json'), 'utf8')) as Pack1122 | PackBeta;
  const png = PNG.sync.read(readFileSync(join(gen, v, 'font.png')));
  return [v, fontTable(pack.font, v !== '1.12.2')!, { data: png.data, width: png.width, cell: png.width / 16 }];
});

const CODES = '0123456789abcdefABCDEFklmnorKLMNORxz§ oooO';
function randomText(r: () => number, f: FontTable): string {
  const n = Math.floor(r() * 40);
  let s = '';
  for (let i = 0; i < n; i++) {
    const p = r();
    if (p < 0.15) s += '§' + (r() < 0.9 ? CODES[Math.floor(r() * CODES.length)] : '');
    else if (p < 0.25) s += ' ';
    else if (p < 0.28) s += ['\0', '\n', 'é', '中', '\u{1F600}', 'ÿ'][Math.floor(r() * 6)];
    else s += f.chars[Math.floor(r() * f.chars.length)];
  }
  return s;
}

describe('font goldens', () => {
  it('blockEntityTex.charWidth / textWidth over every code point', () => {
    const d = new Digest();
    let row = '';
    for (let cp = 0; cp <= 0x10ffff; cp++) {
      row += fallbackWidth(String.fromCodePoint(cp)).toString(36);
      if ((cp & 0xfff) === 0xfff) {
        d.add(row);
        row = '';
      }
    }
    d.add(String([fallbackWidth(''), fallbackWidth('ab'), fallbackWidth('§'), textWidth(''), textWidth('Hello, world! [i]'), textWidth('§l\u{1F600}x')]));
    expect(d.hex()).toBe('002d9d89172636e48234d50c4e28b19f');
  });

  it('widths, trims, sign cuts and rich text', () => {
    const d = new Digest();
    for (const [v, f] of FONTS) {
      const r = rng(v === '1.12.2' ? 11 : 12);
      for (let k = 0; k < 3000; k++) {
        const s = randomText(r, f);
        const width = Math.floor(r() * 120) - 5;
        d.add(`${stringWidth(f, s)}|${trimToWidth(f, s, width)}|${signLineText(f, s)}`);
      }
    }
    const r = rng(13);
    const COLORS = [undefined, null, '', 'red', 'gold', 'white', 'black', 'nope', 'light_purple', 'dark_aqua'];
    for (let k = 0; k < 2000; k++) {
      const runs: SignRun[] = [];
      for (let j = Math.floor(r() * 5); j > 0; j--)
        runs.push({
          text: r() < 0.15 ? '' : 'ab§c d'.slice(0, Math.floor(r() * 7)),
          color: COLORS[Math.floor(r() * COLORS.length)],
          bold: r() < 0.3, italic: r() < 0.3, underlined: r() < 0.3, strikethrough: r() < 0.3, obfuscated: r() < 0.3,
        } as SignRun);
      d.add(richToLegacy(k % 97 === 0 ? (k % 2 ? null : undefined) : runs));
    }
    expect(d.hex()).toBe('3ada0d3cf48016c10f543013fa1d88fd');
  });

  it('glyph layout and raster', () => {
    const d = new Digest();
    for (const [v, f, px] of FONTS) {
      const r = rng(v === '1.12.2' ? 21 : 22);
      for (let k = 0; k < 1500; k++) {
        const s = randomText(r, f);
        const x = Math.floor(r() * 20) - 10, base = Math.floor(r() * 0x1000000), seed = Math.floor(r() * 1e9) - 5e8;
        const line = k % 7 === 0 ? layoutLine(f, s, x) : layoutLine(f, s, x, base, k % 11 === 0 ? 0 : seed);
        d.add(JSON.stringify(line));
        if (k % 2 !== 0) continue;
        const sc = 1 + (k % 4), w = 140 * sc, h = 12 * sc;
        const out = new Uint8Array(w * h * 4);
        rasterLine(out, w, h, sc, px, line, Math.floor(r() * 30) - 10 + (k % 2 ? 0.5 : 0), Math.floor(r() * 6) - 2);
        d.bytes(out);
      }
    }
    expect(d.hex()).toBe('7112cfbd1159233e45594e7451f3ceba');
  });
});
