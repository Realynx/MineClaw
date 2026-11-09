import type { StoryColonyState } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { bodyLabel, designationModel, inDanger, MapPainter, type MapBody } from '@/features/story/mapDraw';
import { buildDragText, columnSpans, dragColor } from '@/features/story/mapDragDraw';
import { preview } from '@/features/story/tools';

/** A 2D context that records every call and property write (in order), for pinning what a layer draws. */
function recorder() {
  const log: string[] = [];
  const fmt = (v: unknown) => (typeof v === 'number' ? String(Math.round(v * 100) / 100) : JSON.stringify(v));
  const ctx = new Proxy(
    {},
    {
      get: (_t, k: string) =>
        k === 'measureText'
          ? (s: string) => {
              log.push(`measureText(${fmt(s)})`);
              return { width: s.length * 6 };
            }
          : (...a: unknown[]) => void log.push(`${k}(${a.map(fmt).join(',')})`),
      set: (_t, k: string, v: unknown) => {
        log.push(`${k}=${fmt(v)}`);
        return true;
      },
    },
  ) as unknown as CanvasRenderingContext2D;
  return { ctx, log };
}

/** Identity matrices: world (x, y) → canvas ((x + 1) / 2 · w, (1 − y) / 2 · h); z is ignored. */
const I = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
function painter(o: { angled?: boolean; cell?: number; cutY?: number | null } = {}) {
  const r = recorder();
  const pt = new MapPainter();
  pt.begin(
    r.ctx,
    { view: I, proj: I, world: I, projInv: I, w: 200, h: 200 },
    o.cell ?? 20,
    o.cutY ?? null,
    !!o.angled,
    { x: 5, y: 9, z: -5 },
    200,
    200,
  );
  return { pt, log: r.log };
}

const pawn = (name: string, extra: Partial<StoryColonyState['pawns'][number]> = {}) =>
  ({ name, display: name.toUpperCase(), ...extra }) as StoryColonyState['pawns'][number];

