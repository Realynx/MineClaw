/**
 * Goldens for the rest of the Story UI (see storyUiGolden.test.tsx): the Assign tab (pawn policies, the policy editor and its
 * edits, material tool rules, spare tools), the equipment view, avatars, fishing zones and the notice stack. DOM snapshots plus
 * the writes each interaction sends.
 */
import type { StoryColonyState, StoryGearItem, StoryPawn, StoryPolicy, StoryZone } from '@mineclaw/contracts';
import { act, cleanup, fireEvent, render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { AssignPanel, PolicyEditor, type AssignWrites } from '@/features/story/assign/AssignPanel';
import { EquipmentView } from '@/features/story/assign/GearActions';
import { StoryNotices } from '@/features/story/StoryNotices';
import { Avatar } from '@/features/story/visuals';
import { FishingZonesPanel } from '@/features/story/zones/FishingZones';
import { storyUiStates, type StoryUiStates } from './storyUiFixture';

afterEach(cleanup);

let S: StoryUiStates;
beforeAll(() => {
  S = storyUiStates();
});

function variants(cases: Record<string, () => ReactElement>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [name, el] of Object.entries(cases)) {
    out[name] = render(el()).container.innerHTML;
    cleanup();
  }
  return out;
}

const gear = (kind: string, label: string | null, durability: number | null, extra: Partial<StoryGearItem> = {}) =>
  ({ item: [278, 0], name: `minecraft:${kind}`, label, kind, durability, ench: {}, ...extra }) as StoryGearItem;

/** The colony's pawns with equipment of every kind (or none). */
const equipped = (): StoryPawn[] =>
  S.worked.pawns.map((p, i) => ({
    ...p,
    policy: i === 1 ? 'miner' : i === 2 ? 'gone' : p.policy,
    equipment: [
      {
        held: gear('pickaxe', 'Iron pickaxe', 0.5),
        tools: [gear('pickaxe', 'Iron pickaxe', 0.5), gear('axe', null, 0.05), gear('shovel', 'Shovel', null)],
        armor: [gear('helmet', 'Iron helmet', 0.9, { slot: 'head' }), gear('boots', 'Boots', 0.2, { slot: 'feet' })],
        weapons: [gear('sword', 'Diamond sword', 1, { ench: { sharpness: 3 } })],
      },
      { held: null, tools: [], armor: [], weapons: [] },
      undefined,
      { held: { ...gear('other', null, null), name: 'torch' }, tools: [], armor: [], weapons: [] },
    ][i % 4],
    held: i === 2 ? 'Bread' : null,
    gearTasks:
      i === 0
        ? [
            { id: 1, op: 'arm', label: 'Arming' },
            { id: 2, op: 'tools' },
          ]
        : i === 1
          ? []
          : undefined,
  }));

