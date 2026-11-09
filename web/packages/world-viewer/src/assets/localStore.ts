import type { Pack } from './bundle';
import type { GameVersion } from '../world/blocks';

export interface LocalPack {
  version: GameVersion;
  name: string;
  importedAt: number;
  pack: Pack;
  images: Record<string, Blob>;
  sounds: Record<string, Blob>;
  soundEvents: Record<string, unknown>;
}
const DB = 'mineclaw-local-game-assets';
export const ASSETS_CHANGED = 'mineclaw-assets-changed';
// Imported resources belong to this browser origin, including other windows and its installed app.
const channel = typeof window !== 'undefined' && typeof window.BroadcastChannel !== 'undefined'
  ? new window.BroadcastChannel(ASSETS_CHANGED) : null;
if (channel) channel.onmessage = ({ data }: MessageEvent<unknown>) => {
  if (data === '1.12.2' || data === 'b1.8.1') window.dispatchEvent(new CustomEvent(ASSETS_CHANGED, { detail: data }));
};
function changed(version: GameVersion): void {
  window.dispatchEvent(new CustomEvent(ASSETS_CHANGED, { detail: version }));
  channel?.postMessage(version);
}

async function database(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore('packs', { keyPath: 'version' });
    req.onerror = () => reject(req.error ?? new Error('Could not open saved game assets on this device.'));
    req.onsuccess = () => resolve(req.result);
  });
}

export async function readLocalPack(version: GameVersion): Promise<LocalPack | null> {
  if (typeof indexedDB === 'undefined') return null;
  const db = await database();
  try {
    return await new Promise((resolve, reject) => {
      const req = db.transaction('packs').objectStore('packs').get(version);
      req.onsuccess = () => {
        const stored: unknown = req.result;
        resolve(stored === null || stored === undefined ? null : stored as LocalPack);
      };
      req.onerror = () => reject(req.error ?? new Error('Could not read saved game assets on this device.'));
    });
  } finally {
    db.close();
  }
}

export async function writeLocalPack(pack: LocalPack): Promise<void> {
  await mutate((store) => store.put(pack));
  // Ask the browser to keep the imported files under storage pressure. Saving still works if it declines.
  if (typeof navigator !== 'undefined') void navigator.storage?.persist?.().catch(() => false);
  changed(pack.version);
}
export async function removeLocalPack(version: GameVersion): Promise<void> {
  await mutate((store) => store.delete(version));
  changed(version);
}
async function mutate(action: (store: IDBObjectStore) => void): Promise<void> {
  const db = await database();
  try {
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction('packs', 'readwrite');
      tx.oncomplete = () => resolve();
      tx.onabort = tx.onerror = () => reject(tx.error ?? new Error('Could not save game assets on this device.'));
      action(tx.objectStore('packs'));
    });
  } finally {
    db.close();
  }
}
