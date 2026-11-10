/**
 * A mine layout's whole round trip in one process: the real Studio and component controller author a nested group over the
 * designer mock, behind an adapter that does to every request and reply what the gateway and the bridge do to it; the design
 * is saved, opened again in a new session, resumed in the saving one, regrouped and saved once more.
 *
 * What the scenario produces (its edits, its layout envelope and its cells) is the fixture the contracts, API and bot tests
 * read too: packages/contracts/test/fixtures/mine-authoring-nested.json. It is written from here, never by hand:
 *
 *   MINE_FIXTURE_WRITE=1 npx vitest run test/mineAuthoringRoundTrip.test.ts
 *
 * Afterwards run the tests that read it (contracts designer.test.ts, api designer-authoring.test.ts, the bot's
 * DesignerMineAuthoringTests). The bot's must still find no torch to turn: when it does, move the scenario, not the file.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  createDesignerMock,
  DESIGNER_CANVAS_BODY_CAP,
  DesignerCanvasRequest,
  DesignerCanvasResponse,
  type DesignerCell,
  type DesignerEdit,
  type DesignerMock,
  DesignerSaveRequest,
  DesignerSaveResponse,
  DesignerSession,
  parseBridgeJson,
  REQUEST_OPAQUE_KEYS,
  toCamel,
  toSnake,
} from '@mineclaw/contracts';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { keyOf, setPlotMode, type V3 } from '@/features/studio/canvas';
import { MineModuleController } from '@/features/studio/mine/MineModuleController';
import { defaultModule } from '@/features/studio/mine/modules';
import { Studio, type StudioApi } from '@/features/studio/studio';

const FIXTURE = path.join(
  path.dirname(fileURLToPath(import.meta.url)),
  '../../../packages/contracts/test/fixtures/mine-authoring-nested.json',
);
const OWNER = 'poofimafox';
const SEED = '4242';
const VERSION = '1.12.2';
/** The bridge's cap on a save's body (`BridgeAuthMiddleware.DesignerSaveBodyLimit`). */
const SAVE_BODY_CAP = 8 * 1024 * 1024;

interface Fixture {
  seed: string;
  version: string;
  /** The canvas edits in the order the studio sent them. */
  ops: DesignerEdit[];
  /** The layout as the save sent it. */
  envelope: string;
  /** The canvas when it was saved, in the bot's order (y, then z, then x). */
  blocks: DesignerCell[];
  dug: V3[];
}

const byPlace = (a: readonly number[], b: readonly number[]) => a[1]! - b[1]! || a[2]! - b[2]! || a[0]! - b[0]!;
const sorted = <T extends readonly number[]>(cells: readonly T[]): T[] => [...cells].sort(byPlace);

/** The file as Prettier lays it out (a cell a line), so writing it again changes only what changed. */
function print(f: Fixture): string {
  const cell = (c: readonly number[]) => `[${c.join(', ')}]`;
  const cells = (list: ReadonlyArray<readonly number[]>, pad: string) => {
    if (list.length < 2) return `[${list.map(cell).join('')}]`;
    return `[\n${list.map((c) => `${pad}  ${cell(c)}`).join(',\n')}\n${pad}]`;
  };
  const text = (value: string) => JSON.stringify(value);
  const op = (o: DesignerEdit) =>
    [
      '    {',
      `      "kind": ${text(o.kind)},`,
      `      "label": ${text(o.label ?? '')},`,
      `      "cells": ${cells(o.cells, '      ')}`,
      '    }',
    ].join('\n');
  return [
    '{',
    `  "seed": ${text(f.seed)},`,
    `  "version": ${text(f.version)},`,
    `  "ops": [\n${f.ops.map(op).join(',\n')}\n  ],`,
    `  "envelope": ${text(f.envelope)},`,
    `  "blocks": ${cells(f.blocks, '  ')},`,
    `  "dug": ${cells(f.dug, '  ')}`,
    '}',
    '',
  ].join('\n');
}

/**
 * The designer mock behind what stands between the studio and the bot. A request is checked by its route's schema, turned to
 * snake case, serialised with the identity the bridge adds, refused over the bridge's cap, and read back as the bot reads
 * it; a reply makes the same trip the other way.
 */
