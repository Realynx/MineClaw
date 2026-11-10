/**
 * Saving from the Design Studio (studioSave.ts): a picture that cannot be taken never stops the save, the bot's canvas is
 * checked against the page's after every save with one "resync and save again", the notice line keeps failures until they
 * are dismissed, and Ctrl+S saves.
 */
import {
  type DesignerCanvasRequest,
  type DesignerSaveRequest,
  type DesignerSaveResponse,
  type DesignerSession,
  MOCK_DESIGNER_PALETTE,
} from '@mineclaw/contracts';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Cell } from '@/features/studio/canvas';
import { Studio, type StudioApi } from '@/features/studio/studio';
import {
  bindSaveKey,
  clearSaveNotice,
  dismissSticky,
  isSaveKey,
  NO_NOTICES,
  rememberUnsavedWork,
  resumeHref,
  resyncAndSave,
  type SaveFlow,
  saveDesign,
  trackUnsavedWork,
  unsavedWorkOn,
  withNotice,
} from '@/features/studio/studioSave';

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'new design',
  rev: 0,
  blocks: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: 'b1.8.1',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  ...over,
});

/** A bot that keeps the cells it is sent (a canvas of its own) and saves revisions of it. */
function fakeBot(start: Cell[]) {
  const blocks = new Map(start.map((c) => [c.slice(0, 3).join(), c] as const));
  let rev = 0;
  let saves = 0;
  const calls: Array<[string, unknown]> = [];
  const api: StudioApi = {
    canvas: vi.fn((_id: number, body: DesignerCanvasRequest) => {
      calls.push(['canvas', body]);
      for (const e of body.ops)
        for (const c of e.cells) {
          if (c[3] === 0) blocks.delete(c.slice(0, 3).join());
          else blocks.set(c.slice(0, 3).join(), c);
        }
      rev++;
      return Promise.resolve({ rev, applied: 1 });
    }),
    message: () => Promise.reject(new Error('unused')),
    save: vi.fn((_id: number, body: DesignerSaveRequest) => {
      calls.push(['save', body]);
      saves++;
      const r: DesignerSaveResponse = {
        designId: 42,
        rev: saves,
        name: body.name,
        blocks: blocks.size,
        created: saves === 1,
        text: `saved #42 r${saves}`,
      };
      return Promise.resolve(r);
    }),
  };
  return {
    api,
    calls,
    blocks,
    /** The session as `designer.get` returns it. */
    get: (): DesignerSession => session({ designId: 42, rev, blocks: [...blocks.values()] }),
  };
}

const flowOf = (
  studio: Studio,
  bot: ReturnType<typeof fakeBot>,
  capture: () => Promise<string> = () => Promise.resolve('data:image/png;base64,AAAA'),
): SaveFlow => ({
  studio,
  capture,
  reload: () => Promise.resolve(bot.get()),
});

afterEach(() => localStorage.clear());

