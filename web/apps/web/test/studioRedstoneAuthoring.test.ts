/**
 * A redstone design's own metadata in the studio's state (components, probes, breakpoints, simulator settings): an edit of
 * it alone is unsaved work, a save sends it, and what was sent becomes the saved copy.
 */
import { MOCK_DESIGNER_PALETTE, type DesignerSaveRequest, type DesignerSession } from '@mineclaw/contracts';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Studio, type StudioApi } from '@/features/studio/studio';

type Saved = Awaited<ReturnType<StudioApi['save']>>;

const session = (over: Partial<DesignerSession> = {}): DesignerSession => ({
  id: 7,
  owner: 'poofimafox',
  model: 'main',
  designId: null,
  name: 'adder',
  rev: 0,
  blocks: [],
  anchor: null,
  messages: [],
  costUsd: 0,
  calls: 0,
  version: '1.12.2',
  palette: MOCK_DESIGNER_PALETTE,
  plot: { size: 64, height: 48, depth: 16 },
  revisions: [],
  kind: 'redstone',
  ...over,
});

const authoring = (label: string) => JSON.stringify({ redstone: 1, components: [], probes: [{ id: 'p1', label }] });
const saved: Saved = { designId: 5, rev: 1, name: 'adder', blocks: 1, created: true, text: 'saved' };

const studios: Studio[] = [];
function make(over: Partial<DesignerSession> = {}) {
  const save = vi.fn((_id: number, _body: DesignerSaveRequest) => Promise.resolve(saved));
  const api: StudioApi = { canvas: () => Promise.resolve({ rev: 1, applied: 1 }), message: vi.fn(), save };
  const s = new Studio(session(over), api, 60_000);
  studios.push(s);
  return { s, api, save };
}
afterEach(() => studios.splice(0).forEach((s) => s.dispose()));

describe('redstone authoring in the studio state', () => {
  it('a redstone session draws on the build plot', () => {
    const { s } = make();
    expect([s.mine, s.terrain]).toEqual([false, null]);
    expect(s.edit([[5, -1, 5, 0, 0]], 'remove', 'dug the grass')).toEqual([[5, -1, 5, 0, 0]]);
  });

  it('the opened revision is clean; changing the metadata alone is unsaved work; changing it back is clean', () => {
    const { s } = make();
    expect(s.dirty).toBe(false);
    s.setRedstoneAuthoring(authoring('sum'), true);
    expect(s.snapshot().dirty).toBe(false);
    s.setRedstoneAuthoring(authoring('carry'));
    expect(s.snapshot().dirty).toBe(true);
    s.setRedstoneAuthoring(authoring('sum'));
    expect(s.snapshot().dirty).toBe(false);
  });

  it('metadata a design did not have before is unsaved work', () => {
    const { s } = make();
    s.setRedstoneAuthoring(authoring('sum'));
    expect(s.snapshot().dirty).toBe(true);
  });

  it('tells its subscribers once per change and not at all for the same text again', () => {
    const { s } = make();
    const heard = vi.fn();
    s.subscribe(heard);
    s.setRedstoneAuthoring(authoring('sum'));
    s.setRedstoneAuthoring(authoring('sum'));
    expect(heard).toHaveBeenCalledTimes(1);
    s.setRedstoneAuthoring(authoring('carry'));
    expect(heard).toHaveBeenCalledTimes(2);
  });

  it('a save sends the metadata, and what was sent is the saved copy from then on', async () => {
    const { s, save } = make();
    s.edit([[5, 0, 5, 55, 0]], 'place', 'dust');
    s.setRedstoneAuthoring(authoring('sum'));
    await s.save('adder');
    expect(save.mock.calls[0]![1]).toEqual({ name: 'adder', redstoneAuthoring: authoring('sum') });
    expect(s.dirty).toBe(false);
    s.setRedstoneAuthoring(authoring('carry'));
    expect(s.dirty).toBe(true);
    s.setRedstoneAuthoring(authoring('sum'));
    expect(s.dirty).toBe(false);
  });

  it('a change made while the save is on its way stays unsaved', async () => {
    const { s, api } = make();
    let done: () => void = () => undefined;
    const save = vi.fn(
      (_id: number, _body: DesignerSaveRequest) =>
        new Promise<Saved>((r) => {
          done = () => r(saved);
        }),
    );
    api.save = save;
    s.setRedstoneAuthoring(authoring('sum'));
    const saving = s.save('adder');
    await vi.waitFor(() => expect(save).toHaveBeenCalled());
    s.setRedstoneAuthoring(authoring('carry'));
    done();
    await saving;
    expect(save.mock.calls[0]![1].redstoneAuthoring).toBe(authoring('sum'));
    expect(s.dirty).toBe(true);
  });

  it('a save that fails leaves the metadata unsaved', async () => {
    const { s, api } = make();
    api.save = () => Promise.reject(new Error('the bot is not reachable'));
    s.setRedstoneAuthoring(authoring('sum'));
    await expect(s.save('adder')).rejects.toThrow('not reachable');
    expect(s.dirty).toBe(true);
  });

  it('a design without redstone metadata saves as it always did', async () => {
    const { s, save } = make({ kind: 'build' });
    s.edit([[5, 0, 5, 4, 0]], 'place', 'cobble');
    await s.save('hut');
    expect(save.mock.calls[0]![1]).toEqual({ name: 'hut' });
    expect(s.dirty).toBe(false);
  });

  it('is tracked apart from the mine metadata', async () => {
    const { s, save } = make();
    s.setMineAuthoring('{"modules":[]}', true);
    s.setRedstoneAuthoring(authoring('sum'), true);
    expect(s.dirty).toBe(false);
    s.setRedstoneAuthoring(authoring('carry'));
    expect(s.dirty).toBe(true);
    await s.save('adder');
    expect(save.mock.calls[0]![1]).toEqual({
      name: 'adder',
      mineAuthoring: '{"modules":[]}',
      redstoneAuthoring: authoring('carry'),
    });
    expect(s.dirty).toBe(false);
    s.setMineAuthoring('{"modules":[1]}');
    expect(s.dirty).toBe(true);
  });
});

