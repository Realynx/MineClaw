import { readFileSync } from 'node:fs';
import { DESIGNER_CANVAS_BATCH_BYTES, DESIGNER_CANVAS_BODY_CAP, toSnake } from '@mineclaw/contracts';
import { afterAll, beforeAll, expect, it } from 'vitest';
import { bodyLimitFor } from '../src/mock/mock-bridge.js';
import { login, ORIGIN, PERSONAS, startApp, type Session, type TestApp } from './helpers.js';

let app: TestApp, user: Session;
beforeAll(async () => { app = await startApp({ bridge: { mock: true } }); user = await login(app.url, PERSONAS.admin.username); });
afterAll(async () => { await app.close(); });
const post = (path: string) => user.agent.post(path).set('Origin', ORIGIN).set('X-CSRF-Token', user.csrf);

it('transports bounded large mine layouts through save while retaining normal route body limits', async () => {
  const made = await post('/api/designer/sessions').send({ kind: 'mine', seed: '4242', version: '1.12.2' }).expect(200);
  const id = made.body.id as number;
  await post(`/api/designer/sessions/${id}/canvas`).send({ rev: 0, ops: [{ kind: 'dig', cells: [[30, 20, 30, 0, 0]] }] }).expect(200);
  const mineAuthoring = '{"size":256,"modules":[],"base":[],"overrides":[]' + ' '.repeat(1_100_000) + '}';
  const saved = await post(`/api/designer/sessions/${id}/save`).send({ name: 'components', mineAuthoring }).expect(200);
  const opened = await post('/api/designer/sessions').send({ designId: saved.body.designId }).expect(200);
  expect(opened.body.mineAuthoring).toBe(mineAuthoring);
  await post(`/api/designer/sessions/${id}/canvas`).send({ rev: 1, unused: mineAuthoring }).expect(413);
  await post(`/api/designer/sessions/${id}/save`).send({ name: 'too large', mineAuthoring: ' '.repeat(2_000_001) }).expect(400);
});

/** A canvas request of `cells` cells, each as long as a mine cell gets (21 bytes with its comma), in edits of at most 20 000. */
function canvasBody(cells: number) {
  const cell = (i: number) => [100 + (i % 156), 127, 100 + (Math.floor(i / 156) % 156), 252, 15];
  const all = Array.from({ length: cells }, (_, i) => cell(i));
  const ops = [];
  for (let i = 0; i < all.length; i += 20_000)
    ops.push({ kind: 'dig', label: 'a floor', cells: all.slice(i, i + 20_000) });
  return { rev: 0, ops };
}
const bridgeBytes = (body: unknown) => Buffer.byteLength(JSON.stringify(toSnake(body)));

it('passes a canvas request as large as the browser makes them and answers a larger one 413, as the bridge does', async () => {
  const made = await post('/api/designer/sessions').send({ kind: 'mine', seed: '4242', version: '1.12.2' }).expect(200);
  const id = made.body.id as number;
  // The most the browser puts into one request (studio.ts): this many bytes of edits, at 22 a cell.
  const fits = canvasBody(Math.floor(DESIGNER_CANVAS_BATCH_BYTES / 22));
  expect(bridgeBytes(fits)).toBeGreaterThan(DESIGNER_CANVAS_BATCH_BYTES - 16_000);
  expect(bridgeBytes(fits)).toBeLessThan(DESIGNER_CANVAS_BODY_CAP);
  const sent = await post(`/api/designer/sessions/${id}/canvas`).send(fits).expect(200);
  expect(sent.body.applied).toBe(fits.ops[0]!.cells.length);
  // Valid for Nest (under its 1 MiB, every edit within the contract) and over the bridge's cap: the 413 is the bridge's.
  const over = canvasBody(Math.ceil(300_000 / 21));
  expect(bridgeBytes(over)).toBeGreaterThan(DESIGNER_CANVAS_BODY_CAP);
  expect(bridgeBytes(over)).toBeLessThan(1024 * 1024);
  const refused = await post(`/api/designer/sessions/${id}/canvas`).send(over).expect(413);
  expect(refused.body.code).toBe('payload_too_large');
});

