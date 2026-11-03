import { StoryBillsRequest, StoryBuildRequest, StoryDesignationRequest, StoryDirectiveRequest, StoryOrderRequest, StoryScheduleRequest, StoryStorageRequest, type StoryColonyState } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { isTyping } from '@/features/world/directives';
import { boxPick, cellAt, cellRect, edgePan, isDrag, nearest, project, rayAt, rectOf, zoomAt, type ViewMats } from '@/features/story/math';
import {
  applyPaint,
  boxBodies,
  clickBody,
  cyclePriority,
  dispatch,
  EMPTY_SELECTION,
  mergeColony,
  nextSort,
  orderAi,
  orderPawns,
  paintRequests,
  paintSpan,
  pasteRequest,
  pawnMood,
  reorderNames,
  sortPawns,
  splitBodies,
} from '@/features/story/model';
import { addBillBody, billProgress, billSummary, buildTree, draftOf, itemAllowed, newDraft, nodeState, pasteBody, reorderBills, searchRecipes, toggleItem, toggleNode, updateBillBody } from '@/features/story/storage';
import {
  buildItem,
  contextMenu,
  designationBody,
  designationDirective,
  entitiesIn,
  faceCell,
  keyAction,
  levelCutY,
  lineEnd,
  nextLevel,
  orderBody,
  preview,
  previewTargets,
  rotateFacing,
  storeyOf,
  toolYRange,
  DESIGNATE_TOOLS,
  type BuildOpts,
} from '@/features/story/tools';
import { STORY_DEFAULT_RECIPES } from '@mineclaw/contracts';

/** A straight-down orthographic-like camera: identity rotation looking −y, world x → screen x, world z → screen y. */
function topMats(cx: number, cz: number, upp: number, w = 800, h = 600): ViewMats {
  // view: rotate so that -y is forward (camera looks down), +x right, -z up; translate to (cx, 100, cz).
  // Column-major. Rows of the rotation: x' = x, y' = -z, z' = y (camera space looks along -z').
  const view = [1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, -cx, cz, -100, 1];
  const sx = 2 / (w * upp), sy = 2 / (h * upp);
  const proj = [sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, -0.01, 0, 0, 0, -1, 1];
  const inv = (m: number[]) => {
    // generic 4x4 inverse (column-major)
    const a = m, o = new Array<number>(16);
    const [a00, a01, a02, a03, a10, a11, a12, a13, a20, a21, a22, a23, a30, a31, a32, a33] = a as [number, number, number, number, number, number, number, number, number, number, number, number, number, number, number, number];
    const b00 = a00 * a11 - a01 * a10, b01 = a00 * a12 - a02 * a10, b02 = a00 * a13 - a03 * a10, b03 = a01 * a12 - a02 * a11;
    const b04 = a01 * a13 - a03 * a11, b05 = a02 * a13 - a03 * a12, b06 = a20 * a31 - a21 * a30, b07 = a20 * a32 - a22 * a30;
    const b08 = a20 * a33 - a23 * a30, b09 = a21 * a32 - a22 * a31, b10 = a21 * a33 - a23 * a31, b11 = a22 * a33 - a23 * a32;
    const det = 1 / (b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06);
    o[0] = (a11 * b11 - a12 * b10 + a13 * b09) * det; o[1] = (a02 * b10 - a01 * b11 - a03 * b09) * det;
    o[2] = (a31 * b05 - a32 * b04 + a33 * b03) * det; o[3] = (a22 * b04 - a21 * b05 - a23 * b03) * det;
    o[4] = (a12 * b08 - a10 * b11 - a13 * b07) * det; o[5] = (a00 * b11 - a02 * b08 + a03 * b07) * det;
    o[6] = (a32 * b02 - a30 * b05 - a33 * b01) * det; o[7] = (a20 * b05 - a22 * b02 + a23 * b01) * det;
    o[8] = (a10 * b10 - a11 * b08 + a13 * b06) * det; o[9] = (a01 * b08 - a00 * b10 - a03 * b06) * det;
    o[10] = (a30 * b04 - a31 * b02 + a33 * b00) * det; o[11] = (a21 * b02 - a20 * b04 - a23 * b00) * det;
    o[12] = (a11 * b07 - a10 * b09 - a12 * b06) * det; o[13] = (a00 * b09 - a01 * b07 + a02 * b06) * det;
    o[14] = (a31 * b01 - a30 * b03 - a32 * b00) * det; o[15] = (a20 * b03 - a21 * b01 + a22 * b00) * det;
    return o;
  };
  return { view, proj, world: inv(view), projInv: inv(proj), w, h };
}

