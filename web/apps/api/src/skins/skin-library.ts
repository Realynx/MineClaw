/**
 * Pawn skins for human browser views: a shared library of skin PNGs (uploaded, or copied from a player's official profile)
 * and the skin each pawn (bot account) wears. Kept in `<web data dir>/skins/`: `pawn-skins.json` (metadata + assignments)
 * and one `<id>.png` per skin, so choices survive reloads and restarts. The game server runs in offline mode, so this never
 * reaches Minecraft clients; HumanSkins serves the assignments to the web world views. Never import into the bridge or AI
 * render pipeline.
 */
import { createHash, randomUUID } from 'node:crypto';
import { closeSync, existsSync, fsyncSync, mkdirSync, openSync, readFileSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  SKIN_LIBRARY_MAX,
  SkinRef,
  type DefaultSkin,
  type PawnSkins,
  type SkinLibraryEntry,
} from '@mineclaw/contracts';
import { Errors } from '../common/errors.js';
import { sanitizeSkinPng, SkinPngError } from './skin-png.js';

const MAX_ASSIGNMENTS = 512;
const ID = /^[a-f0-9]{24}$/;
const NAME = /^[a-z0-9_]{1,16}$/;

type Meta = Omit<SkinLibraryEntry, 'png'>;
interface Stored {
  skins: Meta[];
  assignments: Record<string, SkinRef>;
}

/** One feed row: what the world viewer draws for an account. */
export interface AssignedSkin {
  name: string;
  slim: boolean;
  height: 32 | 64;
  png: string;
  base?: DefaultSkin;
}

/** Writes `text` to `path` through a synced temp file and a rename (no half-written file after a crash). */
export function writeAtomic(path: string, data: string | Buffer): void {
  const temp = path + '.' + randomUUID() + '.tmp';
  try {
    const fd = openSync(temp, 'wx', 0o600);
    try { writeFileSync(fd, data); fsyncSync(fd); } finally { closeSync(fd); }
    renameSync(temp, path);
  } catch {
    if (existsSync(temp)) unlinkSync(temp);
    throw Errors.internal();
  }
}

export class SkinLibrary {
  private readonly dir: string;
  private readonly index: string;
  private readonly skins = new Map<string, SkinLibraryEntry>();
  private assignments = new Map<string, SkinRef>();
  /** Bumped whenever what any pawn wears changes: the viewer feed starts over (a new epoch) when it does. */
  generation = 0;

  constructor(dataDir: string) {
    this.dir = join(dataDir, 'skins');
    this.index = join(this.dir, 'pawn-skins.json');
    this.load();
  }

  private load(): void {
    if (!existsSync(this.index)) return;
    let saved: Partial<Stored>;
    try {
      saved = JSON.parse(readFileSync(this.index, 'utf8')) as Partial<Stored>;
    } catch {
      return; // A damaged index starts an empty library rather than taking the web service down.
    }
    for (const m of Array.isArray(saved.skins) ? saved.skins.slice(0, SKIN_LIBRARY_MAX) : []) {
      if (!m || typeof m.id !== 'string' || !ID.test(m.id)) continue;
      try {
        const { png, height } = sanitizeSkinPng(readFileSync(join(this.dir, `${m.id}.png`)));
        this.skins.set(m.id, {
          id: m.id,
          label: String(m.label ?? '').slice(0, 32),
          slim: m.slim === true && height === 64,
          height,
          png: png.toString('base64'),
          source: m.source === 'player' ? 'player' : 'upload',
          createdAt: String(m.createdAt ?? ''),
          createdBy: typeof m.createdBy === 'string' ? m.createdBy : null,
        });
      } catch { /* A missing or damaged PNG drops that entry. */ }
    }
    for (const [name, ref] of Object.entries(saved.assignments ?? {})) {
      const parsed = SkinRef.safeParse(ref);
      if (NAME.test(name) && parsed.success && this.exists(parsed.data) && this.assignments.size < MAX_ASSIGNMENTS)
        this.assignments.set(name, parsed.data);
    }
  }

  private exists(ref: SkinRef): boolean {
    return ref.kind === 'default' || this.skins.has(ref.id);
  }

