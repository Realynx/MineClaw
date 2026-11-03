/** Binary payloads for the mock bridge: real PNGs, MCWC v1 chunk replies (api.md §7) and a store-only `.mcshard` zip. */
import { createHash } from 'node:crypto';
import { crc32, deflateRawSync, deflateSync } from 'node:zlib';

function chunk(type: string, data: Buffer): Buffer {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(td) >>> 0);
  return Buffer.concat([len, td, crc]);
}

/** An RGBA PNG of `w`×`h` with a soft checker/gradient pattern seeded by `seed`. */
export function makePng(w: number, h: number, seed = 0): Buffer {
  const raw = Buffer.alloc((w * 4 + 1) * h);
  const tile = Math.max(4, Math.floor(Math.min(w, h) / 8));
  for (let y = 0; y < h; y++) {
    const row = y * (w * 4 + 1);
    raw[row] = 0;
    for (let x = 0; x < w; x++) {
      const p = row + 1 + x * 4;
      const check = (Math.floor(x / tile) + Math.floor(y / tile) + seed) % 2 === 0;
      raw[p] = check ? 70 + (x * 120) / w : 40;
      raw[p + 1] = check ? 110 + (y * 100) / h : 60 + (seed % 5) * 20;
      raw[p + 2] = check ? 90 : 120;
      raw[p + 3] = 255;
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0);
  ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** One b1.8.1 column (H = 128): bedrock, stone to y 59, dirt, grass at y 63, a few ores; raw DEFLATE of ChunkSnapshot. */
function column(cx: number, cz: number): Buffer {
  const H = 128;
  const V = 16 * H * 16;
  const blocks = Buffer.alloc(V);
  const meta = Buffer.alloc(V / 2);
  for (let lx = 0; lx < 16; lx++) {
    for (let lz = 0; lz < 16; lz++) {
      const top = 62 + ((lx + cx * 3 + lz + cz * 5) % 3 === 0 ? 1 : 0);
      for (let y = 0; y <= top; y++) {
        const idx = (lx * 16 + lz) * 128 + y;
        let id = 1; // stone
        if (y === 0)
          id = 7; // bedrock
        else if (y >= top - 2 && y < top)
          id = 3; // dirt
        else if (y === top)
          id = 2; // grass
        else if (y < 16 && (lx * 7 + lz * 13 + y) % 41 === 0)
          id = 56; // diamond ore
        else if ((lx * 5 + lz * 3 + y) % 23 === 0) id = 16; // coal ore
        blocks[idx] = id;
      }
    }
  }
  return deflateRawSync(Buffer.concat([blocks, meta]));
}

/** `GET /v1/sup/world/chunks?dim&cols=cx,cz;…` → MCWC v1. */
export function makeMcwc(dim: number, cols: Array<[number, number]>, seenMs: number): Buffer {
  const parts: Buffer[] = [];
  const head = Buffer.alloc(16);
  head.write('MCWC', 0, 'ascii');
  head.writeUInt16LE(1, 4);
  head.writeUInt16LE(0, 6);
  head.writeInt32LE(dim, 8);
  head.writeInt32LE(cols.length, 12);
  parts.push(head);
  for (const [cx, cz] of cols) {
    const data = column(cx, cz);
    const by = Buffer.from('maid_chan', 'utf8');
    const rec = Buffer.alloc(4 + 4 + 8 + 8 + 1 + 1);
    rec.writeInt32LE(cx, 0);
    rec.writeInt32LE(cz, 4);
    rec.writeBigInt64LE(createHash('sha256').update(data).digest().readBigInt64LE(0), 8);
    rec.writeBigInt64LE(BigInt(seenMs), 16);
    rec[24] = 1;
    rec[25] = by.length;
    const len = Buffer.alloc(4);
    len.writeInt32LE(data.length);
    parts.push(rec, by, len, data);
  }
  return Buffer.concat(parts);
}

/** A store-only zip (no compression) holding the given files. */
export function makeZip(files: Record<string, string | Buffer>): Buffer {
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  for (const [name, content] of Object.entries(files)) {
    const data = typeof content === 'string' ? Buffer.from(content, 'utf8') : content;
    const nameBuf = Buffer.from(name, 'utf8');
    const crc = crc32(data) >>> 0;
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0x0800, 6); // UTF-8 names
    local.writeUInt16LE(0, 8); // stored
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    locals.push(local, nameBuf, data);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBuf.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, nameBuf);
    offset += 30 + nameBuf.length + data.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  const count = Object.keys(files).length;
  end.writeUInt16LE(count, 8);
  end.writeUInt16LE(count, 10);
  end.writeUInt32LE(cd.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, cd, end]);
}