describe('map math', () => {
  const m = topMats(10, 20, 0.1);

  it('projects the camera target to the screen centre and casts rays back to the same cell', () => {
    const s = project(m, 10, 64, 20)!;
    expect(s.x).toBeCloseTo(400);
    expect(s.y).toBeCloseTo(300);
    const east = project(m, 11, 64, 20)!;
    expect(east.x).toBeCloseTo(410); // 0.1 world units per pixel
    const south = project(m, 10, 64, 21)!;
    expect(south.y).toBeCloseTo(310); // north is up
    expect(cellAt(m, 405, 315, 65)).toEqual([10, 64, 21]);
    const r = rayAt(m, 400, 300);
    expect(r.d[1]).toBeCloseTo(-1);
  });

  it('box selection: rectangles from any drag direction, points inside, a click is not a drag', () => {
    expect(rectOf({ x: 50, y: 80 }, { x: 10, y: 20 })).toEqual({ x: 10, y: 20, w: 40, h: 60 });
    const items = [
      { key: 'ash', at: { x: 20, y: 30 } },
      { key: 'bryn', at: { x: 200, y: 30 } },
      { key: 'gone', at: null },
    ];
    expect(boxPick(items, rectOf({ x: 0, y: 0 }, { x: 100, y: 100 }))).toEqual(['ash']);
    expect(nearest(items, { x: 205, y: 34 })?.key).toBe('bryn');
    expect(nearest(items, { x: 400, y: 400 })).toBeNull();
    expect(isDrag({ x: 0, y: 0 }, { x: 2, y: 2 })).toBe(false);
    expect(isDrag({ x: 0, y: 0 }, { x: 4, y: 3 })).toBe(true);
    expect(cellRect([5, 64, 9], [2, 64, -1])).toEqual([2, -1, 5, 9]);
  });

  it('zooms around the cursor and pans at the screen edge', () => {
    const z = zoomAt({ x: 0, z: 0 }, 40, -1, { x: 10, z: 0 });
    expect(z.zoom).toBeCloseTo(40 / 1.15);
    // the cursor point stays where it is: target moved toward it in proportion
    expect(z.x).toBeCloseTo(10 - 10 / 1.15);
    expect(zoomAt({ x: 0, z: 0 }, 220, 3, { x: 0, z: 0 }).zoom).toBe(220);
    expect(edgePan({ x: 2, y: 300 }, 800, 600)).toEqual([-1, 0]);
    expect(edgePan({ x: 400, y: 599 }, 800, 600)).toEqual([0, 1]);
    expect(edgePan({ x: 400, y: 300 }, 800, 600)).toEqual([0, 0]);
  });
});

const state = (over: Partial<StoryColonyState> = {}): StoryColonyState =>
  ({
    rev: 5,
    at: 0,
    workTypes: [],
    pawns: [
      { name: 'p_ash', display: 'Ash', kind: 'pawn', queue: [], skills: { chop: { level: 12 } }, traits: [], priorities: { chop: 1 }, schedule: [], inventory: [], hp: 20, food: 20, state: 'working' },
      { name: 'p_bryn', display: 'Bryn', kind: 'pawn', queue: [], skills: { chop: { level: 3 } }, traits: [], priorities: {}, schedule: [], inventory: [], hp: 5, food: 20, state: 'working' },
    ],
    zones: [],
    designations: [],
    orders: [],
    alerts: [],
    ai: [
      { name: 'maid_chan_102', kind: 'subhive', owner: 'maid_chan_109' },
      { name: 'maid_chan', kind: 'main' },
      { name: 'maid_chan_109', kind: 'hive', owner: 'maid_chan' },
    ],
    ...over,
  });

