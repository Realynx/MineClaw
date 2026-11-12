/**
 * Skin PNG checks for anything a browser uploads or a profile lookup returns: a real 64×32 / 64×64 PNG whose chunks are
 * intact and whose image data inflates. The stored copy keeps only the chunks a renderer needs (IHDR, PLTE, tRNS, IDAT, IEND),
 * so text / EXIF / colour-profile metadata from the uploader's editor is never kept or served.
 */
import { inflateSync } from 'node:zlib';
import { SKIN_PNG_MAX_BYTES } from '@mineclaw/contracts';

const SIGNATURE = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const KEEP = new Set(['IHDR', 'PLTE', 'tRNS', 'IDAT', 'IEND']);
/** Colour type → channels, and the bit depths PNG allows for it. */
const COLOUR: Record<number, { channels: number; depths: number[] }> = {
  0: { channels: 1, depths: [1, 2, 4, 8, 16] },
  2: { channels: 3, depths: [8, 16] },
  3: { channels: 1, depths: [1, 2, 4, 8] },
  4: { channels: 2, depths: [8, 16] },
  6: { channels: 4, depths: [8, 16] },
};

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(bytes: Buffer): number {
  let c = 0xffffffff;
  for (const b of bytes) c = CRC_TABLE[(c ^ b) & 0xff]! ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

export class SkinPngError extends Error {}

export interface SkinPng {
  png: Buffer;
  height: 32 | 64;
}

const ADAM7 = [[0, 0, 8, 8], [4, 0, 8, 8], [0, 4, 4, 8], [2, 0, 4, 4], [0, 2, 2, 4], [1, 0, 2, 2], [0, 1, 1, 2]] as const;

/** Bytes of the filtered image data (a filter byte per row; per pass row for Adam7). */
function rawSize(width: number, height: number, bits: number, interlace: number): number {
  const rows = (w: number, h: number) => (w > 0 && h > 0 ? h * (1 + Math.ceil((w * bits) / 8)) : 0);
  if (!interlace) return rows(width, height);
  return ADAM7.reduce((n, [x, y, dx, dy]) => n + rows(Math.ceil((width - x) / dx), Math.ceil((height - y) / dy)), 0);
}

/** Throws SkinPngError (with a message fit for the uploader) unless `input` is a usable skin. */
export function sanitizeSkinPng(input: Buffer): SkinPng {
  if (input.length > SKIN_PNG_MAX_BYTES) throw new SkinPngError(`Skin files can be at most ${SKIN_PNG_MAX_BYTES / 1024} KiB`);
  if (input.length < 8 || !input.subarray(0, 8).equals(SIGNATURE)) throw new SkinPngError('Not a PNG image');
  const kept: Buffer[] = [SIGNATURE];
  const idat: Buffer[] = [];
  let header: { width: number; height: number; depth: number; colour: number; interlace: number } | null = null;
  let palette = false;
  let ended = false;
  for (let at = 8; at < input.length; ) {
    if (ended) throw new SkinPngError('Unexpected data after the end of the PNG');
    if (at + 12 > input.length) throw new SkinPngError('The PNG is truncated');
    const length = input.readUInt32BE(at);
    const type = input.toString('latin1', at + 4, at + 8);
    if (!/^[A-Za-z]{4}$/.test(type) || at + 12 + length > input.length) throw new SkinPngError('The PNG is damaged');
    const data = input.subarray(at + 8, at + 8 + length);
    if (crc32(input.subarray(at + 4, at + 8 + length)) !== input.readUInt32BE(at + 8 + length))
      throw new SkinPngError('The PNG is damaged (checksum)');
    const chunk = input.subarray(at, at + 12 + length);
    at += 12 + length;
    if (!header && type !== 'IHDR') throw new SkinPngError('The PNG has no header');
    if (type === 'IHDR') {
      if (header || length !== 13) throw new SkinPngError('The PNG header is invalid');
      header = {
        width: data.readUInt32BE(0),
        height: data.readUInt32BE(4),
        depth: data[8]!,
        colour: data[9]!,
        interlace: data[12]!,
      };
      const c = COLOUR[header.colour];
      if (!c || !c.depths.includes(header.depth) || data[10] !== 0 || data[11] !== 0 || header.interlace > 1)
        throw new SkinPngError('The PNG header is invalid');
      if (header.width !== 64 || (header.height !== 32 && header.height !== 64))
        throw new SkinPngError(`Skins are 64×32 (b1.8.1) or 64×64 (1.12.2); this image is ${header.width}×${header.height}`);
    } else if (type === 'PLTE') palette = true;
    else if (type === 'IDAT') idat.push(data);
    else if (type === 'IEND') ended = true;
    else if (/^[A-Z]/.test(type)) throw new SkinPngError(`Unsupported PNG chunk ${type}`);
    if (KEEP.has(type)) kept.push(chunk);
  }
  if (!header || !ended || !idat.length) throw new SkinPngError('The PNG is truncated');
  if (header.colour === 3 && !palette) throw new SkinPngError('The PNG is missing its palette');
  const c = COLOUR[header.colour]!;
  const expected = rawSize(header.width, header.height, c.channels * header.depth, header.interlace);
  let raw: Buffer;
  try {
    // Bounded, so a decompression bomb stops at the size a 64×64 image can have.
    raw = inflateSync(Buffer.concat(idat), { maxOutputLength: expected });
  } catch {
    throw new SkinPngError('The PNG image data is damaged');
  }
  if (raw.length !== expected) throw new SkinPngError('The PNG image data is damaged');
  return { png: Buffer.concat(kept), height: header.height as 32 | 64 };
}
