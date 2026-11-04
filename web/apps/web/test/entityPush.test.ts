import type { WorldEntities } from '@mineclaw/contracts';
import { QueryClient } from '@tanstack/react-query';
import { normalizeEntities } from '@mineclaw/world-viewer';
import { EntityStore } from '@mineclaw/world-viewer/testing';
import { describe, expect, it } from 'vitest';
import { createWorldSource } from '@/features/world/WorldSource';
import { applyLiveEvent } from '@/lib/live/dispatch';

/**
 * Live 11-04 "players are moving around slowly": how often a walking bot's drawn target moves in the browser, fed by the shared
 * stream's `entities` frames at the bridge's 5 Hz. A frame where only bots moved keeps the entity revision (bots aren't part of
 * it); such frames were dropped by the cache merge, and the viewer polled the cache on its own 200 ms beat. Now every frame is
 * merged, pushed to the viewer (`WorldSource.onEntities`) and read once (numbered frames).
 */
describe('entity frames reach the viewer at the stream pace', () => {
  const bot = (x: number) => ({ name: 'maid_chan_101', short: '101', main: false, dim: 0, x, y: 64, z: 0, hp: 20, food: 20 });

  it('a bot that walks while every mob stands still moves on every frame', async () => {
    const qc = new QueryClient();
    const source = createWorldSource(qc);
    let now = 0;
    const store = new EntityStore(() => now);
    const targets: number[] = [];
    let reads = 0;
    const read = async () => {
      reads++;
      store.apply(normalizeEntities(await source.entities(store.rev > 0 ? store.rev : undefined)));
      const b = store.bots()[0];
      if (b && (targets.length === 0 || targets[targets.length - 1] !== b.dto.x)) targets.push(b.dto.x);
    };
    let pending: Promise<void> = Promise.resolve();
    const off = source.onEntities!(() => {
      pending = pending.then(read);
    });
    // First frame: full; then 10 s of 5 Hz frames at the same entity revision (nothing but the bot moved).
    const frames = 50;
    for (let i = 0; i <= frames; i++) {
      now = i * 200;
      const f: WorldEntities = { at: 1_700_000_000_000 + now, rev: 42, full: i === 0, bots: [bot(i * 0.86)], entities: [], removed: [] };
      applyLiveEvent(qc, 'entities', f);
      await pending;
      // The viewer's backstop poll in between reads the same frame: nothing changes.
      if (i % 5 === 2) {
        await read();
      }
    }
    off();
    const perSecond = (targets.length - 1) / (frames * 0.2);
    // eslint-disable-next-line no-console -- the measured rate, for the record
    console.log(`bot targets applied: ${targets.length - 1} in ${frames * 0.2} s = ${perSecond.toFixed(1)}/s (frames 5/s), ${reads} reads`);
    expect(perSecond).toBeGreaterThanOrEqual(4.9);
    expect(store.intervalMs).toBeCloseTo(200, 0); // the glide follows the frames, not the reads in between
  });
});