describe('AssignPanel', () => {
  const writes = (log: unknown[]): AssignWrites => ({
    assign: (p, n) => log.push(['assign', p, n]),
    setPolicy: (p) => log.push(['setPolicy', p]),
    removePolicy: (id) => log.push(['removePolicy', id]),
    rule: { set: (r) => log.push(['rule', r]), remove: (m) => log.push(['ruleRemove', m]) },
    options: (o) => log.push(['options', o]),
  });

  it('every tab', () => {
    const out: Record<string, string> = {};
    const log: unknown[] = [];
    for (const [name, state, canWrite] of [
      ['worked', S.worked, true],
      ['readOnly', S.worked, false],
      ['none', undefined, true],
      ['noPolicies', { ...S.worked, policies: [], options: undefined, version: 'b1.8.1' } as StoryColonyState, true],
    ] as const) {
      const r = render(<AssignPanel state={state} pawns={equipped()} canWrite={canWrite} writes={writes(log)} />);
      for (const tab of [...r.container.querySelectorAll('[role="tab"]')]) {
        fireEvent.click(tab);
        out[`${name} ${tab.textContent}`] = r.container.innerHTML;
      }
      cleanup();
    }
    expect(out).toMatchSnapshot();
  });

  it('writes from the pawn grid, the rules and the spare tools', () => {
    const log: unknown[] = [];
    const r = render(<AssignPanel state={S.worked} pawns={equipped()} canWrite writes={writes(log)} />);
    const c = r.container;
    fireEvent.change(c.querySelector('[aria-label^="Policy of"]')!, { target: { value: S.worked.policies![0]!.id } });
    const tab = (t: string) =>
      fireEvent.click([...c.querySelectorAll('[role="tab"]')].find((b) => b.textContent === t)!);
    tab('Tool rules');
    const out: Record<string, string> = {};
    const rule = c.querySelector('[data-rule]');
    if (rule) {
      const m = rule.getAttribute('data-rule');
      fireEvent.change(c.querySelector(`[aria-label="Tier for ${m}"]`)!, { target: { value: 'wood' } });
      fireEvent.change(c.querySelector(`[aria-label="Tier for ${m}"]`)!, { target: { value: 'diamond' } });
      fireEvent.change(c.querySelector(`[aria-label="Tier for ${m}"]`)!, { target: { value: '' } });
      fireEvent.change(c.querySelector(`[aria-label="Tool for ${m}"]`)!, { target: { value: 'pickaxe' } });
      const ench = c.querySelector(`[aria-label="Enchantment for ${m}"]`);
      if (ench) fireEvent.change(ench, { target: { value: 'fortune' } });
      out.ruleAfter = c.innerHTML;
      fireEvent.click(c.querySelector(`[aria-label="Remove the rule for ${m}"]`)!);
    }
    const add = c.querySelector<HTMLSelectElement>('[aria-label="Material to add a rule for"]')!;
    const opt = [...add.options].find((o) => o.value);
    if (opt) {
      fireEvent.change(add, { target: { value: opt.value } });
      fireEvent.click([...c.querySelectorAll('button')].find((b) => b.textContent?.includes('Add'))!);
    }
    tab('Spare tools');
    fireEvent.click(c.querySelector('[data-testid="tool-options"] input[type="checkbox"]')!);
    fireEvent.change(c.querySelector('[aria-label="Spare tools of each kind"]')!, { target: { value: '12' } });
    for (const cb of [...c.querySelectorAll('[data-testid="tool-options"] input[type="checkbox"]')].slice(1, 3))
      fireEvent.click(cb);
    expect({ log, out }).toMatchSnapshot();
  });

  it('the policy editor: pick, new, edit, save, discard, delete', () => {
    const log: unknown[] = [];
    const policies: StoryPolicy[] = [
      ...(S.worked.policies ?? []),
      {
        id: 'scout',
        name: 'Scout',
        default: false,
        toolsByWork: false,
        tools: ['shears'],
        weapon: 'bow',
        armor: 'always',
        food: 4,
      } as StoryPolicy,
    ];
    const r = render(
      <PolicyEditor
        policies={policies}
        canWrite
        onSet={(p) => log.push(['set', p])}
        onRemove={(id) => log.push(['remove', id])}
      />,
    );
    const c = r.container;
    const out: Record<string, string> = { first: c.innerHTML };
    const button = (t: string) => [...c.querySelectorAll('button')].find((b) => b.textContent?.includes(t))!;
    fireEvent.click(button('Scout'));
    out.scout = c.innerHTML;
    fireEvent.change(c.querySelector('[aria-label="Policy name"]')!, { target: { value: '  ' } });
    out.badName = c.innerHTML;
    fireEvent.change(c.querySelector('[aria-label="Policy name"]')!, { target: { value: ' Scout 2 ' } });
    for (const cb of [...c.querySelectorAll('input[type="checkbox"]')].slice(0, 3)) fireEvent.click(cb);
    fireEvent.change(c.querySelector('[aria-label="Weapon"]')!, { target: { value: 'none' } });
    fireEvent.change(c.querySelector('[aria-label="Armour"]')!, { target: { value: 'night' } });
    fireEvent.change(c.querySelector('[aria-label="Food to carry"]')!, { target: { value: '40' } });
    out.edited = c.innerHTML;
    fireEvent.click(button('Save policy'));
    fireEvent.click(button('Delete'));
    fireEvent.click(button('New policy'));
    out.fresh = c.innerHTML;
    fireEvent.change(c.querySelector('[aria-label="Food to carry"]')!, { target: { value: 'x' } });
    fireEvent.click(button('Discard'));
    out.discarded = c.innerHTML;
    cleanup();
    out.empty = render(
      <PolicyEditor policies={[]} canWrite={false} onSet={vi.fn()} onRemove={vi.fn()} />,
    ).container.innerHTML;
    expect({ log, out }).toMatchSnapshot();
  });
});

