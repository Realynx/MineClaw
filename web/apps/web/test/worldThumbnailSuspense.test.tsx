import { Suspense } from 'react';
import { act, render } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import type { WorldSource } from '@mineclaw/world-viewer';

const { views } = vi.hoisted(() => ({ views: [] as Array<{ dispose: ReturnType<typeof vi.fn> }> }));
vi.mock('../../../packages/world-viewer/dist/render/runtime.js', () => ({
  WorldRuntime: class {
    retain() { return this; }
    release() {} replaceSource() {}
  },
}));
vi.mock('../../../packages/world-viewer/dist/render/thumbnails.js', () => ({
  registerThumbnail: () => {
    const view = { dispose: vi.fn(), setFollow() {}, setOptions() {} };
    views.push(view);
    return view;
  },
}));
import { WorldThumbnail, WorldViewerProvider } from '../../../packages/world-viewer/dist/react/WorldThumbnail.js';

it('unregisters previews while Suspense hides a page, and registers them again on reveal', async () => {
  const source = {} as WorldSource;
  const pending = new Promise<never>(() => undefined);
  function Page({ suspend }: { suspend: boolean }) {
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- React Suspense models a pending route.
    if (suspend) throw pending;
    return <WorldViewerProvider source={source} persist={false}><WorldThumbnail follow="109" /></WorldViewerProvider>;
  }
  const tree = (suspend: boolean) => <Suspense fallback="Opening page"><Page suspend={suspend} /></Suspense>;
  const page = render(tree(false));
  expect(views).toHaveLength(1);
  await act(async () => { page.rerender(tree(true)); await Promise.resolve(); });
  expect(views[0]!.dispose).toHaveBeenCalledOnce();
  await act(async () => { page.rerender(tree(false)); await Promise.resolve(); });
  expect(views).toHaveLength(2);
  page.unmount();
  expect(views[1]!.dispose).toHaveBeenCalledOnce();
});