describe('the Story map overlay painter (mapDraw.ts)', () => {
  it('a ghost cube is its top quad top-down; the top and the faces toward the camera when angled; x-rayed above the cut', () => {
    const flat = painter();
    flat.pt.drawGhosts([[0, 0, 0, 4, 0, 1, 'todo'] as never]);
    expect(flat.log).toMatchSnapshot();
    const angled = painter({ angled: true, cutY: 0 });
    angled.pt.drawGhosts([[0, 0, 0, 4, 0, 1, 'leased'] as never, [0, -1, 0, 4, 0, 1, 'done'] as never]);
    expect(angled.log).toMatchSnapshot();
  });

  it('bodies: selection brackets, the token, drafted and job marks, the name tag (danger in red)', () => {
    const { pt, log } = painter();
    const list: MapBody[] = [
      { key: 'ann', at: { x: 50, y: 60 }, kind: 'pawn', pos: [0, 0, 0], look: 'token', r: 18 },
      { key: 'maid_chan_bo', at: { x: 80, y: 90 }, kind: 'ai', pos: [0, 0, 0], look: 'cut', r: 18 },
      { key: 'gone', at: null, kind: 'pawn', pos: [0, 0, 0], look: 'token', r: 18 },
    ];
    const st = {
      pawns: [pawn('ann', { drafted: true, hp: 5, job: { type: 'chop' } as never })],
      ai: [{ name: 'maid_chan_bo', kind: 'sub' }],
    } as unknown as StoryColonyState;
    pt.drawBodies(list, new Set(['ann']), st);
    expect(log).toMatchSnapshot();
  });

  it.each([false, true])('draws pawn numbers instead of old nicknames in the map nameplates (angled=%s)', (angled) => {
    const { pt, log } = painter({ angled });
    const name = 'maid_chan_109';
    const st = { pawns: [pawn(name, { display: 'Old nickname' })] } as unknown as StoryColonyState;
    pt.drawBodies([{ key: name, at: { x: 50, y: 60 }, kind: 'pawn', pos: [0, 0, 0], look: 'token', r: 18 }], new Set([name]), st);
    pt.drawBeds([{ pos: [0, 0, 0], pawn: name }], st.pawns, null);
    expect(log.filter((s) => s.startsWith('fillText("109",'))).toHaveLength(2);
    expect(log.join('\n')).not.toContain('Old nickname');
    expect(log.join('\n')).not.toContain('maid_chan');
    expect(st.pawns[0]!.name).toBe(name);
    expect(st.pawns[0]!.display).toBe('Old nickname');
  });

  it('chests: a registered double chest draws once with its priority pips; a bench shows its bill count; selection boxes', () => {
    const { pt, log } = painter({ cell: 10 });
    pt.vw = 1000; // this fixture tests all four marks; keep their identity-projected x positions on screen
    const st = {
      storages: [{ chest: [0, 0, 0], doubleWith: [1, 0, 0], priority: 'important', filter: { allow: [], deny: [] } }],
      workbenches: [{ pos: [2, 0, 0], bills: [{}, {}] }],
    } as unknown as StoryColonyState;
    pt.drawChests(
      [
        { pos: [0, 0, 0], id: 54 },
        { pos: [1, 0, 0], id: 54 },
        { pos: [3, 0, 0], id: 54 },
        { pos: [2, 0, 0], id: 58 },
      ],
      st,
      { chests: [[1, 0, 0]], bench: [2, 0, 0] },
      new Map(),
      14,
    );
    expect(log).toMatchSnapshot();
  });

  it('designations: a mark per target (a tree at its top log), rings on failing and leased ones; failed rings are reported', () => {
    const { pt, log } = painter();
    const list = [
      {
        id: 1,
        kind: 'chop',
        blocks: [
          [0, -2, 0],
          [0, -1, 0],
          [0, -3, 0],
        ],
        status: 'failed',
        reason: 'no axe',
        pawn: 'ann',
      },
      { id: 2, kind: 'mine', block: [-1, -1, 0], fails: 1 },
      { id: 3, kind: 'cut', block: [9, 9, 0], wait: 'Needs shears' },
    ] as unknown as StoryColonyState['designations'];
    const model = designationModel(list);
    expect(model.marks.map((m) => m.cell)).toEqual([
      [0, -1, 0],
      [-1, -1, 0],
      [9, 9, 0],
    ]);
    expect(model.failed.map((f) => [f.id, f.tone ?? 'fail'])).toEqual([
      [1, 'fail'],
      [3, 'wait'],
    ]);
    expect(designationModel(list)).toBe(model); // once per list
    expect(pt.drawDesignations(model)).toEqual([{ id: 1, cluster: [1], x: 150, y: 100 }]);
    expect(log).toMatchSnapshot();
  });

  it('labels and colours', () => {
    expect(inDanger(pawn('a', { hp: 8 }))).toBe(true);
    expect(inDanger(pawn('a', { food: 5 }))).toBe(false);
    expect(inDanger(undefined)).toBe(false);
    const ai = (kind: string) => ({ name: 'maid_chan_bo', kind }) as NonNullable<StoryColonyState['ai']>[number];
    expect(bodyLabel('k', pawn('ann'), undefined)).toBe('ANN');
    expect(bodyLabel('maid_chan_109', pawn('maid_chan_109', { display: 'Old nickname' }), undefined)).toBe('109');
    expect(bodyLabel('maid_chat_1337', pawn('maid_chat_1337', { display: 'Another nickname' }), undefined)).toBe('1337');
    expect(bodyLabel('HumanPlayer109', undefined, undefined)).toBe('HumanPlayer109');
    expect(bodyLabel('k', undefined, ai('main'))).toBe('main');
    expect(bodyLabel('k', undefined, ai('sub'))).toBe('bo');
    expect(bodyLabel('k', undefined, undefined)).toBe('k');
    expect(dragColor({ kind: 'zone', id: 'farm', op: 'remove' })).toBe('#ff6b6b');
    expect(dragColor({ kind: 'storage' })).toBe('#d9a25f');
    expect(dragColor({ kind: 'designate', id: 'chop' })).toBe('#e8a85a');
    expect(
      columnSpans([
        [0, 5, 0],
        [0, 3, 0],
        [1, 4, 1],
      ]),
    ).toEqual([
      [0, 0, 5, 3, 2],
      [1, 1, 4, 4, 1],
    ]);
    expect(buildDragText(preview({ kind: 'floor', area: [0, 64, 0, 1, 64, 0], material: 'oak_planks' }))).toBe(
      '2 blocks · 2 oak planks',
    );
    expect(buildDragText(preview({ kind: 'remove', area: [0, 64, 0, 0, 64, 0], material: 'stone' }))).toBe('1 block');
  });
});
