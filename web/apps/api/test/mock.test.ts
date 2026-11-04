/** `--mock-bridge`: the synthetic stream validates against SSE_EVENTS, and a chat line resolves the way the web will do it. */
import { CloudDistanceResponse, MemoryCloudResponse, SSE_EVENTS, type SseEventName } from '@mineclaw/contracts';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { z } from 'zod';
import { fetchLogin, ORIGIN, readSse, startApp, type TestApp } from './helpers.js';

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
