/**
 * A flat front view of a skin (head, body, arms, legs and their overlay layers), drawn from the texture with 2D canvas
 * calls only: no WebGL context per thumbnail, so a page of skins stays light on a software-rendered browser.
 */

/** [sx, sy, w, h] in the texture → [dx, dy] in a 16×32 figure; `flip` mirrors (the 64×32 layout's left limbs). */
export interface SkinPart {
  src: [number, number, number, number];
  dst: [number, number];
  flip?: boolean;
  overlay?: boolean;
}

export const FIGURE_W = 16;
export const FIGURE_H = 32;

/** The parts of the front view, base layers first. */
export function frontParts(height: 32 | 64, slim: boolean): SkinPart[] {
  const arm = slim && height === 64 ? 3 : 4;
  const parts: SkinPart[] = [
    { src: [8, 8, 8, 8], dst: [4, 0] },
    { src: [20, 20, 8, 12], dst: [4, 8] },
    { src: [44, 20, arm, 12], dst: [4 - arm, 8] },
    { src: [4, 20, 4, 12], dst: [4, 20] },
  ];
  if (height === 32) {
    parts.push({ src: [44, 20, 4, 12], dst: [12, 8], flip: true }, { src: [4, 20, 4, 12], dst: [8, 20], flip: true });
    parts.push({ src: [40, 8, 8, 8], dst: [4, 0], overlay: true });
    return parts;
  }
  parts.push({ src: [36, 52, arm, 12], dst: [12, 8] }, { src: [20, 52, 4, 12], dst: [8, 20] });
  parts.push(
    { src: [40, 8, 8, 8], dst: [4, 0], overlay: true },
    { src: [20, 36, 8, 12], dst: [4, 8], overlay: true },
    { src: [44, 36, arm, 12], dst: [4 - arm, 8], overlay: true },
    { src: [52, 52, arm, 12], dst: [12, 8], overlay: true },
    { src: [4, 36, 4, 12], dst: [4, 20], overlay: true },
    { src: [4, 52, 4, 12], dst: [8, 20], overlay: true },
  );
  return parts;
}

/** Draws the front view at `scale` px per texel into `ctx` (cleared first), nearest-neighbour. */
export function drawSkinFront(
  ctx: CanvasRenderingContext2D,
  image: CanvasImageSource,
  opts: { height: 32 | 64; slim: boolean; scale: number },
): void {
  const { scale } = opts;
  ctx.imageSmoothingEnabled = false;
  ctx.clearRect(0, 0, FIGURE_W * scale, FIGURE_H * scale);
  for (const p of frontParts(opts.height, opts.slim)) {
    const [sx, sy, w, h] = p.src;
    const [dx, dy] = p.dst;
    if (!p.flip) {
      ctx.drawImage(image, sx, sy, w, h, dx * scale, dy * scale, w * scale, h * scale);
      continue;
    }
    ctx.save();
    ctx.translate((dx + w) * scale, dy * scale);
    ctx.scale(-1, 1);
    ctx.drawImage(image, sx, sy, w, h, 0, 0, w * scale, h * scale);
    ctx.restore();
  }
}

/** Decodes a base64 PNG once per string (the library re-renders often; the images are tiny). */
const decoded = new Map<string, Promise<ImageBitmap>>();
export function skinBitmap(png: string): Promise<ImageBitmap> {
  let p = decoded.get(png);
  if (!p) {
    // Inside a promise: a bad string or a browser without createImageBitmap rejects instead of throwing.
    p = Promise.resolve().then(() =>
      createImageBitmap(new Blob([Uint8Array.from(atob(png), (c) => c.charCodeAt(0))], { type: 'image/png' })),
    );
    p.catch(() => decoded.delete(png));
    if (decoded.size > 256) decoded.delete(decoded.keys().next().value!);
    decoded.set(png, p);
  }
  return p;
}
