import { expect, it, vi } from 'vitest';
import type { AimTarget } from '@mineclaw/world-viewer';
import type { HoloCell } from '@mineclaw/world-viewer/settings';
import type { StoryColonyState, StoryArchitectEntry } from '@mineclaw/contracts';
import {
  aimEdit,
  editCells,
  editHolograms,
  editPalette,
  editVariants,
  pickEditMaterial,
  FlyEditQueue,
  type EditReply,
  type EditSend,
} from '@/features/story/flyEdit';

const entry = { id: 134, meta: 0, name: 'Spruce stairs', facing: true };
const target = (extra = {}): Extract<AimTarget, { kind: 'block' }> => ({
  kind: 'block',
  x: 0,
  y: 64,
  z: 3,
  face: 2,
  id: 4,
  meta: 0,
  name: 'Cobble',
  distance: 3,
  ...extra,
});
const ghost = (z: number, id = 4): HoloCell => ({ x: 0, y: 64, z, id, meta: 0, build: 'house', state: 'todo' });

it('preserves exact stair, slab, log, furnace and torch metadata, with version-aware alternatives', () => {
  expect(editCells(target({ face: 1 }), 2, entry, 3)).toEqual([[0, 65, 3, 134, 3]]);
  expect(editVariants(entry, '1.12.2').map((v) => v.meta)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);
  expect(editVariants(entry, 'b1.8.1').map((v) => v.meta)).toEqual([0, 1, 2, 3]);
  expect(editVariants({ ...entry, id: 126, meta: 1 }, '1.12.2').map((v) => v.meta)).toEqual([1, 9]);
  expect(editVariants({ ...entry, id: 17, meta: 1 }, '1.12.2').map((v) => v.meta)).toEqual([1, 5, 9]);
  expect(editVariants({ ...entry, id: 17, meta: 1 }, 'b1.8.1').map((v) => v.meta)).toEqual([1]);
  expect(editVariants({ ...entry, id: 61 }, '1.12.2')).toContainEqual({ meta: 2, label: 'north' });
  expect(editVariants({ ...entry, id: 50 }, '1.12.2').map((v) => v.meta)).toEqual([5, 1, 2, 3, 4]);
});

it('offers only supported exact placement families, retaining material variants', () => {
  const ids = [4, 134, 53, 64, 26, 63, 66, 62, 74, 43, 106, 144, 175];
  const palette = ids.map((id) => ({ material: [id, 0], label: `Block ${id}` })) as StoryArchitectEntry[];
  expect(editPalette(palette, '1.12.2').map((e) => e.id)).toEqual([4, 134, 53]);
  expect(editPalette(palette, 'b1.8.1').map((e) => e.id)).toEqual([4, 53]);
});

it('middle-picks material, axis and half from physical blocks or plan holograms', () => {
  const palette = editPalette([
    [134, 0], [17, 0], [17, 1], [126, 0], [126, 3], [35, 0], [35, 14], [61, 0], [107, 0],
  ].map((material) => ({ material, label: material.join(':') })) as StoryArchitectEntry[], '1.12.2');
  for (const [id, meta, material] of [[134, 7, 0], [17, 9, 1], [126, 11, 3], [35, 14, 14]]) {
    expect(pickEditMaterial(target({ id, meta, plan: 'house' }), palette, '1.12.2'))
      .toMatchObject({ entry: { id, meta: material }, meta });
  }
  expect(pickEditMaterial(target({ id: 62, meta: 5 }), palette, '1.12.2'))
    .toMatchObject({ entry: { id: 61 }, meta: 5 });
  expect(pickEditMaterial(target({ id: 107, meta: 7 }), palette, '1.12.2'))
    .toMatchObject({ entry: { id: 107 }, meta: 3 });
  expect(pickEditMaterial(target({ id: 134, meta: 7 }), palette, 'b1.8.1')?.meta).toBe(0);
  expect(pickEditMaterial(target({ id: 64 }), palette, '1.12.2')).toBeNull();
  expect(pickEditMaterial(null, palette, '1.12.2')).toBeNull();
});

it('removes both cells when an existing upper door half is edited', () => {
  expect(editCells(target({ id: 64, meta: 11 }), 0, entry, 0)).toEqual([
    [0, 64, 3, 0, 0],
    [0, 63, 3, 0, 0],
  ]);
});

it('aims at a nearer queued block with the exact face and never through physical or pawn occluders', () => {
  const ray = {
    origin: [0.5, 64.5, 0] as [number, number, number],
    direction: [0, 0, 1] as [number, number, number],
    max: 50,
  };
  expect(aimEdit(target({ z: 6, distance: 6 }), ray, [ghost(3)])).toMatchObject({ z: 3, face: 2, plan: 'house' });
  expect(aimEdit(target({ z: 1, distance: 1 }), ray, [ghost(1, 0), ghost(3)])).toMatchObject({ z: 1, id: 4 });
  const pawn: AimTarget = { kind: 'bot', name: '109', x: 0, y: 64, z: 1, distance: 1 };
  expect(aimEdit(pawn, ray, [ghost(3)])).toEqual(pawn);
  expect(aimEdit(null, { ...ray, direction: [0, 0, -1] }, [ghost(3)])).toBeNull();
});

