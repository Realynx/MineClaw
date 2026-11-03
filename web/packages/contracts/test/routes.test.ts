import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import * as C from '../src/index.js';
import { repoRoot } from './helpers.js';

const routes = Object.entries(C.ROUTES) as [C.RouteId, C.RouteDef][];

/** schema object → exported name (every route schema must be a named export, so api.md can cite it). */
const names = new Map<unknown, string>();
for (const [k, v] of Object.entries(C)) if (v instanceof z.ZodType && !names.has(v)) names.set(v, k);

describe('route table', () => {
  it('has unique method+path pairs and ids', () => {
    const seen = new Set<string>();
    for (const [, r] of routes) {
      const key = `${r.method} ${r.path}`;
      expect(seen.has(key), key).toBe(false);
      seen.add(key);
    }
  });

  it('all public paths are under /api and bridge paths under /v1', () => {
    for (const [id, r] of routes) {
      expect(r.path.startsWith('/api/'), id).toBe(true);
      if (r.bridge) expect(r.bridge.path.startsWith('/v1/'), id).toBe(true);
      if (r.bridge === null) expect(r.supervisor, id).toBeNull();
    }
  });

  it('every schema it references is a named export', () => {
    for (const [id, r] of routes) {
      for (const part of ['params', 'query', 'body', 'response'] as const) {
        const s = r[part];
        if (s) expect(names.get(s) ?? '(inline)', `${id}.${part}`).not.toBe('(inline)');
      }
    }
  });

  it('json routes have a response schema, binary ones do not', () => {
    for (const [id, r] of routes) expect(r.response === null, id).toBe(r.responseKind !== 'json');
  });

  it('writes are POST and need a role or session', () => {
    for (const [id, r] of routes) {
      if (r.method === 'POST' && id !== 'auth.login' && id !== 'setup.create') expect(r.access, id).not.toBe('public');
      if (r.path.startsWith('/api/admin/')) expect(r.access, id).toBe('admin');
    }
  });

  it('a specific path is registered before a parameterised sibling', () => {
    const order = routes.map(([, r]) => `${r.method} ${r.path}`);
    const before = (a: string, b: string) => expect(order.indexOf(a)).toBeLessThan(order.indexOf(b));
    before('GET /api/memory/kinds', 'GET /api/memory/:kind');
    before('GET /api/bodies/scripts', 'GET /api/bodies/:body/script');
    before('GET /api/bodies/:body/vision/render', 'GET /api/bodies/:body/vision/:id');
    before('GET /api/bodies/:body/vision/:id/image.png', 'GET /api/bodies/:body/vision/:id');
    before('GET /api/memory-activity/heat', 'GET /api/memory-activity');
  });

  it('builds public and bridge paths', () => {
    expect(C.apiPath('memory.get', { kind: 'facts', id: 'a b' })).toBe('/api/memory/facts/a%20b');
    expect(C.bridgePath('memory.list', { kind: 'claims' }, { sort: 'updated', offset: 0, limit: 50 })).toBe(
      '/v1/memory/claims?sort=updated&offset=0&limit=50',
    );
    expect(C.bridgePath('bots.now', { bot: 'maid_chan' })).toBe('/v1/sup/viewer/now?bot=maid_chan');
    expect(C.bridgePath('memoryActivity.heat', {}, { brain: 'main', windowS: 900 })).toBe(
      '/v1/memory-activity/heat?brain=main&window_s=900',
    );
    expect(C.bridgePath('raptor.graph', {}, { layout: 'radial', colorBy: 'time' })).toBe(
      '/v1/raptor/graph?layout=radial&color_by=time',
    );
    expect(() => C.bridgePath('auth.me')).toThrow();
  });

  it('docs/web/api.md lists every route', () => {
    const md = readFileSync(join(repoRoot, 'docs', 'web', 'api.md'), 'utf8');
    for (const [id, r] of routes) {
      expect(md.includes(`\`${r.method} ${r.path}\``), `${id} missing from api.md`).toBe(true);
    }
    for (const e of C.SSE_EVENT_NAMES) expect(md.includes(`\`${e}\``), `SSE ${e}`).toBe(true);
  });
});

describe('request schemas', () => {
  it('chat body never carries player or allow_commands', () => {
    expect(C.ChatRequest.safeParse({ text: 'hi', requestId: 'r-8f14e45fceea' }).success).toBe(true);
    expect(C.ChatRequest.safeParse({ text: 'hi', requestId: 'r-8f14e45fceea', player: 'poofimafox' }).success).toBe(
      false,
    );
    expect(C.ChatRequest.safeParse({ text: '   ', requestId: 'r-8f14e45fceea' }).success).toBe(false);
  });

  it('memory edit needs an id except for add', () => {
    expect(
      C.MemoryEditRequest.safeParse({ op: 'add', kind: 'fact', fields: { subject: 'Fox', text: 'likes birch' } })
        .success,
    ).toBe(true);
    expect(C.MemoryEditRequest.safeParse({ op: 'delete', kind: 'fact' }).success).toBe(false);
    expect(
      C.MemoryEditRequest.safeParse({ op: 'update', kind: 'fact', id: '12', fields: { text: '' }, who: 'x' }).success,
    ).toBe(false);
  });

  it('coerces query strings with defaults', () => {
    expect(C.MemoryListQuery.parse({ limit: '50' })).toEqual({
      sort: 'updated',
      dir: 'desc',
      dim: 'any',
      offset: 0,
      limit: 50,
    });
    expect(C.MemoryHeatQuery.parse({})).toEqual({ windowS: 900 });
    expect(C.BodyScriptQuery.parse({ code: '1' })).toEqual({ code: true });
    expect(C.WorldChunksQuery.safeParse({ cols: '0,0;-1,2' }).success).toBe(true);
    expect(C.WorldChunksQuery.safeParse({ cols: '0,0;x' }).success).toBe(false);
  });

  it('role checks', () => {
    expect(C.hasRole(['viewer'], 'viewer')).toBe(true);
    expect(C.hasRole(['chat'], 'viewer')).toBe(true);
    expect(C.hasRole(['viewer'], 'chat')).toBe(false);
    expect(C.hasRole(['admin'], 'chat')).toBe(false);
    expect(C.hasRole([], 'authenticated')).toBe(false);
    expect(C.hasRole([], 'public')).toBe(true);
    expect(C.hasRole(['story'], 'story')).toBe(true);
    expect(C.hasRole(['admin'], 'story')).toBe(true);
    expect(C.hasRole(['chat'], 'story')).toBe(false);
  });
});