describe('a build <-> redstone switch', () => {
  const revision = (kind: string) => ({ rev: 3, created: 1, n: 1, kind });
  const opened = { designId: 12, designRevision: 3, blocks: [[5, 0, 5, 1, 0]] as DesignerSession['blocks'] };

  it('is itself unsaved work until it is saved, and switching back is clean again', async () => {
    const { s, save } = make({ ...opened, kind: 'redstone', revisions: [revision('build')] });
    expect(s.dirty).toBe(true);
    save.mockResolvedValueOnce({ ...saved, designId: 12, rev: 4 });
    await s.save('gate');
    expect(s.dirty).toBe(false);
    expect(make({ ...opened, kind: 'build', revisions: [revision('build')] }).s.dirty).toBe(false);
    expect(make({ ...opened, kind: 'redstone', revisions: [revision('redstone')] }).s.dirty).toBe(false);
    // A bot that names no kind per revision, and a design never saved, have nothing to compare with.
    expect(make({ ...opened, kind: 'redstone', revisions: [{ rev: 3, created: 1, n: 1 }] }).s.dirty).toBe(false);
    expect(make({ kind: 'redstone', revisions: [revision('build')] }).s.dirty).toBe(false);
  });

  it('keeps the edits made before it unsaved on the new page, and an edit taken back there is clean', () => {
    const before = make({ ...opened, kind: 'build', revisions: [revision('build')] }).s;
    before.edit([[6, 0, 5, 55, 0]], 'place', 'dust');
    before.edit([[5, 0, 5, 0, 0]], 'remove', 'stone');
    expect(before.dirty).toBe(true);

    // The bot answers with the same cells under the other kind; the page remounts on them.
    const cells = before.canvas.cells();
    const after = make({ ...opened, blocks: cells, kind: 'build', revisions: [revision('build')] }).s;
    expect(after.dirty).toBe(false);
    after.adoptUnsaved(before);
    expect(after.dirty).toBe(true);
    after.edit([[6, 0, 5, 0, 0]], 'remove', 'dust');
    expect(after.dirty).toBe(true);
    after.edit([[5, 0, 5, 1, 0]], 'place', 'stone');
    expect(after.dirty).toBe(false);
  });
});
