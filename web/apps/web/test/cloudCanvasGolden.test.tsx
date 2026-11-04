import { act, fireEvent, render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MemoryCloudResponse } from '@mineclaw/contracts';
import { CloudCanvas, type CloudPane, type CloudPulse } from '@/features/memory-activity/cloud/CloudCanvas';
import { alignXy, visibleMask } from '@/features/memory-activity/cloud/cloudStyle';
import { memoryCloud } from '@/mocks/data/cloud';

/**
 * Golden frames for the memory cloud renderer: every 2-D canvas call and every WebGL call one frame makes, in order, pinned so the
 * draw passes can be reshaped without changing what is drawn. Buffers are logged by length and a rounded checksum.
 */
const fmt = (v: unknown): string => {
  if (typeof v === 'number') return String(Math.round(v * 100) / 100);
  if (v instanceof Float32Array) {
    let s = 0;
    for (let i = 0; i < v.length; i++) s += v[i]! * ((i % 7) + 1);
    return `f32[${v.length}:${Math.round(s * 10) / 10}]`;
  }
  if (v && typeof v === 'object' && 'tag' in v) return String((v as { tag: string }).tag);
  return JSON.stringify(v);
};

function recorder2d(name: string, log: string[]) {
  return new Proxy(
    {},
    {
      get: (_t, k: string) => {
        if (k === 'measureText') return (s: string) => ({ width: s.length * 6 });
        if (k === 'createRadialGradient')
          return (...a: unknown[]) => {
            log.push(`${name}.createRadialGradient(${a.map(fmt).join(',')})`);
            return { tag: 'grad', addColorStop: (...b: unknown[]) => log.push(`${name}.grad.addColorStop(${b.map(fmt).join(',')})`) };
          };
        return (...a: unknown[]) => void log.push(`${name}.${k}(${a.map(fmt).join(',')})`);
      },
      set: (_t, k: string, v: unknown) => {
        log.push(`${name}.${k}=${fmt(v)}`);
        return true;
      },
    },
  );
}

function recorderGl(log: string[]) {
  let id = 0;
  const ret: Record<string, (...a: unknown[]) => unknown> = {
    getExtension: () => null,
    getParameter: () => 'test renderer',
    getShaderParameter: () => true,
    getProgramParameter: () => true,
    getAttribLocation: (_p, n) => (n === 'a_size' ? 3 : 0),
    getUniformLocation: (_p, n) => ({ tag: `loc:${String(n)}` }),
  };
  return new Proxy(
    {},
    {
      get: (_t, k: string) => {
        if (/^[A-Z_0-9]+$/.test(k)) return k;
        if (k in ret) return ret[k];
        if (k.startsWith('create')) return () => ({ tag: `${k.slice(6)}#${++id}` });
        if (k === 'shaderSource' || k === 'compileShader' || k === 'attachShader' || k === 'linkProgram') return () => undefined;
        return (...a: unknown[]) => void log.push(`gl.${k}(${a.map(fmt).join(',')})`);
      },
    },
  );
}

let log: string[] = [];
let frames = new Map<number, FrameRequestCallback>();
let frameId = 0;
let now = 1000;
const canvasNames = new WeakMap<HTMLCanvasElement, string>();

beforeEach(() => {
  log = [];
  frames = new Map();
  now = 1000;
  vi.spyOn(performance, 'now').mockImplementation(() => now);
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
    frames.set(++frameId, cb);
    return frameId;
  });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      disconnect() {}
    },
  );
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(900);
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(600);
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0 } as DOMRect);
  // happy-dom has no canvas: hand out recorders, named by mount order (under, gl, over).
  let mounted = 0;
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (this: HTMLCanvasElement, kind: string) {
    if (!canvasNames.has(this)) canvasNames.set(this, ['under', 'gl', 'over'][mounted++ % 3]!);
    const name = canvasNames.get(this)!;
    const cache = this as unknown as { __ctx?: unknown };
    cache.__ctx ??= kind === 'webgl2' ? recorderGl(log) : recorder2d(name, log);
    return cache.__ctx as never;
  } as never);
  window.devicePixelRatio = 2;
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

/** Runs the queued animation frames (one round) and returns what they drew. */
function flush(): string[] {
  log.length = 0;
  const run = [...frames.values()];
  frames.clear();
  act(() => run.forEach((f) => f(now)));
  return [...log];
}

function pane(primary: MemoryCloudResponse, c: MemoryCloudResponse, metric: boolean): CloudPane {
  return {
    key: c.arrangement,
    xy: alignXy(primary.points.id, c.points.id, c.points.xy),
    sxy: c.summaries.xy,
    clusters: c.clusters,
    metric,
    unitDistance: metric ? 0.8 : null,
  };
}

function setup(over: Partial<React.ComponentProps<typeof CloudCanvas>> = {}) {
  const cloud = memoryCloud('mds');
  const n = cloud.points.id.length;
  const brightness = new Float32Array(n).map((_, i) => (i % 5) / 4);
  const props: React.ComponentProps<typeof CloudCanvas> = {
    cloud,
    panes: [pane(cloud, cloud, true)],
    brightness,
    theme: 'dark',
    hidden: new Set<number>(),
    highlight: null,
    selectedId: null,
    showLinks: false,
    pulses: [],
    lasso: [],
    onHover: () => undefined,
    onSelect: () => undefined,
    onLasso: () => undefined,
    ...over,
  };
  const r = render(<CloudCanvas {...props} />);
  return { ...r, cloud, props };
}

