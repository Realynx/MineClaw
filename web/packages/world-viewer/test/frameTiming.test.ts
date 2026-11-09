import { describe, expect, it, vi } from 'vitest';
import { FrameTiming } from '../src/render/frameTiming';

function fixture(extension = true) {
  let clock = 0, ready = false, disjoint = false;
  const gl = {
    QUERY_RESULT_AVAILABLE: 1, QUERY_RESULT: 2,
    getExtension: vi.fn(() => extension ? { TIME_ELAPSED_EXT: 3, GPU_DISJOINT_EXT: 4 } : null),
    createQuery: vi.fn(() => ({})), beginQuery: vi.fn(), endQuery: vi.fn(), deleteQuery: vi.fn(),
    getQueryParameter: vi.fn((_q, p) => p === 1 ? ready : 25e6), getParameter: vi.fn(() => disjoint),
    readPixels: vi.fn(() => { throw new Error('blocking readback'); }), finish: vi.fn(() => { throw new Error('blocking GPU wait'); }),
  };
  return { gl, timer: new FrameTiming(gl as unknown as WebGL2RenderingContext, () => clock),
    at: (t: number) => { clock = t; }, ready: () => { ready = true; }, disjoint: () => { disjoint = true; } };
}

describe('nonblocking adaptive quality timing', () => {
  it('never reads a GPU result before completion or starts overlapping measurements', () => {
    const f = fixture(); expect(f.timer.begin()).toBe(true); f.at(5);
    expect(f.timer.end(1000)).toBeNull();
    expect(f.timer.poll(1016)).toBeNull(); expect(f.timer.begin()).toBe(false);
    expect(f.gl.getQueryParameter.mock.calls.every((c) => c[1] === 1)).toBe(true);
    f.ready(); expect(f.timer.poll(1032)).toBe(25);
    expect(f.gl.deleteQuery).toHaveBeenCalledOnce();
    expect(f.gl.readPixels).not.toHaveBeenCalled(); expect(f.gl.finish).not.toHaveBeenCalled();
  });
  it('uses CPU timing without a GPU timer extension', () => {
    const f = fixture(false); f.timer.begin(); f.at(7);
    expect(f.timer.end(1000)).toBe(7); expect(f.gl.createQuery).not.toHaveBeenCalled();
  });
  it('discards invalid GPU timing and bounds outstanding-query lifetime', () => {
    const f = fixture(); f.timer.begin(); f.at(4); f.timer.end(1000); f.ready(); f.disjoint();
    expect(f.timer.poll(1016)).toBe(4);
    const stalled = fixture(); stalled.timer.begin(); stalled.at(3); stalled.timer.end(1000);
    expect(stalled.timer.poll(3001)).toBe(3);
    expect(stalled.gl.getQueryParameter.mock.calls.every((c) => c[1] === 1)).toBe(true);
    expect(stalled.gl.deleteQuery).toHaveBeenCalledOnce();
    stalled.timer.begin(); stalled.timer.end(4000); stalled.timer.dispose();
    expect(stalled.gl.deleteQuery).toHaveBeenCalledTimes(2);
  });
});
