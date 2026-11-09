/** Real browser route dispatcher → signed HTTP bridge requests. Expectations deliberately do not derive
 * their upstream methods from the route table: a default GET there caused production takeover 404s. */
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { fakeBridge, json, login, ORIGIN, startApp, type Session, type TestApp } from './helpers.js';

const capability = 'test-pawn-control-session';
const prefix = '/v1/sup/bodies/maid_chan_105/control/';
const state = { x: 225, y: 65, z: 379, yaw: 90, pitch: 0, dim: 0, hp: 20, food: 18, held_slot: 2, on_ground: true, window_id: 0 };
const lease = {
  session: capability, body: 'maid_chan_105', expires_at: '2026-11-09T02:20:03Z',
  input_timeout_ms: 350, lease_timeout_ms: 3000, state,
};

let bridge: Awaited<ReturnType<typeof fakeBridge>>;
let app: TestApp;
let player: Session;

beforeAll(async () => {
  bridge = await fakeBridge((request, response) => {
    if (request.method !== 'POST') return json(response, 404, { error: 'not found', code: 'not_found' });
    switch (request.target) {
      case `${prefix}acquire`:
      case `${prefix}input`:
        return json(response, 200, lease);
      case `${prefix}action`:
        return json(response, 200, { ok: true, message: 'clicked slot', state });
      case `${prefix}release`:
        return json(response, 200, { ok: true, state });
      default:
        return json(response, 404, { error: 'not found', code: 'not_found' });
    }
  });
  app = await startApp({ bridge: { url: bridge.url } });
  player = await login(app.url, 'sky');
});

afterAll(async () => {
  await app.close();
  await bridge.close();
});

describe('pawn control HTTP forwarding', () => {
  it.each([
    ['acquire', { requestId: 'browser-takeover-105' }, { request_id: 'browser-takeover-105' }],
    ['input', {
      session: capability, seq: 1, forward: 0, strafe: 0, yaw: 90, pitch: 0,
      jump: false, sneak: false, sprint: false, hotbar: 2,
    }, {
      session: capability, seq: 1, forward: 0, strafe: 0, yaw: 90, pitch: 0,
      jump: false, sneak: false, sprint: false, hotbar: 2,
    }],
    ['action', { session: capability, seq: 1, kind: 'inventory_click', windowId: 7, slot: 27, button: 0 },
      { session: capability, seq: 1, kind: 'inventory_click', window_id: 7, slot: 27, button: 0 }],
    ['release', { session: capability }, { session: capability }],
  ] as const)('forwards %s as an authenticated POST with its request body', async (operation, body, expected) => {
    const response = await player.agent.post(`/api/bodies/maid_chan_105/control/${operation}`)
      .set('Origin', ORIGIN).set('X-CSRF-Token', player.csrf).send(body).expect(200);
    const sent = bridge.requests.findLast(request => request.target === `${prefix}${operation}`);
    expect(sent?.method).toBe('POST');
    expect(sent?.identity).toEqual({ user: 'sky', player: 'sky_mc', roles: 'story,viewer' });
    expect(JSON.parse(sent!.body.toString('utf8'))).toEqual(expected);
    expect(response.body.state).toMatchObject({ heldSlot: 2, onGround: true, windowId: 0 });
  });
});