  private persist(): void {
    mkdirSync(this.dir, { recursive: true, mode: 0o700 });
    const stored: Stored = {
      skins: [...this.skins.values()].map(({ png: _png, ...meta }) => meta),
      assignments: Object.fromEntries(this.assignments),
    };
    writeAtomic(this.index, JSON.stringify(stored, null, 1) + '\n');
  }

  view(lookup: boolean): PawnSkins {
    return { library: [...this.skins.values()], assignments: Object.fromEntries(this.assignments), lookup };
  }

  /**
   * Adds a skin (the same image is stored once: its id is the hash of the cleaned PNG; adding it again updates the label).
   * 400 for an image that is not a skin, 409 when the library is full.
   */
  add(input: { label: string; slim: boolean; png: Buffer; source: 'upload' | 'player'; by?: string | null }): SkinLibraryEntry {
    let clean;
    try {
      clean = sanitizeSkinPng(input.png);
    } catch (e) {
      if (e instanceof SkinPngError) throw Errors.badRequest([{ path: 'png', message: e.message }], e.message);
      throw e;
    }
    const id = createHash('sha256').update(clean.png).digest('hex').slice(0, 24);
    const prior = this.skins.get(id);
    if (!prior && this.skins.size >= SKIN_LIBRARY_MAX)
      throw Errors.conflict(`The skin library is full (${SKIN_LIBRARY_MAX}); delete a skin first`);
    const entry: SkinLibraryEntry = {
      id,
      label: input.label.trim().slice(0, 32),
      slim: input.slim && clean.height === 64,
      height: clean.height,
      png: clean.png.toString('base64'),
      source: prior?.source ?? input.source,
      createdAt: prior?.createdAt ?? new Date().toISOString(),
      createdBy: prior?.createdBy ?? input.by ?? null,
    };
    mkdirSync(this.dir, { recursive: true, mode: 0o700 });
    if (!prior) writeAtomic(join(this.dir, `${id}.png`), clean.png);
    this.skins.set(id, entry);
    // A slim / label change on a skin someone wears changes how the viewers draw it.
    if (prior && prior.slim !== entry.slim && [...this.assignments.values()].some((r) => r.kind === 'library' && r.id === id))
      this.generation++;
    this.persist();
    return entry;
  }

  /** Removes a skin; the pawns wearing it go back to the default appearance. 404 when it is not in the library. */
  remove(id: string): void {
    if (!this.skins.delete(id)) throw Errors.notFound('No such skin in the library');
    let worn = false;
    for (const [name, ref] of this.assignments)
      if (ref.kind === 'library' && ref.id === id) { this.assignments.delete(name); worn = true; }
    if (worn) this.generation++;
    this.persist();
    const file = join(this.dir, `${id}.png`);
    try { if (existsSync(file)) unlinkSync(file); } catch { /* The index no longer lists it; a stray file is harmless. */ }
  }

  /** Sets (null clears) the skin of each pawn. 404 for a library skin that does not exist. */
  assign(pawns: readonly string[], skin: SkinRef | null): void {
    if (skin && !this.exists(skin)) throw Errors.notFound('No such skin in the library');
    const names = [...new Set(pawns.map((p) => p.toLowerCase()))].filter((n) => NAME.test(n));
    const added = names.filter((n) => !this.assignments.has(n)).length;
    if (skin && this.assignments.size + added > MAX_ASSIGNMENTS) throw Errors.conflict('Too many pawns have skins');
    for (const name of names) {
      if (skin) this.assignments.set(name, skin);
      else this.assignments.delete(name);
    }
    this.generation++;
    this.persist();
  }

  /** What each assigned account looks like, for the human world-view feed. */
  assigned(): AssignedSkin[] {
    const out: AssignedSkin[] = [];
    for (const [name, ref] of this.assignments) {
      if (ref.kind === 'default') {
        out.push({ name, slim: ref.id === 'alex', height: 64, png: '', base: ref.id });
        continue;
      }
      const s = this.skins.get(ref.id);
      if (s) out.push({ name, slim: s.slim, height: s.height, png: s.png });
    }
    return out;
  }

  has(name: string): boolean {
    return this.assignments.has(name.toLowerCase());
  }
}
