/** `--mock-bridge`: the synthetic stream validates against SSE_EVENTS, and a chat line resolves the way the web will do it. */
import { CloudDistanceResponse, MemoryCloudResponse, SSE_EVENTS, type SseEventName } from '@mineclaw/contracts';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { z } from 'zod';
import { CAMP, topAt } from '../src/mock/terrain.js';
import { fetchLogin, login, ORIGIN, readSse, startApp, type TestApp } from './helpers.js';

let t: TestApp;
beforeAll(async () => {
  t = await startApp({ bridge: { mock: true } }, { mockIntervalMs: 40 });
});
afterAll(async () => {
  await t.close();
});

describe('mock bridge', () => {
  it('memory cloud: a viewer gets the camelCase layout through the gateway; the RAPTOR tree route is gone', async () => {
    const s = await fetchLogin(t.url, 'vera');
    const res = await fetch(`${t.url}/api/memory-activity/cloud`, { headers: { cookie: s.cookie } });
    expect(res.status).toBe(200);
    const cloud = MemoryCloudResponse.parse(await res.json());
    expect(cloud.status).toBe('ready');
    expect(cloud.points.id).toHaveLength(3000);
    expect(cloud.points.xy).toHaveLength(6000);
    expect(cloud.clusters.length).toBe(10);
    expect(cloud.clusterLayer).toBe(2);
    expect((await fetch(`${t.url}/api/memory-activity/cloud`)).status).toBe(401);
    expect((await fetch(`${t.url}/api/raptor/tree`, { headers: { cookie: s.cookie } })).status).toBe(404);
    // the default arrangement is the distance layout, with its fidelity and every arrangement listed
    expect(cloud.arrangement).toBe('mds');
    expect(cloud.fidelity?.unitDistance).toBeGreaterThan(0);
    expect(cloud.arrangements.map((a) => a.id)).toEqual(['mds', 'umap', 'pca', 'tree']);
  });

  it('memory cloud: each arrangement over the same points; the embedding distance route', async () => {
    const s = await fetchLogin(t.url, 'vera');
    const get = async (q: string) => fetch(`${t.url}/api/memory-activity/cloud${q}`, { headers: { cookie: s.cookie } });
    const mds = MemoryCloudResponse.parse(await (await get('')).json());
    const umap = MemoryCloudResponse.parse(await (await get('?arrangement=umap')).json());
    expect(umap.arrangement).toBe('umap');
    expect(umap.points.id).toEqual(mds.points.id);
    expect(umap.points.xy).not.toEqual(mds.points.xy);
    expect((await get('?arrangement=tsne')).status).toBe(400);

    const [a, b] = [mds.points.id[0]!, mds.points.id[1500]!];
    const res = await fetch(`${t.url}/api/memory-activity/cloud/distance?a=${a}&b=${b}`, { headers: { cookie: s.cookie } });
    expect(res.status).toBe(200);
    const d = CloudDistanceResponse.parse(await res.json());
    expect(d.metric).toBe('cosine');
    expect(d.distance).toBeGreaterThan(0);
    expect(
      (await fetch(`${t.url}/api/memory-activity/cloud/distance?a=${a}&b=99999999`, { headers: { cookie: s.cookie } })).status,
    ).toBe(404);
    expect(
      (await fetch(`${t.url}/api/memory-activity/cloud/distance?a=x&b=1`, { headers: { cookie: s.cookie } })).status,
    ).toBe(400);
    expect((await fetch(`${t.url}/api/memory-activity/cloud/distance?a=${a}&b=${b}`)).status).toBe(401);
  });

  it('streams every topic, each frame valid against its schema', async () => {
    const s = await fetchLogin(t.url, 'vera');
    const ac = new AbortController();
    const res = await fetch(`${t.url}/api/live/stream`, { headers: { cookie: s.cookie }, signal: ac.signal });
    const want = ['bots', 'now', 'entities', 'event', 'memoryActivity', 'hive', 'feed', 'status', 'story'];
    const frames = await readSse(res.body!, (f) => want.every((w) => f.some((x) => x.event === w)), 6000);
    ac.abort();
    for (const w of want)
      expect(
        frames.some((f) => f.event === w),
        w,
      ).toBe(true);
    for (const f of frames) {
      const r = (SSE_EVENTS[f.event as SseEventName] as z.ZodType).safeParse(f.data);
      expect(r.success, `${f.event}: ${JSON.stringify(f.data).slice(0, 200)}`).toBe(true);
    }
  });

  it('story commands: a formation in one request (all or none), an add without targets, rev on bed and bench answers', async () => {
    const s = await login(t.url, 'sky');
    const post = (action: string, body: unknown) =>
      s.agent
        .post(`/api/story/colonies/story-1/${action}`)
        .set('Origin', ORIGIN)
        .set('X-CSRF-Token', s.csrf)
        .send(body as object);
    // The mock colony keeps working while the test runs, so the test reads the orders the colony recorded (they stay,
    // done or not), never a pawn's queue.
    const colony = async () =>
      (await s.agent.get('/api/story/colonies/story-1').expect(200)).body as {
        rev: number;
        pawns: Array<{ name: string }>;
        orders: Array<{ id: string; pawn: string; kind: string; target: unknown }>;
      };
    const [a, b] = (await colony()).pawns.map((p) => p.name) as [string, string];
    const at = (x: number) => ({ x, y: 64, z: 12 });
    const to = (pawn: string, x: number) => ({ pawn, target: at(x) });

    const moved = await post('orders', { moves: [to(a, 20), to(b, 22)] });
    expect(moved.status).toBe(200);
    expect(moved.body).toEqual({ ok: true, ids: [expect.any(String), expect.any(String)], rev: expect.any(Number) });
    const ids = (moved.body as { ids: string[] }).ids;
    const given = (await colony()).orders.filter((o) => ids.includes(o.id));
    expect(given.map((o) => [o.pawn, o.kind, o.target])).toEqual([
      [a, 'move', at(20)],
      [b, 'move', at(22)],
    ]);

    // A pawn the colony does not have: the bot's own words, and no order for the pawn named before it.
    const orderCount = async () => (await colony()).orders.length;
    const before = await orderCount();
    const refused = await post('orders', { moves: [to(b, 30), to('nobody', 31)] });
    expect(refused.status).toBe(400);
    expect(refused.body).toEqual({ error: "no pawn 'nobody'", code: 'bad_request' });
    // A formation mixed with a group order is refused by the contract, before the bridge.
    const mixed = await post('orders', { moves: [to(a, 20)], pawns: [a], order: { kind: 'hold' } });
    expect(mixed.status).toBe(400);
    expect((mixed.body as { error: string }).error).toBe('invalid request');
    expect(await orderCount()).toBe(before);

    const mine = (x: number) => ({ op: 'add', kind: 'mine', blocks: [[x, 60, 300]] });
    const quiet = await post('designations', { ...mine(300), includeTargets: false });
    expect(quiet.status).toBe(200);
    expect(quiet.body).toEqual({ ok: true, count: 1, rev: expect.any(Number) });
    const echoed = await post('designations', mine(301));
    expect((echoed.body as { targets?: unknown }).targets).toEqual([[301, 60, 300]]);

    const bench = await post('workbenches', { op: 'designate', pos: [17, 63, 7] });
    expect(bench.status).toBe(200);
    const bed = await post('beds', {
      op: 'unassign',
      bed: { x: CAMP[0] - 3, y: topAt(CAMP[0] - 3, CAMP[2] + 3) + 1, z: CAMP[2] + 3 },
    });
    expect(bed.status).toBe(200);
    // Each answer names the revision the colony was at when it took the change: they never go back, and the state follows.
    const revs = [quiet, bench, bed].map((r) => (r.body as { rev?: number }).rev);
    expect(revs.every((r) => Number.isInteger(r))).toBe(true);
    expect(revs).toEqual([...revs].sort((x, y) => x! - y!));
    expect(revs[2]).toBeGreaterThan(revs[0]!);
    expect((await colony()).rev).toBeGreaterThanOrEqual(revs[2]!);
  });

  it('chat: {accepted, id} where id is the viewer line seq; the bot reply is a later `said` line', async () => {
    const s = await fetchLogin(t.url, 'robin');
    const ac = new AbortController();
    const res = await fetch(`${t.url}/api/live/stream?topics=feed&bots=maid_chan`, {
      headers: { cookie: s.cookie },
      signal: ac.signal,
    });
    const reading = readSse(
      res.body!,
      (f) =>
        f.some(
          (x) =>
            x.event === 'feed' && (x.data as { lines: Array<{ kind: string }> }).lines.some((l) => l.kind === 'said'),
        ),
      6000,
    );
    const post = await fetch(`${t.url}/api/bots/maid_chan/chat`, {
      method: 'POST',
      headers: { cookie: s.cookie, Origin: ORIGIN, 'X-CSRF-Token': s.csrf, 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: 'where is the sand?', requestId: 'r-mock0001' }),
    });
    expect(post.status).toBe(202);
    const accepted = (await post.json()) as { accepted: boolean; id: number; feedSeq?: number };
    expect(accepted.accepted).toBe(true);
    const frames = await reading;
    ac.abort();
    const lines = frames
      .filter((f) => f.event === 'feed')
      .flatMap((f) => (f.data as { lines: Array<Record<string, unknown>> }).lines);
    const viewer = lines.find((l) => l.kind === 'viewer' && l.requestId === 'r-mock0001');
    expect(viewer).toMatchObject({ seq: accepted.id, from: 'kiwi', origin: 'viewer', bot: 'maid_chan' });
    const reply = lines.find((l) => l.kind === 'said' && (l.seq as number) > accepted.id);
    expect(reply?.from).toBe('maid_chan');
  });
});