describe('selection and the colonist bar', () => {
  it('click selects only it, Shift adds, Ctrl toggles; a box replaces or adds', () => {
    let s = clickBody(EMPTY_SELECTION, 'p_ash');
    expect(s.bodies).toEqual(['p_ash']);
    s = clickBody(s, 'p_bryn', { shift: true });
    expect(s.bodies).toEqual(['p_ash', 'p_bryn']);
    s = clickBody(s, 'p_ash', { ctrl: true });
    expect(s.bodies).toEqual(['p_bryn']);
    expect(boxBodies(s, ['p_ash'], true).bodies).toEqual(['p_bryn', 'p_ash']);
    expect(boxBodies(s, ['p_ash'], false).bodies).toEqual(['p_ash']);
  });

  it('splits pawns from AI bodies; AI in bar order main → hive → its bodies; pawns in the saved order', () => {
    expect(splitBodies(state(), ['p_ash', 'maid_chan'])).toEqual({ pawns: ['p_ash'], ai: ['maid_chan'] });
    expect(orderAi(state().ai!).map((a) => a.name)).toEqual(['maid_chan', 'maid_chan_109', 'maid_chan_102']);
    expect(orderPawns(state().pawns, ['p_bryn']).map((p) => p.name)).toEqual(['p_bryn', 'p_ash']);
    expect(reorderNames(['a', 'b', 'c'], 'c', 'a')).toEqual(['c', 'a', 'b']);
    expect(reorderNames(['a', 'b', 'c'], 'a', null)).toEqual(['b', 'c', 'a']);
    expect(pawnMood(state().pawns[1]!)).toBe('danger');
    expect(pawnMood({ ...state().pawns[0]!, drafted: true })).toBe('drafted');
  });

  it('merges deltas: changed pawns replace, removed designations go, older revisions are ignored', () => {
    const base = state({ designations: [{ id: 'd1', kind: 'chop', block: [1, 64, 1] }, { id: 'd2', kind: 'mine', block: [2, 64, 2] }] });
    const next = mergeColony(base, { rev: 6, at: 1, full: false, workTypes: [], pawns: [{ ...base.pawns[0]!, state: 'idle' }], zones: [], designations: [], orders: [], alerts: [{ id: 'a1', at: 1, level: 'warn', text: 'x' }], removed: { designations: ['d1'] } });
    expect(next.pawns).toHaveLength(2);
    expect(next.pawns.find((p) => p.name === 'p_ash')?.state).toBe('idle');
    expect(next.designations.map((d) => d.id)).toEqual(['d2']);
    expect(next.alerts).toHaveLength(1);
    expect(mergeColony(next, { ...next, rev: 3, full: false })).toBe(next);
  });
});

describe('the Work grid', () => {
  it('a click cycles off → 1 → 2 → 3 → 4 → off; back goes the other way', () => {
    const seq = [0];
    for (let i = 0; i < 5; i++) seq.push(cyclePriority(seq.at(-1)!));
    expect(seq).toEqual([0, 1, 2, 3, 4, 0]);
    expect(cyclePriority(1, true)).toBe(0);
    expect(cyclePriority(0, true)).toBe(4);
  });

  it('sorts by a work column (skill, best first, then ascending) and by name', () => {
    const s = state();
    let k = nextSort(null, 'chop');
    expect(sortPawns(s.pawns, k).map((p) => p.display)).toEqual(['Ash', 'Bryn']);
    k = nextSort(k, 'chop');
    expect(sortPawns(s.pawns, k).map((p) => p.display)).toEqual(['Bryn', 'Ash']);
    expect(nextSort(k, 'chop')).toBeNull();
  });
});

describe('the Schedule grid', () => {
  it('a drag paints the rectangle of rows × hours it spans, one request per pattern', () => {
    const cells = paintSpan(['a', 'b', 'c'], { row: 0, hour: 5 }, { row: 1, hour: 3 });
    expect([...cells.keys()]).toEqual(['a', 'b']);
    expect(cells.get('a')).toEqual([3, 4, 5]);
    const reqs = paintRequests(cells, 'sleep');
    expect(reqs).toEqual([{ pawns: ['a', 'b'], hours: { '3': 'sleep', '4': 'sleep', '5': 'sleep' } }]);
    for (const r of reqs) expect(StoryScheduleRequest.safeParse(r).success).toBe(true);
    expect(applyPaint([], [0, 23], 'free')[23]).toBe('free');
    const paste = pasteRequest(['work', 'sleep'], ['c']);
    expect(paste.hours['1']).toBe('sleep');
    expect(Object.keys(paste.hours)).toHaveLength(2);
  });
});

