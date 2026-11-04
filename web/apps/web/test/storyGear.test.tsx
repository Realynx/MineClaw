import {
  distributeWeapons,
  gearSummary,
  StoryColonyState,
  StoryGearRequest,
  StoryMaterialRulesRequest,
  StoryOrderRequest,
  StoryPoliciesRequest,
  type StoryMaterialRule,
  type StoryPolicy,
} from '@mineclaw/contracts';
import { SIM_MATERIALS, StorySim } from '@mineclaw/contracts/testing';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PolicyEditor } from '@/features/story/assign/AssignPanel';
import {
  attackSpec,
  durability,
  enchantLabel,
  fairOrder,
  needsConfirm,
  previewArm,
  ruleProblem,
  ruleSummary,
  tierChoices,
} from '@/features/story/assign/gear';
import { MaterialRulesEditor } from '@/features/story/assign/MaterialRulesEditor';

afterEach(cleanup);

const info = (id: string) => SIM_MATERIALS.find((m) => m.id === id)!;

describe('material tool rules: never below the floor', () => {
  it('greys the tiers below a material’s floor and says why', () => {
    const t = tierChoices(info('iron_ore'));
    expect(t.map((x) => [x.tier, x.disabled])).toEqual([
      ['wood', true],
      ['stone', false],
      ['iron', false],
      ['diamond', false],
    ]);
    expect(t[0]!.why).toBe('iron_ore needs at least a stone pickaxe');
    expect(
      tierChoices(info('obsidian'))
        .filter((x) => !x.disabled)
        .map((x) => x.tier),
    ).toEqual(['diamond']);
    expect(tierChoices(info('dirt')).every((x) => !x.disabled)).toBe(true);
  });

  it('validates a rule before it is sent', () => {
    expect(ruleProblem({ material: 'iron_ore', tier: 'wood' }, info('iron_ore'), '1.12.2')).toBe(
      'iron_ore needs at least a stone pickaxe',
    );
    expect(ruleProblem({ material: 'diamond_ore', tier: 'stone' }, info('diamond_ore'), '1.12.2')).toBe(
      'diamond_ore needs at least a iron pickaxe',
    );
    expect(ruleProblem({ material: 'iron_ore', tier: 'iron', enchant: 'fortune' }, info('iron_ore'), 'b1.8.1')).toBe(
      'b1.8.1 has no enchantments',
    );
    expect(
      ruleProblem({ material: 'iron_ore', tier: 'iron', enchant: 'fortune' }, info('iron_ore'), '1.12.2'),
    ).toBeNull();
    expect(ruleProblem({ material: 'nope' }, undefined, null)).toBe("unknown material 'nope'");
  });

  it('says what a rule does', () => {
    const r: StoryMaterialRule = {
      material: 'diamond_ore',
      tool: 'pickaxe',
      tier: null,
      enchant: 'fortune',
      fallback: 'wait',
      floor: 'iron',
    };
    expect(ruleSummary(r)).toBe('Iron pickaxe or better, Fortune (waits for one)');
    expect(ruleSummary({ ...r, enchant: null, tier: 'diamond' })).toBe('Diamond pickaxe or better');
    expect(
      ruleSummary({ material: 'log', tool: 'axe', tier: null, enchant: null, fallback: 'plain', floor: null }),
    ).toBe('Axe preferred (hand is fine)');
  });

  it('the editor disables tiers below the floor and refuses them locally', () => {
    const set = vi.fn();
    const rules: StoryMaterialRule[] = [
      {
        material: 'iron_ore',
        label: 'Iron ore',
        tool: 'pickaxe',
        tier: null,
        enchant: null,
        fallback: 'plain',
        floor: 'stone',
      },
    ];
    render(
      <MaterialRulesEditor
        materials={SIM_MATERIALS}
        rules={rules}
        version="1.12.2"
        canWrite
        write={{ set, remove: vi.fn() }}
      />,
    );
    const tier = screen.getByLabelText<HTMLSelectElement>('Tier for iron_ore');
    const wood = [...tier.options].find((o) => o.value === 'wood')!;
    expect(wood.disabled).toBe(true);
    expect(wood.textContent).toContain('below the minimum');
    fireEvent.change(tier, { target: { value: 'iron' } });
    expect(set).toHaveBeenCalledWith(expect.objectContaining({ material: 'iron_ore', tier: 'iron' }));
    // The enchantment column is there on 1.12.2, and the fallback once one is chosen.
    fireEvent.change(screen.getByLabelText('Enchantment for iron_ore'), { target: { value: 'fortune' } });
    expect(set).toHaveBeenLastCalledWith(expect.objectContaining({ enchant: 'fortune' }));
  });

  it('b1.8.1 has no enchantment column', () => {
    render(
      <MaterialRulesEditor
        materials={SIM_MATERIALS}
        rules={[{ material: 'coal_ore', tool: 'pickaxe', tier: null, enchant: null, fallback: 'plain', floor: 'wood' }]}
        version="b1.8.1"
        canWrite
        write={{ set: vi.fn(), remove: vi.fn() }}
      />,
    );
    expect(screen.queryByLabelText('Enchantment for coal_ore')).toBeNull();
  });
});