it('serializes rapid stacked edits and shares the new plan id only along their pending owner', async () => {
  let resolve!: (reply: EditReply) => void;
  const send = vi
    .fn()
    .mockImplementationOnce(
      () =>
        new Promise<EditReply>((r) => {
          resolve = r;
        }),
    )
    .mockResolvedValue({ buildId: 'new-plan', rev: 8 });
  const q = new FlyEditQueue(send, vi.fn());
  q.enqueue([[0, 64, 3, 4, 0]]);
  const owner = q.owner([0, 64, 3])!;
  q.enqueue([[0, 65, 3, 4, 0]], owner);
  expect(send).toHaveBeenCalledTimes(1);
  expect(editHolograms([], q.overlays)).toHaveLength(2);
  resolve({ buildId: 'new-plan', rev: 7 });
  await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(2));
  expect(send.mock.calls[1]?.[1]).toBe('new-plan');
  q.enqueue([[100, 64, 100, 4, 0]]); // unrelated fresh ground starts a separate plan
  await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(3));
  expect(send.mock.calls[2]?.[1]).toBeUndefined();
  q.reconcile({ rev: 8 } as StoryColonyState);
  expect(q.overlays).toHaveLength(0);
});

it('retains a failed edit and dependent additions until retry, without silently dropping them', async () => {
  const send = vi.fn().mockRejectedValueOnce(new Error('503 busy')).mockResolvedValue({ buildId: 'saved', rev: 9 });
  const q = new FlyEditQueue(send, vi.fn());
  q.enqueue([[0, 64, 3, 4, 0]]);
  q.enqueue([[0, 65, 3, 4, 0]], q.owner([0, 64, 3]));
  await vi.waitFor(() => expect(q.error).toBe('503 busy'));
  expect(q.pending).toBe(2);
  expect(send).toHaveBeenCalledTimes(1);
  expect(q.overlays.some((c) => c.failed)).toBe(true);
  q.retry();
  await vi.waitFor(() => expect(q.pending).toBe(0));
  expect(send).toHaveBeenCalledTimes(3);
  expect(send.mock.calls[2]?.[1]).toBe('saved');
  q.reconcile({ rev: 8 } as StoryColonyState);
  expect(q.overlays).toHaveLength(2);
  q.reconcile({ rev: 9 } as StoryColonyState);
  expect(q.overlays).toHaveLength(0);
});

/** A send whose first request waits until the test answers it; later requests answer at once. */
function heldSend(later: EditReply | Error) {
  let answer!: (reply: EditReply) => void;
  const send = vi.fn<EditSend>().mockImplementationOnce(
    () =>
      new Promise<EditReply>((r) => {
        answer = r;
      }),
  );
  if (later instanceof Error) send.mockRejectedValueOnce(later);
  send.mockResolvedValue(later instanceof Error ? { buildId: 'plan', rev: 30 } : later);
  return { send, answer: (reply: EditReply) => answer(reply) };
}

it('sends the queued edits of one owner as one request, the later edit of a cell winning', async () => {
  const { send, answer } = heldSend({ buildId: 'plan', rev: 21 });
  const q = new FlyEditQueue(send, vi.fn());
  q.enqueue([[0, 64, 3, 4, 0]]);
  const owner = q.owner([0, 64, 3])!;
  q.enqueue([[0, 65, 3, 4, 0]], owner); // clicks made while the first edit is out
  q.enqueue([[0, 66, 3, 5, 0]], owner);
  q.enqueue([[0, 65, 3, 0, 0]], owner); // takes back the block placed two clicks ago
  expect(send).toHaveBeenCalledTimes(1);
  answer({ buildId: 'plan', rev: 20 });
  await vi.waitFor(() => expect(q.pending).toBe(0));
  expect(send).toHaveBeenCalledTimes(2);
  expect(send.mock.calls[1]).toEqual([
    [
      [0, 66, 3, 5, 0],
      [0, 65, 3, 0, 0],
    ],
    'plan',
  ]);
  q.reconcile({ rev: 20 } as StoryColonyState);
  expect(q.overlays).toHaveLength(2); // (0,65,3) and (0,66,3)
  q.reconcile({ rev: 21 } as StoryColonyState);
  expect(q.overlays).toHaveLength(0);
});

