import { afterEach, describe, expect, it, vi } from 'vitest';
import { WorldRuntime } from '../src/render/runtime';

type CaptureRuntime = Pick<WorldRuntime, 'capturePng'> & {
  views: Map<number, object>;
  captures: Map<number, unknown>;
  canvas: { height: number };
  finishCapture: (id: number, width: number, height: number) => void;
};
function runtime(): CaptureRuntime {
  return Object.assign(Object.create(WorldRuntime.prototype) as CaptureRuntime, {
    views: new Map([[7, {}]]), captures: new Map(), canvas: { height: 900 },
  });
}
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('rendered-frame camera pictures', () => {
  it('captures the rendered viewport before its buffer clears, preserves aspect ratio, and caps its size', async () => {
    const drawImage = vi.fn();
    const image = { width: 0, height: 0, getContext: () => ({ drawImage }), toDataURL: vi.fn(() => 'data:image/png;base64,YQ==') };
    vi.stubGlobal('document', { createElement: () => image });
    const r = runtime();
    const capture = r.capturePng(7);
    expect(drawImage).not.toHaveBeenCalled();
    r.finishCapture(7, 1200, 600);
    await expect(capture).resolves.toBe('data:image/png;base64,YQ==');
    expect([image.width, image.height]).toEqual([1200, 600]);
    expect(drawImage).toHaveBeenCalledWith(r.canvas, 0, 300, 1200, 600, 0, 0, 1200, 600);
    expect(r.captures.size).toBe(0);
  });

  it('shrinks detailed frames to fit the bounded request instead of sending an oversized PNG', async () => {
    const image = { width: 0, height: 0, getContext: () => ({ drawImage: vi.fn() }), toDataURL: () => 'data:image/png;base64,' + 'a'.repeat(image.width > 960 ? 2_000_000 : 100) };
    vi.stubGlobal('document', { createElement: () => image });
    const r = runtime();
    const capture = r.capturePng(7);
    r.finishCapture(7, 1200, 600);
    expect((await capture).length).toBeLessThan(2_000_000);
    expect(image.width).toBe(960);
  });

  it('rejects a missing view or duplicate picture and times out when no frame arrives', async () => {
    vi.useFakeTimers();
    const r = runtime();
    await expect(r.capturePng(9)).rejects.toThrow('not ready');
    const capture = r.capturePng(7);
    const rejected = expect(capture).rejects.toThrow('did not draw');
    await expect(r.capturePng(7)).rejects.toThrow('already');
    await vi.advanceTimersByTimeAsync(5000);
    await rejected;
    expect(r.captures.size).toBe(0);
  });
});
