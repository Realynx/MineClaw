import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';

const session = 'test-control-session';
const input = { session, seq: 0, forward: 0, strafe: 0, yaw: 0, pitch: 0, jump: false, sneak: false, sprint: false };
const state = { x: 1, y: 64, z: 2, yaw: 90, pitch: -10, dim: 0, hp: 20, food: 20, heldSlot: 0, onGround: true };

describe('pawn control contracts', () => {
  it('requires story access and excludes session-bearing traffic from request body audits', () => {
    for (const op of ['Acquire', 'Input', 'Action', 'Release'] as const) {
      const id = `bodies.control${op}` as const, route = C.ROUTES[id];
      expect(route.access).toBe('story');
      expect(route.audit).toBeNull();
      expect(route.method).toBe('POST');
      expect(C.apiPath(id, { body: 'maid_chan_109' })).toBe(`/api/bodies/maid_chan_109/control/${op.toLowerCase()}`);
      expect(C.bridgePath(id, { body: 'maid_chan_109' })).toBe(`/v1/sup/bodies/maid_chan_109/control/${op.toLowerCase()}`);
    }
    expect(C.SUPERVISOR_PASSTHROUGH_ALLOW.post).toContain('bodies/');
    expect(C.SUPERVISOR_PASSTHROUGH_ALLOW.storyWrite).toContain('bodies/');
    expect(C.hasRole(['viewer'], 'story')).toBe(false);
    expect(C.hasRole(['story'], 'story')).toBe(true);
  });

  it('requires bounded identifiers and rejects caller-supplied identity', () => {
    expect(C.PawnControlAcquireRequest.parse({ requestId: 'retry-1' })).toEqual({ requestId: 'retry-1' });
    for (const requestId of ['', 'x'.repeat(129)])
      expect(C.PawnControlAcquireRequest.safeParse({ requestId }).success).toBe(false);
    expect(C.PawnControlAcquireRequest.safeParse({ requestId: 'retry-1', who: 'admin' }).success).toBe(false);
    expect(C.PawnControlReleaseRequest.safeParse({ session, who: 'admin' }).success).toBe(false);
    expect(C.PawnControlReleaseRequest.safeParse({ session: '' }).success).toBe(false);
  });

  it('accepts movement limits and rejects unsafe sequence, angle and key values', () => {
    expect(C.PawnControlInputRequest.parse({ ...input, seq: Number.MAX_SAFE_INTEGER, forward: -1, strafe: 1, pitch: -90, hotbar: 8 })).toMatchObject({ hotbar: 8 });
    for (const extra of [{ seq: -1 }, { seq: 0.1 }, { seq: Number.MAX_SAFE_INTEGER + 1 }, { forward: 2 },
      { strafe: -2 }, { yaw: Infinity }, { yaw: 1_000_001 }, { pitch: 91 }, { hotbar: 9 }, { jump: 1 }, { who: 'admin' }])
      expect(C.PawnControlInputRequest.safeParse({ ...input, ...extra }).success, JSON.stringify(extra)).toBe(false);
  });

  it('bounds world actions and permits the protocol outside-inventory slot', () => {
    expect(C.PawnControlActionRequest.parse({ session, seq: 0, kind: 'inventory_click', slot: -999, button: 0, windowId: 0 })).toMatchObject({ slot: -999 });
    expect(C.PawnControlActionRequest.parse({ session, seq: 1, kind: 'dig', target: { x: 30_000_000, y: -64, z: -30_000_000 }, face: 5 })).toMatchObject({ kind: 'dig' });
    expect(C.PawnControlActionRequest.parse({ session, seq: 2, kind: 'release' })).toMatchObject({ kind: 'release' });
    for (const extra of [{ slot: -1 }, { slot: 128 }, { button: 2 }, { face: 6 }, { windowId: 128 },
      { entityId: 0 }, { target: { x: 0, y: 4097, z: 0 } }, { target: { x: 0.5, y: 64, z: 0 } }, { kind: 'teleport' }])
      expect(C.PawnControlActionRequest.safeParse({ session, seq: 0, kind: 'use', ...extra }).success).toBe(false);
  });

  it('accepts acquisition/input without inventory and actions with inventory snapshots', () => {
    const reply = { session, body: 'maid_chan_109', expiresAt: '2026-11-09T10:00:00+00:00', inputTimeoutMs: 350, leaseTimeoutMs: 3000, state };
    expect(C.PawnControlSession.parse(reply)).toEqual(reply);
    const inventory = { windowId: 0, kind: 'player', title: 'Inventory', slotCount: 46, containerSlots: 0, mainStart: 9, hotbarStart: 36,
      slots: [{ slot: 36, id: 4, meta: 0, count: 64 }], cursor: null };
    expect(C.PawnControlActionResponse.parse({ ok: true, message: 'Inventory opened', state, inventory }).inventory).toEqual(inventory);
    expect(C.PawnControlReleaseResponse.parse({ ok: true, state })).toEqual({ ok: true, state });
  });
});