it('merges only consecutive edits of one owner; another owner goes in its own request', async () => {
  const { send, answer } = heldSend({ rev: 5 });
  const q = new FlyEditQueue(send, vi.fn());
  const a = { id: 'a', token: 'a' };
  const b = { id: 'b', token: 'b' };
  q.enqueue([[0, 64, 0, 4, 0]], a);
  q.enqueue([[1, 64, 0, 4, 0]], a);
  q.enqueue([[2, 64, 0, 4, 0]], a);
  q.enqueue([[3, 64, 0, 4, 0]], b);
  q.enqueue([[4, 64, 0, 4, 0]], a);
  answer({ buildId: 'a', rev: 4 });
  await vi.waitFor(() => expect(q.pending).toBe(0));
  expect(send.mock.calls.map((c) => [c[0].map((cell) => cell[0]), c[1]])).toEqual([
    [[0], 'a'],
    [[1, 2], 'a'],
    [[3], 'b'],
    [[4], 'a'],
  ]);
});

it('a new plan learns its id from its first edit before later edits merge', async () => {
  const { send, answer } = heldSend({ buildId: 'fresh', rev: 9 });
  const q = new FlyEditQueue(send, vi.fn());
  const owner = { token: 'new' };
  q.enqueue([[0, 64, 0, 4, 0]], owner);
  q.enqueue([[1, 64, 0, 4, 0]], owner);
  q.enqueue([[2, 64, 0, 4, 0]], owner);
  answer({ buildId: 'fresh', rev: 8 });
  await vi.waitFor(() => expect(q.pending).toBe(0));
  expect(send).toHaveBeenCalledTimes(2);
  expect(send.mock.calls[0]?.[1]).toBeUndefined();
  expect(send.mock.calls[1]?.[1]).toBe('fresh');
  expect(send.mock.calls[1]?.[0]).toHaveLength(2);
});

it('a merged request that fails keeps every edit in it for Retry', async () => {
  const { send, answer } = heldSend(new Error('503 busy'));
  const q = new FlyEditQueue(send, vi.fn());
  const owner = { id: 'p', token: 'p' };
  q.enqueue([[0, 64, 0, 4, 0]], owner);
  q.enqueue([[1, 64, 0, 4, 0]], owner);
  q.enqueue([[2, 64, 0, 4, 0]], owner);
  answer({ buildId: 'p', rev: 2 });
  await vi.waitFor(() => expect(q.error).toBe('503 busy'));
  expect(send).toHaveBeenCalledTimes(2);
  expect(q.pending).toBe(2);
  expect(q.overlays.filter((c) => c.failed).map((c) => c.cell[0])).toEqual([1, 2]);
  q.retry();
  await vi.waitFor(() => expect(q.pending).toBe(0));
  expect(send).toHaveBeenCalledTimes(3);
  expect(send.mock.calls[2]?.[0]).toEqual([
    [1, 64, 0, 4, 0],
    [2, 64, 0, 4, 0],
  ]);
});

it('a merged request stays within the 4096 cells one edit takes', async () => {
  const { send, answer } = heldSend({ buildId: 'big', rev: 3 });
  const q = new FlyEditQueue(send, vi.fn());
  const owner = { id: 'big', token: 'big' };
  const row = (z: number): Array<[number, number, number, number, number]> =>
    Array.from({ length: 1500 }, (_, x) => [x, 64, z, 4, 0]);
  q.enqueue([[0, 70, 0, 4, 0]], owner);
  for (const z of [1, 2, 3]) q.enqueue(row(z), owner);
  answer({ buildId: 'big', rev: 2 });
  await vi.waitFor(() => expect(q.pending).toBe(0));
  expect(send.mock.calls.map((c) => c[0].length)).toEqual([1, 3000, 1500]);
});

it('retargets distinct queued owners and pinned aliases when an emptied original plan becomes a dedicated edit plan', async () => {
  let resolve!: (reply: EditReply) => void;
  const send = vi
    .fn()
    .mockImplementationOnce(
      () =>
        new Promise<EditReply>((r) => {
          resolve = r;
        }),
    )
    .mockResolvedValue({ buildId: 'edited', rev: 12 });
  const q = new FlyEditQueue(send, vi.fn());
  q.enqueue([[0, 64, 3, 4, 0]], { id: 'old', token: 'old' });
  q.enqueue([[1, 64, 3, 4, 0]], { id: 'old', token: 'old' });
  resolve({ buildId: 'edited', rev: 11 });
  await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(2));
  expect(send.mock.calls[1]?.[1]).toBe('edited');
  expect(q.resolve('old')).toBe('edited');
  q.enqueue([[2, 64, 3, 4, 0]], { id: 'old', token: 'independently-pinned' });
  await vi.waitFor(() => expect(send).toHaveBeenCalledTimes(3));
  expect(send.mock.calls[2]?.[1]).toBe('edited');
});
