import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';

describe('Design Studio contract', () => {
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
});