describe('saving a design', () => {
  it('saves without the picture when it cannot be taken, and says so', async () => {
    const bot = fakeBot([[1, 0, 1, 4, 0]]);
    const s = new Studio(session({ blocks: [[1, 0, 1, 4, 0]] }), bot.api, 60_000);
    const out = await saveDesign(
      flowOf(s, bot, () => Promise.reject(new Error('no frame drawn'))),
      'hut',
      'first',
      false,
    );
    expect(bot.calls).toEqual([['save', { name: 'hut', summary: 'first' }]]);
    expect(out).toMatchObject({ missing: 0, retry: null });
    expect(out.notice).toEqual({ text: 'Saved. The picture could not be taken; use Update picture.', tone: 'warn' });
    expect(s.snapshot().dirty).toBe(false);
    s.dispose();
  });

  it('says nothing more than the saved bar when the picture was taken and the bot holds the page', async () => {
    const bot = fakeBot([[1, 0, 1, 4, 0]]);
    const s = new Studio(session({ blocks: [[1, 0, 1, 4, 0]] }), bot.api, 60_000);
    const out = await saveDesign(flowOf(s, bot), 'hut', '', false);
    expect((bot.calls[0]![1] as DesignerSaveRequest).previewPng).toBe('data:image/png;base64,AAAA');
    expect(out).toMatchObject({ missing: 0, notice: null, retry: null, saved: { rev: 1 } });
    expect(out.session?.designId).toBe(42);
    s.dispose();
  });

  it('a bot canvas missing cells of the page is reported, and resync and save again sends exactly those', async () => {
    // The bot lost the block at 2,0,2 (an edit that never reached it).
    const bot = fakeBot([[1, 0, 1, 4, 0]]);
    const s = new Studio(
      session({
        blocks: [
          [1, 0, 1, 4, 0],
          [2, 0, 2, 4, 0],
        ],
      }),
      bot.api,
      60_000,
    );
    const first = await saveDesign(flowOf(s, bot), 'hut', '', false);
    expect(first.missing).toBe(1);
    expect(first.notice).toEqual({ text: 'Saved r1, but 1 cell on this page is not in it.', tone: 'error' });
    expect(first.retry).not.toBeNull();
    bot.calls.length = 0;
    const second = await first.retry!();
    expect(bot.calls.map((c) => c[0])).toEqual(['canvas', 'save']);
    expect((bot.calls[0]![1] as { ops: Array<{ cells: Cell[] }> }).ops[0]!.cells).toEqual([[2, 0, 2, 4, 0]]);
    expect(bot.calls[1]![1]).toMatchObject({ name: 'hut', summary: 'Resynced cells the previous save missed' });
    expect(second).toMatchObject({ missing: 0, notice: null, retry: null, saved: { rev: 2 } });
    s.dispose();
  });

  it('resync and save again reads the bot again: an edit synced after the first save does not hide the missing cells', async () => {
    const bot = fakeBot([[1, 0, 1, 4, 0]]);
    const s = new Studio(
      session({
        blocks: [
          [1, 0, 1, 4, 0],
          [2, 0, 2, 4, 0],
        ],
      }),
      bot.api,
      60_000,
    );
    const first = await saveDesign(flowOf(s, bot), 'hut', '', false);
    expect(first.missing).toBe(1);
    // One more edit reaches the bot: this page's rev is now past the session the first check read.
    s.edit([[3, 0, 3, 4, 0]], 'place', 'placed');
    await s.flush();
    expect(s.snapshot().rev).toBeGreaterThan(first.session!.rev);
    bot.calls.length = 0;
    const second = await first.retry!();
    const resynced = bot.calls.find((c) => c[0] === 'canvas')![1] as { ops: Array<{ cells: Cell[] }> };
    expect(resynced.ops[0]!.cells).toEqual([[2, 0, 2, 4, 0]]);
    expect(second).toMatchObject({ missing: 0, notice: null, retry: null });
    expect(bot.blocks.has('2,0,2')).toBe(true);
    s.dispose();
  });

  it('a second pass that still differs says so and offers nothing more', async () => {
    const bot = fakeBot([]);
    const s = new Studio(session({ blocks: [[2, 0, 2, 4, 0]] }), bot.api, 60_000);
    // A bot that drops what it is sent for that cell.
    bot.api.canvas = vi.fn((_id: number, body: DesignerCanvasRequest) => {
      bot.calls.push(['canvas', body]);
      return Promise.resolve({ rev: 0, applied: 0 });
    });
    const first = await saveDesign(flowOf(s, bot), 'hut', '', false);
    expect(first.missing).toBe(1);
    const second = await resyncAndSave(flowOf(s, bot), 'hut');
    expect(second.missing).toBe(1);
    expect(second.notice).toEqual({
      text: 'Saved r2 again, but 1 cell on this page is still not in it. Reload the page to see what the designer holds before building it.',
      tone: 'error',
    });
    expect(second.retry).toBeNull();
    s.dispose();
  });

  it('a session that cannot be read back leaves the check out, and a failed save rejects', async () => {
    const bot = fakeBot([]);
    const s = new Studio(session({ blocks: [[2, 0, 2, 4, 0]] }), bot.api, 60_000);
    const out = await saveDesign(
      { ...flowOf(s, bot), reload: () => Promise.reject(new Error('offline')) },
      'hut',
      '',
      false,
    );
    expect(out).toMatchObject({ session: null, missing: null, notice: null, retry: null });
    bot.api.save = () => Promise.reject(new Error('the designer is away'));
    await expect(saveDesign(flowOf(s, bot), 'hut', '', false)).rejects.toThrow('the designer is away');
    s.dispose();
  });
});

describe('the notice line', () => {
  it('keeps failures until dismissed, and passes the rest', () => {
    let n = withNotice(NO_NOTICES, 'Tunnel: 30 cells');
    expect(n).toEqual({ sticky: null, passing: { text: 'Tunnel: 30 cells', tone: 'info' } });
    n = withNotice(n, "Couldn't change the seed: offline", { tone: 'error' });
    expect(n.sticky).toEqual({ text: "Couldn't change the seed: offline", tone: 'error' });
    // Clearing (the start of a save) clears only the passing notice; a later passing one shows beside the failure.
    n = withNotice(n, null);
    expect(n).toEqual({ sticky: { text: "Couldn't change the seed: offline", tone: 'error' }, passing: null });
    n = withNotice(n, 'Copied');
    expect(n.sticky?.tone).toBe('error');
    const run = vi.fn();
    n = withNotice(n, 'Recovered unsaved component changes', {
      tone: 'warn',
      sticky: true,
      action: { label: 'Go', run },
    });
    expect(n.sticky).toEqual({
      text: 'Recovered unsaved component changes',
      tone: 'warn',
      action: { label: 'Go', run },
    });
    expect(withNotice(n, 'Saved.', { tone: 'warn' }).sticky).toBe(n.sticky);
    expect(dismissSticky(n)).toEqual({ sticky: null, passing: { text: 'Copied', tone: 'info' } });
  });

  it('a save that goes through takes away what an earlier save said, and nothing else', () => {
    const failed = withNotice(NO_NOTICES, 'The latest edits have not reached the designer. Nothing was saved.', {
      tone: 'error',
      fromSave: true,
    });
    expect(failed.sticky).toMatchObject({ tone: 'error', fromSave: true });
    expect(clearSaveNotice(failed)).toEqual(NO_NOTICES);
    const missing = withNotice(NO_NOTICES, 'Saved r3, but 2 cells on this page are not in it.', {
      tone: 'error',
      fromSave: true,
      action: { label: 'Resync and save again', run: vi.fn() },
    });
    expect(clearSaveNotice(missing).sticky).toBeNull();
    // The restore note and another handler's failure stay until dismissed.
    const restored = withNotice(NO_NOTICES, 'Recovered unsaved component changes', { tone: 'warn', sticky: true });
    expect(clearSaveNotice(restored)).toBe(restored);
    const seed = withNotice(NO_NOTICES, "Couldn't change the seed: offline", { tone: 'error' });
    expect(clearSaveNotice(seed)).toBe(seed);
  });
});

