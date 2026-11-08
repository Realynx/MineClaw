import { afterEach, expect, it, vi } from 'vitest';
import type { AssetBundle } from '../src/assets/bundle';
import { blockIconAtlas, blockIconAtlasAsync } from '../src/render/icons';

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('builds icons in bounded idle slices and shares the same work and result across palette openers', async () => {
  const callbacks: Array<() => void> = [];
  vi.stubGlobal('requestIdleCallback', (callback: () => void) => { callbacks.push(callback); return callbacks.length; });
  let time = 0;
  vi.spyOn(performance, 'now').mockImplementation(() => time += 5);
  const context = { setTransform: vi.fn(), filter: '' };
  const create = vi.fn(() => ({ getContext: () => context, toDataURL: () => 'data:image/png;base64,atlas' }));
  vi.stubGlobal('document', { createElement: create });
  // Missing model data is deliberately tolerated: its placeholder still has a stable atlas slot.
  const bundle = { version: 'b1.8.1', pack: {} } as AssetBundle;
  const entries = Array.from({ length: 12 }, (_, id) => ({ id, meta: 0 }));
  const first = blockIconAtlasAsync(bundle, entries, 29);
  expect(blockIconAtlasAsync(bundle, [...entries], 29)).toBe(first);
  expect(context.setTransform).not.toHaveBeenCalled();
  callbacks.shift()!();
  expect(context.setTransform).toHaveBeenCalledTimes(1);
  expect(callbacks).toHaveLength(1);
  while (callbacks.length) callbacks.shift()!();
  const atlas = await first;
  expect(atlas.index.size).toBe(12);
  expect(await blockIconAtlasAsync(bundle, entries, 29)).toBe(atlas);
  expect(blockIconAtlas(bundle, entries, 29)).toBe(atlas);
  expect(create).toHaveBeenCalledOnce();
});

it('does not let a pre-import idle job overwrite or remove the replacement atlas job', async () => {
  vi.resetModules();
  const events = new EventTarget(), callbacks: Array<() => void> = [];
  vi.stubGlobal('window', events);
  vi.stubGlobal('requestIdleCallback', (callback: () => void) => { callbacks.push(callback); return callbacks.length; });
  let count = 0;
  vi.stubGlobal('document', { createElement: () => {
    const url = `atlas-${++count}`;
    return { getContext: () => ({}), toDataURL: () => url };
  } });
  const { blockIconAtlasAsync: build } = await import('../src/render/icons');
  const bundle = { version: 'b1.8.1', pack: {} } as AssetBundle;
  const old = build(bundle, []);
  events.dispatchEvent(new Event('mineclaw-assets-changed'));
  const fresh = build(bundle, []);
  callbacks.shift()!(); await old;
  expect(build(bundle, [])).toBe(fresh);
  callbacks.shift()!();
  expect((await fresh).url).toBe('atlas-2');
  expect(await build(bundle, [])).toBe(await fresh);
  const other = build({ ...bundle }, []);
  callbacks.shift()!();
  expect((await other).url).toBe('atlas-3');
});
