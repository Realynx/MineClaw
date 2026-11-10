import { describe, expect, it } from 'vitest';
import * as C from '../src/index.js';

/** A redstone design's authoring envelope as the studio writes it (the contract only carries the string). */
const authoring = (label: string) =>
  JSON.stringify({
    redstone: 1,
    components: [{ id: 'c1', def: 'gate.xor', at: [10, 0, 12], turns: 1, mirror: null, label }],
    customDefs: [],
    probes: [],
    breakpoints: [],
    levels: [],
    sim: { speed: 10, displayHz: 10 },
  });

const DUST: C.DesignerCell = [5, 0, 5, 55, 0];
const DIG: C.DesignerCell = [5, -1, 5, 0, 0];

describe('Design Studio contract: the redstone kind', () => {
  it('takes redstone wherever a kind goes and carries its authoring beside the mine one', () => {
    expect(C.DesignerKind.options).toEqual(['build', 'mine', 'redstone']);
    expect(C.DesignerCreateRequest.safeParse({ kind: 'redstone' }).success).toBe(true);
    expect(C.DesignerCreateRequest.safeParse({ kind: 'circuit' }).success).toBe(false);
    expect(C.DesignerVersionRequest.safeParse({ version: '1.12.2', kind: 'redstone' }).success).toBe(true);
    const a = authoring('sum');
    expect(C.DesignerSaveRequest.parse({ name: 'adder', redstoneAuthoring: a }).redstoneAuthoring).toBe(a);
    expect(C.DesignerSaveRequest.safeParse({ name: 'adder', redstoneAuthoring: ' '.repeat(2_000_001) }).success).toBe(
      false,
    );
    expect(C.DesignerSaveRequest.safeParse({ name: 'adder', redstoneLayout: a }).success).toBe(false);
  });

  it('still reads a session from a bot that does not know the kind yet', () => {
    const m = C.createDesignerMock(100);
    const { redstoneAuthoring: _unknownToAnOlderBot, ...older } = m.create({}, 'fox');
    const read = C.DesignerSession.parse(older);
    expect(read.kind).toBe('build');
    expect(read.redstoneAuthoring).toBeUndefined();
  });

  it('the mock opens a redstone canvas on the build plot: the grass, 16 layers of ground, no terrain seed', () => {
    const m = C.createDesignerMock(100);
    const s = C.DesignerSession.parse(m.create({ kind: 'redstone', version: '1.12.2' }, 'fox'));
    expect(s).toMatchObject({ kind: 'redstone', version: '1.12.2', seed: null, redstoneAuthoring: null });
    expect(s.plot).toEqual({ ...C.DESIGNER_PLOT });
    // A cell at a mine's height is outside this plot; the layer under the grass digs like a build's.
    expect(m.canvas(s.id, { ops: [{ kind: 'place', cells: [DUST, DIG, [5, 100, 5, 1, 0]] }] }).applied).toBe(2);
    const now = C.DesignerSession.parse(m.get(s.id));
    expect(now.blocks).toEqual([DUST]);
    expect(now.dug).toEqual([[5, -1, 5]]);
  });

  it('build <-> redstone keeps the canvas, its dug cells and the anchor without asking', () => {
    const m = C.createDesignerMock(100);
    const s = m.create({}, 'fox');
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [DUST, DIG] }], anchor: [5, 0, 5] });
    const kept = { blocks: [DUST], dug: [[5, -1, 5]], anchor: [5, 0, 5] };
    const there = C.DesignerVersionResponse.parse(m.version(s.id, { version: 'b1.8.1', kind: 'redstone' }));
    expect(there).toMatchObject({ version: 'b1.8.1', changed: true, report: null });
    expect(there.needsClear ?? false).toBe(false);
    expect(there.session).toMatchObject({ kind: 'redstone', ...kept });
    expect(there.session?.plot).toEqual({ ...C.DESIGNER_PLOT });
    const back = C.DesignerVersionResponse.parse(m.version(s.id, { version: 'b1.8.1', kind: 'build' }));
    expect(back.changed).toBe(true);
    expect(back.session).toMatchObject({ kind: 'build', ...kept });
    expect(m.version(s.id, { version: 'b1.8.1', kind: 'build' })).toMatchObject({ changed: false });
  });

  it('to or from a mine the plot changes, so a full canvas is still asked about and cleared', () => {
    const m = C.createDesignerMock(100);
    const s = m.create({ kind: 'redstone' }, 'fox');
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [DUST] }] });
    expect(m.version(s.id, { version: 'b1.8.1', kind: 'mine' })).toMatchObject({
      changed: false,
      needsClear: true,
      session: null,
    });
    expect(m.get(s.id)).toMatchObject({ kind: 'redstone', blocks: [DUST] });
    const mine = m.version(s.id, { version: 'b1.8.1', kind: 'mine', seed: '7', apply: true });
    expect(mine.session).toMatchObject({ kind: 'mine', seed: '7', blocks: [], dug: [] });
    // A mine's version tag and seed do not follow it onto a redstone plot.
    m.version(s.id, { version: 'b1.8.1', preferredFor: 'b1.8.1' });
    m.canvas(s.id, { ops: [{ kind: 'dig', cells: [[30, 20, 30, 0, 0]] }] });
    expect(m.version(s.id, { version: 'b1.8.1', kind: 'redstone' }).needsClear).toBe(true);
    const circuit = m.version(s.id, { version: 'b1.8.1', kind: 'redstone', apply: true });
    expect(circuit.session).toMatchObject({ kind: 'redstone', seed: null, preferredFor: null, blocks: [], dug: [] });
    expect(circuit.session?.plot).toEqual({ ...C.DESIGNER_PLOT });
  });

  it('a kind switch that also changes the version converts the kept canvas by the usual rules', () => {
    const m = C.createDesignerMock(100);
    const s = m.create({ version: '1.12.2' }, 'fox');
    const comparator: C.DesignerCell = [6, 0, 5, 149, 0];
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [DUST, comparator] }] });
    const check = m.version(s.id, { version: 'b1.8.1', kind: 'redstone' });
    expect(check).toMatchObject({ version: '1.12.2', target: 'b1.8.1', changed: false, session: null });
    expect(check.needsClear ?? false).toBe(false);
    expect(check.report?.missing).toEqual([{ name: 'Redstone Comparator', count: 1 }]);
    expect(m.get(s.id).blocks).toEqual([DUST, comparator]);
    // The check changed nothing, the kind included: the relabel waits for the apply.
    expect(m.get(s.id).kind).toBe('build');
    const converted = m.version(s.id, { version: 'b1.8.1', kind: 'redstone', apply: true });
    expect(converted.session).toMatchObject({ kind: 'redstone', version: 'b1.8.1', blocks: [DUST] });
  });

  it('saves the authoring with each revision and reopens a revision with its own', () => {
    const m = C.createDesignerMock(100);
    const s = m.create({ kind: 'redstone', version: '1.12.2' }, 'fox');
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [DUST] }] });
    const first = authoring('sum');
    const second = authoring('carry');
    const r1 = C.DesignerSaveResponse.parse(m.save(s.id, { name: 'adder', redstoneAuthoring: first }, 'fox'));
    expect(r1).toMatchObject({ designId: 100, rev: 1, created: true });
    expect(m.get(s.id).redstoneAuthoring).toBe(first);
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [[6, 0, 5, 55, 0]] }] });
    expect(m.save(s.id, { name: 'adder', redstoneAuthoring: second }, 'fox')).toMatchObject({ rev: 2, created: false });
    expect(m.saved[0]).toMatchObject({ kind: 'redstone', redstoneAuthoring: second });

    const newest = C.DesignerSession.parse(m.create({ designId: r1.designId }, 'kiwi'));
    expect(newest).toMatchObject({ kind: 'redstone', redstoneAuthoring: second });
    expect(newest.blocks).toHaveLength(2);
    expect(newest.revisions.map((r) => [r.rev, r.kind])).toEqual([
      [2, 'redstone'],
      [1, 'redstone'],
    ]);
    const old = C.DesignerSession.parse(m.create({ designId: r1.designId, rev: r1.rev }, 'kiwi'));
    expect(old).toMatchObject({ kind: 'redstone', redstoneAuthoring: first, blocks: [DUST] });
    // Saving the older revision again (the revert) carries that revision's authoring, not the newest.
    const copy = m.save(old.id, { name: 'adder as it was', asNew: true }, 'kiwi');
    expect(m.create({ designId: copy.designId }, 'kiwi').redstoneAuthoring).toBe(first);
  });

  it('opens an older revision as the kind it was saved as, as the bot does', () => {
    const m = C.createDesignerMock(100);
    const s = m.create({}, 'fox');
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [DUST] }] });
    const r1 = m.save(s.id, { name: 'gate' }, 'fox');
    m.version(s.id, { version: 'b1.8.1', kind: 'redstone' });
    const r2 = m.save(s.id, { name: 'gate', redstoneAuthoring: authoring('a') }, 'fox');
    expect(r2.rev).toBe(r1.rev + 1);
    expect(m.create({ designId: r1.designId }, 'fox')).toMatchObject({
      kind: 'redstone',
      redstoneAuthoring: authoring('a'),
    });
    expect(m.create({ designId: r1.designId, rev: r2.rev }, 'fox').kind).toBe('redstone');
    expect(m.create({ designId: r1.designId, rev: r1.rev }, 'fox')).toMatchObject({
      kind: 'build',
      redstoneAuthoring: null,
    });
  });

  it('keeps the authoring for redstone designs only', () => {
    const m = C.createDesignerMock(100);
    const s = m.create({ kind: 'redstone' }, 'fox');
    m.canvas(s.id, { ops: [{ kind: 'place', cells: [DUST] }] });
    m.save(s.id, { name: 'circuit', redstoneAuthoring: authoring('a') }, 'fox');
    expect(m.version(s.id, { version: 'b1.8.1', kind: 'build' }).session?.redstoneAuthoring).toBeNull();
    const plain = m.save(s.id, { name: 'plain', asNew: true, redstoneAuthoring: authoring('b') }, 'fox');
    expect(m.saved[0]).toMatchObject({ id: plain.designId, kind: 'build', redstoneAuthoring: null });
    expect(m.create({ designId: plain.designId }, 'fox')).toMatchObject({ kind: 'build', redstoneAuthoring: null });
    expect(m.create({ designId: plain.designId, rev: plain.rev }, 'fox').redstoneAuthoring).toBeNull();
  });
});
