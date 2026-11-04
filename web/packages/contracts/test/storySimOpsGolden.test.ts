/**
 * Golden runs of the mock director's policy and material-rule ops (storySimGear.ts `policiesOp`, `materialRulesOp`): every
 * op, valid and refused, recorded with the answer or the error and the state after. They pin the ops so their code can be
 * restructured without changing what the mocks answer.
 */
import { createHash } from 'node:crypto';
import { writeFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { STORY_TOOL_KINDS, type StoryPawn } from '../src/index.js';
import { DEFAULT_POLICIES, SIM_MATERIALS, materialRulesOp, policiesOp, type SimGear } from '../src/storySimGear.js';

function gearState(): SimGear {
  return {
    policies: JSON.parse(JSON.stringify(DEFAULT_POLICIES)) as SimGear['policies'],
    rules: [],
    seq: 1,
    heldBefore: new Map(),
    stocked: false,
  };
}

function attempt(f: () => unknown): string {
  try {
    return JSON.stringify(f());
  } catch (e) {
    const err = e as Error & { status?: number };
    return `! ${err.status} ${err.message}`;
  }
}

describe('policy and material-rule ops (golden)', () => {
  it('policiesOp answers and changes exactly as before', () => {
    const gear = gearState();
    const pawns = new Map<string, { p: StoryPawn }>(
      ['ann', 'bob', 'cy'].map((n) => [n, { p: { name: n, policy: null } as unknown as StoryPawn }]),
    );
    const bodies: Array<Parameters<typeof policiesOp>[2]> = [
      { op: 'set' },
      { op: 'set', policy: { name: '   ' } },
      { op: 'set', policy: { name: 'x'.repeat(41) } },
      { op: 'set', policy: { name: ' Miner ' } },
      {
        op: 'set',
        policy: { name: 'Hunter', tools: ['sword'], weapon: 'none', armor: 'always', food: 3 },
      },
      { op: 'set', policy: { id: 'nope', name: 'Ghost' } },
      {
        op: 'set',
        policy: { id: 'pol1', name: 'Miner 2', toolsByWork: false, spare: true, tools: ['pickaxe', 'shovel'] },
      },
      { op: 'set', policy: { id: 'default', name: 'Everyone', food: 1 } },
      { op: 'assign', id: 'pol1', pawns: ['ann', 'bob'] },
      { op: 'assign', id: 'pol2', pawns: ['cy', 'zed'] },
      { op: 'assign', id: 'default', pawns: ['bob'] },
      { op: 'assign', id: 'pol2' },
      { op: 'assign', id: 'missing', pawns: ['ann'] },
      { op: 'remove', id: 'default' },
      { op: 'remove', id: 'pol1' },
      { op: 'remove' },
      { op: 'rename', id: 'pol2' },
      { op: 'rename' },
    ];
    const trace = bodies.map(
      (b) =>
        `${JSON.stringify(b)} → ${attempt(() => policiesOp(gear, pawns, b))} | ${JSON.stringify(gear.policies)} | ${[
          ...pawns.values(),
        ]
          .map((x) => `${x.p.name}:${x.p.policy}`)
          .join(',')} | seq ${gear.seq}`,
    );
    expect(trace).toMatchSnapshot();
  });

  it('materialRulesOp answers and changes exactly as before', () => {
    const gear = gearState();
    const trace: string[] = [];
    const run = (body: Parameters<typeof materialRulesOp>[1], version: string | null) =>
      trace.push(`${JSON.stringify(body)} ${version} → ${attempt(() => materialRulesOp(gear, body, version))}`);
    run({ op: 'set' }, null);
    run({ op: 'copy' }, null);
    run({ op: 'set', rule: { material: 'unobtainium' } }, null);
    for (const m of SIM_MATERIALS)
      for (const tool of [undefined, ...STORY_TOOL_KINDS, 'spoon'])
        for (const tier of [undefined, null, 'wood', 'iron', 'diamond'])
          run({ op: 'set', rule: { material: m.id, tool, tier } }, null);
    run({ op: 'set', rule: { material: SIM_MATERIALS[0]!.id, enchant: 'efficiency', fallback: 'skip' } }, 'b1.8.1');
    run({ op: 'set', rule: { material: SIM_MATERIALS[0]!.id, enchant: 'efficiency', fallback: 'skip' } }, '1.12.2');
    run({ op: 'remove', material: SIM_MATERIALS[0]!.id }, null);
    run({ op: 'remove', material: 'unobtainium' }, null);
    trace.push(JSON.stringify(gear.rules));
    if (process.env.STORY_GOLDEN_DUMP) writeFileSync(`${process.env.STORY_GOLDEN_DUMP}.rules`, trace.join('\n'));
    expect(trace.filter((l) => l.includes('→ !')).length).toBe(585);
    expect(createHash('sha256').update(trace.join('\n')).digest('hex')).toMatchSnapshot();
  });
});
