import { describe, expect, it, vi } from 'vitest';
import type { ContainerSummary, StoryColonyState, StoryStorage } from '@mineclaw/contracts';
import { ChestBadgeCache } from '@/features/story/chestBadges';
import { MapPainter } from '@/features/story/mapDraw';

describe('chest overlay work', () => {
  it('reuses unchanged derived labels and fill, then updates when storage or inspection changes', () => {
    const cache = new ChestBadgeCache();
    const storage = { label: 'Food', freeSlots: 27, knownAt: 10 } as StoryStorage;
    const first = cache.get('1,64,1', storage, undefined, undefined);
    for (let frame = 0; frame < 120; frame++) expect(cache.get('1,64,1', storage, undefined, undefined)).toBe(first);
    expect(first).toEqual({ text: 'Food', fill: 0 });
    const renamed = { ...storage, label: 'Seeds' };
    expect(cache.get('1,64,1', renamed, undefined, undefined)).toEqual({ text: 'Seeds', fill: 0 });
    const inspected = { source: 'bot', size: 27, used: 27, seenAt: 20 } as unknown as ContainerSummary;
    expect(cache.get('1,64,1', renamed, inspected, undefined)).toEqual({ text: 'Seeds', fill: 1 });
  });

  it('does not repaint offscreen chests and does not reindex an unchanged storage list every frame', () => {
    const p = new MapPainter();
    const mark = vi.spyOn(p, 'mark').mockImplementation(() => undefined);
    const storage = { chest: [0, 0, 0], priority: 'normal' } as StoryStorage;
    const list = [storage];
    const iterator = vi.spyOn(list, Symbol.iterator);
    const state = { storages: list } as unknown as StoryColonyState;
    const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
    const ctx = { fillRect: vi.fn() } as unknown as CanvasRenderingContext2D;
    p.begin(ctx, { view: identity, proj: identity, world: identity, projInv: identity, w: 200, h: 200 }, 10, null, false,
      { x: 0, y: 5, z: 0 }, 200, 200);
    const selected = { chests: [], bench: null }, badges = new Map();
    for (let frame = 0; frame < 60; frame++) p.drawChests([
      { pos: [0, 0, 0], id: 54 }, { pos: [100, 0, 0], id: 54 },
    ], state, selected, badges, 14);
    expect(mark).toHaveBeenCalledTimes(60);
    expect(iterator).toHaveBeenCalledTimes(1);
  });
});