describe('orders, designations and the mixed selection', () => {
  it('the right-click menu fits the target, and order bodies are valid (Shift queues)', () => {
    const tree = contextMenu({ kind: 'block', x: 6, y: 72, z: 6, id: 17, name: 'log' }, true, false);
    expect(tree.map((i) => i.id)).toEqual(['chop', 'move', 'hold', 'stop']);
    const chop = orderBody(['p_ash', 'p_bryn'], tree[0]!, true)!;
    expect(chop).toEqual({ pawns: ['p_ash', 'p_bryn'], order: { kind: 'chop', target: { x: 6, y: 72, z: 6 } }, queue: true });
    expect(StoryOrderRequest.safeParse(chop).success).toBe(true);
    expect(contextMenu({ kind: 'mob', entity: 9, name: 'cow', animal: true, x: 1, y: 2, z: 3 }, true, false)[0]).toMatchObject({ id: 'attack', label: 'Hunt cow', order: { kind: 'attack', target: { entity: 9 } } });
    expect(contextMenu({ kind: 'item', entity: 4, name: 'cobble', x: 1, y: 2, z: 3 }, true, false)[0]?.order).toEqual({ kind: 'haul', target: { entity: 4 } });
    expect(contextMenu({ kind: 'block', x: 1, y: 2, z: 3, id: 54, name: 'chest' }, false, false).map((i) => i.id)).toEqual(['storage']);
    expect(contextMenu({ kind: 'ghost', build: 'b1', label: 'Wall', x: 0, y: 0, z: 0 }, false, false).map((i) => i.action)).toEqual(['first-build', 'cancel-build']);
    expect(orderBody([], tree[0]!, false)).toBeNull();
  });

  it('pawns get the director request, AI bodies one directive with the same payload', () => {
    const area = [0, 60, 0, 9, 80, 9] as [number, number, number, number, number, number];
    const body = designationBody('chop', area);
    expect(StoryDesignationRequest.safeParse(body).success).toBe(true);
    const plan = dispatch({ pawns: ['p_ash'], ai: ['maid_chan'] }, { route: 'story.designations', body }, designationDirective('chop', area, [[6, 66, 6]]), 'r-12345678', 'designated');
    expect(plan.director?.route).toBe('story.designations');
    expect(plan.directive).toEqual({ bots: ['maid_chan'], directive: { kind: 'chop', area, targets: [[6, 66, 6]] }, requestId: 'r-12345678' });
    expect(StoryDirectiveRequest.safeParse(plan.directive).success).toBe(true);
    expect(plan.summary).toBe('colony: designated · sent to maid_chan (AI)');
    // An order needs pawns; with only AI selected only the directive goes.
    const only = dispatch({ pawns: [], ai: ['maid_chan'] }, { route: 'story.orders', body: {} }, { kind: 'move', targets: [[1, 2, 3]] }, 'r-12345678');
    expect(only.director).toBeNull();
    expect(only.summary).toBe('sent to maid_chan (AI)');
    expect(dispatch({ pawns: ['a', 'b', 'c'], ai: [] }, { route: 'story.orders', body: {} }, null, 'r-12345678').summary).toBe('3 pawns ordered');
    expect(designationBody('cancel', area)).toEqual({ op: 'remove', area });
    expect(designationBody('mine', area, 'ores')).toEqual({ op: 'add', kind: 'mine', area, filter: 'ores' });
  });

  it('the designate preview counts whole trees and only ripe crops; drops and animals by entity', () => {
    // scan rows: x, y, z, id, meta, below
    const scan = [6, 66, 6, 17, 0, 2, 6, 67, 6, 17, 0, 17, 9, 66, 9, 17, 0, 2, 1, 70, 1, 59, 7, 60, 2, 70, 1, 59, 2, 60];
    expect(previewTargets('chop', scan)).toMatchObject({ count: 2 });
    expect(previewTargets('harvest', scan).cells).toEqual([[1, 70, 1]]);
    const hunt = DESIGNATE_TOOLS.find((t) => t.id === 'hunt')!;
    const ents = [{ id: 1, kind: 'mob', type: 92, x: 2.5, y: 70, z: 2.5 }, { id: 2, kind: 'mob', type: 54, x: 3, y: 70, z: 3 }, { id: 3, kind: 'item', x: 2, y: 70, z: 2 }];
    expect(entitiesIn(hunt, [0, 0, 5, 5], ents).map((e) => e.id)).toEqual([1]);
    expect(entitiesIn(DESIGNATE_TOOLS.find((t) => t.id === 'haul')!, [0, 0, 5, 5], ents).map((e) => e.id)).toEqual([3]);
    expect(toolYRange('mine', 64, 3, 68)).toEqual([63, 67]);
  });
});

