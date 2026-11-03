import type { GraphNode } from '@mineclaw/contracts';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { baseFrame, hitTest, indexGraph, toScreen, toUnit, walk, zoomAt } from '@/features/graph/geometry';
import { mergeMcwc } from '@/features/world/WorldSource';
import { needsTotp, safeNext } from '@/lib/loginFlow';
import { ApiError } from '@/lib/api/client';

const node = (id: number, parent: number | null, x: number, y: number, extra: Partial<GraphNode> = {}): GraphNode => ({
  id,
  parent,
  layer: parent === null ? 1 : 0,
  isLeaf: parent !== null,
  x,
  y,
  w: 0.5,
  h: 0.5,
  label: String(id),
  terms: [],
  category: 'Fact',
  timeRank: 0,
  cluster: 0,
  weight: 1,
  tokens: 10,
  color: '#336699',
  opacity: 1,
  ...extra,
});

describe('graph geometry', () => {
  const nodes = [node(1, null, 0.5, 0.25, { w: 1 }), node(2, 1, 0.25, 0.75), node(3, 1, 0.75, 0.75)];
  const g = { nodes, edges: [] };

  it('maps unit space to pixels and back through zoom and pan', () => {
    const f = baseFrame('icicle', 1000, 600, 0);
    const v = { k: 2, tx: -100, ty: 50 };
    const [sx, sy] = toScreen(f, v, 0.3, 0.6);
    const [ux, uy] = toUnit(f, v, sx, sy);
    expect(ux).toBeCloseTo(0.3);
    expect(uy).toBeCloseTo(0.6);
  });

  it('zooms about the pointer', () => {
    const v = zoomAt({ k: 1, tx: 0, ty: 0 }, 2, 100, 100);
    expect(v.k).toBe(2);
    expect((100 - v.tx) / v.k).toBeCloseTo(100); // the point under the cursor stays put
  });

  it('hits the topmost (deepest) shape', () => {
    expect(hitTest([...nodes].sort((a, b) => b.layer - a.layer), 'icicle', 0.3, 0.8)?.id).toBe(2);
    expect(hitTest(nodes, 'icicle', 0.5, 0.2)?.id).toBe(1);
    expect(hitTest(nodes, 'icicle', 0.5, 1.2)).toBeNull();
  });

  it('hit-tests radial arcs by angle (0 = up, clockwise)', () => {
    const arc = node(9, null, 0, 0, { a0: 0, a1: Math.PI / 2, r0: 0.1, r1: 0.3 });
    expect(hitTest([arc], 'radial', 0.6, 0.35)?.id).toBe(9); // up-right
    expect(hitTest([arc], 'radial', 0.4, 0.35)).toBeNull(); // up-left
  });

  it('walks the tree with the arrow keys', () => {
    const idx = indexGraph(g);
    expect(walk(idx, null, 'down')).toBe(1);
    expect(walk(idx, 1, 'down')).toBe(2);
    expect(walk(idx, 2, 'right')).toBe(3);
    expect(walk(idx, 3, 'right')).toBe(3);
    expect(walk(idx, 3, 'up')).toBe(1);
  });
});

describe('world source', () => {
  const part = (dim: number, count: number, body: number[]) => {
    const b = new Uint8Array(16 + body.length);
    b.set([0x4d, 0x43, 0x57, 0x43]);
    const v = new DataView(b.buffer);
    v.setUint16(4, 2, true);
    v.setInt32(8, dim, true);
    v.setInt32(12, count, true);
    b.set(body, 16);
    return b.buffer;
  };
  it('merges MCWC payloads of the 64-column batches into one', () => {
    const m = new DataView(mergeMcwc([part(-1, 2, [1, 2, 3]), part(-1, 1, [4])]));
    expect(m.byteLength).toBe(20);
    expect(m.getUint16(4, true)).toBe(2);
    expect(m.getInt32(8, true)).toBe(-1);
    expect(m.getInt32(12, true)).toBe(3);
    expect([16, 17, 18, 19].map((o) => m.getUint8(o))).toEqual([1, 2, 3, 4]);
  });
  it('rejects a payload that is not MCWC', () => {
    expect(() => mergeMcwc([new ArrayBuffer(20), new ArrayBuffer(20)])).toThrow(/MCWC/);
  });
});

describe('login flow', () => {
  it('recognises a TOTP request and keeps redirects on this site', () => {
    expect(needsTotp(new ApiError(401, 'Invalid credentials', { details: [{ path: 'totp', message: 'required' }] }))).toBe(true);
    expect(needsTotp(new ApiError(401, 'Invalid credentials', {}))).toBe(false);
    expect(safeNext('/scripts?name=x')).toBe('/scripts?name=x');
    expect(safeNext('//evil.example')).toBe('/live');
    expect(safeNext('https://evil.example')).toBe('/live');
    expect(safeNext(null)).toBe('/live');
  });
});

describe('generated route types', () => {
  it('are up to date with the contracts', async () => {
    const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
    const gen = (await import('../scripts/gen-route-types.mjs'));
    const current = readFileSync(path.join(here, '..', 'src', 'lib', 'api', 'route-types.gen.ts'), 'utf8');
    expect(current.replace(/\r\n/g, '\n')).toBe(await gen.generate());
  });
});