function gateway(mock: DesignerMock) {
  const ops: DesignerEdit[] = [];
  const upstream = <T extends object>(body: T, cap: number): T => {
    const wire = JSON.stringify(
      toSnake({ ...body, player: OWNER, who: `${OWNER} (web)` }, { opaque: REQUEST_OPAQUE_KEYS }),
    );
    if (Buffer.byteLength(wire) > cap) throw Object.assign(new Error('payload too large'), { status: 413 });
    return toCamel<T>(parseBridgeJson(wire));
  };
  const downstream = (reply: unknown): unknown => toCamel(parseBridgeJson(JSON.stringify(toSnake(reply))));
  const api: StudioApi = {
    canvas: (id, body) => {
      const request = DesignerCanvasRequest.parse(body);
      ops.push(...request.ops);
      const reply = mock.canvas(id, upstream(request, DESIGNER_CANVAS_BODY_CAP));
      return Promise.resolve(DesignerCanvasResponse.parse(downstream(reply)));
    },
    message: () => Promise.reject(new Error('the scenario sends no message')),
    save: (id, body) => {
      const reply = mock.save(id, upstream(DesignerSaveRequest.parse(body), SAVE_BODY_CAP), OWNER);
      return Promise.resolve(DesignerSaveResponse.parse(downstream(reply)));
    },
  };
  return { api, ops, session: (s: unknown) => DesignerSession.parse(downstream(s)) };
}

const WALL: Record<number, readonly [number, number] | undefined> = { 1: [-1, 0], 2: [1, 0], 3: [0, -1], 4: [0, 1] };

/** The wall torches the bot would turn: those whose wall is not solid, untouched ground (`DesignerCanvas.FixAttachments`). */
function looseTorches(studio: Studio): DesignerCell[] {
  return studio.canvas.cells().filter(([x, y, z, id, meta]) => {
    const wall = id === 50 ? WALL[meta] : undefined;
    if (!wall) return false;
    const [wx, wz] = [x + wall[0], z + wall[1]];
    return !studio.terrain!.solid(wx, y, wz) || studio.canvas.authoredState(keyOf(wx, y, wz)) !== -1;
  });
}

const studios: Studio[] = [];
const controllers: MineModuleController[] = [];

/** A studio with its component controller over a session, as the page opens one (no browser storage). */
function open(session: DesignerSession, api: StudioApi) {
  const studio = new Studio(session, api, 60_000);
  const controller = new MineModuleController(studio, null);
  studios.push(studio);
  controllers.push(controller);
  return { studio, controller };
}

/**
 * The scenario: a trial pit dug by hand; a spiral shaft with torches, a branch mine, a tunnel and a chamber; the shaft and
 * the branch mine combined; that group combined with the tunnel; the nested group turned a quarter, named and hidden; a
 * torch stood on its floor by hand. The chamber stays a piece of its own. Each step goes to the bot in a request of its own,
 * and after each one no wall torch may hang loose: the bot would turn it, and the layout would no longer match its canvas.
 */
async function author(studio: Studio, c: MineModuleController) {
  const sent = async () => {
    expect(looseTorches(studio)).toEqual([]);
    await studio.flush();
    expect(studio.snapshot()).toMatchObject({ pending: 0, sync: 'idle' });
  };
  studio.edit([[133, 28, 130, 0, 0]], 'dig', 'trial pit');
  await sent();
  const shaft = c.add(
    { ...defaultModule('spiral'), depth: 12, clockwise: false, torchEvery: 4, landingDepths: [12] },
    [120, 40, 120],
  )!;
  await sent();
  const floor = c.add(
    { ...defaultModule('branches'), length: 16, branchLength: 6, spacing: 3, torchEvery: 5 },
    [131, 28, 121],
  )!;
  await sent();
  const tunnel = c.add({ ...defaultModule('tunnel'), length: 9 }, [144, 28, 121])!;
  await sent();
  const chamber = c.add(defaultModule('chamber'), [146, 28, 131])!;
  await sent();
  const access = c.combine([shaft.id, floor.id])!;
  const nested = c.combine([access.id, tunnel.id])!;
  expect(c.update(nested.id, { ...nested.spec, rotation: 1 })).toBe(true);
  await sent();
  expect(c.update(nested.id, { ...c.snapshot()[1]!.spec, label: 'Diamond floor' })).toBe(true);
  c.setVisible(nested.id, false);
  studio.edit([[133, 28, 132, 50, 5]], 'place', 'manual torch');
  await sent();
  return { chamber, nested };
}

beforeEach(() => {
  // Component ids hold the time they were made.
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date('2026-11-09T12:00:00Z'));
});
afterEach(() => {
  for (const c of controllers.splice(0)) c.dispose();
  for (const s of studios.splice(0)) s.dispose();
  vi.useRealTimers();
  setPlotMode({ kind: 'build' });
});