describe('the Architect', () => {
  const o: BuildOpts = { height: 3, roof: 'flat', overhang: false, facing: 's', material: 'cobblestone' };

  it('walls are a rectangle outline (or an Alt line) rising the wall height; floors fill one below', () => {
    const wall = buildItem('wall', [0, 65, 0], [4, 65, 4], o);
    expect(wall).toEqual({ kind: 'wall', area: [0, 65, 0, 4, 65, 4], material: 'cobblestone', height: 3 });
    expect(preview(wall).blocks).toHaveLength(48);
    const line = buildItem('wall', [0, 65, 0], [5, 65, 2], { ...o, line: true });
    expect(line.area).toEqual([0, 65, 0, 5, 65, 0]);
    expect(lineEnd([0, 65, 0], [1, 65, 6])).toEqual([0, 65, 6]);
    expect(buildItem('floor', [0, 65, 0], [2, 65, 2], o).area).toEqual([0, 64, 0, 2, 64, 2]);
  });

  it('a roof sits on the walls; a gable rises to the ridge; requests are valid', () => {
    const roof = buildItem('roof', [0, 65, 0], [6, 65, 4], { ...o, roof: 'gable', overhang: true });
    expect(roof.area?.[1]).toBe(68);
    expect(roof.roof).toEqual({ style: 'gable', overhang: 1 });
    const blocks = preview(roof).blocks;
    expect(Math.max(...blocks.map((b) => b[1]))).toBe(68 + 3);
    expect(StoryBuildRequest.safeParse({ op: 'add', items: [roof] }).success).toBe(true);
    expect(StoryBuildRequest.safeParse({ op: 'add', items: [buildItem('door', [2, 65, 0], [2, 65, 0], o), buildItem('furniture:bed', [1, 65, 1], [1, 65, 1], o)] }).success).toBe(true);
    expect(preview(buildItem('block', [0, 65, 0], [0, 65, 0], o), 0).reason).toBe('short of 1');
  });

  it('levels, the cut, facing and face snapping', () => {
    expect(levelCutY(64, 3, true)).toBe(68);
    expect(levelCutY(64, 3, false)).toBeNull();
    expect(nextLevel(64, 3, 1, false)).toBe(68);
    expect(nextLevel(64, 3, -1, true)).toBe(63);
    expect(storeyOf(68, 64, 3)).toBe(2);
    expect(storeyOf(64, 64, 3)).toBe(1);
    expect(rotateFacing('n', 1)).toBe('e');
    expect(rotateFacing('n', -1)).toBe('w');
    expect(faceCell({ x: 3, y: 65, z: 3, face: 1 })).toEqual([3, 66, 3]);
    expect(faceCell({ x: 3, y: 65, z: 3, face: 5 })).toEqual([4, 65, 3]);
    expect(faceCell({ x: 3, y: 65, z: 3, face: 2 })).toEqual([3, 65, 2]);
  });
});

describe('storage settings', () => {
  const tree = buildTree(null);
  const res = tree.find((n) => n.id === 'resources')!;
  const wood = res.children.find((n) => n.id === 'wood')!;

  it('tri-state: a parent allows its children, a child deny makes the parent partial; deny wins', () => {
    let f = toggleNode({ allow: [], deny: [] }, res);
    expect(f).toEqual({ allow: ['resources'], deny: [] });
    expect(nodeState(f, wood)).toBe('on');
    f = toggleNode(f, wood);
    expect(f).toEqual({ allow: ['resources'], deny: ['wood'] });
    expect(nodeState(f, res)).toBe('partial');
    expect(nodeState(f, wood)).toBe('off');
    f = toggleNode(f, wood);
    expect(f).toEqual({ allow: ['resources'], deny: [] });
    f = toggleItem(f, wood, '17:-1');
    expect(itemAllowed(f, wood, '17:-1')).toBe(false);
    expect(nodeState(f, wood)).toBe('partial');
    expect(toggleNode(f, res)).toEqual({ allow: ['resources'], deny: [] });
  });

  it('copy / paste and request bodies', () => {
    const body = pasteBody({ filter: { allow: ['food'], deny: [] }, priority: 'preferred' }, [[1, 64, 2], [5, 64, 2]]);
    expect(body).toEqual({ op: 'set', chests: [[1, 64, 2], [5, 64, 2]], filter: { allow: ['food'], deny: [] }, priority: 'preferred' });
    expect(StoryStorageRequest.safeParse(body).success).toBe(true);
  });
});