describe('Ctrl+S', () => {
  const key = (init: KeyboardEventInit) => new KeyboardEvent('keydown', { cancelable: true, bubbles: true, ...init });

  it('saves once per press wherever the focus is, and leaves other keys alone', () => {
    const save = vi.fn();
    const unbind = bindSaveKey(save, window);
    const input = document.createElement('input');
    document.body.append(input);
    const press = key({ key: 's', ctrlKey: true });
    input.dispatchEvent(press);
    expect(save).toHaveBeenCalledTimes(1);
    expect(press.defaultPrevented).toBe(true);
    window.dispatchEvent(key({ key: 'S', metaKey: true }));
    window.dispatchEvent(key({ key: 's', ctrlKey: true, repeat: true }));
    expect(save).toHaveBeenCalledTimes(2);
    const plain = key({ key: 's' });
    window.dispatchEvent(plain);
    expect(plain.defaultPrevented).toBe(false);
    expect(isSaveKey({ key: 's', ctrlKey: true, metaKey: false, altKey: false, shiftKey: true })).toBe(false);
    unbind();
    window.dispatchEvent(key({ key: 's', ctrlKey: true }));
    expect(save).toHaveBeenCalledTimes(2);
    input.remove();
  });
});

describe('unsaved work in an earlier session', () => {
  const memory = () => {
    const m = new Map<string, string>();
    return {
      getItem: (k: string) => m.get(k) ?? null,
      setItem: (k: string, v: string) => void m.set(k, v),
      removeItem: (k: string) => void m.delete(k),
      m,
    };
  };

  it('remembers the last dirty session of a design and offers it to every other session of it', () => {
    const s = memory();
    rememberUnsavedWork(s, 'fox', 7, 41, true);
    expect(unsavedWorkOn(s, 'fox', 7, 42)).toBe(41);
    expect(unsavedWorkOn(s, 'fox', 7, 41)).toBeNull();
    expect(unsavedWorkOn(s, 'fox', 8, 42)).toBeNull();
    expect(unsavedWorkOn(s, 'robin', 7, 42)).toBeNull();
    rememberUnsavedWork(s, 'fox', 7, 43, true);
    expect(unsavedWorkOn(s, 'fox', 7, 42)).toBe(43);
    expect(resumeHref(43, 7)).toBe('/designs/studio?session=43&id=7');
  });

  it('a clean session forgets only its own entry', () => {
    const s = memory();
    rememberUnsavedWork(s, 'fox', 7, 41, true);
    rememberUnsavedWork(s, 'fox', 7, 42, false);
    expect(unsavedWorkOn(s, 'fox', 7, 42)).toBe(41);
    rememberUnsavedWork(s, 'fox', 7, 41, false);
    expect(s.m.size).toBe(0);
  });

  it('a session saved as a new design lets go of the entry under the design it had', () => {
    const s = memory();
    // Session 41 is dirty on design 5, then "Save as new" makes it design 9, clean.
    let under = trackUnsavedWork(s, 'fox', null, 5, 41, true);
    expect(under).toBe(5);
    expect(unsavedWorkOn(s, 'fox', 5, 50)).toBe(41);
    under = trackUnsavedWork(s, 'fox', under, 9, 41, false);
    expect(under).toBe(9);
    expect(unsavedWorkOn(s, 'fox', 5, 50)).toBeNull();
    expect(unsavedWorkOn(s, 'fox', 9, 50)).toBeNull();
    // Another session's entry on the old design is not this session's to remove.
    rememberUnsavedWork(s, 'fox', 5, 44, true);
    under = trackUnsavedWork(s, 'fox', 5, 9, 41, true);
    expect(unsavedWorkOn(s, 'fox', 5, 50)).toBe(44);
    expect(unsavedWorkOn(s, 'fox', 9, 50)).toBe(41);
    expect(under).toBe(9);
  });

  it('storage that is missing or refuses only loses the offer', () => {
    const refusing = {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      },
    };
    expect(() => rememberUnsavedWork(refusing, 'fox', 7, 41, true)).not.toThrow();
    expect(() => rememberUnsavedWork(refusing, 'fox', 7, 41, false)).not.toThrow();
    expect(unsavedWorkOn(refusing, 'fox', 7, 42)).toBeNull();
    expect(unsavedWorkOn(null, 'fox', 7, 42)).toBeNull();
  });
});
