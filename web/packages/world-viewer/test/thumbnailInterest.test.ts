import { afterEach, expect, it, vi } from 'vitest';
import { ThumbnailView } from '../src/render/thumbnails';
import type { WorldRuntime } from '../src/render/runtime';

afterEach(() => vi.unstubAllGlobals());

it('stops loading a remote pawn region when its thumbnail leaves the viewport and resumes when it returns', () => {
  let intersect!: (entries: Array<{ isIntersecting: boolean }>) => void;
  vi.stubGlobal(
    'IntersectionObserver',
    class {
      constructor(cb: typeof intersect) {
        intersect = cb;
      }
      observe() {}
      disconnect() {}
    },
  );
  const bot = vi.fn(() => ({ dim: 0, x: 400, y: 64, z: 800 }));
  const rt = { store: { bot } } as unknown as WorldRuntime;
  const thumb = new ThumbnailView(rt, { getContext: () => null } as unknown as HTMLCanvasElement, 'Remote');
  expect(thumb.interest()).toMatchObject({ x: 400, z: 800 });
  intersect([{ isIntersecting: false }]);
  bot.mockClear();
  expect(thumb.interest()).toBeNull();
  expect(bot).not.toHaveBeenCalled();
  intersect([{ isIntersecting: true }]);
  expect(thumb.interest()).toMatchObject({ x: 400, z: 800 });
});
