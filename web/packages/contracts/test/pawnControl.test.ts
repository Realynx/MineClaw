import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';

const session = 'test-control-session';
const input = { session, seq: 0, forward: 0, strafe: 0, yaw: 0, pitch: 0, jump: false, sneak: false, sprint: false };
const state = { x: 1, y: 64, z: 2, yaw: 90, pitch: -10, dim: 0, hp: 20, food: 20, heldSlot: 0, onGround: true };
const dig = { x: 230, y: 64, z: 381, face: 4, progress: 0.35, id: 1, meta: 0 };
const lease = { session, body: 'maid_chan_109', expiresAt: '2026-11-09T10:00:00+00:00', inputTimeoutMs: 350, leaseTimeoutMs: 3000, state };
const sorted = (o: object) => Object.keys(o).sort();
/** Every object key in a JSON value, at any depth. */
const keysOf = (v: unknown): string[] => Array.isArray(v) ? v.flatMap(keysOf)
  : v && typeof v === 'object' ? Object.entries(v).flatMap(([k, inner]) => [k, ...keysOf(inner)]) : [];

describe('pawn control contracts', () => {
  it('requires story access and excludes session-bearing traffic from request body audits', () => {
    for (const op of ['Acquire', 'Input', 'Action', 'Release'] as const) {
      const id = `bodies.control${op}` as const, route = C.ROUTES[id];
      expect(route.access).toBe('story');
      expect(route.audit).toBeNull();
      expect(route.method).toBe('POST');
      expect(route.bridge?.method).toBe('POST');
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

  it('accepts a state with and without the per-tick sample and keeps a null aim apart from a missing one', () => {
    const sample = { ...state, windowId: 0, tick: 48_213, ackSeq: 912, ackWaitMs: 31.5, vx: 0.1178, vy: -0.0784, vz: 0,
      teleports: 2, impulses: 1, usingItem: true, stopped: false, effects: { speed: 1, slowness: -1, jumpBoost: 0 },
      dig, aim: { x: 230, y: 64, z: 381, face: -1 }, broke: { count: 3, x: 230, y: 65, z: 381, id: 17, meta: 2 },
      armor: 7, air: -20 };
    expect(C.PawnControlState.parse(sample)).toEqual(sample);
    expect(sorted(sample)).toEqual(sorted(C.PawnControlState.shape));
    // An older server, and a newer one whose pawn is riding or dead (no fresh tick to report).
    expect(C.PawnControlState.parse(state)).toEqual(state);
    expect(C.PawnControlState.parse({ ...state, stopped: true })).toEqual({ ...state, stopped: true });
    expect(C.PawnControlState.parse(state).aim).toBeUndefined();
    expect(C.PawnControlState.parse({ ...state, aim: null }).aim).toBeNull();
    expect(C.PawnControlState.parse({ ...state, ackSeq: -1, dig: null, broke: null, effects: null })).toMatchObject({ ackSeq: -1, dig: null });
    for (const extra of [{ tick: 1.5 }, { ackSeq: '912' }, { vx: Number.NaN }, { usingItem: 1 }, { stopped: 'yes' },
      { effects: { speed: 1, slowness: -1 } }, { aim: { x: 1, y: 2, z: 3 } }, { broke: { x: 1, y: 2, z: 3, id: 1, meta: 0 } }])
      expect(C.PawnControlState.safeParse({ ...state, ...extra }).success, JSON.stringify(extra)).toBe(false);
  });

  it('rejects dig progress outside 0..1 and a face that is not a block side', () => {
    for (const progress of [0, 1]) expect(C.PawnControlState.parse({ ...state, dig: { ...dig, progress } }).dig?.progress).toBe(progress);
    for (const face of [-1, 0, 5]) expect(C.PawnControlState.parse({ ...state, dig: { ...dig, face } }).dig?.face).toBe(face);
    for (const extra of [{ progress: -0.01 }, { progress: 1.01 }, { progress: Number.NaN }, { face: -2 }, { face: 6 }, { x: 230.5 }])
      expect(C.PawnControlState.safeParse({ ...state, dig: { ...dig, ...extra } }).success, JSON.stringify(extra)).toBe(false);
    expect(C.PawnControlState.safeParse({ ...state, aim: { x: 230, y: 64, z: 381, face: 6 } }).success).toBe(false);
  });

  it('carries reach on the session, action codes as plain strings and the repeat mark on a held attack', () => {
    expect(C.PawnControlSession.parse(lease).reach).toBeUndefined();
    expect(C.PawnControlSession.parse({ ...lease, reach: 4.25 }).reach).toBe(4.25);
    for (const reach of [0, -4.25, Number.POSITIVE_INFINITY]) expect(C.PawnControlSession.safeParse({ ...lease, reach }).success).toBe(false);
    const status = { seq: 4, pending: false, ok: false, message: 'Dig stopped because the aim, held item or control input changed.' };
    expect(C.PawnControlActionStatus.parse(status)).toEqual(status);
    // A code this browser does not know must never end the takeover: the schema takes any string.
    for (const code of [...C.PAWN_CONTROL_ACTION_CODES, 'added_by_a_newer_server', null])
      expect(C.PawnControlActionStatus.parse({ ...status, code })).toEqual({ ...status, code });
    expect(C.PawnControlActionStatus.safeParse({ ...status, code: 7 }).success).toBe(false);
    expect(C.PawnControlActionResponse.parse({ ok: false, message: status.message, state, code: 'aim_changed' }).code).toBe('aim_changed');
    expect(C.PAWN_CONTROL_ACTION_CODES).toEqual(['dug', 'aim_changed', 'cancelled', 'placed']);
    expect(C.PAWN_CONTROL_END_REASONS).toEqual(['died', 'expired', 'replaced', 'offline']);
    expect(C.PawnControlActionRequest.parse({ session, seq: 3, kind: 'attack', repeat: true })).toMatchObject({ repeat: true });
    expect(C.PawnControlActionRequest.parse({ session, seq: 3, kind: 'attack' }).repeat).toBeUndefined();
    expect(C.PawnControlActionRequest.safeParse({ session, seq: 3, kind: 'attack', repeat: 1 }).success).toBe(false);
  });

  it('keeps every field of a literal supervisor state frame through toCamel and parse', () => {
    // Written by hand in the supervisor's snake_case. A fixture made from a camelCase object with toSnake would hide a
    // key that decodes to the wrong name.
    const here = dirname(fileURLToPath(import.meta.url));
    const frame = JSON.parse(readFileSync(join(here, 'fixtures', 'pawn-control-frame.json'), 'utf8')) as unknown;
    expect(keysOf(frame).filter((key) => !/^[a-z0-9_]+$/.test(key))).toEqual([]);
    // The control socket decodes its envelope with no opaque keys: `data` is a typed DTO there.
    const camel = C.toCamel<{ type: string; sampledAtMs: number; data: Record<string, unknown> }>(frame, { opaque: new Set<string>() });
    expect(camel).toMatchObject({ type: 'state', sampledAtMs: 1_794_241_000_050 });
    const parsed = C.PawnControlSession.parse(camel.data);
    expect(parsed).toEqual(camel.data); // nothing was stripped
    // The frame names every field of the wire, so a field added to a schema has to be added here as well.
    expect(sorted(parsed)).toEqual(sorted(C.PawnControlSession.shape));
    expect(sorted(parsed.state)).toEqual(sorted(C.PawnControlState.shape));
    expect(sorted(parsed.action!)).toEqual(sorted(C.PawnControlActionStatus.shape));
    expect(sorted(parsed.state.effects!)).toEqual(sorted(C.PawnControlEffects.shape));
    expect(sorted(parsed.state.dig!)).toEqual(sorted(C.PawnControlDig.shape));
    expect(sorted(parsed.state.aim!)).toEqual(sorted(C.PawnControlAim.shape));
    expect(sorted(parsed.state.broke!)).toEqual(sorted(C.PawnControlBroke.shape));
    expect(parsed.state).toMatchObject({ heldSlot: 6, onGround: true, windowId: 0, tick: 48_213, ackSeq: 912, ackWaitMs: 31.5,
      vx: 0.1178, vy: -0.0784, vz: -0.0012, teleports: 2, impulses: 1, usingItem: false, stopped: false, armor: 7, air: 300 });
    expect(parsed.state.effects).toEqual({ speed: 1, slowness: -1, jumpBoost: 0 });
    expect(parsed.state.dig).toEqual({ x: 231, y: 65, z: 381, face: 4, progress: 0.35, id: 1, meta: 0 });
    expect(parsed.state.aim).toEqual({ x: 231, y: 64, z: 381, face: 4 });
    expect(parsed.state.broke).toEqual({ count: 3, x: 230, y: 65, z: 381, id: 17, meta: 2 });
    expect(parsed).toMatchObject({ reach: 4.25, action: { seq: 41, pending: false, ok: false, code: 'aim_changed' } });
    expect(parsed.inventory?.slots[1]).toEqual({ slot: 42, id: 257, meta: 12, count: 1 });
    expect(parsed.inventory?.version).toBe(118);
  });
});
