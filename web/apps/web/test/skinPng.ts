/** Synthetic skin PNGs for tests (no real player skins are committed). Same encoder as apps/api/test/skin-fixtures.ts. */
import { crc32, deflateSync } from 'node:zlib';

function chunk(type: string, data: Buffer): Buffer {
  const head = Buffer.alloc(8);
  head.writeUInt32BE(data.length, 0);
  head.write(type, 4, 'latin1');
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(Buffer.concat([head.subarray(4), data])) >>> 0, 0);
  return Buffer.concat([head, data, crc]);
}

/** An RGBA PNG of `width`×`height` filled with `rgba`; `text` adds a tEXt metadata chunk (which the server strips). */
export function skinPng(opts: { width?: number; height?: number; rgba?: [number, number, number, number]; text?: string } = {}): Buffer {
  const { width = 64, height = 64, rgba = [200, 120, 40, 255], text } = opts;
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  const row = Buffer.alloc(1 + width * 4);
  for (let x = 0; x < width; x++) row.set(rgba, 1 + x * 4);
  const raw = Buffer.concat(Array.from({ length: height }, () => row));
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    ...(text ? [chunk('tEXt', Buffer.from(`Comment\0${text}`, 'latin1'))] : []),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

/** happy-dom cannot decode images: an ImageBitmap stand-in sized from the PNG header. */
export async function fakeImageBitmap(blob: Blob): Promise<ImageBitmap> {
  const head = new DataView(await blob.slice(0, 24).arrayBuffer());
  const bitmap: ImageBitmap = { width: head.getUint32(16), height: head.getUint32(20), close: () => {} };
  return bitmap;
}