describe('policies', () => {
  const policies: StoryPolicy[] = [
    {
      id: 'default',
      name: 'Default',
      default: true,
      toolsByWork: true,
      tools: [],
      weapon: 'best',
      armor: 'drafted',
      food: 2,
    },
  ];

  it('a new policy needs a name; saving sends it', () => {
    const onSet = vi.fn();
    render(<PolicyEditor policies={policies} canWrite onSet={onSet} onRemove={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /New policy/ }));
    const save = screen.getByRole('button', { name: 'Save policy' });
    expect((save as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/needs a name/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Policy name'), { target: { value: 'Miners' } });
    fireEvent.change(screen.getByLabelText('Armour'), { target: { value: 'always' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save policy' }));
    expect(onSet).toHaveBeenCalledWith(expect.objectContaining({ name: 'Miners', armor: 'always', toolsByWork: true }));
    expect(StoryPoliciesRequest.safeParse({ op: 'set', policy: { name: 'Miners', armor: 'always' } }).success).toBe(
      true,
    );
  });

  it('the default policy cannot be deleted from the editor', () => {
    render(<PolicyEditor policies={policies} canWrite onSet={vi.fn()} onRemove={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /Delete/ })).toBeNull();
  });
});

describe('bulk gear: a fair spread, each stored item to one pawn', () => {
  it('spreads the best weapons one per pawn instead of piling them on one', () => {
    const picks = distributeWeapons(
      [
        { name: 'a', carried: [] },
        { name: 'b', carried: [{ from: 'pawn:b', id: 268, meta: 0, score: 10 }] },
        { name: 'c', carried: [] },
      ],
      [
        { from: '1,2,3', id: 276, meta: 0, score: 16 },
        { from: '1,2,3', id: 267, meta: 0, score: 14 },
      ],
    );
    expect(picks.map((p) => [p.pawn, p.take?.id ?? null, p.keeps?.id ?? null])).toEqual([
      ['a', 276, null],
      ['b', 267, 268],
      ['c', null, null],
    ]);
  });

  it('the preview orders pawns by combat skill and matches the mock director', () => {
    const chest = (x: number, y: number, z: number): readonly [number, number] =>
      x === 3 && y === 64 && z === 3 ? [54, 0] : [0, 0];
    const sim = new StorySim({ world: { blockAt: chest, ground: () => 63, entities: () => [] }, timeRateMs: 0 });
    const { id } = sim.create({ name: 'Gear', pawns: 4 });
    // A chest with the armory in it.
    sim.storage(id, { op: 'set', chests: [[3, 64, 3]], priority: 'normal' });
    const state = StoryColonyState.parse(sim.state(id));
    const names = state.pawns.map((p) => p.name);
    const lines = previewArm(state, names);
    expect(lines.map((l) => l.pawn)).toEqual(fairOrder(state.pawns).map((p) => p.name));
    const out = sim.gear(id, StoryGearRequest.parse({ op: 'arm', pawns: names }));
    const got = (n: string) => out.results.find((r) => r.pawn === n)!;
    for (const l of lines)
      expect(got(l.pawn).got.join(',') || got(l.pawn).missing.join(',')).toContain(
        l.text.replace(/_/g, ' ').split(' ').pop()!,
      );
    expect(out.summary).toBe(gearSummary(out.results));
    // Reserved: the same stored sword never goes to two pawns.
    const taken = out.results.flatMap((r) => r.got.filter((g) => !g.startsWith('keeps')));
    const swords = state
      .storages![0]!.contents.filter(([i]) => [267, 272, 276, 268, 283].includes(i))
      .reduce((s, x) => s + x[2], 0);
    expect(taken.length).toBeLessThanOrEqual(swords);
    // A second Arm finds the best already handed out.
    const again = sim.gear(id, StoryGearRequest.parse({ op: 'arm', pawns: names }));
    expect(again.results.every((r) => r.got.every((g) => g.startsWith('keeps')) || r.missing.length)).toBe(true);
  });
});

describe('attacks', () => {
  it('players and bots need a confirm; mobs and animals do not', () => {
    expect(needsConfirm({ name: 'zombie', kind: 'mob', entity: 4 })).toBe(false);
    expect(needsConfirm({ name: 'cow', kind: 'animal', entity: 5 })).toBe(false);
    expect(needsConfirm({ name: 'poofimafox', kind: 'player', entity: 300 })).toBe(true);
    expect(needsConfirm({ name: 'maid_chan', kind: 'bot', entity: null })).toBe(true);
    const spec = attackSpec({ name: 'maid_chan', kind: 'bot', entity: null }, true);
    expect(spec).toEqual({ kind: 'attack', target: { player: 'maid_chan' }, confirm: true });
    expect(StoryOrderRequest.safeParse({ pawns: ['p'], order: spec }).success).toBe(true);
    expect(
      StoryOrderRequest.safeParse({ pawns: ['p'], order: attackSpec({ name: 'z', kind: 'mob', entity: 9 }, false) })
        .success,
    ).toBe(true);
  });

  it('the mock director answers 409 without confirm, 400 for an own pawn', () => {
    const sim = new StorySim({
      world: {
        blockAt: () => [0, 0],
        ground: () => 63,
        entities: () => [{ id: 300, kind: 'player', name: 'PoofimaFox', x: 1, y: 64, z: 1 }],
      },
      timeRateMs: 0,
    });
    const { id } = sim.create({ name: 'War', pawns: 2 });
    const pawns = [...sim.colony(id).pawns.keys()];
    const order = (target: object, confirm?: boolean) =>
      sim.orders(
        id,
        StoryOrderRequest.parse({ pawns, order: { kind: 'attack', target, ...(confirm ? { confirm } : {}) } }),
      );
    expect(() => order({ entity: 300 })).toThrow(/needs confirm/);
    try {
      order({ entity: 300 });
    } catch (e) {
      expect((e as { status: number }).status).toBe(409);
    }
    expect(order({ entity: 300 }, true).ok).toBe(true);
    expect(() => order({ player: pawns[1]! }, true)).toThrow(/own pawns/);
  });

  it('the mock director refuses a rule below the floor (400)', () => {
    const sim = new StorySim({ world: { blockAt: () => [0, 0], ground: () => 63, entities: () => [] }, timeRateMs: 0 });
    const { id } = sim.create({ name: 'Rules', pawns: 1 });
    const req = StoryMaterialRulesRequest.parse({ op: 'set', rule: { material: 'iron_ore', tier: 'wood' } });
    expect(() => sim.materialRules(id, req)).toThrow('iron_ore needs at least a stone pickaxe');
    const ok = sim.materialRules(
      id,
      StoryMaterialRulesRequest.parse({
        op: 'set',
        rule: { material: 'diamond_ore', enchant: 'fortune', fallback: 'wait' },
      }),
    );
    expect(ok.rule).toMatchObject({ material: 'diamond_ore', floor: 'iron', enchant: 'fortune', fallback: 'wait' });
  });
});

describe('equipment', () => {
  it('durability tones and enchantment labels', () => {
    expect(durability({ durability: 0.05 })).toEqual({ pct: 5, tone: 'worn' });
    expect(durability({ durability: 0.3 })).toEqual({ pct: 30, tone: 'low' });
    expect(durability({ durability: null })).toEqual({ pct: null, tone: 'ok' });
    expect(enchantLabel('fortune', 3)).toBe('Fortune III');
    expect(enchantLabel('silk_touch', 1)).toBe('Silk Touch');
  });
});
