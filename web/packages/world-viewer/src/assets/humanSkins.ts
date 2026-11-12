/** Optional human-browser cosmetics. No protocol entity fields, local packs, AI vision renders or disk caches use these assets. */
import * as THREE from 'three';

/** `base`: a pawn given a default skin (drawn from the user's own imported Steve / Alex texture; `png` is empty). */
export interface HumanSkin { name: string; slim: boolean; height: 32 | 64; png: string; base?: 'steve' | 'alex' }
export interface HumanSkinFeed { enabled: boolean; epoch: string; revision: number; skins: HumanSkin[] }
export type HumanSkinSource = (query: { epoch?: string; since: number }) => Promise<HumanSkinFeed>;
const WIDTH = 1024, HEIGHT = 2048, CAPACITY = 512;
const entries = new Map<string, { slim: boolean; height: 32 | 64; slot: number; base?: 'steve' | 'alex' }>();
/** Atlas slots in use (default-skin entries take none). */
let slots = 0;
const listeners = new Set<() => void>();
let source: HumanSkinSource | null = null;
let timer: ReturnType<typeof setTimeout> | undefined;
let epoch: string | undefined;
let revision = 0, generation = 0, sourceGeneration = 0;
let canvas: HTMLCanvasElement | null = null;
let texture: THREE.CanvasTexture | null = null;
let polling = false;
/** A refresh asked for while a poll was in flight: poll again as soon as it ends. */
let again = false;

function changed(): void { generation++; for (const listener of listeners) listener(); }
function clear(): void {
  if (!entries.size) return;
  entries.clear();
  slots = 0;
  canvas?.getContext('2d')?.clearRect(0, 0, WIDTH, HEIGHT);
  if (texture) texture.needsUpdate = true;
  changed();
}

/** Called only by the signed-in web application's lazy viewer boundary; standalone renderers default to no skins. */
export function configureHumanSkins(next: HumanSkinSource | null): void {
  if (next === source) return;
  source = next;
  sourceGeneration++;
  epoch = undefined;
  revision = 0;
  clear();
  clearTimeout(timer);
  if (source && listeners.size) void poll();
}

async function poll(): Promise<void> {
  if (polling || !source || !listeners.size) return;
  polling = true;
  const current = sourceGeneration;
  try {
    if (typeof document !== 'undefined' && document.hidden) return;
    const feed = await source({ epoch, since: revision });
    if (current !== sourceGeneration) return;
    if (feed.epoch !== epoch || !feed.enabled) { clear(); revision = 0; }
    epoch = feed.epoch;
    if (!feed.enabled) return;
    const count = entries.size;
    for (const skin of feed.skins) {
      if (entries.has(skin.name) || entries.size >= CAPACITY) continue;
      if (!/^[a-z0-9_]{1,16}$/.test(skin.name) || skin.png.length > 87384) continue;
      if (skin.base === 'steve' || skin.base === 'alex') {
        entries.set(skin.name, { slim: skin.base === 'alex', height: 64, slot: -1, base: skin.base });
        continue;
      }
      let bitmap: ImageBitmap | undefined;
      try {
        const bytes = Uint8Array.from(atob(skin.png), (c) => c.charCodeAt(0));
        bitmap = await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
        if (current !== sourceGeneration) return;
        if (bitmap.width !== 64 || bitmap.height !== skin.height || ![32, 64].includes(skin.height)) continue;
        if (!canvas) {
          canvas = document.createElement('canvas'); canvas.width = WIDTH; canvas.height = HEIGHT;
          texture = new THREE.CanvasTexture(canvas);
          // Entity UVs have their origin at the top-left, matching the ordinary entity sheet.
          texture.flipY = false;
          texture.magFilter = texture.minFilter = THREE.NearestFilter;
          texture.generateMipmaps = false;
          texture.colorSpace = THREE.NoColorSpace;
        }
        const slot = slots++;
        canvas.getContext('2d')!.drawImage(bitmap, (slot % 16) * 64, Math.floor(slot / 16) * 64);
        entries.set(skin.name, { slim: skin.slim, height: skin.height, slot });
      } catch { /* A malformed profile falls back to the normal player texture. */ }
      finally { bitmap?.close(); }
    }
    revision = feed.revision;
    if (entries.size !== count) {
      if (texture) texture.needsUpdate = true;
      changed();
    }
  } catch { /* Keep already-loaded cosmetics during a temporary gateway outage. */ }
  finally {
    polling = false;
    if (listeners.size) timer = setTimeout(() => void poll(), again ? 0 : 5000);
    again = false;
  }
}

/** Asks every human world view for the newest skins now (no-op when none is shown or no source is configured). */
export function refreshHumanSkins(): void { humanSkins.refresh(); }

/** A single shared atlas keeps skins batched with ordinary player geometry in every human world view. */
export const humanSkins = {
  get generation() { return generation; },
  get texture() { return texture; },
  get(name: string) { return entries.get(name.toLowerCase()); },
  rect(name: string, beta: boolean): [number, number, number, number] | null {
    const entry = entries.get(name.toLowerCase());
    return entry && entry.slot >= 0 ? [(entry.slot % 16) * 64 / WIDTH, Math.floor(entry.slot / 16) * 64 / HEIGHT, 64 / WIDTH, (beta ? 32 : entry.height) / HEIGHT] : null;
  },
  /** Asks the gateway now instead of at the next five-second poll (after the Skin Browser changes a pawn's skin). */
  refresh(): void {
    if (!source || !listeners.size) return;
    if (polling) { again = true; return; }
    clearTimeout(timer);
    void poll();
  },
  subscribe(listener: () => void): () => void {
    listeners.add(listener); listener();
    if (source && listeners.size === 1) void poll();
    return () => { listeners.delete(listener); if (!listeners.size) clearTimeout(timer); };
  },
};