describe('a nested mine layout, from the studio to a saved design and back', () => {
  it('authors the shared fixture, saves it, reopens it, resumes it and regroups it', async () => {
    const mock = createDesignerMock(100);
    const g = gateway(mock);
    const first = g.session(mock.create({ kind: 'mine', seed: SEED, version: VERSION }, OWNER));
    const { studio, controller } = open(first, g.api);
    const { chamber, nested } = await author(studio, controller);

    // 1. What was authored is the fixture every layer reads.
    const authored = controller.snapshot();
    expect(authored).toHaveLength(2);
    expect(authored[0]).toEqual(chamber);
    const group = authored[1]!;
    expect(group).toMatchObject({
      id: nested.id,
      visible: false,
      spec: { kind: 'custom', rotation: 1, label: 'Diamond floor' },
    });
    expect(group.spec.parts!.map((p) => p.spec.kind)).toEqual(['tunnel', 'custom']);
    expect(group.spec.parts![1]!.spec.parts!.map((p) => p.spec.kind)).toEqual(['spiral', 'branches']);
    const saves: string[] = [];
    const save = vi.spyOn(g.api, 'save');
    const saved = await studio.save('nested floor');
    saves.push(save.mock.calls[0]![1].mineAuthoring!);
    const fixture: Fixture = {
      seed: SEED,
      version: VERSION,
      ops: g.ops.map((o) => ({ kind: o.kind, label: o.label, cells: o.cells })),
      envelope: saves[0]!,
      blocks: sorted(studio.canvas.cells()),
      dug: sorted(studio.canvas.holes()),
    };
    if (process.env.MINE_FIXTURE_WRITE) writeFileSync(FIXTURE, print(fixture));
    const onDisk = readFileSync(FIXTURE, 'utf8');
    expect(fixture).toEqual(JSON.parse(onDisk));
    // Exactly as written from here (a checkout may give it CRLF line ends): nobody tidied or touched it by hand.
    expect(onDisk.replace(/\r\n/g, '\n')).toBe(print(fixture));

    // 2. No wall torch hangs loose (checked after every step too), so the bot's canvas is this one, cell for cell.
    expect(fixture.blocks.filter((c) => c[3] === 50).length).toBeGreaterThan(5);
    expect(looseTorches(studio)).toEqual([]);
    const held = g.session(mock.get(first.id));
    expect(sorted(held.blocks)).toEqual(fixture.blocks);
    expect(sorted(held.dug!)).toEqual(fixture.dug);

    // 3. Opened from the design in a new session: the same components, nothing unsaved, nothing to say.
    expect(saved).toMatchObject({ designId: 100, rev: 1, created: true });
    expect(studio.dirty).toBe(false);
    const reopened = open(g.session(mock.create({ designId: saved.designId }, OWNER)), g.api);
    expect(reopened.studio.session.mineAuthoring).toBe(fixture.envelope);
    expect(reopened.controller.snapshot()).toEqual(authored);
    expect(reopened.studio.dirty).toBe(false);
    expect(reopened.controller.restoreNote).toBeNull();
    // Taking the group away leaves what was there before it: the pit, the torch stood in it, and the chamber.
    expect(reopened.controller.remove(group.id)).toBe(true);
    expect(sorted(reopened.studio.canvas.cells())).toEqual([[133, 28, 132, 50, 5]]);
    const left = reopened.studio.canvas.holes();
    expect(left).toHaveLength(7 * 7 * 3 + 1);
    expect(left).toContainEqual([133, 28, 130]);

    // 4. The saving session resumed (a reload): the same again.
    const resumed = open(g.session(mock.get(first.id)), g.api);
    expect(resumed.studio.session).toMatchObject({ mineAuthoring: fixture.envelope, designId: 100, designRevision: 1 });
    expect(resumed.controller.snapshot()).toEqual(authored);
    expect(resumed.studio.dirty).toBe(false);

    // 5. Combined once more, which changes no cell, and saved: a new revision with the new grouping; the first keeps its own.
    vi.setSystemTime(new Date('2026-11-09T12:05:00Z'));
    const whole = controller.combine([group.id, chamber.id])!;
    expect(studio.snapshot()).toMatchObject({ dirty: true, pending: 0 });
    const again = await studio.save('nested floor');
    expect(again).toMatchObject({ designId: 100, rev: 2, created: false });
    saves.push(save.mock.calls[1]![1].mineAuthoring!);
    expect(saves[1]).not.toBe(saves[0]);
    const newest = open(g.session(mock.create({ designId: 100 }, OWNER)), g.api);
    expect(newest.studio.session).toMatchObject({ mineAuthoring: saves[1], designRevision: 2 });
    expect(newest.controller.snapshot()).toEqual([whole]);
    expect(newest.controller.snapshot()[0]!.spec.parts!.map((p) => p.spec.label ?? p.spec.kind)).toEqual([
      'chamber',
      'Diamond floor',
    ]);
    expect(newest.studio.dirty).toBe(false);
    const older = open(g.session(mock.create({ designId: 100, rev: 1 }, OWNER)), g.api);
    expect(older.studio.session).toMatchObject({ mineAuthoring: fixture.envelope, designRevision: 1 });
    expect(older.controller.snapshot()).toEqual(authored);
    expect(sorted(older.studio.canvas.holes())).toEqual(fixture.dug);
  });
});
