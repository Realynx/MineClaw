/**
 * Mock gateway for the Skin Browser: the library and each pawn's skin, kept in sessionStorage so a reload in mock mode keeps
 * them (the real gateway keeps them in its data directory). Same checks as the server: PNG, 64×32 / 64×64, size limit.
 */
import { SKIN_LIBRARY_MAX, type PawnSkins, type SkinLibraryEntry, type SkinRef } from '@mineclaw/contracts';
import { fromBase64, readSkinHeader } from '@/features/story/skins/skinFile';

const KEY = 'mcx-mock-skins';
let state: { library: SkinLibraryEntry[]; assignments: Record<string, SkinRef>; generation: number } | null = null;

type Fail = (status: number, message: string) => Error;

function load() {
  if (state) return state;
  try {
    const raw = typeof sessionStorage !== 'undefined' ? sessionStorage.getItem(KEY) : null;
    if (raw) state = JSON.parse(raw) as typeof state;
  } catch { /* storage blocked: start empty */ }
  state ??= { library: [], assignments: {}, generation: 0 };
  return state;
}

function save() {
  try { sessionStorage.setItem(KEY, JSON.stringify(state)); } catch { /* memory only */ }
}

/** 24 hex digits from the bytes (FNV-1a in three lanes; the real server uses SHA-256). */
function idOf(bytes: Uint8Array): string {
  const lanes = [0x811c9dc5, 0x01000193 ^ 0x5bd1e995, 0x9e3779b9];
  for (const b of bytes) for (let i = 0; i < 3; i++) lanes[i] = Math.imul(lanes[i]! ^ (b + i), 0x01000193) >>> 0;
  return lanes.map((x) => x.toString(16).padStart(8, '0')).join('');
}

export function mockSkinsView(lookup: boolean): PawnSkins {
  const s = load();
  return { library: s.library, assignments: s.assignments, lookup };
}

export function mockSkinUpload(body: { label: string; slim?: boolean; png: string }, by: string, fail: Fail, source: 'upload' | 'player' = 'upload') {
  const s = load();
  const bytes = fromBase64(body.png);
  const header = readSkinHeader(bytes);
  if (!header.ok) throw fail(400, header.error);
  const id = idOf(bytes);
  const prior = s.library.find((x) => x.id === id);
  if (!prior && s.library.length >= SKIN_LIBRARY_MAX) throw fail(409, 'The skin library is full; delete a skin first');
  const skin: SkinLibraryEntry = {
    id, label: body.label.trim().slice(0, 32), slim: !!body.slim && header.height === 64, height: header.height, png: body.png,
    source: prior?.source ?? source, createdAt: prior?.createdAt ?? new Date().toISOString(), createdBy: prior?.createdBy ?? by,
  };
  s.library = prior ? s.library.map((x) => (x.id === id ? skin : x)) : [...s.library, skin];
  save();
  return skin;
}

export function mockSkinDelete(id: string, fail: Fail) {
  const s = load();
  if (!s.library.some((x) => x.id === id)) throw fail(404, 'No such skin in the library');
  s.library = s.library.filter((x) => x.id !== id);
  for (const [name, ref] of Object.entries(s.assignments)) if (ref.kind === 'library' && ref.id === id) delete s.assignments[name];
  s.generation++;
  save();
}

export function mockSkinAssign(pawns: string[], skin: SkinRef | null, fail: Fail) {
  const s = load();
  if (skin?.kind === 'library' && !s.library.some((x) => x.id === skin.id)) throw fail(404, 'No such skin in the library');
  for (const p of pawns) {
    if (skin) s.assignments[p.toLowerCase()] = skin;
    else delete s.assignments[p.toLowerCase()];
  }
  s.generation++;
  save();
}

/** The world views' feed: only the pawn skins (mock mode has no official profiles). */
export function mockSkinFeed(official: boolean, query: { epoch?: string }) {
  const s = load();
  const epoch = `mock.${official ? 1 : 0}.${s.generation}`;
  const skins = query.epoch === epoch ? [] : Object.entries(s.assignments).flatMap(([name, ref]) => {
    if (ref.kind === 'default') return [{ name, slim: ref.id === 'alex', height: 64 as const, png: '', base: ref.id }];
    const e = s.library.find((x) => x.id === ref.id);
    return e ? [{ name, slim: e.slim, height: e.height, png: e.png }] : [];
  });
  return { enabled: official || Object.keys(s.assignments).length > 0, epoch, revision: 0, skins };
}

/** Test reset (fixture tests share one module). */
export function resetMockSkins() {
  state = { library: [], assignments: {}, generation: 0 };
  try { sessionStorage.removeItem(KEY); } catch { /* none */ }
}
