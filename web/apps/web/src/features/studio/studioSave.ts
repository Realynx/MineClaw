/**
 * Saving a design from the Design Studio, and saying how it went: the picture (a failed one never stops the save), the save,
 * a check of the bot's canvas against this page's afterwards with one "resync and save again", the notice line's model, the
 * session a design was left unsaved in, and Ctrl+S. Outside React, so it can be tested without the 3D viewer.
 */
import type { DesignerSaveResponse, DesignerSession } from '@mineclaw/contracts';
import type { Studio } from './studio';

// ------------------------------------------------------------------ the notice line

export type NoticeTone = 'info' | 'ok' | 'warn' | 'error';
export interface NoticeAction {
  label: string;
  run: () => void;
}
export interface StudioNotice {
  text: string;
  tone: NoticeTone;
  action?: NoticeAction;
  /** A save said it: the next save that goes through takes it away (`clearSaveNotice`). */
  fromSave?: true;
}
export interface NoticeOptions {
  tone?: NoticeTone;
  /** Stays until dismissed or replaced by another one that stays (default: errors stay, the rest pass). */
  sticky?: boolean;
  action?: NoticeAction;
  fromSave?: boolean;
}
/** What the notice line shows: the newest notice that stays until dismissed, and the newest passing one. */
export interface Notices {
  sticky: StudioNotice | null;
  passing: StudioNotice | null;
}
export const NO_NOTICES: Notices = { sticky: null, passing: null };

/** The notices after `text` (null clears the passing one; one that stays is only ever dismissed or replaced). */
export function withNotice(cur: Notices, text: string | null, o: NoticeOptions = {}): Notices {
  if (text === null) return cur.passing ? { ...cur, passing: null } : cur;
  const tone = o.tone ?? 'info';
  const notice: StudioNotice = {
    text,
    tone,
    ...(o.action ? { action: o.action } : {}),
    ...(o.fromSave ? { fromSave: true as const } : {}),
  };
  return (o.sticky ?? tone === 'error') ? { ...cur, sticky: notice } : { ...cur, passing: notice };
}

export const dismissSticky = (cur: Notices): Notices => (cur.sticky ? { ...cur, sticky: null } : cur);

/**
 * A save went through: what an earlier save said ("Nothing was saved.", "N cells … not in it" with its resync) no longer
 * holds, so it goes. Other notices that stay (the restore note, another handler's failure) are left.
 */
export const clearSaveNotice = (cur: Notices): Notices => (cur.sticky?.fromSave ? { ...cur, sticky: null } : cur);

// ------------------------------------------------------------------ saving

export interface SaveFlow {
  studio: Pick<Studio, 'save' | 'differsFrom' | 'resync' | 'settle'>;
  /** The design's picture from the current view. */
  capture: () => Promise<string>;
  /** The session as the bot holds it now (`designer.get`). */
  reload: () => Promise<DesignerSession>;
}

export interface SaveOutcome {
  saved: DesignerSaveResponse;
  /** The session read back after the save (for its revision list), when it could be read. */
  session: DesignerSession | null;
  /** Cells on this page that the saved canvas does not have (null: the check could not be made). */
  missing: number | null;
  /** What to tell the reader beyond the saved bar, or null. */
  notice: { text: string; tone: NoticeTone } | null;
  /** Sends the missing cells and saves again: offered once, after the first save only. */
  retry: (() => Promise<SaveOutcome>) | null;
}

const cellCount = (n: number) => (n === 1 ? '1 cell on this page is' : `${n} cells on this page are`);
const PICTURE_FAILED = 'The picture could not be taken; use Update picture.';

/** The picture, or nothing when it could not be taken (a hidden tab draws no frame): the save goes ahead without it. */
const pictureOrNothing = (flow: SaveFlow): Promise<string | undefined> => flow.capture().catch(() => undefined);

/**
 * Saves, then reads the session back and compares the bot's canvas with this page's. The bot saves the canvas it holds, so
 * a cell it lacks is a cell the saved design lacks: say so, and offer to send those cells and save again.
 */
export async function saveDesign(flow: SaveFlow, name: string, summary: string, asNew: boolean): Promise<SaveOutcome> {
  const picture = await pictureOrNothing(flow);
  const saved = await flow.studio.save(name, summary, asNew, picture);
  return checked(flow, name, saved, picture !== undefined, false);
}

/**
 * The one second pass: this page's state of the cells the bot's copy lacks, sent as an edit, then a save of that. The bot's
 * copy is read again first (once this page's edits have reached it): the one the first save's check read is older than any
 * edit made since, and could not be compared.
 */
