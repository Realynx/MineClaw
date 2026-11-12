/**
 * Checks a skin file in the browser before it is previewed or uploaded: a PNG of 64×32 (b1.8.1 layout) or 64×64 (1.12.2
 * layout) within the size limit. The server checks it again (and strips metadata chunks) on upload.
 */
import { SKIN_PNG_MAX_BYTES } from '@mineclaw/contracts';

export const SKIN_MAX_KIB = SKIN_PNG_MAX_BYTES / 1024;
const SIGNATURE = [137, 80, 78, 71, 13, 10, 26, 10];

export type SkinHeader = { ok: true; width: 64; height: 32 | 64 } | { ok: false; error: string };

/** Reads the PNG signature and IHDR: is this a skin-sized PNG? */
export function readSkinHeader(bytes: Uint8Array): SkinHeader {
  if (bytes.length > SKIN_PNG_MAX_BYTES) return { ok: false, error: `Skin files can be at most ${SKIN_MAX_KIB} KiB` };
  if (bytes.length < 33 || SIGNATURE.some((b, i) => bytes[i] !== b)) return { ok: false, error: 'Not a PNG image' };
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const type = String.fromCharCode(...bytes.subarray(12, 16));
  if (view.getUint32(8) !== 13 || type !== 'IHDR') return { ok: false, error: 'The PNG header is invalid' };
  const width = view.getUint32(16), height = view.getUint32(20);
  if (width !== 64 || (height !== 32 && height !== 64))
    return { ok: false, error: `Skins are 64×32 (b1.8.1) or 64×64 (1.12.2); this image is ${width}×${height}` };
  return { ok: true, width: 64, height };
}

export function toBase64(bytes: Uint8Array): string {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

export function fromBase64(b64: string): Uint8Array {
  return Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
}

/**
 * Slim (3-pixel, "Alex") arms or classic: in a slim 64×64 skin the right arm's outer column (x 54-55 on its front row) is
 * empty. Classic for 64×32 skins, which have no slim layout.
 */
export function looksSlim(rgba: Uint8ClampedArray, height: 32 | 64): boolean {
  if (height !== 64 || rgba.length < 64 * 64 * 4) return false;
  const alpha = (x: number, y: number) => rgba[(y * 64 + x) * 4 + 3]!;
  return alpha(54, 20) === 0 && alpha(55, 20) === 0 && alpha(54, 31) === 0;
}

/** A label from the file name: "my_cool-skin (1).png" → "my_cool-skin (1)", at most 32 characters. */
export function labelFromFileName(name: string): string {
  return name.replace(/\.png$/i, '').trim().slice(0, 32) || 'Skin';
}

export interface CheckedSkin {
  png: string;
  height: 32 | 64;
  slim: boolean;
  label: string;
}

/**
 * The whole browser-side check of a picked file: type, size, header, and that it decodes as an image of that size.
 * Resolves the base64 PNG and a slim guess, or rejects with a message for the user.
 */
export async function checkSkinFile(file: File): Promise<CheckedSkin> {
  if (file.type && file.type !== 'image/png') throw new Error('Choose a PNG image');
  if (file.size > SKIN_PNG_MAX_BYTES) throw new Error(`Skin files can be at most ${SKIN_MAX_KIB} KiB`);
  const bytes = new Uint8Array(await file.arrayBuffer());
  const header = readSkinHeader(bytes);
  if (!header.ok) throw new Error(header.error);
  let slim = false;
  if (typeof createImageBitmap === 'function') {
    let bitmap: ImageBitmap | undefined;
    try {
      bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
    } catch {
      throw new Error('The PNG could not be decoded');
    }
    try {
      if (bitmap.width !== 64 || bitmap.height !== header.height) throw new Error('The PNG could not be decoded');
      const canvas = typeof document !== 'undefined' ? document.createElement('canvas') : null;
      const ctx = canvas?.getContext('2d', { willReadFrequently: true });
      if (canvas && ctx) {
        canvas.width = 64;
        canvas.height = header.height;
        ctx.drawImage(bitmap, 0, 0);
        slim = looksSlim(ctx.getImageData(0, 0, 64, header.height).data, header.height);
      }
    } finally {
      bitmap.close();
    }
  }
  return { png: toBase64(bytes), height: header.height, slim, label: labelFromFileName(file.name) };
}