describe('bills', () => {
  const sticks = STORY_DEFAULT_RECIPES.find((r) => r.id === 'sticks')!;

  it('the dialog state becomes add / update / reorder requests', () => {
    const d = { ...newDraft(sticks, 'until', 64), radius: 20, pawn: 'p_ash' };
    const add = addBillBody([1, 64, 2], d);
    expect(add.spec).toMatchObject({ recipe: 'sticks', mode: 'until', target: 64, radius: 20, pawn: 'p_ash', store: 'best' });
    expect(StoryBillsRequest.safeParse(add).success).toBe(true);
    const bill = { id: 'b1', recipe: 'sticks', mode: 'until', target: 64, done: 3, paused: false, ingredients: d.ingredients, radius: 20, pawn: 'p_ash', minSkill: null, store: 'best' as const };
    expect(updateBillBody([1, 64, 2], bill, draftOf(bill))).toBeNull();
    const up = updateBillBody([1, 64, 2], bill, { ...draftOf(bill), radius: 48, pawn: 'any' })!;
    expect(up).toEqual({ op: 'update', workbench: [1, 64, 2], id: 'b1', spec: { radius: 48, pawn: 'any' } });
    expect(StoryBillsRequest.safeParse(up).success).toBe(true);
    expect(reorderBills([1, 64, 2], ['a', 'b', 'c'], 2, 0).ids).toEqual(['c', 'a', 'b']);
    expect(billSummary({ mode: 'count', target: 8 })).toBe('Do 8 times');
    expect(billSummary({ mode: 'until', target: 64 })).toBe('Do until you have 64');
    expect(billSummary({ mode: 'forever', target: 1 })).toBe('Do forever');
    expect(billProgress({ mode: 'count', target: 8, done: 3 })).toBe('3/8');
  });

  it('recipe search matches labels and ingredients, grouped by category', () => {
    const g = searchRecipes(STORY_DEFAULT_RECIPES, 'stick');
    expect(g.flatMap((x) => x.recipes.map((r) => r.id))).toContain('sticks');
    expect(g.flatMap((x) => x.recipes.map((r) => r.id))).toContain('torch'); // uses sticks
    expect(searchRecipes(STORY_DEFAULT_RECIPES, '').find((x) => x.group === 'food_cooked')?.label).toBe('Meals');
  });
});

describe('keys', () => {
  it('maps the genre controls and leaves 1–9 alone', () => {
    expect(keyAction({ code: 'KeyR', key: 'r' }, false)).toBe('draft');
    expect(keyAction({ code: 'Space', key: ' ' }, false)).toBe('pause');
    expect(keyAction({ code: 'KeyC', key: 'c' }, false)).toBe('tool:chop');
    expect(keyAction({ code: 'KeyP', key: 'p' }, false)).toBe('tab:work');
    expect(keyAction({ code: 'PageUp', key: 'PageUp' }, false)).toBe('levelUp');
    expect(keyAction({ code: 'Equal', key: '=' }, true)).toBe('heightUp');
    expect(keyAction({ code: 'Equal', key: '=' }, false)).toBeNull();
    expect(keyAction({ code: 'Digit1', key: '1' }, false)).toBeNull();
    expect(keyAction({ code: 'KeyC', key: 'c', ctrlKey: true }, false)).toBeNull();
  });

  it('never fires while typing in a field', () => {
    const input = document.createElement('input');
    document.body.appendChild(input);
    expect(isTyping(input)).toBe(true);
    const ta = document.createElement('textarea');
    expect(isTyping(ta)).toBe(true);
    expect(isTyping(document.body)).toBe(false);
    input.remove();
  });
});