export async function resyncAndSave(flow: SaveFlow, name: string): Promise<SaveOutcome> {
  await flow.studio.settle('Nothing was saved.');
  await flow.studio.resync(await flow.reload());
  const picture = await pictureOrNothing(flow);
  const saved = await flow.studio.save(name, 'Resynced cells the previous save missed', false, picture);
  return checked(flow, name, saved, picture !== undefined, true);
}

async function checked(
  flow: SaveFlow,
  name: string,
  saved: DesignerSaveResponse,
  pictured: boolean,
  again: boolean,
): Promise<SaveOutcome> {
  const session = await flow.reload().catch(() => null);
  const missing = session ? flow.studio.differsFrom(session) : null;
  const rev = saved.rev ? ` r${saved.rev}` : '';
  const parts: string[] = [];
  if (missing && again)
    parts.push(
      `Saved${rev} again, but ${cellCount(missing)} still not in it. Reload the page to see what the designer holds before building it.`,
    );
  else if (missing) parts.push(`Saved${rev}, but ${cellCount(missing)} not in it.`);
  else if (!pictured) parts.push('Saved.');
  if (!pictured) parts.push(PICTURE_FAILED);
  return {
    saved,
    session,
    missing,
    notice: parts.length ? { text: parts.join(' '), tone: missing ? 'error' : 'warn' } : null,
    retry: missing && session && !again ? () => resyncAndSave(flow, name) : null,
  };
}

// ------------------------------------------------------------------ unsaved work in another session

/**
 * "Open in studio" (`?id=N`) starts a new session from the design's newest revision, so the edits of an earlier session that
 * was never saved stay behind in it. This browser remembers, per design, the last session it left with unsaved changes.
 */
const unsavedWorkKey = (owner: string, designId: number) => `mineclaw.studio.unsaved:${owner}:${designId}`;

type WorkStorage = Pick<Storage, 'getItem' | 'setItem'> & Partial<Pick<Storage, 'removeItem'>>;

/** Remembers that `sessionId` holds unsaved work on the design, or forgets it once clean (only its own entry). */
export function rememberUnsavedWork(
  storage: WorkStorage | null,
  owner: string,
  designId: number,
  sessionId: number,
  dirty: boolean,
): void {
  const key = unsavedWorkKey(owner, designId);
  try {
    if (dirty) storage?.setItem(key, String(sessionId));
    else if (storage?.getItem(key) === String(sessionId)) storage.removeItem?.(key);
  } catch {
    /* Storage that refuses only loses the offer to resume. */
  }
}

/**
 * `rememberUnsavedWork` for a session whose design can change under it ("Save as new" moves it to a new design): the entry
 * it wrote under the design it had before (`before`) is let go first, since that design's work is now saved as the other
 * one. Returns the design the entry is now under, to pass as `before` next time.
 */
export function trackUnsavedWork(
  storage: WorkStorage | null,
  owner: string,
  before: number | null,
  designId: number | null,
  sessionId: number,
  dirty: boolean,
): number | null {
  if (before !== null && before !== designId) rememberUnsavedWork(storage, owner, before, sessionId, false);
  if (designId !== null) rememberUnsavedWork(storage, owner, designId, sessionId, dirty);
  return designId;
}

/** The other session this browser left with unsaved work on the design, or null. */
export function unsavedWorkOn(
  storage: WorkStorage | null,
  owner: string,
  designId: number,
  sessionId: number,
): number | null {
  try {
    const at = Number(storage?.getItem(unsavedWorkKey(owner, designId)) ?? NaN);
    return Number.isInteger(at) && at > 0 && at !== sessionId ? at : null;
  } catch {
    return null;
  }
}

/** The studio on that session (a full load: the page opens one session per visit). */
export const resumeHref = (sessionId: number, designId: number): string =>
  `/designs/studio?session=${sessionId}&id=${designId}`;

// ------------------------------------------------------------------ Ctrl+S

export function isSaveKey(e: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey' | 'shiftKey'>): boolean {
  return (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 's';
}

/**
 * Ctrl+S (Cmd+S) saves the design wherever the focus is, a text field included, instead of the browser saving the page.
 * Held down it saves once. Returns the unbind.
 */
export function bindSaveKey(save: () => void, win: Window = window): () => void {
  const onKey = (e: KeyboardEvent): void => {
    if (!isSaveKey(e)) return;
    e.preventDefault();
    e.stopPropagation();
    if (!e.repeat) save();
  };
  win.addEventListener('keydown', onKey, true);
  return () => win.removeEventListener('keydown', onKey, true);
}