it('the mock bridge caps a body by its path exactly as the bridge does', () => {
  const json = DESIGNER_CANVAS_BODY_CAP;
  const save = 8 * 1024 * 1024;
  const upload = 50 * 1024 * 1024;
  expect(bodyLimitFor('/v1/sup/designer/sessions/1/save')).toBe(save);
  expect(bodyLimitFor('/v1/sup/designer/sessions/0017/save')).toBe(save);
  for (const path of ['canvas', 'messages', 'other/save', 'save/other'])
    expect(bodyLimitFor(`/v1/sup/designer/sessions/1/${path}`), path).toBe(json);
  for (const id of ['0', '-1', 'x', '', '99999999999999999999'])
    expect(bodyLimitFor(`/v1/sup/designer/sessions/${id}/save`), id).toBe(json);
  expect(bodyLimitFor('/v1/sup/designer/designs/1/delete')).toBe(json);
  expect(bodyLimitFor('/v1/sup/admin/memory')).toBe(json);
  expect(bodyLimitFor('/v1/shards/import/preview')).toBe(upload);
  expect(bodyLimitFor('/V1/sup/admin/shard/IMPORT')).toBe(upload);
  expect(bodyLimitFor('/v1/sup/admin/shard/import/x')).toBe(json);
});

/**
 * What the real studio made of one scenario (a nested, turned, named and hidden group with a torch placed by hand): its
 * canvas edits, the layout it saved, the cells it held. apps/web/test/mineAuthoringRoundTrip.test.ts writes the file.
 */
const nested = JSON.parse(
  readFileSync(
    new URL('../../../packages/contracts/test/fixtures/mine-authoring-nested.json', import.meta.url),
    'utf8',
  ),
) as {
  seed: string;
  version: string;
  ops: Array<{ cells: number[][] }>;
  envelope: string;
  blocks: number[][];
  dug: number[][];
};
const byPlace = (a: number[], b: number[]) => a[1]! - b[1]! || a[2]! - b[2]! || a[0]! - b[0]!;

it('carries a nested mine layout from the real studio through the gateway and the capped bridge, and gives it back unchanged', async () => {
  const made = await post('/api/designer/sessions')
    .send({ kind: 'mine', seed: nested.seed, version: nested.version })
    .expect(200);
  const id = made.body.id as number;
  const sent = await post(`/api/designer/sessions/${id}/canvas`).send({ rev: 0, ops: nested.ops }).expect(200);
  // Every cell the studio sent was a change to the canvas the bridge holds.
  expect(sent.body.applied).toBe(nested.ops.reduce((n, op) => n + op.cells.length, 0));
  const saved = await post(`/api/designer/sessions/${id}/save`)
    .send({ name: 'nested floor', mineAuthoring: nested.envelope })
    .expect(200);
  const opened = await post('/api/designer/sessions').send({ designId: saved.body.designId }).expect(200);
  const resumed = await user.agent.get(`/api/designer/sessions/${id}`).expect(200);
  for (const session of [opened.body, resumed.body] as Array<{
    mineAuthoring: string;
    designRevision: number;
    blocks: number[][];
    dug: number[][];
  }>) {
    expect(session.mineAuthoring).toBe(nested.envelope);
    expect(session.designRevision).toBe(saved.body.rev);
    expect([...session.blocks].sort(byPlace)).toEqual(nested.blocks);
    expect([...session.dug].sort(byPlace)).toEqual(nested.dug);
  }
  expect(opened.body.id).not.toBe(id);
});

it('accepts detailed preview bodies and removes a named design from the library without losing its saved revision', async () => {
  const made = await post('/api/designer/sessions').send({}).expect(200);
  const id = made.body.id as number;
  await post(`/api/designer/sessions/${id}/canvas`).send({ rev: 0, ops: [{ kind: 'place', cells: [[30, 0, 30, 4, 0]] }] }).expect(200);
  const previewPng = 'data:image/png;base64,' + 'a'.repeat(1_000_000);
  const saved = await post(`/api/designer/sessions/${id}/save`).send({ name: 'Detailed storehouse', previewPng }).expect(200);
  const designId = saved.body.designId as number;
  await post(`/api/designs/${designId}/delete`).send({ name: 'Wrong name' }).expect(400);
  await post(`/api/designs/${designId}/delete`).send({ name: 'Detailed storehouse' }).expect(200);
  const list = await user.agent.get('/api/designs').expect(200);
  expect(list.body.items.some((d: { id: number }) => d.id === designId)).toBe(false);
  await post(`/api/designer/sessions/${id}/save`).send({ name: 'Detailed storehouse' }).expect(400);
  await post(`/api/designer/sessions/${id}/save`).send({ name: 'Storehouse copy', asNew: true }).expect(200);
});
