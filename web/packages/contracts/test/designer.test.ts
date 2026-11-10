import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';

/**
 * What the real studio made of one scenario: a nested, turned, named and hidden group of mine components with a torch placed
 * by hand. Its canvas edits in order, the layout it saved and the cells it held. Written by
 * apps/web/test/mineAuthoringRoundTrip.test.ts (never by hand); the API and bot tests read the same file.
 */
const nested = JSON.parse(readFileSync(new URL('./fixtures/mine-authoring-nested.json', import.meta.url), 'utf8')) as {
  seed: string;
  version: string;
  ops: C.DesignerEdit[];
  envelope: string;
  blocks: C.DesignerCell[];
  dug: number[][];
};

describe('Design Studio contract', () => {
  it('carries a nested mine layout from the real studio through the save schema and the mock, byte for byte', () => {
    const body = C.DesignerSaveRequest.parse({ name: 'nested floor', mineAuthoring: nested.envelope });
    expect(body.mineAuthoring).toBe(nested.envelope);
    // To the bot and back, key case conversion leaves the layout alone: it is one string, whatever it nests.
    const bridged = (value: unknown, opaque = C.OPAQUE_KEYS): unknown =>
      C.toCamel(C.parseBridgeJson(JSON.stringify(C.toSnake(value, { opaque }))));
    expect(bridged(body, C.REQUEST_OPAQUE_KEYS)).toEqual(body);
    const m = C.createDesignerMock(100);
    const s = m.create({ kind: 'mine', seed: nested.seed, version: nested.version }, 'fox');
    for (const op of nested.ops) m.canvas(s.id, C.DesignerCanvasRequest.parse({ rev: 0, ops: [op] }));
    const saved = m.save(s.id, body, 'fox');
    const byPlace = (a: readonly number[], b: readonly number[]) => a[1]! - b[1]! || a[2]! - b[2]! || a[0]! - b[0]!;
    for (const session of [m.create({ designId: saved.designId }, 'fox'), m.get(s.id)]) {
      const read = C.DesignerSession.parse(bridged(session));
      expect(read.mineAuthoring).toBe(nested.envelope);
      expect(read.designRevision).toBe(saved.rev);
      expect([...read.blocks].sort(byPlace)).toEqual(nested.blocks);
      expect([...read.dug!].sort(byPlace)).toEqual(nested.dug);
    }
    const layout = JSON.parse(nested.envelope) as {
      modules: Array<{ spec: { parts?: Array<{ spec: { parts?: unknown[] } }> } }>;
    };
    expect(layout.modules[1]!.spec.parts![1]!.spec.parts).toHaveLength(2);
  });

  it('keeps component metadata with each mock revision instead of borrowing the latest layout when opening old cells', () => {
    const m = C.createDesignerMock(100), s = m.create({ kind: 'mine', seed: '1', version: '1.12.2' }, 'fox');
    m.canvas(s.id, { ops: [{ kind: 'dig', cells: [[30, 20, 30, 0, 0]] }] });
    const first = JSON.stringify({ size: 256, modules: [{ id: 'a', visible: true }], base: [], overrides: [] });
    const second = first.replace('true', 'false');
    expect(C.DesignerSaveRequest.parse({ name: 'mine', mineAuthoring: first }).mineAuthoring).toBe(first);
    const r1 = m.save(s.id, { name: 'mine', mineAuthoring: first }, 'fox');
    m.save(s.id, { name: 'mine', mineAuthoring: second }, 'fox');
    expect(m.create({ designId: r1.designId }, 'fox').mineAuthoring).toBe(second);
    const old = m.create({ designId: r1.designId, rev: r1.rev }, 'fox');
    expect(old.mineAuthoring).toBe(first);
    const copy = m.save(old.id, { name: 'copy', asNew: true }, 'fox');
    expect(m.create({ designId: copy.designId }, 'fox').mineAuthoring).toBe(first);
  });
  it('design role: admin implies it, nothing else does', () => {
    expect(C.hasRole(['admin'], 'design')).toBe(true);
    expect(C.hasRole(['design'], 'design')).toBe(true);
    expect(C.hasRole(['viewer', 'chat', 'commands'], 'design')).toBe(false);
    expect(C.hasRole(['design'], 'admin')).toBe(false);
    expect(C.ROUTES['designer.message'].access).toBe('design');
    expect(C.ROUTES['designer.models'].access).toBe('viewer');
    expect(C.ROUTES['designer.message'].timeoutMs).toBeGreaterThan(60_000);
    expect(C.SUPERVISOR_PASSTHROUGH_ALLOW.post).toContain('designer/');
  });

  it('request bodies are strict (no player or who from the browser)', () => {
    expect(C.DesignerCreateRequest.safeParse({ model: 'main' }).success).toBe(true);
    expect(C.DesignerCreateRequest.safeParse({ model: 'main', player: 'kiwi' }).success).toBe(false);
    expect(C.DesignerMessageRequest.safeParse({ text: 'add a door', canvas: { rev: 3 } }).success).toBe(true);
    expect(C.DesignerMessageRequest.safeParse({ text: ' ', canvas: { rev: 3 } }).success).toBe(false);
    expect(
      C.DesignerCanvasRequest.safeParse({ rev: 1, ops: [{ kind: 'place', cells: [[1, 2, 3, 4, 0]] }] }).success,
    ).toBe(true);
    expect(C.DesignerCanvasRequest.safeParse({ rev: 1, ops: [{ kind: 'place', cells: [[1, 2, 3]] }] }).success).toBe(
      false,
    );
    expect(C.DesignerSaveRequest.safeParse({ name: '' }).success).toBe(false);
  });

  it('the mock studio builds a house, adds a door, saves revisions and reloads them', () => {
    const m = C.createDesignerMock(100);
    const s = C.DesignerSession.parse(m.create({ model: 'main' }, 'poofimafox'));
    expect(s.blocks).toEqual([]);
    const house = C.DesignerMessageResponse.parse(
      m.message(s.id, { text: 'build a little house', canvas: { rev: 0 } }),
    );
    expect(house.ops.map((o) => o.kind)).toEqual(['fill', 'walls', 'fill']);
    expect(house.costUsd).toBeGreaterThan(0);
    const door = m.message(s.id, { text: 'add a door', canvas: { rev: house.rev } });
    expect(door.ops[0]!.cells.map((c) => c[3])).toEqual([64, 64]);
    expect(door.ops[0]!.cells[1]![4] & 8).toBe(8);
    const saved = C.DesignerSaveResponse.parse(m.save(s.id, { name: 'hut', summary: 'first' }, 'poofimafox'));
    expect(saved).toMatchObject({ designId: 100, rev: 1, created: true });
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [[1, 0, 1, 4, 0]] }] });
    expect(m.save(s.id, { name: 'hut' }, 'poofimafox')).toMatchObject({ designId: 100, rev: 2, created: false });
    const again = m.create({ designId: 100 }, 'kiwi');
    expect(again.blocks.length).toBe(m.get(s.id).blocks.length);
    expect(again.revisions.map((r) => r.rev)).toEqual([2, 1]);
    expect(C.fakeDesignerTurn('hello', []).ops).toEqual([]);
    expect(
      C.DesignerModelsResponse.parse(m.models()).models.every(
        (x) => !x.model.startsWith('claude') || x.model.endsWith('5-5'),
      ),
    ).toBe(true);
  });

  it('deletes only the library entry, persists that choice, and requires Save as new for an open session', () => {
    let stored: string | null = null;
    const persistence = { load: () => stored, save: (json: string) => { stored = json; } };
    const m = C.createDesignerMock(100, persistence);
    const s = m.create({}, 'builder');
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [[1, 0, 1, 4, 0]] }] });
    m.save(s.id, { name: 'Storehouse' }, 'builder');
    expect(() => m.deleteDesign(100, 'Other')).toThrow('name changed');
    expect(C.DesignerDeleteResponse.parse(m.deleteDesign(100, 'Storehouse')).id).toBe(100);
    expect(m.saved[0]?.revisions.length).toBe(1);
    expect(() => m.create({ designId: 100 }, 'builder')).toThrow('removed');
    expect(() => m.save(s.id, { name: 'Storehouse' }, 'builder')).toThrow('Save as new');
    expect(C.createDesignerMock(100, persistence).hiddenDesignIds.has(100)).toBe(true);
    expect(m.save(s.id, { name: 'Copy', asNew: true }, 'builder').designId).toBe(101);
  });

  it('leaves room between what the browser sends in one canvas request and what the bridge and the bot accept', () => {
    // The C# caps (BridgeAuthMiddleware.JsonBodyLimit, DesignerRoutes.MaxBodyBytes) are 256 KiB.
    expect(C.DESIGNER_CANVAS_BODY_CAP).toBe(256 * 1024);
    expect(C.DESIGNER_CANVAS_BATCH_BYTES + 32_768).toBeLessThan(C.DESIGNER_CANVAS_BODY_CAP);
  });
});
