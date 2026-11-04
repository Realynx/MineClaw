/**
 * A golden run of the mock director's growing-zone bills (storySim.ts `billOp`) and the zone status it reports (`growStatus`,
 * `quarryStatus`): every bill op, valid and refused, each answer recorded with the zones the state shows after it.
 */
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StorySim, type StoryWorld } from '../src/testing.js';

const flat: StoryWorld = {
  blockAt: (_x, y) => (y > 63 ? [0, 0] : y === 63 ? [2, 0] : y > 59 ? [3, 0] : [1, 0]),
  ground: () => 63,
  entities: () => [],
};

describe('growing-zone bills (golden)', () => {
  let t = 1_762_000_000_000;
  beforeEach(() => {
    t = 1_762_000_000_000;
    let a = 7;
    vi.spyOn(Math, 'random').mockImplementation(() => ((a = (a * 16807) % 2147483647) - 1) / 2147483646);
    vi.spyOn(Date, 'now').mockImplementation(() => t);
  });
  afterEach(() => vi.restoreAllMocks());

  it('answers every bill op and reports the zones exactly as before', () => {
    const sim = new StorySim({ world: flat, defaultHome: [0, 63, 0], now: () => t });
    const { id } = sim.create({ name: 'Bills', pawns: 1 });
    const trace: unknown[] = [];
    const zone = (label: string, body: unknown) => {
      try {
        trace.push([label, sim.zones(id, body as never)]);
      } catch (e) {
        const err = e as { status?: number; message?: string };
        trace.push([label, 'error', err.status ?? null, err.message ?? String(e)]);
      }
      trace.push(['zones', sim.state(id).zones]);
    };
    zone('farm', { op: 'add', kind: 'farm', area: [0, 63, 10, 4, 63, 13], plant: 'wheat' });
    zone('cane', { op: 'add', kind: 'farm', area: [10, 63, 10, 12, 63, 12], plant: 'sugar_cane' });
    zone('chop', { op: 'add', kind: 'chop', area: [20, 63, 20, 22, 63, 22] });
    zone('quarry', { op: 'add', kind: 'mine', area: [-10, 63, -10, -7, 63, -7], mode: 'quarry' });
    zone('ores', { op: 'add', kind: 'mine', area: [-20, 63, -20, -18, 63, -18] });
    const ids = () => sim.state(id).zones.map((z) => String(z.id));
    const [farm, cane, chop] = ids();
    zone('no zone', { op: 'bill_add', id: 'nope', plant: 'wheat' });
    zone('not farm', { op: 'bill_add', id: chop, plant: 'wheat' });
    zone('bad plant', { op: 'bill_add', id: farm, plant: 'rice' });
    zone('add carrots', { op: 'bill_add', id: farm, plant: 'carrots', mode: 'count', target: 20 });
    zone('add pumpkin', { op: 'bill_add', id: farm, plant: 'pumpkin', paused: true });
    zone('add melon until', { op: 'bill_add', id: farm, plant: 'melon', mode: 'until', target: 5 });
    zone('add potatoes', { op: 'bill_add', id: farm, plant: 'potatoes' });
    const bills = () =>
      sim
        .state(id)
        .zones.find((z) => String(z.id) === farm)!
        .bills!.map((b) => String(b.id));
    const [b1, b2, b3, b4] = bills();
    zone('update plant', { op: 'bill_update', id: farm, bill: b1, plant: 'beetroot' });
    zone('update mode', { op: 'bill_update', id: farm, bill: b2, mode: 'until', target: 3, paused: false });
    zone('update target', { op: 'bill_update', id: farm, bill: b3, target: 9 });
    zone('update bad plant', { op: 'bill_update', id: farm, bill: b3, plant: 'rice' });
    zone('update no bill', { op: 'bill_update', id: farm, bill: 'zz', target: 1 });
    zone('pause', { op: 'bill_pause', id: farm, bill: b1 });
    zone('resume', { op: 'bill_resume', id: farm, bill: b1 });
    zone('reorder', { op: 'bill_reorder', id: farm, ids: [b4, b2, b4] });
    zone('reorder bad', { op: 'bill_reorder', id: farm, ids: ['zz'] });
    zone('remove', { op: 'bill_remove', id: farm, bill: b3 });
    for (let i = 0; i < 16; i++)
      zone(`fill ${i}`, { op: 'bill_add', id: cane, plant: 'sugar_cane', mode: 'count', target: i });
    zone('too many', { op: 'bill_add', id: cane, plant: 'cactus' });
    for (const b of bills()) zone(`pause ${b}`, { op: 'bill_pause', id: farm, bill: b });
    for (const b of bills()) zone(`remove ${b}`, { op: 'bill_remove', id: farm, bill: b });
    const text = JSON.stringify(trace);
    if (process.env.STORY_GOLDEN_DUMP) writeFileSync(`${process.env.STORY_GOLDEN_DUMP}.bills`, text);
    expect(trace.filter((e) => (e as unknown[])[1] === 'error')).toMatchSnapshot();
    expect(createHash('sha256').update(text).digest('hex')).toMatchSnapshot();
  });
});