describe('EquipmentView and Avatar', () => {
  it('every equipment state', () => {
    const pols = S.worked.policies ?? [];
    const cases: Record<string, () => ReactElement> = {};
    equipped().forEach((p, i) => {
      cases[`pawn ${i}`] = () => <EquipmentView pawn={p} policies={pols} />;
      cases[`pawn ${i} no policies`] = () => <EquipmentView pawn={p} policies={[]} />;
    });
    expect(variants(cases)).toMatchSnapshot();
  });

  it('avatars', () => {
    const cases: Record<string, () => ReactElement> = {};
    for (let i = 0; i < 40; i++) {
      const name = `maid_s${i % 3}_${['dara', 'elio', 'fenn', 'gwen', 'ivo', 'noor', 'zed'][i % 7]}${i}`;
      cases[name] = () => (
        <Avatar name={name} size={18 + (i % 3)} ai={i % 5 === 0} className={i % 4 ? undefined : 'x'} />
      );
    }
    expect(variants(cases)).toMatchSnapshot();
  });
});

describe('FishingZonesPanel', () => {
  it('every zone state', () => {
    const fish = S.worked.zones.find((z) => z.kind === 'fish')!;
    const bill = {
      id: 'b1',
      plant: 'fish',
      mode: 'until',
      target: 20,
      done: 3,
      stored: 7,
      active: true,
      paused: false,
      met: false,
    };
    const zones: StoryZone[] = [
      fish,
      {
        ...fish,
        id: 'f2',
        name: 'Pond',
        waterCells: 12,
        catches: 5,
        status: 'no rod',
        fishers: ['dara', 'fenn'],
        bills: [
          bill,
          { ...bill, id: 'b2', mode: 'count', paused: true, active: false },
          { ...bill, id: 'b3', mode: 'forever', met: true, active: false },
        ],
      },
      {
        ...fish,
        id: 'f3',
        name: null,
        label: 'Lake',
        waterCells: undefined,
        cells: 30,
        catches: undefined,
        status: undefined,
        fishers: [],
      },
      { ...fish, id: 'f4', name: null, label: null, waterCells: undefined, cells: undefined },
    ] as never;
    const st = { ...S.worked, zones };
    expect(
      variants({
        none: () => <FishingZonesPanel state={S.founded} canWrite onBill={vi.fn()} onShow={vi.fn()} />,
        write: () => <FishingZonesPanel state={st} canWrite onBill={vi.fn()} onShow={vi.fn()} />,
        read: () => <FishingZonesPanel state={st} canWrite={false} onBill={vi.fn()} onShow={vi.fn()} />,
      }),
    ).toMatchSnapshot();
  });
});

describe('StoryNotices', () => {
  it('conditions, the stack, more, dismiss and the unread badge', () => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] });
    try {
      const now = Math.max(
        ...(S.worked.alerts ?? []).map((a) => (typeof a.at === 'number' ? a.at : 0)),
        1_762_000_150_000,
      );
      vi.setSystemTime(now + 1000);
      const alerts = (n: number, level: string) =>
        Array.from({ length: n }, (_, i) => ({
          id: `${level}${i}`,
          level,
          text: `${level} thing ${String.fromCharCode(97 + (i % 26))}${String.fromCharCode(97 + Math.floor(i / 26))} at 1,64,${i}`,
          at: now,
        }));
      const out: Record<string, string> = {};
      const props = { colony: 'c1', onJump: vi.fn(), onCondition: vi.fn() };
      const r = render(<StoryNotices state={S.worked} toast={null} {...props} />);
      out.worked = r.container.innerHTML;
      r.rerender(
        <StoryNotices
          state={{ ...S.worked, alerts: [...(S.worked.alerts ?? []), ...alerts(6, 'warn'), ...alerts(2, 'danger')] }}
          toast={{ text: 'Saved', tone: 'ok' }}
          {...props}
        />,
      );
      out.many = r.container.innerHTML;
      fireEvent.click(r.container.querySelector('[data-testid="message"] button[aria-label]') ?? r.container);
      out.dismissedOne = r.container.innerHTML;
      fireEvent.click(r.container.querySelector('[data-testid="dismiss-all"]')!);
      out.dismissed = r.container.innerHTML;
      r.rerender(
        <StoryNotices
          state={{ ...S.worked, alerts: alerts(120, 'info') }}
          toast={{ text: 'Careful', tone: 'warn' }}
          {...props}
        />,
      );
      out.unread = r.container.innerHTML;
      act(() => void vi.advanceTimersByTime(7000));
      out.expired = r.container.innerHTML;
      r.rerender(<StoryNotices state={S.founded} toast={null} {...props} colony="c2" />);
      out.otherColony = r.container.innerHTML;
      expect(out).toMatchSnapshot();
    } finally {
      vi.useRealTimers();
    }
  });
});
