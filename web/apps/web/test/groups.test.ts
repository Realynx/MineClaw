import type { BotStatusView, HiveTreeNode } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { groupBodies } from '@/features/live/groups';

const at = '2026-11-03T06:00:00+00:00';
const row = (name: string, extra: Partial<BotStatusView> = {}): BotStatusView => ({
  name,
  status: 'online',
  at,
  dim: 0,
  hp: 20,
  food: 20,
  budgetMode: 'Normal',
  chat: [],
  ...extra,
});

describe('groupBodies', () => {
  it('groups every body under the hive that drives it: main first, then each hive with its subhive bodies', () => {
    const bots = [
      row('maid_chan_124', { kind: 'subhive', owner: 'maid_chan_111' }),
      row('maid_chan', { kind: 'main' }),
      row('maid_chan_111', { kind: 'hive', owner: 'maid_chan' }),
      row('maid_chan_102', { kind: 'subhive', owner: 'maid_chan_111', status: 'connecting' }),
      row('maid_chan_68', { kind: 'subhive', owner: 'maid_chan' }),
      row('maid_chan_70', { kind: 'script', owner: 'maid_chan' }),
    ];
    const g = groupBodies(bots, 'maid_chan', new Map());
    expect(g.map((x) => [x.id, x.head?.name, x.bodies.map((b) => b.name), x.online])).toEqual([
      ['maid_chan', 'maid_chan', ['maid_chan_68', 'maid_chan_70'], 3],
      ['maid_chan_111', 'maid_chan_111', ['maid_chan_102', 'maid_chan_124'], 2],
    ]);
  });

  it('falls back to the hive tree when the rows carry no kind/owner (an older bot)', () => {
    const tree = new Map<string, HiveTreeNode>([
      ['maid_chan', { id: 'main', name: 'maid_chan', kind: 'main', state: 'running' }],
      ['maid_chan_111', { id: '111', name: 'maid_chan_111', kind: 'hive', owner: 'maid_chan', state: 'running' }],
      ['maid_chan_67', { id: '67', name: 'maid_chan_67', kind: 'subhive', owner: 'maid_chan_111', state: 'running' }],
    ]);
    const g = groupBodies([row('maid_chan'), row('maid_chan_111'), row('maid_chan_67')], 'maid_chan', tree);
    expect(g.map((x) => [x.id, x.bodies.map((b) => b.name)])).toEqual([
      ['maid_chan', []],
      ['maid_chan_111', ['maid_chan_67']],
    ]);
  });

  it('keeps bodies of a hive that is not listed yet (still spawning) in a group of their own', () => {
    const g = groupBodies(
      [row('maid_chan'), row('maid_chan_150', { kind: 'subhive', owner: 'maid_chan_149' })],
      'maid_chan',
      new Map(),
    );
    expect(g.map((x) => [x.id, x.head?.name ?? null, x.bodies.length])).toEqual([
      ['maid_chan', 'maid_chan', 0],
      ['maid_chan_149', null, 1],
    ]);
  });
});
