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

async function database(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore('packs', { keyPath: 'version' });
    req.onerror = () => reject(req.error);
    req.onsuccess = () => resolve(req.result);
  });
}

export async function readLocalPack(version: GameVersion): Promise<LocalPack | null> {
  if (typeof indexedDB === 'undefined') return null;
  const db = await database();
  try {
    return await new Promise((resolve, reject) => {
      const req = db.transaction('packs').objectStore('packs').get(version);
      req.onsuccess = () => resolve(req.result ?? null);
      req.onerror = () => reject(req.error);
    });
  } finally {
    db.close();
  }
}

export async function writeLocalPack(pack: LocalPack): Promise<void> {
  await mutate((store) => store.put(pack));
  window.dispatchEvent(new CustomEvent(ASSETS_CHANGED, { detail: pack.version }));
}
export async function removeLocalPack(version: GameVersion): Promise<void> {
  await mutate((store) => store.delete(version));
  window.dispatchEvent(new CustomEvent(ASSETS_CHANGED, { detail: version }));
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