describe('the memory cloud renderer (golden frames)', () => {
  it('one pane: blobs, points, labels and the scale bar (dark)', () => {
    setup();
    expect(flush()).toMatchSnapshot();
  });

  it('one pane, light: links, a highlight search, the selection ring, the lasso and pulses', () => {
    const cloud = memoryCloud('mds');
    const highlight = new Uint8Array(cloud.points.id.length).map((_, i) => (i % 3 === 0 ? 1 : 0));
    const pulses: CloudPulse[] = [
      { index: 2, at: 900 },
      { index: 4, at: 1200 },
      { index: 6, at: -5000 },
    ];
    setup({
      theme: 'light',
      showLinks: true,
      highlight,
      selectedId: cloud.points.id[3]!,
      lasso: [1, 5, 7],
      pulses,
      hidden: new Set([1]),
    });
    expect(flush()).toMatchSnapshot();
    // the pulse is still fading: another frame was asked for
    expect(frames.size).toBe(1);
  });

  it('a selected summary draws a dashed ring', () => {
    const cloud = memoryCloud('mds');
    setup({ selectedId: cloud.summaries.id[0]! });
    const out = flush();
    expect(out.filter((l) => l.startsWith('over.setLineDash'))).toEqual(['over.setLineDash([3,3])', 'over.setLineDash([])']);
    expect(out).toMatchSnapshot();
  });

  it('compare mode: two panes, scissored, framed, and a switch animation mid-way', () => {
    const cloud = memoryCloud('mds');
    const umap = memoryCloud('umap');
    const { rerender, props } = setup({ panes: [pane(cloud, cloud, true), pane(cloud, umap, false)], showLinks: true });
    expect(flush()).toMatchSnapshot();
    const pca = memoryCloud('pca');
    rerender(<CloudCanvas {...props} panes={[pane(cloud, pca, true), props.panes[1]!]} />);
    now += 300;
    expect(flush()).toMatchSnapshot();
    expect(frames.size).toBe(1);
  });

  it('hover, lasso drag and zoom change the next frame', () => {
    const { container } = setup({ lasso: [5] });
    const first = flush();
    const el = container.firstElementChild as HTMLElement;
    el.setPointerCapture = () => undefined;
    // hover the lassoed point: the hover ring follows
    const at = first.find((l) => /^over\.arc\(.*,5,0,6\.28\)$/.test(l))!;
    const [hx, hy] = at.slice('over.arc('.length).split(',').map(Number);
    fireEvent.pointerMove(el, { clientX: hx, clientY: hy });
    const hovered = flush();
    expect(hovered.some((l) => l.endsWith(',7,0,6.28)'))).toBe(true);
    expect(hovered).toMatchSnapshot();
    fireEvent.wheel(el, { clientX: 400, clientY: 300, deltaY: -300 });
    fireEvent.pointerMove(el, { clientX: 450, clientY: 300 });
    fireEvent.pointerDown(el, { button: 0, clientX: 100, clientY: 100, shiftKey: true });
    fireEvent.pointerMove(el, { clientX: 300, clientY: 120 });
    fireEvent.pointerMove(el, { clientX: 320, clientY: 330 });
    expect(flush()).toMatchSnapshot();
    expect(el.dataset.scale0).toMatch(/\|/);
  });

  it('keys: fit, zoom, pan and Escape (every key the canvas handles)', () => {
    const keys = ['f', 'F', 'Home', '+', '=', '-', '_', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'x', 'Enter'];
    const seen: Record<string, string> = {};
    for (const key of keys) {
      const { container, unmount } = setup();
      flush();
      const el = container.firstElementChild as HTMLElement;
      fireEvent.keyDown(el, { key: '+' }); // a zoomed start, so fit shows
      flush();
      const handled = !fireEvent.keyDown(el, { key });
      const frame = flush();
      const view = frame.filter((l) => l.startsWith('gl.uniform2f(loc:u_scale') || l.startsWith('gl.uniform2f(loc:u_off'));
      seen[key] = `${handled ? 'handled' : 'ignored'} ${view.slice(0, 2).join(' ')}`;
      unmount();
    }
    expect(seen).toMatchSnapshot();
    const onLasso = vi.fn();
    const onSelect = vi.fn();
    const a = setup({ lasso: [1], onLasso, onSelect });
    fireEvent.keyDown(a.container.firstElementChild!, { key: 'Escape' });
    expect(onLasso).toHaveBeenCalledWith([]);
    expect(onSelect).not.toHaveBeenCalled();
    a.unmount();
    const b = setup({ onLasso, onSelect });
    expect(fireEvent.keyDown(b.container.firstElementChild!, { key: 'Escape' })).toBe(false);
    expect(onSelect).toHaveBeenCalledWith(null);
  });

  it('keeps a hidden cluster out of the mask', () => {
    const cloud = memoryCloud('mds');
    expect(visibleMask(cloud, new Set([1])).some((v) => !v)).toBe(true);
  });
});
