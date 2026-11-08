import { afterAll, beforeAll, expect, it } from 'vitest';
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
