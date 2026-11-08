import { afterEach, expect, it } from 'vitest';
import { createWorldSource } from '@/features/world/WorldSource';
import { setFetch } from '@/lib/api/client';

afterEach(() => setFetch((...args) => globalThis.fetch(...args)));

it('releases all world request slots on departure and permits fresh reads on return', async () => {
  const signals: AbortSignal[] = [];
  setFetch((_url, init) => new Promise((_resolve, reject) => {
    const signal = init!.signal!;
    signals.push(signal);
    signal.addEventListener('abort', () => reject(new DOMException('View closed', 'AbortError')));
  }));
  const source = createWorldSource();
  const requests = Promise.allSettled([
    source.index(0, 0, 0, 32), source.chunks(0, [[0, 0]]), source.entities(), source.overlays(0),
    source.blockEntities!(0, [[0, 0]]), source.blocks!(),
  ]);
  expect(signals).toHaveLength(6);
  source.cancelPending!();
  expect(signals.every((signal) => signal.aborted)).toBe(true);
  expect((await requests).every((r) => r.status === 'rejected' && r.reason instanceof Error && r.reason.name === 'AbortError')).toBe(true);
  const next = source.index(0, 0, 0, 32);
  expect(signals.at(-1)?.aborted).toBe(false);
  source.cancelPending!();
  await expect(next).rejects.toHaveProperty('name', 'AbortError');
});
