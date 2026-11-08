import { Suspense, useEffect } from 'react';
import { act, render, screen } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import type { WorldSource } from '@mineclaw/world-viewer';

const { engines } = vi.hoisted(() => ({ engines: [] as Array<{ dispose: ReturnType<typeof vi.fn>; suspend: ReturnType<typeof vi.fn>; resume: ReturnType<typeof vi.fn> }> }));
vi.mock('../../../packages/world-viewer/dist/render/engine.js', () => ({
  WorldViewerEngine: class {
    dispose = vi.fn();
    suspend = vi.fn();
    resume = vi.fn();
    constructor() { engines.push(this); }
    setCallbacks() {} setSource() {} setDim() {} setFollow() {} setControls() {} setLayers() {}
    setXRay() {} setDrawDistance() {} setMarker() {} setHighlighted() {}
  },
}));
vi.mock('../../../packages/world-viewer/dist/react/WorldThumbnail.js', () => ({ useWorldRuntime: () => undefined }));
import { WorldViewer } from '@mineclaw/world-viewer';

it('releases the live renderer when navigation suspends the old page, before passive effects unmount', async () => {
  const passiveCleanup = vi.fn();
  const source = {} as WorldSource;
  const pending = new Promise<never>(() => undefined);
  function Page({ suspend }: { suspend: boolean }) {
    useEffect(() => passiveCleanup, []);
    // eslint-disable-next-line @typescript-eslint/only-throw-error -- React Suspense waits on the thrown route promise.
    if (suspend) throw pending;
    return <WorldViewer source={source} />;
  }
  const tree = (suspend: boolean) => <Suspense fallback={<p>Opening page</p>}><Page suspend={suspend} /></Suspense>;
  const view = render(tree(false));
  expect(engines).toHaveLength(1);
  await act(async () => { view.rerender(tree(true)); await Promise.resolve(); });
  expect(screen.getByText('Opening page')).toBeVisible();
  expect(passiveCleanup).not.toHaveBeenCalled();
  expect(engines[0]!.suspend).toHaveBeenCalledOnce();
  expect(engines[0]!.dispose).not.toHaveBeenCalled();
  await act(async () => { view.rerender(tree(false)); await Promise.resolve(); });
  expect(engines).toHaveLength(1);
  expect(engines[0]!.resume).toHaveBeenCalledOnce();
  expect(engines[0]!.dispose).not.toHaveBeenCalled();
  view.unmount();
  expect(engines[0]!.dispose).toHaveBeenCalledOnce();
});
